import { InstanceBase, InstanceStatus, runEntrypoint } from '@companion-module/base'
import dgram from 'node:dgram'
import { getPresets } from './presets.js'

const OSC_PACK = (address, args) => {
	const parts = [address].concat(args)
	let out = ''
	const pad = (s) => s + '\0'.repeat((4 - (s.length % 4)) % 4)
	out += pad(parts[0])
	for (let i = 1; i < parts.length; i++) {
		const v = parts[i]
		if (typeof v === 'number') {
			out += pad(',f')
			const buf = Buffer.alloc(4)
			buf.writeFloatBE(v)
			out += buf.toString('binary')
		} else if (typeof v === 'string') {
			out += pad(',s')
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
				description: 'Para a cue em reprodução',
				options: [],
				callback: () => this.sendOsc('/stageplayout/stop', 1),
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
				callback: () => this.sendOsc('/stageplayout/mute/toggle', 1),
			},
			panic: {
				name: 'PANIC - eject all',
				description: 'Ejecta todas as cues (botão de pânico)',
				options: [],
				callback: () => this.sendOsc('/stageplayout/panic', 1),
			},
		})

		this.setVariableDefinitions([
			{ variableId: 'hh', name: 'Remaining Hours' },
			{ variableId: 'mm', name: 'Remaining Minutes' },
			{ variableId: 'ss', name: 'Remaining Seconds' },
			{ variableId: 'total', name: 'Remaining Total Seconds' },
			{ variableId: 'status', name: 'Status' },
		])

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
				label: 'SmartCue IP (send)',
				default: '127.0.0.1',
				width: 6,
			},
			{
				type: 'number',
				id: 'port',
				label: 'SmartCue Port (send)',
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
