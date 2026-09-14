import { InstanceBase, InstanceStatus, runEntrypoint } from '@companion-module/base'
import dgram from 'node:dgram'
import { getPresets } from './presets.js'

// OSC: strings têm sempre >=1 \0 terminador, depois padding até 4
const pad = (s) => s + '\0'.repeat(4 - (s.length % 4))

const OSC_PACK = (address, args) => {
	// mensagem OSC mínima: endereço + type tag (sempre presente) + argumentos
	const tags = args.map((v) => (typeof v === 'number' ? 'f' : 's')).join('')
	let out = pad(address) + pad(',' + tags)
	for (const v of args) {
		if (typeof v === 'number') {
			const buf = Buffer.alloc(4)
			buf.writeFloatBE(v)
			out += buf.toString('binary')
		} else if (typeof v === 'string') {
			out += pad(v)
		}
	}
	return Buffer.from(out, 'binary')
}

const OSC_UNPACK = (msg) => {
	let offset = 0
	const address = msg.toString('utf8', 0, msg.indexOf(0))
	offset += Math.ceil((address.length + 1) / 4) * 4
	const typeTag = msg.toString('utf8', offset, msg.indexOf(0, offset))
	offset += Math.ceil((typeTag.length + 1) / 4) * 4
	const args = []
	for (let i = 1; i < typeTag.length; i++) {
		const t = typeTag[i]
		if (t === 'f') {
			args.push(msg.readFloatBE(offset))
			offset += 4
		} else if (t === 'i') {
			args.push(msg.readInt32BE(offset))
			offset += 4
		} else if (t === 's') {
			const end = msg.indexOf(0, offset)
			args.push(msg.toString('utf8', offset, end))
			offset = Math.ceil((end + 1) / 4) * 4
		}
	}
	return { address, args }
}

class SmartCueInstance extends InstanceBase {
	constructor(internal) {
		super(internal)
		this.socket = null
		this.listenSocket = null
		this.vars = { hh: 0, mm: 0, ss: 0, total: 0, status: 'STANDBY' }
	}

