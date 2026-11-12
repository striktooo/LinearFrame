# Third-party software

LinearFrame's own source code is MIT licensed (see `LICENSE`). The ready-to-run download additionally ships
these independent programs and libraries in the `engine` folder, each under its own license:

| Component | File | License | Source code |
|---|---|---|---|
| **mpv** (playback engine, built by shinchiro) | `engine/libmpv-2.dll` | GPL-2.0-or-later (this build includes GPL parts) | https://github.com/mpv-player/mpv · build scripts: https://github.com/shinchiro/mpv-winbuild-cmake |
| **FFmpeg** (lossless trimming, thumbnails) | `engine/ffmpeg.exe` | GPL-3.0 (gyan.dev "essentials" build) | https://ffmpeg.org/download.html · build info: https://www.gyan.dev/ffmpeg/builds/ |
| **.NET runtime + WPF** (bundled inside `LinearFrame.exe`) | `LinearFrame.exe` | MIT | https://github.com/dotnet/runtime · https://github.com/dotnet/wpf |

Notes

* Because the release download links against GPL-licensed libmpv, the **combined** download is distributed under the
  terms of the GPL. LinearFrame's MIT-licensed source is GPL-compatible and is available in full in this repository,
  so the GPL's source-availability requirement is met. You may still reuse LinearFrame's own code under MIT.
* `ffmpeg.exe` is a separate program that LinearFrame starts as a child process.
* The exact versions used for a release are listed in that release's notes.
