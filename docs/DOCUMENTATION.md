# Smart Play Cue — Documentação Completa

**Software de playout de vídeo para eventos ao vivo** — minimalista, dark mode azul escuro, nativo Windows.
Desenvolvido por SmartChoice. Versão 2.5.0.

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

Smart Play Cue é um playout de vídeo para palco, inspirado no Mitti/QLab mas com motor
próprio de composição GPU. Filosofia: **simples de operar em tempo real**, fiável,
instalador único `.exe`.

Funcionalidades principais:

- Playlist de cues com drag & drop e reordenação visual (linha de inserção azul)
- **GO instantâneo** (barra de espaço): o próximo cue está sempre pré-carregado e pausado no 1.º frame
- **Crossfade real** vídeo + áudio (GPU, vsync-locked) com **Cross (dissolve)** ou **Dip (via preto)** por cue
- **PROGRAM preview**: preview ao vivo (30 fps) da saída composta na janela de controlo
- **Presets de output por projeto**: ecrã de saída + refresh preferido, guardados no projeto
  (right-click no botão de output; o refresh é aplicado/restaurado ao abrir/fechar o output)
- **Grupos/playlists** expansíveis com loop e cadeias de auto-continuar
- **2–4 layers** de composição com **alpha real** (HAP Alpha / WebM), geometria editável em tempo real,
  e **blend modes** (Normal / Add aditivo) por layer — **lista dinâmica, estado guardado no projeto**
- **Volume por cue** (25/50/75/100%) aplicado ao áudio e guardado no projeto
- **Fade editor na linha**: pegas arrastáveis de fade in/out no timebar de cada cue
- Timecode grande de tempo restante com alerta vermelho nos últimos 5s
- Info por cue: resolução, fps, codecs vídeo/áudio, tamanho, duração/restante, barra de progresso
- VU meter por cue com **picos reais** calculados pelo decoder (attack rápido + decay suave)
- Mute master + mute por layer; volume master
- **Overlay de confiança no palco**: cue atual + tempo restante no canto do output (botão OVL)
- **Robustez live**: crash handlers globais (log + minidump), guarda no Escape do output,
  aviso de GO no último cue, raças de slots corrigidas (STOP→GO, hide→show),
  **recuperação de TDR** (device D3D11 recriado se a GPU reiniciar o driver),
  **áudio isolado** (falha do device de áudio nunca congela o vídeo)
- **Frame drops monitor**: contador de vsync perdidos (status bar + OSC)
- **Projeto corrompido?** o load tenta automaticamente o auto-backup `.bak`
- **Projetos portáteis**: media dentro da pasta do show é guardada como caminho relativo
- **Auto-backup rotativo** (5 cópias com timestamp) + indicador de projeto "sujo"
- **Show mode (Ctrl+L)**: trava edições acidentais em live (sem context menus/drag/add media)
- **Auto-load por argumento**: `SmartPlayCue.exe show.stageplayout.json [--output] [--autoplay] [--selftest]`
- Controlo total via **OSC** (Bitfocus Companion / Stream Deck), com feedback HH/MM/SS configurável
- Goto por cue (estilo Mitti: "no fim → salta para cue N")
- Deteção do modo da saída (progressivo/interlaçado) na status bar
- Deinterlace automático (yadif bob) para conteúdo 1080i
- Guardar/carregar projetos (JSON) com tudo incluído

---

## 2. Arquitetura técnica