	async init(config) {
		this.config = config || {}
		this.updateStatus(InstanceStatus.Ok)

		this.setActionDefinitions({
			go: {
				name: 'GO',
				description: 'Avança para o próximo cue (toca)',
				options: [],
				callback: () => this.sendOsc('/stageplayout/go'),
			},
			prev: {
				name: 'Previous cue',
				description: 'Volta ao cue anterior',
				options: [],
				callback: () => this.sendOsc('/stageplayout/prev'),
			},
			pause: {
				name: 'Pause / resume',
				description: 'Alterna pausa/resume do cue no ar',
				options: [],
				callback: () => this.sendOsc('/stageplayout/pause'),
			},
			playCue: {
				name: 'Play cue',
				description: 'Toca uma cue específica pelo número',
				options: [
					{
						type: 'number',
						label: 'Cue number',
						id: 'cueNumber',
						default: 1,
						min: 1,
						max: 999,
					},
				],
				callback: (action) => this.sendOsc('/stageplayout/cue', action.options.cueNumber),
			},
			stopCue: {
				name: 'Stop cue',
				description: 'Para a cue em reprodução (fade to black)',
				options: [],
				callback: () => this.sendOsc('/stageplayout/stop'),
			},
			muteCue: {
				name: 'Mute cue audio',
				description: 'Muta/desmuta o áudio de uma cue específica',
				options: [
					{
						type: 'number',
						label: 'Cue number',
						id: 'cueNumber',
						default: 1,
						min: 1,
						max: 999,
					},
				],
				callback: (action) => this.sendOsc('/stageplayout/cue/mute', action.options.cueNumber),
			},
			masterMute: {
				name: 'Master mute on/off',
				description: 'Muta/desmuta o volume master',
				options: [],
				callback: () => this.sendOsc('/stageplayout/mute/toggle'),
			},
			volume: {
				name: 'Master volume',
				description: 'Define o volume master (0-100)',
				options: [
					{
						type: 'number',
						label: 'Volume (0-100)',
						id: 'volume',
						default: 100,
						min: 0,
						max: 100,
					},
				],
				callback: (action) => this.sendOsc('/stageplayout/volume', action.options.volume / 100),
			},
			output: {
				name: 'Output window on/off',
				description: 'Abre/fecha a janela de output (2.º ecrã)',
				options: [
					{
						type: 'dropdown',
						label: 'State',
						id: 'state',
						default: '1',
						choices: [
							{ id: '1', label: 'Open' },
							{ id: '0', label: 'Close' },
						],
					},
				],
				callback: (action) => this.sendOsc('/stageplayout/output', Number(action.options.state)),
			},
			layerShow: {
				name: 'Layer show/hide/toggle',
				description: 'Mostra/oculta/alterna uma layer (1 ou 2)',
				options: [
					{
						type: 'dropdown',
						label: 'Layer',
						id: 'layer',
						default: '1',
						choices: [
							{ id: '1', label: 'Layer 1' },
							{ id: '2', label: 'Layer 2' },
						],
					},
					{
						type: 'dropdown',
						label: 'Action',
						id: 'action',
						default: 'toggle',
						choices: [
							{ id: 'toggle', label: 'Toggle' },
							{ id: 'show', label: 'Show' },
							{ id: 'hide', label: 'Hide' },
						],
					},
				],
				callback: (action) =>
					this.sendOsc(`/stageplayout/layer/${action.options.layer}/${action.options.action}`),
			},
			layerMute: {
				name: 'Layer mute toggle',
				description: 'Alterna o som de uma layer (1 ou 2)',
				options: [
					{
						type: 'dropdown',
						label: 'Layer',
						id: 'layer',
						default: '1',
						choices: [
							{ id: '1', label: 'Layer 1' },
							{ id: '2', label: 'Layer 2' },
						],
					},
				],
				callback: (action) => this.sendOsc(`/stageplayout/layer/${action.options.layer}/mute/toggle`),
			},
			layerBlend: {
				name: 'Layer blend mode',
				description: 'Blend mode da layer: alpha normal ou aditivo (Add)',
				options: [
					{
						type: 'dropdown',
						label: 'Layer',
						id: 'layer',
						default: '1',
						choices: [
							{ id: '1', label: 'Layer 1' },
							{ id: '2', label: 'Layer 2' },
						],
					},
					{
						type: 'dropdown',
						label: 'Mode',
						id: 'mode',
						default: '0',
						choices: [
							{ id: '0', label: 'Normal (alpha)' },
							{ id: '1', label: 'Add (additive)' },
						],
					},
				],
				callback: (action) => this.sendOsc(`/stageplayout/layer/${action.options.layer}/blend`, Number(action.options.mode)),
			},
			panic: {
				name: 'PANIC - eject all',
				description: 'Ejecta todas as cues (botão de pânico)',
				options: [],
				callback: () => this.sendOsc('/stageplayout/panic'),
			},
		})

		this.setVariableDefinitions([
			{ variableId: 'hh', name: 'Remaining Hours' },
			{ variableId: 'mm', name: 'Remaining Minutes' },
			{ variableId: 'ss', name: 'Remaining Seconds' },
			{ variableId: 'total', name: 'Remaining Total Seconds' },
			{ variableId: 'status', name: 'Status' },
			{ variableId: 'cueId', name: 'Current cue number' },
			{ variableId: 'cueName', name: 'Current cue name' },
			{ variableId: 'drops', name: 'Frame drops (vsync)' },
		])

		this.setFeedbackDefinitions({
			remainingTime: {
				name: 'Remaining time (HH:MM:SS)',
				type: 'advanced',
				callback: () => {
					const p2 = (v) => String(Math.max(0, v)).padStart(2, '0')
					return {
						text: `${p2(this.vars.hh)}:${p2(this.vars.mm)}:${p2(this.vars.ss)}`,
					}
				},
			},
			status: {
				name: 'Status (ON AIR / STANDBY)',
				type: 'advanced',
				callback: () => ({ text: this.vars.status }),
			},
			currentCue: {
				name: 'Current cue (number + name)',
				type: 'advanced',
				callback: () => ({
					text: this.vars.cueId > 0 ? `CUE ${this.vars.cueId}\\n${this.vars.cueName}` : 'NO CUE',
				}),
			},
			timeCritical: {
				name: 'Alarm: last 5 seconds (red)',
				type: 'advanced',
				callback: () =>
					this.vars.status === 'ON AIR' && this.vars.total > 0 && this.vars.total <= 5
						? { bgcolor: 0xc62828, color: 0xffffff }
						: { bgcolor: 0x222222, color: 0xffffff },
			},
		})

		this.setPresetDefinitions(getPresets())
		this.startListener()
	}

