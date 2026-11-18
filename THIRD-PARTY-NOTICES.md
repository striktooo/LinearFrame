# Third-party software

LinearFrame's own source code is MIT licensed (see `LICENSE`). The ready-to-run download additionally ships
these independent programs and libraries in the `engine` folder, each under its own license:

| Component | File | License | Source code |
|---|---|---|---|
| **mpv** (playback engine, built by shinchiro) | `engine/libmpv-2.dll` | GPL-2.0-or-later (this build includes GPL parts) | https://github.com/mpv-player/mpv · build scripts: https://github.com/shinchiro/mpv-winbuild-cmake |
| **FFmpeg** (lossless trimming, thumbnails) | `engine/ffmpeg.exe` | GPL-3.0 (gyan.dev "essentials" build) | https://ffmpeg.org/download.html · build info: https://www.gyan.dev/ffmpeg/builds/ |
| **.NET runtime + WPF** (bundled inside `LinearFrame.exe`) | `LinearFrame.exe` | MIT | https://github.com/dotnet/runtime · https://github.com/dotnet/wpf |
| **TikTok Sans** SemiBold (caption font of the video editor, embedded) | `LinearFrame.exe` | SIL Open Font License 1.1 — full text in `OFL-TikTokSans.txt` | https://github.com/tiktok/TikTokSans |

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
* The exact versions used for a release are listed in that release's notes.
