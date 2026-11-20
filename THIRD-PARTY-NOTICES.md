# Third-party software

LinearFrame's own source code is MIT licensed (see `LICENSE`). The ready-to-run download additionally ships
these independent programs and libraries in the `engine` folder, each under its own license:

| Component | File | License | Source code |
|---|---|---|---|
| **mpv** (playback engine, built by shinchiro) | `engine/libmpv-2.dll` | GPL-2.0-or-later (this build includes GPL parts) | https://github.com/mpv-player/mpv · build scripts: https://github.com/shinchiro/mpv-winbuild-cmake |
| **FFmpeg** (lossless trimming, thumbnails) | `engine/ffmpeg.exe` | GPL-3.0 (gyan.dev "essentials" build) | https://ffmpeg.org/download.html · build info: https://www.gyan.dev/ffmpeg/builds/ |
| **.NET runtime + WPF** (bundled inside `LinearFrame.exe`) | `LinearFrame.exe` | MIT | https://github.com/dotnet/runtime · https://github.com/dotnet/wpf |
| **TikTok Sans** SemiBold (caption font of the video editor, embedded) | `LinearFrame.exe` | SIL Open Font License 1.1 — full text in `OFL-TikTokSans.txt` | https://github.com/tiktok/TikTokSans |
| **ONNX Runtime** 1.32 (runs the text recognition models; Microsoft) | `engine/onnxruntime.dll` | MIT — `engine/onnxruntime-LICENSE.txt`; its own third-party notices in `engine/onnxruntime-ThirdPartyNotices.txt` | https://onnxruntime.ai · NuGet package Microsoft.ML.OnnxRuntime |
| **PaddleOCR PP-OCRv5** models: text detector (mobile) and Latin-script recogniser (mobile) with its character list, in ONNX form | `engine/ch_PP-OCRv5_det_mobile.onnx`, `engine/latin_PP-OCRv5_rec_mobile.onnx`, `engine/ppocrv5_latin_dict.txt` | Apache-2.0 (PaddlePaddle Authors; ONNX conversion by RapidAI/RapidOCR, Apache-2.0) | https://github.com/PaddlePaddle/PaddleOCR · https://github.com/RapidAI/RapidOCR · license: https://www.apache.org/licenses/LICENSE-2.0 |
| **Microsoft Visual C++ runtime** (needed by onnxruntime.dll; app-local copy so nothing has to be installed) | `engine/vcruntime140.dll`, `engine/vcruntime140_1.dll`, `engine/msvcp140.dll`, `engine/msvcp140_1.dll` | Microsoft Visual C++ Redistributable — distributable code under the Visual Studio license terms | https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist |

Notes

* Because the release download links against GPL-licensed libmpv, the **combined** download is distributed under the
  terms of the GPL. LinearFrame's MIT-licensed source is GPL-compatible and is available in full in this repository,
  so the GPL's source-availability requirement is met. You may still reuse LinearFrame's own code under MIT.
* `ffmpeg.exe` is a separate program that LinearFrame starts as a child process.
* **HD captures** (optional, off by default) use the **Virtual Display Driver** by MikeTheTech and contributors
  (MIT license, https://github.com/VirtualDrivers/Virtual-Display-Driver). It is *not* part of the download: when you
  switch HD captures on, LinearFrame fetches the signed driver package from that project's GitHub release, checks it
  against a pinned SHA-256 and installs it; Settings → HD captures removes it again.
* **Emoji** in the video editor are Apple's emoji artwork, taken from the public `emoji-datasource-apple` package
  (https://github.com/iamcal/emoji-data). They are *not* part of LinearFrame or its download: the first time you open the
  emoji gallery, LinearFrame fetches them from the jsDelivr CDN, checks them against pinned SHA-256 hashes and keeps them in
  `%LOCALAPPDATA%\LinearFrame\emoji` for your own use. The artwork is © Apple Inc.
* The **Polish emoji names and keywords** used by the emoji search (`Assets/emoji-pl.txt.gz`, built into the app) come from the
  Unicode Common Locale Data Repository (CLDR 49, https://cldr.unicode.org), © Unicode, Inc., Unicode License v3
  (https://www.unicode.org/license.txt).
* **Text recognition** combines the OCR engine that is part of Windows (Windows.Media.Ocr) with the PaddleOCR PP-OCRv5 models
  above, run locally by ONNX Runtime, and uses the spelling dictionaries that are part of Windows (ISpellChecker) to restore
  Polish letters; nothing is downloaded or sent anywhere for it.
* The exact versions used for a release are listed in that release's notes.