	startListener() {
		const port = Number(this.config.listenPort || 8011)
		try {
			if (this.listenSocket) this.listenSocket.close()
			this.listenSocket = dgram.createSocket('udp4')
			this.listenSocket.on('message', (msg) => {
				try {
					const { address, args } = OSC_UNPACK(msg)
					this.handleOsc(address, args)
				} catch (e) {
					// ignore malformed
				}
			})
			this.listenSocket.on('error', () => {})
			this.listenSocket.bind(port)
		} catch (e) {
			this.log('error', `Listener failed: ${e.message}`)
		}
	}

	handleOsc(address, args) {
		const num = (i) => (typeof args[i] === 'number' ? args[i] : 0)
		switch (address.toLowerCase()) {
			case '/smartcue/time/hh':
				this.vars.hh = Math.floor(num(0))
				this.setVariableValues({ hh: this.vars.hh })
				break
			case '/smartcue/time/mm':
				this.vars.mm = Math.floor(num(0))
				this.setVariableValues({ mm: this.vars.mm })
				break
			case '/smartcue/time/ss':
				this.vars.ss = Math.floor(num(0))
				this.setVariableValues({ ss: this.vars.ss })
				break
			case '/smartcue/time/total':
				this.vars.total = Math.floor(num(0))
				this.setVariableValues({ total: this.vars.total })
				break
			case '/smartcue/status':
				this.vars.status = typeof args[0] === 'string' ? args[0] : ''
				this.setVariableValues({ status: this.vars.status })
				break
			case '/smartcue/cue/id':
				this.vars.cueId = Math.floor(num(0))
				this.setVariableValues({ cueId: this.vars.cueId })
				break
			case '/smartcue/cue/name':
				this.vars.cueName = typeof args[0] === 'string' ? args[0] : ''
				this.setVariableValues({ cueName: this.vars.cueName })
				break
			case '/smartcue/health/drops':
				this.vars.drops = Math.floor(num(0))
				this.setVariableValues({ drops: this.vars.drops })
				break
		}
	}

	sendOsc(address, value) {
		const target = this.config.host || '127.0.0.1'
		const port = Number(this.config.port || 8010)
		const args = typeof value === 'undefined' ? [] : [value]
		try {
			if (!this.socket) this.socket = dgram.createSocket('udp4')
			const msg = OSC_PACK(address, args)
			this.socket.send(msg, port, target)
		} catch (e) {
			this.log('error', `OSC send failed: ${e.message}`)
		}
	}

	getConfigFields() {
		return [
			{
				type: 'textinput',
				id: 'host',
				label: 'Smart Play Cue IP (send)',
				default: '127.0.0.1',
				width: 6,
			},
			{
				type: 'number',
				id: 'port',
				label: 'Smart Play Cue Port (send)',
				default: 8010,
				min: 1,
				max: 65535,
				width: 6,
			},
			{
				type: 'number',
				id: 'listenPort',
				label: 'Listen Port (receive time)',
				default: 8011,
				min: 1,
				max: 65535,
				width: 6,
			},
		]
	}

	async destroy() {
		if (this.socket) {
			try { this.socket.close() } catch (e) {}
			this.socket = null
		}
		if (this.listenSocket) {
			try { this.listenSocket.close() } catch (e) {}
			this.listenSocket = null
		}
	}
}

runEntrypoint(SmartCueInstance, [])
