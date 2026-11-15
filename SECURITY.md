# Security policy

TVScreenPlus shows your PC screen on a TV and, since version 1.2.4, lets that TV control the PC's mouse and keyboard. Security problems are therefore taken seriously.

## Supported versions

Only the **latest release** receives security fixes. PCs install updates automatically, and the TV app offers them at launch.

| Version | Supported |
|---|---|
| Latest release | ✅ |
| Older versions | ❌ Please update |

## Reporting a vulnerability

**Please do not open a public issue, pull request or Discord message for a security problem.**

Report it privately through GitHub: go to the [Security tab](https://github.com/AbelSlawd/TVScreenPlus/security) › **Report a vulnerability** (or open [this link](https://github.com/AbelSlawd/TVScreenPlus/security/advisories/new) directly). Only the maintainer can read it.

Please include:

- the affected version(s) of the PC program and/or the TV app;
- what an attacker can do, and what they need first (same Wi-Fi network, a paired TV, local access to the PC…);
- the steps to reproduce, or a proof of concept;
- your suggested fix, if you have one.

You can write in English or French.

## What happens next

This is a one-person project in free time, so these are goals, not guarantees:

1. Acknowledgement within **7 days**.
2. An assessment and a fix plan within **30 days**.
3. The fix is released, then the advisory is published. You are credited unless you prefer not to be.

Please give us a reasonable time to release a fix before disclosing the problem publicly.

## Scope

**In scope** (examples):

- Watching, injecting or changing the stream without being paired: pairing (ECDH P-256 + 6-digit code), the per-connection challenge (HMAC-SHA256), the AES-256-GCM channel.
- Controlling the PC (mouse, keyboard, launching apps) from a device that is not an accepted TV.
- Making the PC accept connections from outside the local network.
- The update process: making a PC or TV install something that is not an official release.
- Gaining administrator rights through the PC program, its installer or `C:\ProgramData\TVScreenPlus`.
- Reading the pairing keys from a standard (non-administrator) Windows account.
- Crashing the PC program, or making it use a lot of memory, from the network without being paired.

**Out of scope**:

- An attacker who is already an administrator on the PC, or who has root/ADB access to the TV.
- A TV the user has accepted: it is trusted by design (it can see the screen and, if remote control is on, use the mouse and keyboard).
- The Windows SmartScreen / Defender warning about the unsigned program (known, see the README).
- Vulnerabilities in third-party components that are already fixed upstream (FFmpeg, the virtual display driver, ViGEmBus): please report them to their projects. Do tell us if we ship an outdated version.
- Denial of service by flooding the local network.

The security design is described in the README ([Security](README.md#how-it-works) under *How it works*).
