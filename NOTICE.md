# Third-Party Notices

Smart Play Cue uses the following open-source components:

| Component | License | Usage |
|---|---|---|
| [FFmpeg](https://ffmpeg.org/) (v8.0, patched build via Flyleaf releases) | LGPL v3 | Video/audio decode (dynamic linking, separate DLLs) |
| [Flyleaf.FFmpeg.Bindings](https://github.com/SuRGeoNix/Flyleaf.FFmpeg.Generator) | LGPL v3 | .NET bindings for FFmpeg |
| [NAudio](https://github.com/naudio/NAudio) | MIT | Audio output (WASAPI) |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) | MIT | Direct3D11 / DXGI bindings |
| [SharpVectors](https://github.com/ELC/SharpVectors) | BSD-3-Clause | SVG rendering (logo) |
| [xUnit](https://xunit.net/) | Apache 2.0 | Unit tests (dev only) |

FFmpeg is used as dynamically linked, unmodified separate libraries
(`thirdparty/FFmpeg/*.dll`). Source and build details are available via the
links above.

Smart Play Cue itself is **proprietary** — see [LICENSE](LICENSE).
Copyright © 2026 Nelson Teixeira, SmartChoice. All Rights Reserved.
