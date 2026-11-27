# TVScreenPlus

**English** | [Français](README.fr.md)

**Turn your Google TV / Android TV into a wireless secondary display for your Windows PC.**

The TV becomes a real second monitor (*extend* mode): you drag windows onto it and the mouse moves across, as if it were plugged in over HDMI. The picture is encoded by the PC's graphics card and decoded by the TV's video chip, for a sharp, smooth image with very little latency.

[![Install - Windows PC](https://img.shields.io/badge/Install-Windows%20PC-0078D6?style=for-the-badge&logo=windows)](#1-on-the-windows-pc)
[![Download - Google TV](https://img.shields.io/badge/Download-Google%20TV-3DDC84?style=for-the-badge&logo=android)](#2-on-the-tv)
[![Discord](https://img.shields.io/badge/Discord-Join-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.gg/xd2wfAApxr)

> [!IMPORTANT]
> **🧪 Still in testing**
>
> TVScreenPlus is under active development and has only been tried on a few PCs and TVs so far.
>
> **Found a bug, a crash, a TV or a gamepad that does not work? Please report it in the [Issues](https://github.com/AbelSlawd/TVScreenPlus/issues) section**, with:
> - your PC and TV models;
> - if possible, the log (right-click the 📺 tray icon › *Open the log*).
>
> Every report helps improve the project.
>
> [![Report a problem](https://img.shields.io/badge/Report%20a%20problem-Issues-D73A49?style=for-the-badge&logo=github)](https://github.com/AbelSlawd/TVScreenPlus/issues/new)

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
- [Contributing](#contributing)
- [Credits](#credits)
- [License](#license)

---

## Highlights
- **Plug & play**: the TV finds the PCs on the network by itself and connects automatically.
- **A real extended display**: Windows sees an additional monitor, visible in *Settings › Display*. It only exists while connected.
- **Quality and smoothness**: up to 4K at 60 fps. NVIDIA / AMD / Intel hardware encoding, HEVC or H.264, low-latency hardware decoding on the TV.
- **In-house encoder (new in 1.1)**: TVScreenPlus encodes the picture itself with the graphics card's hardware encoder, without going through ffmpeg, for a steady 60 fps stream. ffmpeg (the 1.0 encoder) remains available as an automatic fallback.
- **Automatic tuning**: the network, the PC and the TV are measured at every connection to pick the best resolution and bitrate. The bitrate keeps adapting if the network weakens.
- **PC sound on the TV**: whatever Windows plays comes out of the TV's speakers, with very little delay and in sync with the picture.
- **TV gamepads = PC gamepads**: Bluetooth or USB gamepads connected to the TV are seen by Windows as Xbox controllers (up to 4), rumble included. PC games recognize them directly.
- **Lightweight**: the PC program is a few hundred KB (excluding ffmpeg) and the TV app about 220 KB. No runtime to install.
- **Automatic updates** on the PC, offered on the TV.
- **Four languages**: French, English, Spanish and German, on the TV and on the PC. The PC program follows the Windows display language (English if it is another language); the TV app follows the TV's language, or the one chosen in its settings.

## Requirements
| | Minimum | Recommended |
|---|---|---|
| **PC** | Windows 10 (2004+) or 11, 64-bit | NVIDIA GTX 900+, AMD RX 400+ or Intel 6th gen+ graphics |
| **TV** | Google TV / Android TV 5.0+ | Chromecast with Google TV, Shield, recent Sony/TCL/Philips TVs |
| **Network** | PC and TV on the same local network | PC on Ethernet and TV on Ethernet or 5 GHz Wi-Fi / Wi-Fi 6 |

> The in-house encoder needs a graphics card with a hardware H.264 / HEVC encoder (reachable through Media Foundation). It is validated on NVIDIA; AMD and Intel still need testing, with ffmpeg as an automatic fallback. Without a compatible graphics card, only ffmpeg can use the processor (x264), with reduced quality and smoothness.

## Installation

### 1. On the Windows PC
1. Open **PowerShell**: right-click the Start button › *Terminal* (or *Windows PowerShell*).
2. Paste this command, then press Enter:
   ```powershell
   cd $env:TEMP; curl.exe -fL -o TVScreenPlus-PC.zip https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-PC.zip; tar -xf TVScreenPlus-PC.zip; .\TVScreenPlus-PC\TVScreenPlus.exe
   ```
   It downloads the latest version from the project's GitHub releases, unzips it in the Windows temporary folder and runs the program. Downloaded this way, the file does not trigger the Windows SmartScreen warning.
3. Accept the administrator permission prompt. It says "Publisher: unknown", because the program is not digitally signed.
4. That's it. The program:
   - installs itself in `C:\Program Files\TVScreenPlus` and replaces any previous version;
   - installs the virtual display driver and allows it through the Windows firewall;
   - starts with Windows, with a 📺 icon near the clock;
   - shows up in *Settings › Apps*.

<details>
<summary><b>Other method: download the zip by hand</b></summary>

1. Download **[TVScreenPlus-PC.zip](https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-PC.zip)** and unzip it anywhere.
2. Run **`TVScreenPlus.exe`** and accept the administrator permission prompt.
   - If Windows SmartScreen shows "Windows protected your PC": click *More info*, then *Run anyway*. To avoid it, right-click the zip › *Properties* › tick *Unblock* before unzipping it.
3. The program installs itself as with the command. You can then delete the unzipped folder.

</details>

### 2. On the TV
The app is not on the Play Store: you need to install the APK file.

**Easy method, with the *Downloader* app** (free on the TV's Play Store):
1. Install *Downloader* from the TV's Play Store.
2. Allow it to install apps: *Settings › Apps › Security & restrictions › Unknown sources › Downloader*.
3. In *Downloader*, enter the code **`2652668`** and press *Go*.
   *If the code doesn't work, enter the full address instead:* `https://github.com/AbelSlawd/TVScreenPlus/releases/latest/download/TVScreenPlus-TV.apk`
4. Install, then open **TVScreenPlus** from the TV's home screen.

*Other methods: the "Send Files to TV" app from the PC, or `adb install TVScreenPlus-TV.apk`.*

## Usage
1. The PC must be on, with TVScreenPlus running (icon near the clock).
2. Open **TVScreenPlus** on the TV: the PCs on the network are listed.
   - If there is only one PC, or if it is the last one used, the connection is **automatic**.
   - Otherwise, pick the PC with the remote.
3. **First connection of a TV**: the TV and the PC show the same 6-digit code. Click **Allow** on the PC, then **Pair** on the TV, if both codes match (without an answer the request is refused). From then on, this TV connects by itself.
4. The first connection takes a few extra seconds (calibration). The new display then appears in Windows.

**Remote control while streaming**
| Key | Action |
|---|---|
| **OK**, **hold OK** (or **Menu**) | Quick menu over the picture: PC mouse, keyboard, windows and applications, fluid / game mode, sound sync (◀ ▶, OK = 0), gamepads (who is connected, reconnect a Switch 2 controller), statistics (resolution, bitrate, frames/s, sound, gamepads, encryption), disconnect |
| **Back** | Disconnect (except in mouse mode, where it does nothing). The display disappears from Windows |
| **▲ / ▼** | **Fluid** mode (steady 60 fps, synced sound) / **game** mode (minimal latency). Applied within a fraction of a second, without disconnecting |
| **◀ / ▶** | Advance / delay the sound by 10 ms (by-ear sync adjustment, remembered) |

**Gamepad** buttons do not control the TV while streaming: they go to the PC (the B button does not disconnect). The remote always keeps its keys.

**PC tray icon menu** (right-click): *Restart the connection*, resolution, quality, **game mode**, **video encoder** (TVScreenPlus / ffmpeg), **sound on the TV**, **mute PC sound while streaming**, **TV gamepads**, **control of the PC from the TV**, **picture and sound encryption**, start with Windows, updates, recalibration, **forget the allowed TVs**, **debug mode**, log.

### Driving the PC with the remote
From the quick menu (**OK**), with TVScreenPlus PC 1.2.4 or newer:
- **Remote = mouse** (on as soon as the TV connects): the arrows move the PC's cursor, slowly at first, then faster and faster. **OK** = click, **OK OK** (two quick presses) = right click, **hold OK** = quick menu (drag, leave mouse mode…). **Back** does nothing in mouse mode, so it is never left by mistake. Channel + / − (or Page up / down) scroll.
- **Keyboard**: what you type with the TV keyboard (or dictate with the remote's microphone) is typed on the PC at once. It opens **by itself** when the mouse clicks into a text field, and stays in the menu. Enter, Delete, Esc, Tab, Windows and *Close the window* (Alt + F4) keys.
- **PC windows**: the open windows, by screen. **OK** brings the window to the TV, full screen, and to the front; **◀ / ▶** put it on the left / right half of the TV (two windows side by side); **hold OK** sends it back to the PC's main screen.
- **PC applications**: every application of the Start menu (programs, Store apps, Steam games) and Steam Big Picture, with their icons. **OK** launches it, **hold OK** adds it to the favorites (shown first) or to the TV home screen (below). A search field filters the list. Applications start without administrator rights, as from the Start menu.

- **Shortcuts on the TV home screen** (new in 1.2.8): PC applications › **hold OK** on a game › *Add to the TV's home screen*. The TV asks to install a small app with the game's name and icon, found in the TV's apps. Opening it starts TVScreenPlus, connects to that PC (waking it if it can), makes the TV the PC's main screen for the session (games in exclusive full screen open there) and launches the game on the TV. When TVScreenPlus closes, the PC closes the game like Alt+F4 after the delay set in the TV settings (2 min by default; coming back in time keeps it; launchers such as Steam stay open). Remove a shortcut like any TV app.

These commands only travel over an encrypted connection. PC tray menu › *Control of the PC from the TV* turns them off (immediately).

**Copy for a report**: the debug mode window (PC) has a button that copies the version, the configuration (PC settings, hardware, TV model and settings; never the pairing keys), the statistics and the end of the log, ready to paste in an [issue](https://github.com/AbelSlawd/TVScreenPlus/issues).

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

### Wake the PC remotely
A PC the TV has seen before that no longer answers (asleep or off) shows up dimmed in the list after a few seconds: **OK** sends a wake-up packet (Wake-on-LAN), then the TV connects as soon as the PC answers. The PC must allow wake-on-LAN (BIOS and network card, preferably Ethernet). Available with TVScreenPlus PC 1.2.2 or later, which gives the TV its network card address.

### Gamepads
Enabled by default. Connect your gamepads to the TV (*TV settings › Remotes & accessories › Add accessory* for Bluetooth), then connect the TV to the PC: each gamepad appears in Windows as an **Xbox 360 controller** (up to 4). Games and Steam recognize it as a gamepad plugged into the PC.
- Any gamepad the TV recognizes as a game controller works: Xbox (360, One, Series), PlayStation (DualShock 3, DualShock 4, DualSense), Nintendo Switch (Pro Controller, Joy-Con), 8BitDo, Stadia, generic Bluetooth "Android" gamepads, USB gamepads…
- **Gamepads page**: connected gamepads show small at the bottom left of the TV app's home screen (also: the *Gamepads* button at the top of the home screen, or *Settings › Gamepads*). An opened gamepad is drawn live (buttons, sticks, triggers) with its settings: player number, A B X Y buttons Xbox-style or Nintendo-style, rumble strength, stick dead zone, split / pair Joy-Cons, *Test the buttons*, *Rumble to find it*, disconnect (Switch 2) or the TV's Bluetooth settings.
- **Nintendo buttons**: placed as on an Xbox controller (the bottom button is *A*), so PC games get them right; or as on a Switch (*A B X Y buttons* in the Gamepads page).
- **Joy-Cons**: a left and a right one make one controller; a single Joy-Con is held sideways (SL / SR = shoulder buttons; for Switch 1 Joy-Cons, whose SL / SR some TVs don't report, L / ZL or R / ZR). To play with two, *Split the two Joy-Cons* on the Gamepads page (and *Pair with the other Joy-Con* to put them back).
- **Switch 2 controllers** (Joy-Con 2, Pro Controller 2, Switch 2 GameCube controller): they use Nintendo's own Bluetooth protocol, which Android doesn't handle by itself. Turn on *Switch 2 controllers* in the Gamepads page (the TV asks for access to nearby devices), then press the small **SYNC** button on the controller: it connects within a few seconds. Needed every time, as these controllers only reconnect by themselves to their console. During a stream: quick menu › *Gamepads* › *Reconnect a Switch 2 controller*, then SYNC.
- The *HOME* button of the Switch 1 Pro Controller takes the TV to its home screen (Android reserves it): use the *Capture* button instead, which acts as *Guide*.
- Sticks, analog triggers, D-pad, all buttons (the central Xbox / PS button becomes the *Guide* button, often reserved by the TV's system).
- **Rumble**: when a game makes the virtual Xbox controller vibrate, the TV's gamepad vibrates (if it and the TV allow it; better support from Android 12). Switch 1 Nintendo controllers (Pro Controller, Joy-Con) only vibrate if the TV's system includes Nintendo rumble (not on the TCL tested): the Gamepads page says so. Switch 2 controllers always vibrate.
- A gamepad connected or disconnected while streaming appears or disappears on the PC right away.
- **First use**: the PC automatically (and silently) installs the **ViGEmBus** driver, which creates the virtual gamepads. It takes a few seconds, once.
- To disable: right-click the icon › *TV gamepads*, or in the TV's settings.

### TV settings (⚙ at the top right, or the Menu key)
| Setting | Purpose |
|---|---|
| **Language** | System language, or Français / English / Español / Deutsch |
| **Picture priority** | *Automatic*, *Smoothness* (cautious bitrate, 1080p max) or *Maximum quality* (higher bitrate) |
| **Maximum resolution** | Automatic, 720p, 1080p, 1440p or 4K |
| **Picture size** | 100 % to 90 %: reduce it if the TV cuts the edges of the picture (taskbar not visible) |
| **Automatic connection** | Connect by itself to the last PC used |
| **Statistics while streaming** | Show them by default (otherwise: OK key) |
| **Close a shortcut's game after disconnecting** | At once (15 s), after 2 (default), 5 or 15 min, or never |
| **Computer sound on the TV** | Play PC sound on the TV (enabled by default) |
| **Sound output** | *Fast* (lowest latency; skips the TV's sound effects on TVs where the fast path goes straight to the speakers), *Steady* (same with more margin: no tiny cuts, ~30 ms more) or *TV quality* (default: through the TV's sound effects such as Dolby, ~200 ms; best in fluid mode, where the image waits for the sound; in game mode the sound lags, so choose *Fast*). Applied at the next connection |
| **Gamepads** | Opens the Gamepads page (above), which also holds the next two settings |
| **TV gamepads used by the computer** | Send the TV's gamepads to the PC (enabled by default). When off, they control the TV as before |
| **Switch 2 controllers (Bluetooth)** | Look for Joy-Con 2 / Pro Controller 2 / GameCube controllers whose SYNC button is pressed (off by default) |
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
- **Test versions**: click *Check for updates* 5 times in a row (within 10 s; PC tray menu, or *Settings › Check for updates* on the TV) to install the latest test version before its official release. It may have bugs: please report them in the [Issues](https://github.com/AbelSlawd/TVScreenPlus/issues). The PC then follows the test versions, and goes back to the official ones by itself with the next one.

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
| No rumble | The gamepad and the TV's Android version must allow remote rumble (Android 12+ recommended). Not all Bluetooth gamepads allow it. Switch 1 Pro Controller / Joy-Con: depends on the TV's system (the Gamepads page shows "Not supported by the TV"). |
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
- Only TVs accepted on the PC receive its screen. Pairing uses an ECDH P-256 key exchange: the TV and the PC show the same 6-digit code, which a device in the middle cannot reproduce. A new pairing must also be accepted on the TV, and the TV commits to its key before the code is shown. On every connection the TV then proves it holds the key (HMAC-SHA256 of a random challenge, bound to that connection). TV apps older than 1.2.4 are refused; a 1.2.4 TV app still connects to an allowed PC, with the picture and the sound always encrypted, but must be updated to pair. *Forget the allowed TVs* (PC) or *Forget saved computers* (TV) cancels the pairings.
- The PC only answers local network addresses (private or link-local). A new TV never interrupts the current stream before it is authenticated.
- **Encryption** (PC and TV 1.2.4 or newer): AES-256-GCM, with keys derived from the pairing key and a challenge unique to each connection. A packet that is changed, replayed or sent by another device ends the connection.
  - What the TV sends (gamepads, mouse, keyboard) is **always** encrypted: a few bytes, no cost.
  - The picture and the sound follow the PC setting *Picture and sound encryption*. **Automatic** (default) encrypts them when the TV is fast enough to keep the extra cost under ~1 ms per frame (it measures its speed at every connection: 267 MB/s on a 2024 TCL, ~0.4 ms per frame). **Always** or **Never** force the choice. Applied at the next connection.
  - The TV statistics (OK) and the debug window show what is encrypted (🔒).
- Updates only come from the project's GitHub releases; the TV APK must also be signed with the same key. On the PC nothing is downloaded and run outside updates: ffmpeg, the display driver and the gamepad driver are in the zip. `C:\ProgramData\TVScreenPlus` and the virtual display driver's folder `C:\VirtualDisplayDriver` can only be modified by administrators, and `settings.txt` (pairing keys) can only be read by them.

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

## Contributing
Help is welcome, and not only code: testing on your PC and TV (AMD or Intel graphics, other TV brands), bug reports, translations, ideas. See [CONTRIBUTING.md](CONTRIBUTING.md).
- **Security problem?** Do not open a public issue: report it privately as described in [SECURITY.md](SECURITY.md).
- **Using an AI coding agent?** [AGENTS.md](AGENTS.md) gives it the project's layout, build commands and rules.

## Credits
- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT license): Windows virtual display.
- [FFmpeg](https://ffmpeg.org/), [BtbN](https://github.com/BtbN/FFmpeg-Builds) build (GPL license): fallback encoder, still bundled.
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) 1.22 (BSD 3-clause license, © 2016-2020 Nefarius Software Solutions e.U.): virtual Xbox gamepads. The signed installer is included as-is in `TVScreenPlus-PC.zip`, with its license (`ViGEmBus_LICENSE.txt`). The requests sent to the driver follow those of [ViGEmClient](https://github.com/nefarius/ViGEmClient) (MIT license). The ViGEm project is no longer developed, but the driver remains signed and works on Windows 10 and 11.

## License
TVScreenPlus is released under the [MIT license](LICENSE.md). The third-party components bundled in `TVScreenPlus-PC.zip` (FFmpeg, the virtual display driver, ViGEmBus) keep their own licenses, listed in [Credits](#credits).
