# Smart Play Cue

**Video playout software for live stage events** — minimalist, dark, fast.
Cues, playlists (groups), GPU composition with layers (real alpha), instant GO,
and full control via Bitfocus Companion / Stream Deck (OSC).

Windows native (.exe — no .NET required). Developed by SmartChoice.

![Status](https://img.shields.io/badge/version-2.1.0-green)

---

## Screenshots

### Main window (media loaded)

![Smart Play Cue - Main Window](screenshots/main-window.png)

### In production (cue playing)

![Smart Play Cue - Playing](screenshots/main-window-playing.png)

### Output / Program display

![Smart Play Cue - Output](screenshots/output-window.png)

---

## Branches

| Branch | Descrição |
|---|---|
| `main` | **SmartCue** — versão master atual (D3D11 + FFmpeg) |
| `legacy/stagepixplay` | StagePixPlay — base .NET original |
| `legacy/smartvideoplayer` | SmartVideoPlayer — primeira tentativa (Python/PySide6) |

## Quick start

1. Download `SmartPlayCue-Setup-2.1.0.exe` (instalador) ou `SmartPlayCue-v2.1.0-win64.zip` (portátil) from [Releases](../../releases)
2. Install and run — no dependencies (self-contained .NET)
3. Drag videos into the list, double-click a cue or press **Space = GO**
4. The output opens fullscreen on your second display / projector

## Highlights

- **Instant GO** — next cue preloaded and paused on frame 1
- **Real crossfade** — custom D3D11 GPU compositor (video + audio)
- **Layers with real alpha** — HAP Alpha / WebM overlays, PiP, lower-thirds
- **Playlists (groups)** — expandable, loopable, auto-chained cue groups
- **Per-cue controls** — play/stop/pause/rewind, mute, fill mode, rotation (0/90/180/270)
- **Cue IDs + color tags** — renumerated by list order, jump-to-cue chains
- **Stream Deck ready** — OSC control of everything (GO, layers, mutes, volume, panic)
- **Companion module included** — presets + HH/MM/SS remaining-time feedback
- **Codecs** — MP4/MOV/MKV/WebM, H.264/HEVC/ProRes, **HAP** (the show codec)

## Companion / OSC

- OSC listen port: **8010**
- OSC feedback port: **8011** (remaining time: `/smartcue/time/hh|mm|ss`)
- Module: `companion-module-smartcue/` (base ~2.3.4)

## Build

```
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish
```

## License

MIT © SmartChoice — see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).
