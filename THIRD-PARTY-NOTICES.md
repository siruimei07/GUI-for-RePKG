# Third-party notices

Wallpaper Field includes source from **RePKG 0.4.0**, Copyright (c) 2019
notscuffed, to read Wallpaper Engine PKG archives and perform RePKG's default
TEX conversion. The application also keeps its security-hardened streaming PKG
extractor around that conversion pipeline.

RePKG is licensed under the MIT License. Its complete license is distributed at
`ThirdParty/RePKG/LICENSE.txt`. The dependency and incorporated-code notices
from the upstream RePKG repository are distributed at
`ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt`.
Wallpaper Field's bounded-reader, decode-budget, ownership, C-string, and RG88
changes relative to RePKG 0.4.0 are listed in
`ThirdParty/RePKG/UPSTREAM-PATCHES.md`.

Wallpaper Field uses RePKG's texture reader, LZ4 decompressor, and TEX metadata
generator with the local safety patches listed above. PNG and GIF encoding
uses Windows' built-in WPF/Windows Imaging Component (WIC) APIs. The runtime is
linked into the desktop application; users do not need to install or launch a
separate RePKG executable.

In v1.2.2, the build compiles only the RePKG.Application reader/converter
roles used by that runtime. The unused eager package reader/writer and TEX
writer/compressor roles are excluded from the assembly. The full RePKG 0.4.0
source snapshot and RePKG.Core source surface remain in the source repository;
the license, incorporated-code notices, and Wallpaper Field patch record remain
in the release bundle without reduction.

ImageSharp is no longer a runtime or build dependency. Its older dependency
notice remains in the unmodified upstream RePKG notice file as a historical
attribution. The Windows encoder replacement does not require a Six Labors
license key or package.

Wallpaper Field also uses **XamlAnimatedGif 2.3.2** by Thomas Levesque to
decode, compose, and schedule animated GIF preview frames in WPF. It is
licensed under the Apache License 2.0. The complete Apache 2.0 terms are
included in `ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt` (under the
`SixLabors.ImageSharp` heading); the same unmodified license terms apply to
XamlAnimatedGif. Project source: https://github.com/XamlAnimatedGif/XamlAnimatedGif
