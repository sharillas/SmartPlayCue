# Third-Party Notices

StagePixPlay uses the following open-source components:

| Component | License | Usage |
|---|---|---|
| [FFmpeg](https://ffmpeg.org/) (v8.0, patched build via Flyleaf releases) | LGPL v3 | Video/audio decode (dynamic linking, separate DLLs) |
| [Flyleaf.FFmpeg.Bindings](https://github.com/SuRGeoNix/Flyleaf.FFmpeg.Generator) | LGPL v3 | .NET bindings for FFmpeg |
| [NAudio](https://github.com/naudio/NAudio) | MIT | Audio output (WASAPI) |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) | MIT | Direct3D11 / DXGI bindings |
| [Rug.Osc](https://bitbucket.org/rugcode/rug-osc) | MIT-style | OSC protocol (Companion/Stream Deck) |
| [SharpVectors](https://github.com/ELC/SharpVectors) | BSD-3-Clause | SVG rendering (logo) |

FFmpeg is used as dynamically linked, unmodified separate libraries
(`thirdparty/FFmpeg/*.dll`). Source and build details are available via the
links above, complying with LGPL dynamic-linking terms.

StagePixPlay itself is licensed under the MIT License (see `LICENSE`).
