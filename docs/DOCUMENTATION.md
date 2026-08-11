# StagePixPlay — Documentação Completa

**Software de playout de vídeo para eventos ao vivo** — minimalista, dark mode azul escuro, nativo Windows.
Desenvolvido por SmartChoice. Versão 1.0.0.

---

## Índice

1. [Visão geral](#1-visão-geral)
2. [Arquitetura técnica](#2-arquitetura-técnica)
3. [Guia de operação](#3-guia-de-operação)
4. [Referência OSC (Companion / Stream Deck)](#4-referência-osc)
5. [Atalhos de teclado](#5-atalhos-de-teclado)
6. [Projetos (guardar/carregar)](#6-projetos)
7. [Codecs e performance](#7-codecs-e-performance)
8. [Build e publish](#8-build-e-publish)
9. **Journal de desenvolvimento — erros e lições** ⭐
10. [Roadmap](#10-roadmap)

---

## 1. Visão geral

StagePixPlay é um playout de vídeo para palco, inspirado no Mitti/QLab mas com motor
próprio de composição GPU. Filosofia: **simples de operar em tempo real**, fiável,
instalador único `.exe`.

Funcionalidades principais:

- Playlist de cues com drag & drop e reordenação visual (linha de inserção azul)
- **GO instantâneo** (barra de espaço): o próximo cue está sempre pré-carregado e pausado no 1.º frame
- **Crossfade real** vídeo + áudio (GPU, vsync-locked)
- **Grupos/playlists** expansíveis com loop e cadeias de auto-continuar
- **2 layers** de composição com **alpha real** (HAP Alpha / WebM), geometria editável em tempo real
- Timecode grande de tempo restante com alerta vermelho nos últimos 5s
- Info por cue: resolução, fps, codecs vídeo/áudio, tamanho, duração/restante, barra de progresso
- Mute master + mute por layer; volume master
- Controlo total via **OSC** (Bitfocus Companion / Stream Deck)
- Deteção do modo da saída (progressivo/interlaçado) na status bar
- Deinterlace automático (yadif bob) para conteúdo 1080i
- Guardar/carregar projetos (JSON) com tudo incluído

---

## 2. Arquitetura técnica

```
StagePlayout.sln
├── src/StagePlayout.Core      → modelos (Cue, Playlist, CueEnd), ProjectStore (JSON), MediaPool
└── src/StagePlayout.App       → WPF app
    ├── Video/
    │   ├── FFDecoder.cs        → decoder próprio FFmpeg (HW D3D11VA + SW fallback,
    │   │                         áudio NAudio/WASAPI, pacing sub-ms, loop, seek, yadif)
    │   ├── D3DCompositor.cs    → renderer D3D11 (Vortice): 4 slots com opacidade animada
    │   │                         no render loop (crossfade vídeo+áudio), Z-order, geometria
    │   ├── CompositorHost.cs   → HwndHost do swapchain na OutputWindow
    │   ├── FFmpegNatives.cs    → DllImportResolver nome→versão (avformat → avformat-62.dll)
    │   ├── MediaInfoReader.cs  → metadata leve (só headers)
    │   └── VideoLog.cs         → diagnóstico %TEMP%\stageplayout_video.log
    ├── Services/
    │   ├── CompanionControl.cs → OSC server UDP :8010 (Rug.Osc)
    │   ├── DisplayInfo.cs      → EnumDisplaySettings (i/p + refresh)
    │   ├── ShellThumbnail.cs   → thumbnails via Windows Shell
    │   ├── PathHelper.cs       → expansão short names 8.3
    │   └── ShortcutConfig.cs   → shortcuts.json
    ├── OutputWindow.xaml       → output fullscreen (compositor)
    ├── MainWindow.xaml         → janela de controlo (dark blue)
    └── installer/setup.iss     → Inno Setup
```

**Decisões de arquitetura relevantes:**

- **Motor 100% próprio** (sem player libraries de terceiros no caminho do vídeo):
  começámos com Flyleaf; na fase 2 substituímos por decoder/compositor próprios —
  foi o que desbloqueou crossfade real, layers com alpha e controlo total.
- **Preload window** (inspiração vMix/PlayDeck): só o cue atual e o seguinte têm decoder
  aberto; os restantes 98+ são metadata + thumbnail. RAM/GPU estáveis com 100+ clips.
- **Opacidade no render loop** (não na UI thread): fades sample-smooth a vsync; o volume
  de cada decoder = base × opacidade do slot (fade de áudio = fade de vídeo, sempre em sync).
- **Threads**: pump de decode por decoder; render loop do compositor com Present(1) vsync;
  UI nunca faz trabalho pesado (opens em background com geração de transições).

---

## 3. Guia de operação

### Cues

- **Adicionar**: botão "+ Adicionar media" ou arrastar ficheiros para a lista
- **Reordenar**: arrastar (linha azul mostra o ponto de inserção; topo = mais prioritário)
- **Duplo-clique**: tocar esse cue · **Espaço**: GO (próximo)
- **Botão direito** num cue:
  - **No fim**: Congelar no último frame *(default)* · Parar (fade out) · Loop · Auto-continuar
  - **Fade in / Fade out**: 0 / 0,5 / 1 / 2 / 3 / 5 s
  - **Agrupar seleção** (multi-seleção com Ctrl) · **Remover cue**

### Grupos (playlists)

- Agrupar cria cabeçalho `▾ Nome (N clips)`; os filhos ficam em cadeia auto-continuar
  (o último pára a sequência)
- Duplo-clique no grupo = correr do início · clique = expandir/colapsar
- Menu do grupo: **Renomear**, **Repetir grupo (loop)** (último → primeiro),
  Desagrupar, Remover
- Mover o grupo move o bloco todo; largar um cue sobre um filho insere-o nesse grupo

### Composição com layers

- **Layer 1 / Layer 2**: vídeo independente por cima do programa (alpha real!)
- Editor visual: arrastar = mover · canto = redimensionar (em tempo real no output)
- **MOSTRAR/OCULTAR** com fade · **SOM** por layer (default: mudas)
- Layers ficam sempre por cima, mesmo durante crossfades do programa

### Transporte

- **GO** (grande) · **PAUSE/RESUME** · **STOP** (fade to black + fade de áudio)
- **RESTANTE**: timecode grande; vermelho nos últimos 5s
- **VOL** + **SOM** (mute master) · painel **A SEGUIR** (próximo cue + thumbnail)

---

## 4. Referência OSC

Servidor OSC na porta UDP **8010**. No Companion: ligação "Generic OSC" → IP da máquina → port 8010.

| Endereço | Args | Ação |
|---|---|---|
| `/stageplayout/go` | — | GO (próximo cue) |
| `/stageplayout/prev` | — | cue anterior |
| `/stageplayout/pause` | — | pausa/resume |
| `/stageplayout/stop` | — | stop (fade) |
| `/stageplayout/cue` | int N | tocar cue N (1-based) |
| `/stageplayout/volume` | 0–1 | volume master |
| `/stageplayout/output` | 0/1 | abrir/fechar output |
| `/stageplayout/mute` | 0/1 *(vazio=toggle)* | mute master |
| `/stageplayout/mute/toggle` | — | toggle mute master |
| `/stageplayout/layer/1/show` · `/hide` · `/toggle` | — | layer 1 |
| `/stageplayout/layer/2/show` · `/hide` · `/toggle` | — | layer 2 |
| `/stageplayout/layer/1/mute` | 0/1 | mute layer (1=muda) |
| `/stageplayout/layer/1/mute/toggle` | — | toggle mute layer 1 |

---

## 5. Atalhos de teclado

Configuráveis em **`shortcuts.json`** (pasta do exe; criado no 1.º arranque):

```json
{ "Go": "Space", "Next": "Right", "Previous": "Left", "Stop": "S", "Pause": "P" }
```

---

## 6. Projetos

Guardar/Abrir (`.stageplayout.json`): cues na ordem, grupos (nome, expansão, loop),
comportamentos de fim, fades, volume — tudo restaurado.

---

## 7. Codecs e performance

- **Recomendado para palco**: HAP / HAP Alpha (.mov) — decode leve, alpha real nas layers
- MP4 H.264/HEVC: hw decode D3D11VA · ProRes, WebM (incl. VP9 alpha), imagens (stills)
- **FPS**: conteúdo 25/50fps pede output a **50Hz** (Windows → definições do ecrã);
  25fps em 60Hz tem judder matemático em qualquer player. A status bar mostra o modo da saída
- Clips interlaçados (1080i): deinterlace automático yadif bob (→ 50p suave)
- SSD recomendado (HAP FHD ≈ 250 Mbps; HAP Q ≈ 440 Mbps)

---

## 8. Build e publish

```powershell
# desenvolvimento (build + run)
.\run.ps1

# produção: publish self-contained single-file + instalador Inno Setup
.\publish.ps1     # -> publish\ + dist\StagePixPlay-Setup-<versao>.exe
```

Requisitos de build: .NET 8 SDK · Inno Setup 6 (opcional, para o instalador).
As DLLs nativas do FFmpeg v8 ficam em `thirdparty/FFmpeg` (copiadas no build/publish).

---

## 9. Journal de desenvolvimento — erros e lições ⭐

Registo honesto dos bugs encontrados nesta primeira fase (para referência futura):

1. **NRE no arranque (race)**: camadas inicializadas no ctor, mas o engine async podia
   chamar `InitPlayers()` antes → NullReferenceException. *Lição: inicializar estado
   estrutural na declaração do campo, não no ctor.*
2. **Vídeo congelado no 2.º clip**: `avcodec_send_packet` devolve `EAGAIN` quando o
   decoder está cheio — pacote descartado → frames de referência perdidos → freeze.
   *Lição: loop EAGAIN com drain+resend é obrigatório em FFmpeg com frame threading.*
3. **Freeze total (UI+vídeo, áudio ok)**: spin EAGAIN infinito + Open síncrono na UI
   thread + `Dispatcher.Invoke` a partir do render thread. *Lição: opens sempre em
   background com "geração de transições"; callbacks de threads com BeginInvoke;
   spins sempre limitados.*
4. **Crash `Cannot find resource 'BgElevatedBrush'`**: estilo de ProgressBar definido
   antes dos brushes no App.xaml — templates resolvem StaticResource por ordem de parse.
   *Lição: recursos primeiro, estilos depois.*
5. **Nomes tipo `16435_~1.MP4`**: drag&drop de certas apps entrega short paths 8.3 DOS.
   *Lição: `GetLongPathName` em todas as entradas de ficheiros.*
6. **`Unable to load DLL 'avformat'`** após remover o Flyleaf: os bindings pedem nomes
   sem versão (`avformat`) mas os ficheiros são versionados (`avformat-62.dll`), e
   .NET 8 não honra `SetDllDirectory`. *Lição: `NativeLibrary.SetDllImportResolver`
   com mapeamento nome→`nome-*.dll`.*
7. **Playback a 2× de velocidade** com hw decode: `best_effort_timestamp` falha em
   alguns frames via D3D11VA → pacing por pts quebrava. *Lição: pts híbrido —
   real quando monotónico, sintético por frame-duration quando falha.*
8. **Judder em motion graphics**: `Thread.Sleep(1)` sem `timeBeginPeriod(1)` dorme
   até 15,6ms. *Lição: `timeBeginPeriod(1)` no arranque + wait híbrido sleep/spin.*
9. **Quirks de bindings** (fork FFmpeg do Flyleaf): `AVHWDeviceType.D3d11va`,
   `AVPixelFormat.D3d11`, `AVRational.Num/Den`, `FrameFlags` com `HasFlag`,
   `avcodec_get_name` devolve `string` — confirmar sempre contra o código-fonte.
10. **Vortice**: `BlendDescription.NonPremultiplied` pronto a usar; `SamplerDescription`
    com ctor reduzido; `Compiler.Compile` devolve `ReadOnlyMemory<byte>`.
11. **PublishSingleFile não copia `None`+`CopyToPublishDirectory`**: o FFmpeg ficava
    de fora do publish. *Lição: cópia explícita no `publish.ps1`.*

---

## 10. Roadmap

- [ ] Módulo Companion dedicado (feedback no Stream Deck: cue atual, tempo)
- [ ] NDI output
- [ ] Preview de vídeo (PROGRAM) na janela de controlo
- [ ] Goto por cue (estilo Mitti: "no fim → salta para cue N")
- [ ] Crossfade configurável por tipo (dip/cross) e blend modes nas layers
- [ ] HW decode também nas layers (partilha de device)
- [ ] Presets de output (taxa de refresh por projeto)
