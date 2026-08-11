# StagePixPlay

**Video playout software for live stage events** — minimalist, dark, fast.
Cues, playlists (groups), GPU composition with layers (real alpha), instant GO,
and full control via Bitfocus Companion / Stream Deck (OSC).

Windows native (.exe installer — no .NET required). Developed by SmartChoice.

![Status](https://img.shields.io/badge/version-1.0.0-blue)

---

## Quick start

1. Download `StagePixPlay-Setup-1.0.0.exe` from [Releases](../../releases)
2. Install and run — no dependencies
3. Drag videos into the list, press **Space = GO**
4. The output opens fullscreen on your second display / projector

**Documentation completa (PT):** [docs/DOCUMENTATION.md](docs/DOCUMENTATION.md)

## Highlights

- **Instant GO** — next cue preloaded and paused on frame 1
- **Real crossfade** — custom D3D11 GPU compositor (video + audio)
- **Layers with real alpha** — HAP Alpha / WebM overlays, PiP, lower-thirds
- **Playlists (groups)** — expandable, loopable, auto-chained cue groups
- **Stream Deck ready** — OSC control of everything (GO, layers, mutes, volume)
- **Solid engine** — HW decode (D3D11VA), sub-ms frame pacing, deinterlacer
- **Codecs** — MP4/MOV/MKV/WebM, H.264/HEVC/ProRes, **HAP** (the show codec)

## Build

```
dotnet build            # .NET 8 SDK
publish.ps1             # self-contained exe + Inno Setup installer
```

## License

MIT © SmartChoice — see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).
