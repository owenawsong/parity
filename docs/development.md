# Build Parity

The desktop build uses the x64 .NET SDK pinned in [global.json](../global.json), Node.js 24, and PowerShell 7. Hardware control requires an installed WPods Bluetooth service and its driver. The UI preview runs with simulated devices.

## Build the desktop package

From the repository root:

```powershell
npm --prefix ui ci
./build.ps1 -NativeOnly
./tools/build-pairside-live.ps1 -PackagePath dist/Parity-win-x64.zip
```

The native build uses the upstream Rust/C++ toolchain and downloads models for microphone processing. See [the native build scripts](../native) for compiler and dependency setup. You can skip the native build if you already have a complete x64 runtime from a compatible WPods install. The package builder checks `.build/native/bin` and then the installed WPods folder by default. Pass `-RuntimeRoot <directory>` to select the native payload explicitly.

The builder creates the archive, a per-file integrity manifest, and the installer. It prints the archive’s SHA-256. It builds the UI in a fresh directory using the repository’s illustrations. Optional, locally imported Apple media stays out of the ZIP.

Extract the archive and run `./Install-Parity.ps1 -StageDirectory <empty-directory>` to verify a package in isolation. To install for the current user, run the installer without arguments. The Start menu entry is **Parity**. The install directory remains `%LOCALAPPDATA%\Programs\Pairside` for compatibility with previous builds.

## Preview the UI

```powershell
npm --prefix ui ci
npm --prefix ui run build
node ui/preview/build.mjs
```

Open `dist/Pairside.html` for the dashboard or `dist/Latchwave Popup.html` for the nearby prompt. For a separate Windows preview app, publish `src/Pairside/Pairside.csproj` to `dist/pairside-desktop` and run `./tools/install-pairside-desktop.ps1` without `-Live`.

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

The bundled AirPods Pro 3 illustration is generated with [Blender source](../tools/render-pro3.py). With Blender and Pillow available, run the following from the repository root:

```powershell
blender -b -t 6 --python-exit-code 1 --python tools/render-pro3.py -- .build/pro3
python tools/pack-pro3.py .build/pro3 ui/src/assets/airpods-pro-3.webp
```

[Local artwork and motion tools](local-artwork.md) describes how to prepare optional reference clips for local development.
