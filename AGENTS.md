# AGENTS.md

Guidance for AI coding agents (Claude Code, Codex, Copilot, Cursor, Gemini…) working on TVScreenPlus.
Human contributors: see [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: see [SECURITY.md](SECURITY.md).

## What this project is

TVScreenPlus turns a Google TV / Android TV into a wireless extended display for a Windows PC. Two apps talk over the local network:

| Part | Folder | Stack |
|---|---|---|
| PC program (tray app, server, encoder) | `windows/TVScreenPlus/` | C# on .NET Framework 4.8 (SDK-style project, `LangVersion latest`), WinForms, P/Invoke (DXGI, Media Foundation, CNG, SendInput, UI Automation) |
| TV app (client, decoder, gamepads) | `android/app/src/main/java/com/tvscreenplus/` | Kotlin, no AndroidX, minSdk 21; one small C file for AAudio (`android/app/src/main/jni/`) |

`README.md` ("How it works" and the "Protocol" section) describes the architecture and the wire protocol. The protocol is also documented at the top of `windows/TVScreenPlus/Server.cs`.

## Build

```bash
# PC (needs the .NET SDK; the net48 reference assemblies come from NuGet)
dotnet build windows/TVScreenPlus -c Release

# TV (needs JDK 17, Android SDK 35 and NDK 27.2.12479018)
cd android && ./gradlew assembleRelease          # release build, debug key unless CI secrets are set
cd android && ./gradlew assembleRelease -Pdev     # test build installed next to the released app (com.tvscreenplus.dev)
```

On Windows, if Gradle says "SDK location not found", set `ANDROID_HOME` (usually `%LOCALAPPDATA%\Android\Sdk`).

There is no automated test suite. Each change must at least **compile both apps**; behaviour is checked on real hardware (a PC with a GPU encoder and a TV). Say clearly what you could not test.

## Rules

- **Never push to `main`.** Every push to `main` publishes a public release that installed PCs and TVs update to automatically. The `test` branch publishes pre-releases. Work on a feature branch and open a pull request.
- **Do not bump `VERSION`** or edit the release process (`.github/workflows/build.yml`, signing) unless asked: the maintainer does releases. New user-visible changes can be added at the top of `RELEASE_NOTES.md` (written in French).
- **Keep the protocol backward compatible.** An older TV app must keep working with a newer PC program and the reverse. New features are announced by the TV in its hello line (`key=1`) and confirmed by the PC in its config packet; anything the other side did not announce is never sent.
- **Security first.** Do not weaken pairing (ECDH + HMAC challenge), the AES-GCM channel, the local-network-only checks, the update checks (GitHub releases only, same APK signing key) or the `C:\ProgramData\TVScreenPlus` permissions. Remote control commands are only accepted on an encrypted, authenticated connection. Never log or copy pairing keys (`settings.txt`).
- **No new dependencies** without a strong reason. The PC program uses only the .NET Framework and Windows APIs; the TV app uses only the Android SDK. The TV APK stays **under 150 KB** (the README promises it): check its size after a change.
- **Latency matters.** The video path (capture → encode → network → decode → display) runs at 60 fps with a few ms of budget per stage. Do not add work, allocations, locks or logging per frame on that path; heavy work goes on its own thread.
- **Never commit secrets**: keystores, passwords, tokens, `local.properties`, `settings.txt`.

## Conventions

- Match the style of the surrounding code: short methods, comments that explain *why*, in English.
- **User-facing text**:
  - TV app: every string lives in `L.kt` in four languages (`fr`, `en`, `es`, `de`). Add all four.
  - PC program: the UI is in French.
- **Docs**: a user-visible change updates both `README.md` (English) and `README.fr.md` (French), kept in sync.
- **Commits**: short imperative English subject, for example `TV: fix the keyboard bar position`. Docs-only commits add `[skip ci]`.
- Line endings are normalised by git; do not mass-reformat files.

## Where things are

| Topic | PC (`windows/TVScreenPlus/`) | TV (`android/.../tvscreenplus/`) |
|---|---|---|
| Connection, protocol, sessions | `Server.cs` | `StreamClient.kt` |
| Pairing | `Pairing.cs`, `PairDialog.cs` | `Pairing.kt` |
| Encryption | `SecureChannel.cs` | `SecureChannel.kt` |
| Video capture / encoding | `NativeEncoder.cs`, `Streamer.cs` (ffmpeg fallback), `EncoderBench.cs` | `Codecs.kt`, `HardwareTest.kt` (what the TV can decode) |
| Sound | `Audio.cs` | `AudioPlayer.kt`, `AAudioOut.kt`, `jni/aaudio_out.c` |
| Gamepads | `Gamepads.cs` (ViGEmBus) | `Gamepads.kt`, `PadView.kt`, `Switch2.kt` |
| Remote mouse / keyboard / windows / apps | `RemoteControl.cs` | `MainActivity.kt` (quick menu) |
| Cursor | `Cursor.cs` | `MainActivity.kt` |
| Virtual display | `VirtualDisplay.cs` | — |
| Tray menu, settings, install | `Program.cs`, `Theme.cs` | `MainActivity.kt` (settings screen) |
| Updates | `Updater.cs` | `Updater.kt` |
| Diagnostics | `DebugWindow.cs` (debug window, "Copier pour un signalement") | `DisplayCaps.kt` |
