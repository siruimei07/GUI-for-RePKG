# Third-party notices

Wallpaper Field includes source from **RePKG 0.4.0**, Copyright (c) 2019
notscuffed, to read Wallpaper Engine PKG archives and perform RePKG's default
TEX conversion. The application also keeps its security-hardened streaming PKG
extractor around that conversion pipeline.

RePKG is licensed under the MIT License. Its complete license is distributed at
`ThirdParty/RePKG/LICENSE.txt`. The dependency and incorporated-code notices
from the upstream RePKG repository are distributed at
`ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt`.

Wallpaper Field uses the same RePKG texture reader, LZ4 decompressor, image
converter, and TEX metadata generator as the upstream command-line program.
The runtime is linked into the desktop application; users do not need to
install or launch a separate RePKG executable.

For security maintenance, the bundled ImageSharp dependency is updated from
RePKG's original 2.1.9 reference to the API-compatible patched 2.1.13 release.

Wallpaper Field also uses **XamlAnimatedGif 2.3.2** by Thomas Levesque to
decode, compose, and schedule animated GIF preview frames in WPF. It is
licensed under the Apache License 2.0. The complete Apache 2.0 terms are
included in `ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt` (under the
`SixLabors.ImageSharp` heading); the same unmodified license terms apply to
XamlAnimatedGif. Project source: https://github.com/XamlAnimatedGif/XamlAnimatedGif
