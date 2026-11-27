# Build Parity

The desktop build uses the x64 .NET SDK pinned in [global.json](../global.json), Node.js 24, and PowerShell 7. The packaged client uses the Parity Bluetooth service for hardware controls. The UI preview runs with simulated devices.

## Build Parity Setup

From the repository root on Windows:

```powershell
npm --prefix ui ci
./build.ps1 -NativeOnly
./tools/build-parity-setup.ps1 -OutputPath dist/Parity-Setup-x64.exe
```

The build uses the .NET Framework compiler included in Windows to assemble Setup. It embeds the manifest-verified desktop ZIP, a Parity build of the Bluetooth service, and the pinned usbip-win2 installer. Setup verifies the USB/IP installer’s hash and publisher before running it for the optional Bluetooth install. Microsoft runtimes are downloaded only when missing and checked for a valid Microsoft signature.

To verify the EXE without changing the live app or Bluetooth, use `--verify-package <empty-directory>` for the desktop payload and `--verify-bluetooth-package <empty-directory>` for the service payload. The staged `Uninstall-Parity.ps1` accepts `-StageDirectory <directory>` to test file removal. [Private beta checklist](private-beta.md)

`./tools/tests/check-parity-setup.ps1 -Setup dist/Parity-Setup-x64.exe` runs these checks together and verifies the bundled USB/IP package.

Add `-CheckUi` to test the extracted UI with Edge and a simulated Bluetooth host. It checks the first-launch empty state, battery renders, available listening modes, and Nearby connection flow. Use a physical Windows test for pairing, audio, and sleep/wake.

## Build the desktop package

From the repository root:

```powershell
npm --prefix ui ci
./build.ps1 -NativeOnly
./tools/build-pairside-live.ps1 -PackagePath dist/Parity-win-x64.zip
```

The native build uses the upstream Rust/C++ toolchain and downloads models for microphone processing. See [the native build scripts](../native) for compiler and dependency setup. The package builder reads `.build/native/bin` by default. Pass `-RuntimeRoot <directory>` to select an existing native payload for either build. The local development build without `-PackagePath` still supports the legacy development adapter.

The builder creates the archive, a per-file integrity manifest, and the installer. It prints the archive’s SHA-256. It builds the UI in a fresh directory using original Blender renders for AirPods Pro 3 and the repository’s illustrations for other models. Optional, locally imported Apple media stays out of the ZIP.

Extract the archive and run `./Install-Parity.ps1 -StageDirectory <empty-directory>` to verify a package in isolation. To install for the current user, run the installer without arguments. The Start menu entry is **Parity**. The install directory remains `%LOCALAPPDATA%\Programs\Pairside` for compatibility with previous builds.

## Preview the UI

```powershell
npm --prefix ui ci
npm --prefix ui run build
node ui/preview/build.mjs
```

Open `dist/Pairside.html` for the dashboard or `dist/Latchwave Popup.html` for the nearby prompt. For a separate Windows preview app, publish `src/Pairside/Pairside.csproj` to `dist/pairside-desktop` and run `./tools/install-pairside-desktop.ps1` without `-Live`.

Use `./tools/build-pairside-live.ps1 -OwnerPreview` for the local Owner Preview page. It uses the locally imported artwork and lets you compare the bundled renders. The build rejects `-OwnerPreview` together with `-PackagePath`.

After importing the optional rotation assets, generate the local Overview poses and motions from the same cached bundles with `python tools/import-airpods-overview.py --ffmpeg <ffmpeg.exe>`. The importer uses Pillow, NumPy, OpenCV, and FFmpeg 9 or newer with HEVC alpha support. Owner Preview's **Overview connection** view plays the opening, holds the turn, and lets you confirm the connection to see the reveal. These media stay in `.build` and the opt-in local build.

## Run the checks

With Python, Playwright, Microsoft Edge, the pinned .NET SDK, and the UI dependencies installed:

```powershell
python -m pip install playwright
python tools/tests/run-regressions.py
```

The suite covers model-dependent listening controls, connection behavior, spatial rendering, microphone routing, battery history, gesture input, and UI handling of media and localization. Live Bluetooth behavior still needs a test on the relevant AirPods and adapter. See [Windows audio validation](windows-audio-validation.md) for hands-on checks.

## Project layout

| Path | Role |
| --- | --- |
| [`ui/src`](../ui/src) | Desktop UI, nearby popup, and components |
| [`src/WPods`](../src/WPods) | Windows host, device state, Bluetooth protocol, and audio pipeline |
| [`src/Pairside`](../src/Pairside) | Separate desktop host for the simulated preview |
| [`tools/tests`](../tools/tests) | Regression tests |
| [`native`](../native) | Native build and microphone-processing dependencies |

## Artwork

The bundled AirPods Pro 3 illustration is generated with [Blender source](../tools/render-pro3.py). With Blender and Pillow available, run the following from the repository root to refresh the isolated hardware renders used by the package:

```powershell
blender -b -t 6 --python-exit-code 1 --python tools/render-pro3.py -- .build/parity-bundled-hardware --hardware
python tools/pack-pro3-hardware.py .build/parity-bundled-hardware ui/src/assets/parity-pro3
```

For the other shipped artwork, render with `--hardware --family=<family>` and pack into `ui/src/assets/parity-models/<family>`. The families are `classic`, `airpods3`, `airpods4`, `pro1`, `pro2`, and `max`. The known-model mapping is in `ui/tools/popup-assets.mjs`.

[Local artwork and motion tools](local-artwork.md) describes how to prepare optional reference clips for local development.
