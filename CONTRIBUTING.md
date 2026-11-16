# Contributing to TVScreenPlus

Thanks for your interest! TVScreenPlus is a young project, maintained by one person and tested on only a few PCs and TVs so far, so **every kind of help counts**, not only code. Issues and pull requests can be written in **English or French**.

Join the [Discord](https://discord.gg/xd2wfAApxr) to ask questions or discuss an idea.

## Ways to help

### 1. Test on your hardware (the most useful help right now)

The project is only validated on an NVIDIA RTX 5080 PC and one TCL Google TV. Reports from other hardware are very valuable:

- **PC**: AMD or Intel graphics, laptops with two GPUs, older NVIDIA cards, Windows 10.
- **TV**: other brands and chips (Sony, Philips, Hisense, Chromecast with Google TV, Nvidia Shield, Fire TV…).
- **Gamepads**: any controller connected to the TV.

Even "it works perfectly on my *model*" is useful: open an issue, or say it on Discord.

You can also try the **test versions** before they are released: click *Check for updates* 5 times in a row (within 10 s), on the PC tray menu or in the TV settings.

### 2. Report a bug

1. Check that it is not already in the [Issues](https://github.com/AbelSlawd/TVScreenPlus/issues).
2. On the PC: right-click the 📺 tray icon › *Mode debug (statistiques en direct)* › **Copier pour un signalement**. This copies the version, your configuration (PC settings, hardware, TV model and settings; never the pairing keys), the statistics and the end of the log.
3. [Open an issue](https://github.com/AbelSlawd/TVScreenPlus/issues/new), paste that report, and describe what you did, what you expected and what happened.

**Security problems** must not be reported in public issues: see [SECURITY.md](SECURITY.md).

### 3. Suggest an idea

Open an issue describing the need (what you want to do, on which hardware), not only the solution. Latency, picture quality and simplicity come first, so some ideas may be declined or postponed.

### 4. Translate

- The TV app is available in French, English, Spanish and German. All its texts are in [`L.kt`](android/app/src/main/java/com/tvscreenplus/L.kt), one line per text with the four languages side by side. Fixes to existing translations are welcome.
- The README exists in English and French.

### 5. Contribute code

1. **Open an issue first** for anything bigger than a small fix, so we can agree on the approach before you spend time on it.
2. Fork the repository and create a branch from `main`.
3. Build both apps (see below) and **test on real hardware** whenever you can.
4. Open a pull request against `main`, explaining what changes, why, and **how you tested it** (PC, GPU, TV model). Screenshots or videos help for anything visible.

Every push to `main` publishes a release that installed PCs and TVs update to automatically. The maintainer therefore merges carefully: often through a test version first, and together with the version number and the release notes.

## Building

| App | Requirements | Command |
|---|---|---|
| PC program | Windows, [.NET SDK](https://dotnet.microsoft.com/download) (targets .NET Framework 4.8, included in Windows) | `dotnet build windows/TVScreenPlus -c Release` |
| TV app | JDK 17, Android SDK 35, NDK 27.2.12479018 (Android Studio installs them) | `cd android && ./gradlew assembleRelease -Pdev` |

- `-Pdev` builds `com.tvscreenplus.dev`, which installs next to the released app instead of replacing it. Install it with `adb install -r android/app/build/outputs/apk/release/app-release.apk`.
- The PC program downloads nothing at startup. To run it, put the files of the published zip (`ffmpeg.exe`, `ViGEmBus_setup.exe`, the `driver` folder) next to `TVScreenPlus.exe`.
- `TVScreenPlus.exe --bench-encoder [seconds]` measures the encoder without a TV.

The architecture and the network protocol are described in the README ([How it works](README.md#how-it-works)). [AGENTS.md](AGENTS.md) lists where each feature lives in the code, and the project rules. It is written for AI coding agents, but is just as useful to humans.

## Guidelines

- **Keep compatibility**: a newer PC program must keep working with an older TV app, and the reverse. New features are announced by the TV at connection and accepted by the PC (see the protocol).
- **Mind the latency**: no extra work per frame on the video path.
- **No new dependencies** unless really needed. The TV app must stay small (about 150 KB).
- **Text and docs**: add new TV texts in all four languages of `L.kt` (ask for help if you do not speak them). Update `README.md` and `README.fr.md` together when behaviour changes.
- **Do not change** `VERSION`, the CI workflow or the signing setup in a pull request: the maintainer handles releases.
- Code comments and commit messages are in English. Commit subjects are short and in the imperative.
- AI-assisted contributions are welcome, as long as you have read, built and tested the result yourself.

## Code of conduct

Be kind and patient. Everyone here helps in their free time. Harassment and personal attacks are not tolerated, and the maintainer may remove such content and block its authors.