```
SmartCue.sln
├── src/StagePlayout.Core      → modelos (Cue, Playlist, CueEnd), ProjectStore (JSON), OscUdp
├── src/StagePlayout.App       → WPF app
│   ├── App.xaml.cs            → crash handlers (UI thread / AppDomain / tasks) + CrashLog
│   ├── Video/
    │   ├── FFDecoder.cs        → decoder próprio FFmpeg (HW D3D11VA com device partilhado
    │   │                         entre decoders + SW fallback, áudio NAudio/WASAPI com
    │   │                         picos reais para o VU meter, pacing sub-ms, loop, seek, yadif)
    │   ├── D3DCompositor.cs    → renderer D3D11 (Vortice): 4 slots com opacidade animada
    │   │                         no render loop (crossfade cross/dip), Z-order, geometria,
    │   │                         blend states por slot (Normal/Add), preview PROGRAM
    │   │                         offscreen (480×270, lido pela UI)
    │   ├── CompositorHost.cs   → HwndHost do swapchain na OutputWindow
    │   ├── FFmpegNatives.cs    → DllImportResolver nome→versão (avformat → avformat-62.dll)
    │   ├── MediaInfoReader.cs  → metadata leve (só headers)
    │   └── VideoLog.cs         → diagnóstico %TEMP%\stageplayout_video.log
    ├── Services/
    │   ├── CompanionControl.cs → OSC server UDP :8010 (implementação própria, sem Rug.Osc)
    │   ├── CompanionConfig.cs  → companion.json (portas + IP do feedback)
    │   ├── DisplayInfo.cs      → EnumDisplaySettings + ChangeDisplaySettingsEx
    │   │                         (deteção i/p + presets de refresh por projeto)
    │   ├── ShellThumbnail.cs   → thumbnails via Windows Shell
    │   ├── PathHelper.cs       → expansão short names 8.3
    │   └── ShortcutConfig.cs   → shortcuts.json
    ├── OutputWindow.xaml       → output fullscreen (compositor; Escape só com Ctrl+Shift)
    ├── MainWindow.xaml         → janela de controlo (dark blue, com preview PROGRAM)
    └── installer/setup.iss     → Inno Setup
├── tests/StagePlayout.Tests   → xUnit (Playlist, ProjectStore, OscUdp)
└── .github/workflows/ci.yml   → CI: build + testes em push/PR; publish em tags v*
```

**Decisões de arquitetura relevantes:**

- **Motor 100% próprio** (sem player libraries de terceiros no caminho do vídeo):
  começámos com Flyleaf; na fase 2 substituímos por decoder/compositor próprios —
  foi o que desbloqueou crossfade real, layers com alpha e controlo total.
- **Preload window** (inspiração vMix/PlayDeck): só o cue atual e o seguinte têm decoder
  aberto; os restantes 98+ são metadata + thumbnail. RAM/GPU estáveis com 100+ clips.
- **Opacidade no render loop** (não na UI thread): fades sample-smooth a vsync; o volume
  de cada decoder = base × opacidade do slot (fade de áudio = fade de vídeo, sempre em sync).
- **Preview PROGRAM**: o render loop desenha a cena 2.ª vez num target offscreen 480×270
  (animate=false para não avançar fades 2×), copia para staging e publica os bytes num
  buffer partilhado; a UI lê com `TryCopyPreviewInto` — zero chamadas D3D fora do render thread.
- **OSC próprio**: o Rug.Osc (.NET Framework 4.x) foi substituído por `OscUdp.cs`
  (~160 linhas, mensagens + bundles) — sem warnings NU1701 e sem dependências.
- **Ciclo de vida do output**: fechar o output para TODOS os decoders imediatamente
  (`StopAllPlayback`) — o som nunca continua sem vídeo.
- **Threads**: pump de decode por decoder; render loop do compositor com Present(1) vsync;
  UI nunca faz trabalho pesado (opens em background com geração de transições).

---

## 3. Guia de operação

### Cues

- **Adicionar**: botão "+ Adicionar media" ou arrastar ficheiros para a lista
- **Reordenar**: arrastar (linha azul mostra o ponto de inserção; topo = mais prioritário)
- **Duplo-clique**: tocar esse cue · **Espaço**: GO (próximo)
- **Botão direito** num cue:
  - **No fim**: Congelar no último frame *(default)* · Parar (fade out) · Loop · **Jump to cue**
  - **Fade in / Fade out**: 0 / 0,5 / 1 / 2 / 3 / 5 s
  - **Fade Type**: **Cross** (dissolve sobreposto, default) ou **Dip** (via preto:
    o cue atual desce primeiro, o novo entra depois)
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
- **BLEND**: Normal (alpha) ou **Add** (aditivo — ideal para HAP com glow/luzes)
- Layers ficam sempre por cima, mesmo durante crossfades do programa

### Preview PROGRAM e output

- Coluna direita: **PROGRAM** mostra a saída composta ao vivo (30 fps) — programa + layers
- **A SEGUIR** mostra o próximo cue
- **External Display** (right-click): escolher o ecrã de saída e o refresh (Auto/50/60/75 Hz).
  O preset é guardado no projeto; ao abrir o output o refresh é aplicado temporariamente
  e restaurado ao fechar.
- **Guarda anti-acidente**: Escape no ecrã de palco só fecha o output com **Ctrl+Shift+Esc**

### Transporte

- **GO** (grande) · **PAUSE/RESUME** · **STOP** (fade to black + fade de áudio)
- **RESTANTE**: timecode grande; vermelho nos últimos 5s
- **VOL** + **SOM** (mute master) · painel **A SEGUIR** (próximo cue + thumbnail)
- GO no **último cue** mostra aviso na status bar (repete o cue atual)
- Título da janela mostra **"•"** quando há alterações por guardar; ao fechar pergunta
  se quer guardar. O auto-backup grava `projeto.stageplayout.json.bak` a cada N minutos
  (`AutoBackupMinutes` em `shortcuts.json`, default 5, 0 = off)

