# Desktop preview dependencies and licenses

PhoneDeck is distributed under the repository MIT license. Runtime packages contain fixed upstream speech source and an optional fixed multilingual model. No user credentials, recordings, personal settings or signing keys are bundled.

| Component | Version / provenance | License |
|---|---|---|
| .NET / ASP.NET Core | SDK fixed by `global.json`; runtime copied by self-contained publish | MIT; upstream third-party notices |
| whisper.cpp / ggml | v1.9.4, commit `927cfce34f31707e17f2bff35c349632fb9e2c3a` | MIT |
| GGML small multilingual Q5_1 model | ggerganov/whisper.cpp revision `5359861c739e955e79d9a303bcbc70fb988958b1` | Whisper model MIT |
| QRCoder | 1.6.0 | MIT |
| Makaretu.Dns / Multicast | 2.0.1 / 0.27.0 | MIT |
| Android ZXing embedded / core | 4.3.0 / transitive pinned by Android build | Apache-2.0 |
| xdotool, optional Linux X11 input | OS package manager | BSD-3-Clause |
| wtype, optional supported Wayland input | OS package manager | MIT |
| SimpleBase | 1.3.1, transitive DNS dependency | Apache-2.0 |
| Common.Logging / Core | 3.4.1, transitive DNS dependency | Apache-2.0 |
| IPNetwork2 | 2.1.2, transitive DNS dependency | BSD-2-Clause |
| Tmds.LibC | 0.2.0, transitive multicast dependency | MIT |
| miniaudio / stb_vorbis | pinned whisper.cpp examples | MIT-0 / MIT |

Model filename: `ggml-small-q5_1.bin`, length 190085487 bytes, SHA-256 `ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb`.

Sources: [whisper.cpp](https://github.com/ggml-org/whisper.cpp/tree/927cfce34f31707e17f2bff35c349632fb9e2c3a), [Whisper license](https://github.com/openai/whisper/blob/main/LICENSE), [model repository](https://huggingface.co/ggerganov/whisper.cpp/tree/5359861c739e955e79d9a303bcbc70fb988958b1), [QRCoder](https://github.com/codebude/QRCoder), [Makaretu DNS](https://github.com/richardschneider/net-dns), [DNS multicast](https://github.com/richardschneider/net-mdns), [xdotool](https://github.com/jordansissel/xdotool), [wtype](https://github.com/atx/wtype).

Native CLI builds disable host-specific tuning and GPU backends, and statically link whisper/ggml. x64 packages include a baseline and a separate AVX2/FMA/F16C/BMI2/SSE4.2 variant; the receiver checks CPU and OS support for the entire feature set before selecting it, otherwise it uses the baseline. ARM64 uses its native CPU backend. Windows also statically links the C++ runtime. macOS Accelerate, Linux libc/libstdc++/OpenSSL/ICU and OS input tools remain system dependencies. macOS includes PhoneDeck's fixed-command native hotkey helper, which owns the application event loop on the OS main thread. Copied files before signing are listed in `build-manifest.json`; the external `PhoneDeck-<version>-<rid>.build.json` records final distributed bytes after Mac code signing. Archive and external manifest SHA-256 values are in `checksums.sha256`.

Distributed packages must retain the license and notice texts in `licenses/`, `LICENSE-PhoneDeck.txt`, `speech-runtime/LICENSE-whisper.cpp.txt` and this document. Preview packaging does not replace a publisher's release signing or macOS notarization.
