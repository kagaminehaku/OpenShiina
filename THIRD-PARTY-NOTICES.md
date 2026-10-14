# Third-party notices

OpenShiina is free software under the GNU General Public License, version 3 or (at your option)
any later version; see [LICENSE](LICENSE). Some of its code is ported from, or follows, works
under the MIT License, whose notices are kept here as that license asks. Those parts are
distributed as part of OpenShiina under the GPL; their own terms still apply to the originals.

## Code ported into OpenShiina (MIT License)

| Work | Copyright | Used in |
|---|---|---|
| [GARbro](https://github.com/morkt/GARbro) | Copyright (c) 2014-2020 morkt | Archive formats and encryption schemes (`src/OpenShiina.Core/Archives`, the scheme data) |
| [WPF](https://github.com/dotnet/wpf) `bezier.cpp` | Copyright (c) .NET Foundation and Contributors | The Bézier flattener of the ellipses (`src/OpenShiina.Core/Platform/DibShapes.cs`) |
| [FidelityFX FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR) `ffx_fsr1.h` | Copyright (c) 2021 Advanced Micro Devices, Inc. | The FSR 1 upscaler (`src/OpenShiina.App/Upscalers.cs`) |
| [Anime4K](https://github.com/bloc97/Anime4K) Upscale CNN x2 (S) | Copyright (c) 2019-2021 bloc97 | The Anime4K upscaler (`src/OpenShiina.App/Upscalers.cs`) |

Each of them is under these terms (with its own copyright line above):

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Libraries the programs ship with

Each under its own license, all of which allow it to be used by GPLv3 software:

| Library | License |
|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT (Skia: BSD-3-Clause) |
| [SDL3](https://github.com/libsdl-org/SDL) through [SDL3-CS](https://github.com/ppy/SDL3-CS) | zlib, MIT |
| [NAudio](https://github.com/naudio/NAudio) | MIT |
| [NLayer](https://github.com/naudio/NLayer) | MIT |
| [NVorbis](https://github.com/NVorbis/NVorbis) | MIT |
| [Iced](https://github.com/icedland/iced) | MIT |
| [Silk.NET](https://github.com/dotnet/Silk.NET) | MIT (shaderc: Apache-2.0) |
| [.NET](https://github.com/dotnet/runtime) | MIT |