### Arranque automático (shows recorrentes)

```
SmartPlayCue.exe "C:\shows\natal.stageplayout.json"
SmartPlayCue.exe "C:\shows\natal.stageplayout.json" --output --autoplay
SmartPlayCue.exe "C:\shows\natal.stageplayout.json" --selftest
```

O projeto é carregado no arranque (playlist + presets de output + layers).
`--output` abre a janela de output; `--autoplay` toca o 1.º cue (kiosk desatendido);
`--selftest` toca todos os cues e layers automaticamente e termina com exit code
0 (OK) / 1 (falhas) — relatório em `%TEMP%\stageplayout_selftest.log`.

### Show mode (Ctrl+L)

Trava edições acidentais em live: sem context menus, sem drag & drop, sem adicionar
media. Transporte (GO/STOP/PAUSE), mutes, layers e output continuam ativos.
O título mostra "SHOW MODE" e Ctrl+L volta ao normal.

### Overlay de confiança (OVL)

Botão **OVL** na barra de transporte: mostra "CUE n — nome — -HH:MM:SS" no canto
inferior esquerdo do ecrã de palco (janela transparente, sem foco, clicável-through).

---

## 4. Referência OSC

Servidor OSC na porta UDP **8010** (configurável em `companion.json`).
No Companion: ligação "Generic OSC" → IP da máquina → port 8010.

| Endereço | Args | Ação |
|---|---|---|
| `/stageplayout/go` | — | GO (próximo cue) |
| `/stageplayout/prev` | — | cue anterior |
| `/stageplayout/next` | — | cue seguinte |
| `/stageplayout/pause` | — | pausa/resume |
| `/stageplayout/stop` | — | stop (fade) |
| `/stageplayout/panic` | — | eject all |
| `/stageplayout/cue` | int N | tocar cue N (1-based) |
| `/stageplayout/cue/mute` | int N | toggle mute do cue N |
| `/stageplayout/volume` | 0–1 | volume master |
| `/stageplayout/output` | 0/1 | abrir/fechar output |
| `/stageplayout/mute` | 0/1 *(vazio=toggle)* | mute master |
| `/stageplayout/mute/toggle` | — | toggle mute master |
| `/stageplayout/layer/1/show` · `/hide` · `/toggle` | — | layer 1 |
| `/stageplayout/layer/2/show` · `/hide` · `/toggle` | — | layer 2 |
| `/stageplayout/layer/1/mute` | 0/1 | mute layer (1=muda) |
| `/stageplayout/layer/1/mute/toggle` | — | toggle mute layer 1 |
| `/stageplayout/layer/1/blend` | 0/1 | blend da layer (1 = Add aditivo) |
| `/stageplayout/layer/1/blend/toggle` | — | toggle blend da layer 1 |

**Feedback** (enviado para `FeedbackHost:FeedbackPort` do `companion.json`, defaults 127.0.0.1:8011):

| Endereço | Tipo | Valor |
|---|---|---|
| `/smartcue/time/hh` · `/mm` · `/ss` | int | tempo restante (horas/min/seg) |
| `/smartcue/time/total` | int | segundos totais restantes |
| `/smartcue/status` | string | `STANDBY` ou `ON AIR` |
| `/smartcue/cue/id` | int | número do cue no ar (0 = nenhum) |
| `/smartcue/cue/name` | string | nome do cue no ar |
| `/smartcue/health/drops` | int | frames de vsync perdidos (saúde do output) |

Se o Companion correr noutra máquina, definir `FeedbackHost` no `companion.json`
para o IP dessa máquina.

---

## 5. Atalhos de teclado

Configuráveis em **`shortcuts.json`** (pasta do exe; criado no 1.º arranque):

```json
{ "Go": "Space", "Next": "Right", "Previous": "Left", "Stop": "S", "Pause": "P",
  "AutoBackupMinutes": 5 }
```

---

## 6. Projetos

Guardar/Abrir (`.stageplayout.json`): cues na ordem, grupos (nome, expansão, loop),
comportamentos de fim, fades (tipo cross/dip + tempos), volume por cue, **estado das
layers L1/L2** (ficheiro, geometria, mute, blend) — tudo restaurado. Inclui também os
**presets de output** (ecrã de saída + refresh). Ficheiros antigos (formato lista simples)
continuam a abrir (compatibilidade retroativa).

