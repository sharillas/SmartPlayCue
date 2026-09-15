# Smart Play Cue

Controlo do **Smart Play Cue** (video playout para palco) via OSC.

## Configuração

| Campo | Default | Descrição |
|---|---|---|
| Smart Play Cue IP (send) | `127.0.0.1` | IP da máquina onde corre o Smart Play Cue |
| Smart Play Cue Port (send) | `8010` | Porta OSC de comandos da app |
| Listen Port (receive time) | `8011` | Porta local que recebe o feedback (tempo restante) |

Na app, o feedback é enviado para o host/porta definidos em `companion.json`
(pasta do executável) — se o Companion correr noutra máquina, definir
`FeedbackHost` para o IP dessa máquina.

## Ações

- **GO** — avançar para o próximo cue
- **Previous cue** — voltar ao cue anterior
- **Pause / resume** — alternar pausa
- **Play cue** — tocar cue N (1-based)
- **Stop cue** — parar (fade to black)
- **Mute cue audio** — muta/desmuta o áudio de um cue
- **Master mute on/off** — mute master
- **Master volume** — 0–100
- **Output window on/off** — abrir/fechar a janela de output
- **Layer show/hide/toggle** — layers 1–4
- **Layer mute toggle** — som das layers
- **Layer blend mode** — Normal (alpha) / Add (aditivo)
- **PANIC - eject all** — ejectar tudo

## Feedback / Variáveis

| Variável | Descrição |
|---|---|
| `hh` / `mm` / `ss` / `total` | Tempo restante do cue no ar |
| `status` | `STANDBY` / `ON AIR` |
| `cueId` / `cueName` | Cue atual (número + nome) |
| `drops` | Frames de vsync perdidos (saúde do output) |

Feedbacks incluídos: tempo restante (HH:MM:SS), alarme vermelho nos últimos 5 s,
cue atual, status.

## Referência OSC (aplicação)

| Endereço | Args | Ação |
|---|---|---|
| `/stageplayout/go` · `/prev` · `/next` | — | transporte |
| `/stageplayout/pause` · `/stop` · `/panic` | — | transporte |
| `/stageplayout/cue` | int N | tocar cue N |
| `/stageplayout/cue/mute` | int N | mute do cue N |
| `/stageplayout/volume` | 0–1 | volume master |
| `/stageplayout/output` | 0/1 | abrir/fechar output |
| `/stageplayout/mute` · `/mute/toggle` | — | mute master |
| `/stageplayout/layer/N/show` · `/hide` · `/toggle` | — | layers (N = 1–4) |
| `/stageplayout/layer/N/mute` · `/mute/toggle` | — | som das layers |
| `/stageplayout/layer/N/blend` · `/blend/toggle` | 0/1 | blend Normal/Add |

Feedback recebido: `/smartcue/time/hh|mm|ss|total`, `/smartcue/status`,
`/smartcue/cue/id|name`, `/smartcue/health/drops`.
