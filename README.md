# TVScreenPlus

**English** | [Français](README.fr.md)

**Turn your Google TV / Android TV into a wireless secondary display for your Windows PC.**

The TV becomes a real second monitor (*extend* mode): you drag windows onto it and the mouse moves across, as if it were plugged in over HDMI. The picture is encoded by the PC's graphics card and decoded by the TV's video chip, for a sharp, smooth image with very little latency.

[![Download - Windows PC](https://img.shields.io/badge/Download-Windows%20PC-0078D6?style=for-the-badge&logo=windows)](https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-PC.zip)
[![Download - Google TV](https://img.shields.io/badge/Download-Google%20TV-3DDC84?style=for-the-badge&logo=android)](https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-TV.apk)

---

## Table of contents
- [Highlights](#highlights)
- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
- [Automatic quality tuning](#automatic-quality-tuning)
- [Updates](#updates)
- [Troubleshooting](#troubleshooting)
- [Uninstalling](#uninstalling)
- [How it works](#how-it-works)
- [Building from source](#building-from-source)
- [Credits](#credits)

---

## Highlights
- **Plug & play**: the TV finds the PCs on the network by itself and connects automatically.
- **A real extended display**: Windows sees an additional monitor, visible in *Settings › Display*. It only exists while connected.
- **Quality and smoothness**: up to 4K at 60 fps. NVIDIA / AMD / Intel hardware encoding, HEVC or H.264, low-latency hardware decoding on the TV.
- **In-house encoder (new in 1.1)**: TVScreenPlus encodes the picture itself with the graphics card's hardware encoder, without going through ffmpeg, for a steady 60 fps stream. ffmpeg (the 1.0 encoder) remains available as an automatic fallback.
- **Automatic tuning**: the network, the PC and the TV are measured at every connection to pick the best resolution and bitrate. The bitrate keeps adapting if the network weakens.
- **PC sound on the TV**: whatever Windows plays comes out of the TV's speakers, with very little delay and in sync with the picture.
- **TV gamepads = PC gamepads**: Bluetooth or USB gamepads connected to the TV are seen by Windows as Xbox controllers (up to 4), rumble included. PC games recognize them directly.
- **Lightweight**: the PC program is a few hundred KB (excluding ffmpeg) and the TV app is under 150 KB. No runtime to install.
- **Automatic updates** on the PC, offered on the TV.

## Requirements
| | Minimum | Recommended |
|---|---|---|
| **PC** | Windows 10 (2004+) or 11, 64-bit | NVIDIA GTX 900+, AMD RX 400+ or Intel 6th gen+ graphics |
| **TV** | Google TV / Android TV 5.0+ | Chromecast with Google TV, Shield, recent Sony/TCL/Philips TVs |
| **Network** | PC and TV on the same local network | PC on Ethernet and TV on Ethernet or 5 GHz Wi-Fi / Wi-Fi 6 |

> The in-house encoder needs a graphics card with a hardware H.264 / HEVC encoder (reachable through Media Foundation). It is validated on NVIDIA; AMD and Intel still need testing, with ffmpeg as an automatic fallback. Without a compatible graphics card, only ffmpeg can use the processor (x264), with reduced quality and smoothness.

## Installation

### 1. On the Windows PC
1. Download **[TVScreenPlus-PC.zip](https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-PC.zip)** and unzip it anywhere.
2. Run **`TVScreenPlus.exe`** and accept the administrator permission prompt.
   - If Windows SmartScreen shows "Windows protected your PC": click *More info*, then *Run anyway* (the program is not digitally signed).
3. That's it. The program:
   - installs itself in `C:\Program Files\TVScreenPlus` and replaces any previous version;
   - installs the virtual display driver and allows it through the Windows firewall;
   - starts with Windows, with a 📺 icon near the clock;
   - shows up in *Settings › Apps*.

   You can delete the unzipped folder.

### 2. On the TV
The app is not on the Play Store: you need to install the APK file.

**Easy method, with the *Downloader* app** (free on the TV's Play Store):
1. Install *Downloader* from the TV's Play Store.
2. Allow it to install apps: *Settings › Apps › Security & restrictions › Unknown sources › Downloader*.
3. In *Downloader*, enter the address:
   `https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-TV.apk`
4. Install, then open **TVScreenPlus** from the TV's home screen.

*Other methods: the "Send Files to TV" app from the PC, or `adb install TVScreenPlus-TV.apk`.*

## Usage
1. The PC must be on, with TVScreenPlus running (icon near the clock).
2. Open **TVScreenPlus** on the TV: the PCs on the network are listed.
   - If there is only one PC, or if it is the last one used, the connection is **automatic**.
   - Otherwise, pick the PC with the remote.
3. **First connection of a TV**: the TV shows a 6-digit code and the PC opens a *"new TV"* window with the same code. Click **Allow** if both codes match (without an answer the request is refused after 60 s). From then on, this TV connects by itself.
4. The first connection takes a few extra seconds (calibration). The new display then appears in Windows.

**Remote control while streaming**
| Key | Action |
|---|---|
| **OK** or **Menu** | Show / hide the statistics (resolution, bitrate, frames/s, sound, gamepads) |
| **Back** | Disconnect. The display disappears from Windows |
| **▲ / ▼** | **Fluid** mode (steady 60 fps, synced sound) / **game** mode (minimal latency). Applied within a fraction of a second, without disconnecting |
| **◀ / ▶** | Advance / delay the sound by 10 ms (by-ear sync adjustment, remembered) |

**Gamepad** buttons do not control the TV while streaming: they go to the PC (the B button does not disconnect). The remote always keeps its keys.

**PC tray icon menu** (right-click): *Restart the connection*, resolution, quality, **game mode**, **video encoder** (TVScreenPlus / ffmpeg), **sound on the TV**, **mute PC sound while streaming**, **TV gamepads**, start with Windows, updates, recalibration, **forget the allowed TVs**, **debug mode**, log.

### Fluid mode or game mode (PC)
With the **TVScreenPlus encoder** (the default), both modes keep a **steady 60 frames/s** and neither holds back any frame: the roughly 30 ms of pipelining of the old ffmpeg encoder is gone. The difference lies in the sound and in the encoder setting:
- **Fluid mode** (default): the image may be slightly delayed to stay in sync with the sound, and the encoder is tuned for quality. Ideal for the desktop, browsing and video.
- **Game mode**: the image is never delayed to wait for the sound, and the encoder is set to its fastest setting. Useful for gaming.

Press **▼** on the remote while streaming, or right-click the icon › *Game mode (minimal latency)*; **▲** goes back to fluid mode. The change is applied within a fraction of a second, without disconnecting, and the PC menu follows.

With the **ffmpeg fallback**, the old behavior applies: in fluid mode, the encoder keeps one or two frames ahead to hold a steady 60 frames/s, which adds at most about thirty milliseconds; in game mode, each frame is sent as soon as it is encoded, but a few frames may be skipped (about 55 to 58 fps in 4K).

### Video encoder (PC)
Right-click the icon › *Video encoder*: **TVScreenPlus** (in-house encoder, default) or **ffmpeg** (the 1.0 encoder).
- If the in-house encoder fails or is too slow, ffmpeg takes over automatically.
- The in-house encoder is only used when the TV app draws the mouse cursor itself (the current TV app does); otherwise ffmpeg is used.
- Validated on NVIDIA (GeForce RTX 5080, 4K 60 fps). AMD, Intel, other NVIDIA generations and dual-GPU laptops are **not tested yet**: if you run into trouble, choose *ffmpeg* and report it in an [issue](https://github.com/AbelSlawd/TVScreenPlus/issues).

### PC sound on the TV
Enabled by default. While streaming, everything Windows plays (games, videos, music, notifications) is sent to the TV and comes out of its speakers (or its soundbar).
- Sound is captured from the **Windows default audio output**: if you switch outputs (headphones…), the TV follows automatically.
- The PC also keeps playing the sound on its own speakers. To hear it only on the TV: right-click the icon › *Mute PC sound while streaming* (PC sound is restored on disconnect).
- To turn off sound on the TV: right-click › *Sound on the TV*, or in the TV's settings.
- Sound travels on its own channel (UDP, about 1.5 Mb/s, counted in the bitrate tuning): it never waits behind a large frame. The TV keeps a buffer of about 30 ms, automatically enlarged if the network is irregular, then slowly reduced when it becomes stable again (never below 25 ms, 15 ms in game mode, to avoid dropouts). Excess sound is only trimmed if it persists (minimum level over one second): Wi-Fi bursts no longer cause dropouts. In game mode, sound also goes through Android's "game" channel, which some TVs process with fewer effects, and therefore faster.
- The app asks the TV for its "game" mode (low latency, ALLM) when supported: less processing, so less picture and sound delay.
- **Automatic sync**: the TV measures the real sound delay (capture → speakers or soundbar) and the picture delay (capture → screen), and compares them. If sound arrives after the picture, the picture is slightly delayed; if it arrives before, the sound is. Changes are gradual and invisible. In **game mode** (PC), the picture is never delayed to wait for the sound: responsiveness comes first.
- **By-ear adjustment**: while streaming, the **◀ ▶** keys on the remote advance or delay the sound by 10 ms (remembered). *TV settings › Sound sync* resets it to 0. Handy with a lip-sync test video.

### Gamepads
Enabled by default. Connect your gamepads to the TV (*TV settings › Remotes & accessories › Add accessory* for Bluetooth), then connect the TV to the PC: each gamepad appears in Windows as an **Xbox 360 controller** (up to 4). Games and Steam recognize it as a gamepad plugged into the PC.
- Any gamepad the TV recognizes as a game controller works: Xbox, PlayStation (DualShock 4, DualSense), 8BitDo, Stadia, generic Bluetooth "Android" gamepads, USB gamepads…
- Sticks, analog triggers, D-pad, all buttons (the central Xbox / PS button becomes the *Guide* button, often reserved by the TV's system).
- **Rumble**: when a game makes the virtual Xbox controller vibrate, the TV's gamepad vibrates (if it and the TV allow it; better support from Android 12).
- A gamepad connected or disconnected while streaming appears or disappears on the PC right away.
- **First use**: the PC automatically (and silently) installs the **ViGEmBus** driver, which creates the virtual gamepads. It takes a few seconds, once.
- To disable: right-click the icon › *TV gamepads*, or in the TV's settings.

### TV settings (⚙ at the top right, or the Menu key)
| Setting | Purpose |
|---|---|
| **Language** | System language, or Français / English / Español / Deutsch |
| **Picture priority** | *Automatic*, *Smoothness* (cautious bitrate, 1080p max) or *Maximum quality* (higher bitrate) |
| **Maximum resolution** | Automatic, 720p, 1080p, 1440p or 4K |
| **Automatic connection** | Connect by itself to the last PC used |
| **Statistics while streaming** | Show them by default (otherwise: OK key) |
| **Computer sound on the TV** | Play PC sound on the TV (enabled by default) |
| **Sound output** | *Fast* (lowest latency, default; skips the TV's sound effects on TVs where the fast path goes straight to the speakers), *Steady* (same with more margin: no tiny cuts, ~30 ms more) or *TV quality* (through the TV's sound effects such as Dolby, ~200 ms: best in fluid mode, where the image waits for the sound; in game mode the sound lags). Applied at the next connection |
| **TV gamepads used by the computer** | Send the TV's gamepads to the PC (enabled by default). When off, they control the TV as before |
| **TV hardware test** | Measures what the video chip really decodes (see below) |
| **Check for updates** | Checks for and installs the latest version |
| **Forget saved computers** | Clears the last PC and the typed IP |
| **About** | Version, model, Android, IP address, network type (Ethernet / Wi-Fi 2.4-5-6 GHz) |

The app version is shown at the top of the home screen.

### Mouse cursor
The cursor is no longer burned into the video: the PC sends its position (several hundred times per second) and its shape, and the TV draws it itself on top of the picture. It follows the mouse almost instantly, without waiting for video encoding and decoding. With an older TV app, the cursor stays in the video (and ffmpeg then does the encoding).

### Debug mode (PC)
Right-click the icon › *Debug mode*: a window shows live
- **signal stability** (score out of 100, network saturation, latency jitter, frames lost by the TV, bitrate drops);
- the **frames per second** sent by the PC and displayed by the TV;
- the **resolution**, codec, encoder and bitrate;
- **latency**: network round trip measured every second, decoding latency **measured live on the TV** (average and maximum) and an estimate of the total PC → screen latency;
- **TV decoder saturation**: the share of time the video chip can no longer keep up with the stream;
- the **measured capture → screen latency**: each frame carries its capture time, and the TV notes when it is displayed (PC and TV clocks continuously aligned using the pings). The TV's own image processing is not included: set the TV to its "Game" picture mode to reduce it;
- **sound**: captured format, bitrate, TV buffer, dropouts, packets discarded to catch up, jitter;
- the **gamepads** connected to the TV and their virtual Xbox controller.

The same figures are written to the log every 10 seconds (`Stats:` lines), along with the information the TV sends at each connection (`TV:` lines: model, decoder used, hardware test details). A session can thus be analyzed afterwards, without any development tool on the TV.

> Tip: to place the TV to the left or right of your main display, drag it in *Settings › System › Display*.

## Automatic quality tuning
At every connection, within 2 to 5 seconds:
1. **Network**: the latency and the real throughput between the PC and the TV are measured.
2. **TV**: the app reports the screen resolution, its refresh rate (50 or 60 Hz), the network type and the maximum resolution its video chip decodes at 60 fps. After the **hardware test** (TV settings), the **measured** values are sent instead, along with the decoding latency. The test encodes a short clip on the TV and then decodes it in HEVC and H.264, from 4K down to 720p.
3. **PC**: the graphics card's encoder is detected, then **tested in real conditions** (result remembered). The "quality" setting is preferred, and the "fast" setting is used if the PC is not powerful enough.
4. TVScreenPlus picks the **highest resolution** the whole chain supports, with a suitable bitrate.

While streaming, if the network saturates (microwave, another device downloading…), the bitrate **drops automatically** to avoid stutter, then **rises again** when the connection becomes stable. With the in-house encoder, the bitrate changes live, without restarting the encoder.

To force a value: right-click the icon › *Resolution* or *Quality / bitrate*. Even when forced, the bitrate never exceeds a **ceiling learned for each TV and each network type (Ethernet or Wi-Fi)**: it starts from a cautious value (50 Mb/s at most, and 60% of the measured throughput on a slow link such as the 100 Mb/s Ethernet port of many TVs, 50% on Wi-Fi, 85% on a fast wired network), rises by 15% after 30 seconds without any sign of saturation (socket, pings, sound, lost frames), drops as soon as it saturates, and is remembered for later connections. A more capable setup therefore gets a higher ceiling on its own, never exceeding 85% of the measured throughput. *Recalibrate* clears these learned ceilings. If you change graphics card, use *Recalibrate*.

## Updates
- **PC**: the program checks every 6 hours and installs itself when no TV is connected. Manual check: right-click › *Check for updates*.
- **TV**: at launch, the app offers the new version. Android always asks for confirmation.
- **Manual installation**: running a newer `.exe` or `.apk` automatically replaces the old one.

All versions are listed in the [Releases](https://github.com/AbelSlawd/TVScreenPlus/releases).

## Troubleshooting
| Problem | Solution |
|---|---|
| The TV can't find the PC | Check that both are on the **same network** (no "guest" network or Wi-Fi isolation). Otherwise, type the PC's IP address on the TV (`ipconfig` in a Windows terminal). |
| Frozen image, black screen or stuck connection | The TV reconnects by itself if no frame arrives for 6 seconds. Otherwise: right-click the icon › *Restart the connection*. |
| Image problems, black screen or poor smoothness after updating to 1.1 | Try *Video encoder › ffmpeg* in the tray icon menu, then report it in an [issue](https://github.com/AbelSlawd/TVScreenPlus/issues) with the log attached. |
| Choppy image | Prefer Ethernet or 5 GHz Wi-Fi, move the TV closer to the router. The statistics (OK key) show the real bitrate. |
| Blurry image | Right-click the icon › *Quality / bitrate › Maximum*, and check that *Resolution* is set to *Automatic* or *Native*. |
| "Update impossible: different signature" on the TV | Uninstall the app from the TV, then reinstall the APK once. Later updates will work. |
| Nothing is displayed after connecting | Right-click › *Recalibrate*, then reconnect the TV. Update the graphics card driver. |
| No sound on the TV | Check *Sound on the TV* (PC icon) and the TV setting, then the TV volume. The captured sound is that of Windows' **default output**: software playing on another output, or in exclusive mode, is not transmitted. Also update the TV app (older versions have no sound). |
| No sound at all when *Mute PC sound* is on | On some PCs (volume handled by the sound card itself), muting also mutes the capture: turn this option off and lower the PC speakers' volume instead. |
| Choppy or delayed sound | Prefer Ethernet or 5 GHz Wi-Fi. Debug mode (*Sound* lines) shows the TV's buffer and dropouts. A Bluetooth soundbar always adds delay. |
| Gamepad not recognized by the PC | It must first work on the TV (paired in its settings). Debug mode lists the gamepads seen. If the ViGEmBus driver could not be installed, a notification says so: restart TVScreenPlus, or install [ViGEmBus 1.22](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0) by hand. Beyond 4 gamepads, the extra ones stay on the TV. |
| The gamepad controls the TV instead of the PC | Enable *Gamepads* in the TV settings and on the PC icon, and update both apps. |
| No rumble | The gamepad and the TV's Android version must allow remote rumble (Android 12+ recommended). Not all Bluetooth gamepads allow it. |
| Other issue | Right-click › *Open the log* (`C:\ProgramData\TVScreenPlus\log.txt`) and attach it to an [issue](https://github.com/AbelSlawd/TVScreenPlus/issues). It also contains the TV's information (`TV:` lines) and the streaming statistics (`Stats:` lines). |

## Uninstalling
- **PC**: *Settings › Apps › TVScreenPlus › Uninstall*. The virtual display driver, the firewall rule and the automatic startup are also removed, as is the ViGEmBus gamepad driver if it was installed by TVScreenPlus (if it was already there for other software, it is kept).
- **TV**: *Settings › Apps › TVScreenPlus › Uninstall*.

## How it works
```
Windows PC                                                       Google TV
┌───────────────────────────────────────────────┐   local     ┌──────────────────────────────┐
│ Virtual display (IddCx driver)                │   network   │ MediaCodec hardware decoder  │
│   → DXGI Desktop Duplication capture          │  ───────▶   │   (low-latency mode)         │
│   → GPU conversion BGRA → NV12                │   TCP       │   → direct display (60 Hz)   │
│   → hardware encoder via Media Foundation     │             └──────────────────────────────┘
│     (NVENC / AMF / Quick Sync)                │
└───────────────────────────────────────────────┘
        (fallback: ffmpeg ddagrab + NVENC / AMF / Quick Sync)
```
| Stage | Technique |
|---|---|
| Virtual display | [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (IddCx, MIT), enabled only while connected |
| Capture | DXGI Desktop Duplication, image staying in GPU memory, then BGRA → NV12 conversion on the GPU (D3D11 video processor). The cursor is not burned in: the TV draws it. A new image is encoded as soon as it is captured; on a still desktop, the last image is re-encoded so the TV always gets a steady 60 fps |
| Encoding | TVScreenPlus encoder: Windows Media Foundation hardware encoder (NVENC, AMF or Quick Sync behind a single API), low-latency mode, no B-frames, CBR with a short buffer (1/6 s), bitrate changed live without restarting, keyframe forced when the TV reports dropped frames. HEVC if the TV decodes it, otherwise H.264. **Fallback**: FFmpeg (`ddagrab` + NVENC, AMF or Quick Sync in zero-copy, x264 fallback), chosen automatically if the in-house encoder fails or is too slow, or manually |
| Timestamps | Capture times use a precise clock (QPC, anchored on the system time) |
| Transport | TCP without Nagle's algorithm, one frame = one packet, buffers sized to about 40 ms |
| Control | Throughput and latency measured at connection, adaptive bitrate based on network saturation, statistics reported by the TV |
| TV decoding | Hardware MediaCodec in *low-latency* mode (+ vendor options), immediate rendering, Wi-Fi in low-latency mode, display synced to the TV's refresh rate |
| Sound | WASAPI *loopback* capture of Windows' default output, converted to 16-bit stereo PCM (surround channels downmixed, 96/192 kHz brought down to 48/44.1 kHz) and sent uncompressed in 10 to 20 ms packets in the same connection as the picture. On the TV: AudioTrack in *low-latency* mode (Android 8+), adaptive buffer (about 30 ms at start), late sound is discarded rather than played late |
| Gamepads | The TV sends the state of each gamepad (buttons, sticks, triggers, D-pad) at every change. The PC creates one virtual Xbox 360 controller per gamepad with the [ViGEmBus](https://github.com/nefarius/ViGEmBus) driver, driven directly (no DLL). Rumble requested by the game is sent back to the TV |

**Network ports**: UDP 47801 (discovery), TCP 47800 (stream). The firewall rule is added automatically, limited to these two ports and the local network.

**Security**
- Only TVs accepted on the PC receive its screen. Pairing uses an ECDH P-256 key exchange: the TV and the PC show the same 6-digit code, which a device in the middle cannot reproduce. On every connection the TV then proves it holds the key (HMAC-SHA256 of a random challenge). *Forget the allowed TVs* (PC) or *Forget saved computers* (TV) cancels the pairings.
- The PC only answers local network addresses (private or link-local). A new TV never interrupts the current stream before it is authenticated.
- The video stream is not encrypted (TV performance, latency): pairing keeps other devices from connecting, not a local network eavesdropper.
- Updates only come from the project's GitHub releases; the TV APK must also be signed with the same key. On the PC nothing is downloaded and run outside updates: ffmpeg, the display driver and the gamepad driver are in the zip. `C:\ProgramData\TVScreenPlus` can only be modified by administrators, and `settings.txt` (pairing keys) can only be read by them.

<details>
<summary>Protocol</summary>

- **Discovery**: the TV broadcasts `TVSP?` over UDP and the PC answers `TVSP!\nname=…\nbusy=0|1\nver=…`.
- **Connection**: the TV sends `TVSP1;v=2;name=…;w=…;h=…;hz=…;hevc=<max height>;avc=<max height>;…`.
- **Stream**: packets `[u32 size][u8 flags][data]`. Flags: `0x80` padding (throughput test), `0x40` ping (the TV answers `ACK`), `0x20` configuration, `0x10` status message, otherwise an Annex-B video frame (`1` = keyframe).
- **TV → PC feedback**: `STAT;fps=…;kbps=…;drop=…;dec=…;decmax=…;wait=…` every 2 seconds (`dec`/`decmax`: decoder latency in ms, `wait`: % of time its input is full), `LOG;…` (diagnostics written to the PC log), and `ACK` in reply to the pings sent every second to measure latency.
- **Additional connection fields**: `lang`, `tested`, `lat` (decoding latency in ms), `net`/`band`/`link` (network), `pref` (priority), `maxh` (max resolution), `audio=0|1` (the TV plays sound), `pads=0|1` (the TV sends its gamepads).
- **Optional features**: the PC only uses them if the TV announces them in its connection line, and confirms what it accepts in its configuration packet (`…;audio=0|1;pads=0|1`). The TV also gives `audioudp=<port>`: the PC then sends it the sound as 5 ms UDP datagrams (same content as the 0x08 packets), otherwise in the TCP stream. `avsync=0` (game mode) forbids delaying the picture; `mode=fluid|game` gives the active mode, and the TV requests a change with the line `MODE;fluid|game` (▲ ▼ keys). `ts=1`: each video frame carries the `0x04` flag and starts with its capture time (u32, ms since 1970 modulo 2³¹); pings carry the PC's time; the TV sends back `e2e`/`e2emax` (capture → screen) in `STAT`. `cursor=1`: the TV draws the cursor; the PC sends its position (`[3][visible][u16 x][u16 y][u32 time]`, over UDP) and its shape (`[4][u16 w][u16 h][u16 hotspot x][u16 y][BGRA]`, 0x08 packet). An older TV app or an older PC program therefore keeps working (without sound or gamepads).
- **`0x08` packets (extension)**: the first byte gives the type. An older TV never receives them (they would be taken for video).
  - `1` = sound: `[1][format 1 = 16-bit LE PCM][channels][0][frequency u32][PC timestamp in ms u32][samples]`, 10 to 20 ms per packet.
  - `2` = rumble: `[2][slot 0-3][large motor 0-255][small motor 0-255]`.
- **Gamepads, TV → PC**: `PADON;<slot>;<name>` (gamepad appeared), `PADOFF;<slot>`, and at every change `PAD;<slot>;<buttons hex, XInput bits>;<left trigger 0-255>;<right trigger>;<left stick x>;<left stick y>;<right stick x>;<right stick y>` (sticks from -32768 to 32767, y up).
- **Sound statistics**: `STAT;…;alat=<TV buffer in ms>;aund=<dropouts>;adrop=<discarded packets>;ajit=<jitter in ms>;aout=<write → speaker delay in ms>;abuf=<target buffer>;aext=<delay added to sound>;vdel=<delay added to picture>;avoff=<user setting>`.
- **Synchronization**: the PC's configuration packet gives `vpc` (picture delay relative to sound at the PC's output, in ms) and `apc` (sound capture delay).
</details>

## Building from source
```bash
# PC program (.NET Framework 4.8, included in Windows)
dotnet build windows/TVScreenPlus -c Release

# TV app
cd android && ./gradlew assembleRelease
```
The program downloads nothing at startup: put the files of the published zip (`ffmpeg.exe`, `ViGEmBus_setup.exe`, `driver` folder) next to `TVScreenPlus.exe`, as the CI does (`.github/workflows/build.yml`).

**Local benchmark**: `TVScreenPlus.exe --bench-encoder [seconds] [adapter/output] [kbps]` measures the in-house encoder pipeline without a TV (fps held, capture → encoded packet latency).

**Continuous integration** (`.github/workflows/build.yml`): every push, on any branch, builds both apps (adding ffmpeg, the display driver and the ViGEmBus installer, signature verified). Only the `main` branch publishes a GitHub Release (marked "latest"), which PCs install automatically and TVs offer.
- The version shown to users comes from the `VERSION` file (for example 1.1.2); the Release text comes from `RELEASE_NOTES.md`.
- The tag is `v<major>.<minor>.<CI run number>` (for example v1.1.30), because installed apps compare it (the TV reads its last number as its update code).
- So that APK updates install over the previous ones, CI signs the APK with a fixed key provided by two repository secrets: `ANDROID_KEYSTORE_BASE64` (base64 keystore, alias `tvsp`) and `ANDROID_KEYSTORE_PASSWORD`.

## Credits
- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT license): Windows virtual display.
- [FFmpeg](https://ffmpeg.org/), [BtbN](https://github.com/BtbN/FFmpeg-Builds) build (GPL license): fallback encoder, still bundled.
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) 1.22 (BSD 3-clause license, © 2016-2020 Nefarius Software Solutions e.U.): virtual Xbox gamepads. The signed installer is included as-is in `TVScreenPlus-PC.zip`, with its license (`ViGEmBus_LICENSE.txt`). The requests sent to the driver follow those of [ViGEmClient](https://github.com/nefarius/ViGEmClient) (MIT license). The ViGEm project is no longer developed, but the driver remains signed and works on Windows 10 and 11.