**Corrupção**: se o ficheiro principal não abrir (JSON inválido), a app tenta
automaticamente `<projeto>.stageplayout.json.bak` (auto-backup). O auto-backup roda a
cada N minutos (`AutoBackupMinutes` em `shortcuts.json`) e mantém 5 cópias com timestamp
(`.bak.yyyyMMdd-HHmmss`).

---

## 7. Codecs e performance

- **Recomendado para palco**: HAP / HAP Alpha (.mov) — decode leve, alpha real nas layers
- MP4 H.264/HEVC: hw decode D3D11VA · ProRes, WebM (incl. VP9 alpha), imagens (stills)
- **FPS**: conteúdo 25/50fps pede output a **50Hz** — usar o preset de refresh por
  projeto (right-click no botão de output); a status bar mostra o modo da saída
- Clips interlaçados (1080i): deinterlace automático yadif bob (→ 50p suave)
- SSD recomendado (HAP FHD ≈ 250 Mbps; HAP Q ≈ 440 Mbps)

---

## 8. Build e publish

```powershell
# desenvolvimento (build + run)
.\run.ps1

# testes do núcleo (Playlist, ProjectStore, OSC)
dotnet test SmartCue.sln -c Release

# produção: publish self-contained single-file + instalador Inno Setup
.\publish.ps1     # -> publish\ + dist\SmartPlayCue-Setup-<versao>.exe

# módulo Companion (tgz para instalação manual no Bitfocus Companion)
npm pack          # em companion-module-smartplaycue\
```

Requisitos de build: .NET 8 SDK · Inno Setup 6 (opcional, para o instalador).
As DLLs nativas do FFmpeg v8 ficam em `thirdparty/FFmpeg` (copiadas no build/publish).

---

## 9. Journal de desenvolvimento — erros e lições ⭐

Registo honesto dos bugs encontrados (para referência futura):

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
    com ctor reduzido; `Compiler.Compile` devolve `ReadOnlyMemory<byte>`;
    `Texture2DDescription.CPUAccessFlags` (não `CpuAccessFlags`).
11. **PublishSingleFile não copia `None`+`CopyToPublishDirectory`**: o FFmpeg ficava
    de fora do publish. *Lição: cópia explícita no `publish.ps1`.*
12. **Áudio continuava com o output fechado** (2.2.0): `StopPlayback` fazia return
    cedo sem compositor e o `Closed` do output não parava os decoders; o `_compWired`
    ficava true para o compositor novo (fades nunca terminavam → slots nunca
    libertados). *Lição: o fecho do output tem de ejetar TODO o motor
    (`StopAllPlayback`) e resetar o wiring do compositor.*
13. **Rug.Osc (NU1701)**: pacote .NET Framework 4.x restaurado "como compatível" —
    risco silencioso. *Lição: substituído por `OscUdp.cs` próprio (mensagens +
    bundles em ~160 linhas); menos uma dependência.*
14. **VU meter era falso**: `Random` por tick em vez dos picos do decoder.
    *Lição: calcular máx. absoluto por canal no `swr_convert` (attack imediato,
    decay ×0.90 por frame) — meter real sem custo.*
15. **Preview PROGRAM**: desenhar a cena 2.ª vez no render loop (pass 2) avançava a
    opacidade 2× por frame. *Lição: pass de preview com `animate=false`; só a pass
    principal avança fades/volumes.*
16. **MediaPool era um no-op**: TODOs com `new object()`; o preload real sempre viveu
    no `PreloadNext()` do MainWindow. *Lição: removido — uma fonte de verdade.*
17. **Robustez em live events (2.2.0)**: exceção na UI thread matava a app
    silenciosamente. *Lição: `DispatcherUnhandledException` + `AppDomain` +
    `TaskScheduler` com log em `%TEMP%\stageplayout_crash.log`; diálogo
    "continuar/fechar" na UI thread, motor de vídeo (threads próprias) continua.*
18. **Playlists grandes arrancavam N decoders FFmpeg em paralelo** (thumbnails).
    *Lição: `SemaphoreSlim(3)` na extração de thumbnails.*
19. **Zero testes automatizados**: regressões no núcleo (Playlist/ProjectStore/OSC)
    passavam despercebidas. *Lição: projeto xUnit em `tests/` — 22 testes no
    núcleo, `dotnet test SmartCue.sln`.*
20. **Race STOP→GO matava o cue novo (2.3.0)**: o slot em fade-out de um STOP
    continuava em `_closingSlots`; o GO seguinte reutilizava o slot e, ao fim do
    fade, o `FadeCompleted` dispunha o decoder NOVO no ar. Igual em layers
    (hide→show). *Lição: remover o slot do set de fecho ANTES de o reutilizar;
    `_pendingDip` guarda a geração da transição para descartar fades antigos.*
