# Parity

[![Parity for Windows. The Overview shows AirPods Pro 3 batteries, listening modes, and Cry for Me by Magdalena Bay in Spotify.](docs/media/parity-banner.webp)](docs/media/overview.webp)

<p align="center">
  <a href="https://github.com/owenawsong/parity/releases"><strong>Windows downloads</strong></a> ·
  <a href="#install">Setup</a> ·
  <a href="#compatibility">AirPods compatibility</a> ·
  <a href="docs/development.md">Build from source</a>
</p>

Parity brings the controls you reach for most into the Windows tray. See each earbud and the case at a glance, switch from Transparency to Noise Cancellation, and get back to what you were playing.

## Open the case and connect

Open a recognized case near your PC and use the nearby prompt to connect. Once you're connected, Overview shows the separate batteries, listening modes available on your model, and playback controls for the active media app.

A case can stop reporting after you close it. When Parity has an earlier reading, it shows the battery level with the time it was last seen.

The nearby card shows one battery reading for the pair and one for the case. Turn on **Separate earbud batteries** in Settings if you want both earbud readings there too.

<p align="center">
  <img src="docs/media/nearby.webp" width="262" alt="Parity’s nearby connection popup with rendered AirPods Pro 3, earbud and case battery rings, and a Connect button">
</p>

## Make it sound the way you want

- **Listening modes and EQ.** Switch between the modes your AirPods support, adjust the Adaptive Audio balance, or tune the three-band equalizer stored on compatible AirPods.
- **Spatial Audio on Windows.** Try Fixed or Head Tracked with an interactive speaker view. Parity renders the spatial effect on your PC and can use AirPods motion to keep the soundstage anchored as you turn.
- **A microphone you can check before a call.** See the input level, record a sample, listen back, and adjust voice processing. The stereo microphone route depends on your AirPods model and the WPods service.
- **Gestures and Pulse.** Practice a nod or shake, assign shortcuts, or view live heart rate and session history on AirPods Pro 3.

<details>
<summary>See the equalizer and spatial audio controls</summary>
<br>
<p align="center">
  <img src="docs/media/sound.webp" width="46%" alt="Parity's custom equalizer with bass, midrange, and treble controls">
  <img src="docs/media/spatial.webp" width="46%" alt="Parity's Head Tracked mode and speaker diagram">
</p>
</details>

## Install

The preview requires **Windows 10 (2004 or newer) or Windows 11 on x64**, the **.NET 10 Desktop Runtime (x64)**, and the **Microsoft Edge WebView2 Runtime**. AirPods control uses the **WPods Bluetooth service and driver**.

1. Install and configure [WPods](https://github.com/FPNDev/wpods#install). Pair your AirPods in Windows, then quit the WPods tray app. Leave its background service running.
2. Download the Windows x64 ZIP from [Releases](https://github.com/owenawsong/parity/releases) when a build is available and extract it.
3. Open PowerShell in the extracted folder, run `.\Install-Parity.ps1`, and open **Parity** from Start.

To switch back, quit Parity from the tray and reopen WPods. Both apps keep separate preferences and share the Bluetooth service.

## Compatibility

**AirPods Pro 3** is the primary hardware test pair. Other model layouts and modes have been checked with simulated devices. Nearby discovery, the stereo microphone, spatial head tracking, and on-device settings also depend on your adapter, service version, model, or firmware.

| AirPods family | Listening modes in Parity |
| --- | --- |
| AirPods 1–4 without ANC | Standard playback |
| AirPods Pro 1 and AirPods Max 1 | Off, Transparency, Noise Cancellation |
| AirPods 4 with ANC, AirPods 5, and AirPods Max 2 | Off, Transparency, Adaptive, Noise Cancellation |
| AirPods Pro 2 and AirPods Pro 3 | Transparency, Adaptive, Noise Cancellation. Enable Off in Advanced settings. |

The screenshots show Parity with locally prepared AirPods artwork. The downloadable ZIP uses Parity’s bundled illustrations. Spatial processing depends on the selected Windows playback route. Apple hearing setup is available on supported Apple devices.

## Development and feedback

[Build or preview the app](docs/development.md) or [share an issue or hardware test result](https://github.com/owenawsong/parity/issues/new/choose). Include the Parity version, AirPods model, and Windows build if you’re reporting a problem.

## Credits and license

Parity takes design inspiration from [MagicPods](https://magicpods.app/), [AirPodsDesktop](https://github.com/SpriteOvO/AirPodsDesktop), and Apple’s AirPods experience. Its Bluetooth and device-control code builds on [WPods by FPNDev](https://github.com/FPNDev/wpods).

This modified version of WPods v0.4.1 (commit `0019866`) is dated **November 22, 2026** and is licensed under the [GNU General Public License v3.0](LICENSE). See the [third-party notices](THIRD-PARTY-NOTICES.txt) and [original WPods documentation](docs/wpods-upstream.md).

Parity is independent of Apple. AirPods and related Apple product names are trademarks of Apple Inc.