21. **Escape no ecrã de palco fechava o output**: um toque acidental matava o show.
    *Lição: output só fecha com Ctrl+Shift+Esc; a janela de controlo fecha
    normalmente.*
22. **Dip (via preto)**: incoming pausado até o fade-out do outgoing terminar —
    o arranque é feito no `FadeCompleted` (render thread → BeginInvoke), com
    verificação de geração para transições canceladas.
23. **Blend Add nas layers**: segundo blend state (One/One) por slot; o
    `OMSetBlendState` é por-draw no render loop. *Lição: `BlendDescription` do
    Vortice usa fixed buffer — atribuir `RenderTarget[0]`, não uma array.*
24. **Device D3D11VA partilhado**: criar/destruir o device por transição era caro;
    agora é 1 por processo (static + `av_buffer_ref` por contexto).
25. **Logs sem limite**: rotação por tamanho (2 MB vídeo / 512 KB crash, .old).
26. **`Cue.Volume` existia mas nunca era aplicado (2.4.0)**: o volume por cue
    era gravado no projeto mas o playback só usava o master. *Lição: o volume
    efetivo do slot = master × escala do cue × opacidade (nova `SetVolumeScale`
    no compositor) — rever sempre o caminho completo de cada propriedade.*
27. **Projeto corrompido parava o arranque**: com auto-backup ativo, o fallback
    para `.bak` no `ProjectStore.Load` salva o show. *Lição: resiliência de
    dados vem a par da geração de backups.*
28. **TDR (device lost)**: driver da GPU a reiniciar deixava o output preto até
    reabrir. *Lição: `DeviceRemovedReason` no render loop → recriar device +
    pipeline + texturas (lazy) e continuar.*
29. **WPF não desenha por cima de HwndHost** (overlay de confiança): qualquer
    TextBlock da OutputWindow ficava atrás do swapchain. *Lição: janela WPF
    separada, transparente, Topmost, ShowActivated=false e IsHitTestVisible=false,
    posicionada sobre o output.*
30. **Falha de áudio congelava o vídeo (2.5.0)**: qualquer exceção no pump
    (ex.: device USB desligado) punha o decoder em Failed. *Lição: caminho de
    áudio isolado — erro desativa o áudio e o vídeo continua; erros de vídeo
    só falham após N consecutivos.*
31. **Frames perdidos invisíveis**: degradação de vsync só se via no palco.
    *Lição: medir o intervalo do render loop contra a média móvel (drops) e
    expor na UI + OSC.*
32. **Paths absolutos quebravam shows movidos**: media dentro da pasta do
    projeto é agora gravada relativa (`Path.GetRelativePath`), com fallback
    por nome de ficheiro no load.
33. **Multi-layers**: slots fixos → lista dinâmica de 4 com re-encadeamento
    (ReattachLayers) após add/remove. *Lição: índices de slot = 2 + posição
    na lista; manter uma única rotina de reattach evita slots órfãos.*

---

## 10. Roadmap

- [x] Módulo Companion dedicado (feedback no Stream Deck: cue atual, tempo)
- [x] Goto por cue (estilo Mitti: "no fim → salta para cue N")
- [x] Preview de vídeo (PROGRAM) na janela de controlo
- [x] Presets de output (ecrã + taxa de refresh por projeto)
- [x] Crossfade configurável por tipo (dip/cross) e blend modes nas layers (Normal/Add)
- [x] HW decode também nas layers (partilha do device D3D11VA entre decoders)
- [x] Auto-load de projeto por argumento de linha de comandos
- [x] Backup automático do projeto (intervalo configurável)
- [x] Robustez live: crash handlers, guarda do output, GO no último cue, raças de slots
- [x] CI no GitHub (build + testes + publish por tag)
- [x] Presets de geometria das layers guardados no projeto (ficheiro, geom, mute, blend)
- [x] Volume por cue aplicado ao playback
- [x] Overlay de confiança no output (cue + tempo restante)
- [x] Show mode (lock UI em live)
- [x] Multi-layers (lista dinâmica até 4, com persistência)
- [x] Editor de fades na linha (pegas arrastáveis no timebar de cada cue)
- [ ] Editor de duração/trim por cue (in/out points)
- [ ] Scheduler por hora do dia (auto-fire a wall-clock)

> **NDI output foi descartado** deste projeto (SDK proprietário + licença de redistribuição).
> Não está planeado.
