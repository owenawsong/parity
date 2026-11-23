# Parity 0.19 private beta

Use the Parity 0.19.1 x64 Setup EXE from the draft release. The desktop app installs for the current user. The optional AirPods controls install Parity’s own Bluetooth service and reuse USB/IP if already installed and verified.

## Test setups

| AirPods | Adapter | What to learn |
| --- | --- | --- |
| Pro 3 | Main development adapter | Confirm the package behaves like the current local installation. |
| Pro 2, either case | A different Bluetooth adapter | Check discovery, battery sources, listening modes, and the microphone route on another hardware combination. |
| Pro 1 or a standard pair | A second adapter if available | Check the controls the model exposes, including Off on Pro 1 and basic playback on a standard pair. |

Record the AirPods firmware, Bluetooth adapter name and USB hardware ID, Windows build, Parity version, and Bluetooth service version. Mark each check **Pass**, **Fail**, or **Skipped**.

## Setup and removal

1. Install desktop-only on a fresh Windows 11 x64 PC. Check that the native Bluetooth adapter and existing devices keep working.
2. Run Setup with full AirPods controls selected and decline administrator approval. Check that the desktop app still opens and the adapter is untouched.
3. Run Setup again with approval. If prompted, restart before pairing. Check the tray, Overview, and connection diagnostics. Verify that a Bluetooth keyboard or mouse, if paired, reconnects and that normal AirPods playback works.
4. On a PC with WPods installed, confirm Setup offers desktop-only. Test migration by removing WPods through Windows first and adding Parity controls afterward.
5. Update Parity and verify that its preferences and Start shortcut carry over.
6. Uninstall from Installed apps. Verify that Parity restores the native Bluetooth adapter before removing the service, and that native Bluetooth playback still works after reboot. Check both choices for keeping and removing a Parity-installed USB/IP driver.

## AirPods checks

| Check | Expected behavior |
| --- | --- |
| Nearby discovery and reconnect | Open the case, connect from the prompt, disconnect, then reconnect after sleeping the PC. Try at least one reconnect with the AirPods already paired. |
| Battery and charging | Verify each earbud and the case with the lid open. Close it with both buds out. Report whether there is a fresh broadcast or an aged last reading. Try one earbud in the case and verify the charging indicator. |
| Listening modes | Cycle the available modes quickly, then leave one selected for a few seconds. Confirm the setting in the earbuds and the UI. Check that the available modes match the model. |
| Music and calls | Play music, start a call using the selected input, end the call, and check stereo playback afterward. If there is an issue, report the player's output, the call app's input, and how the route recovered. |
| Device settings | Try an ear detection setting, stem action, or microphone side setting that the model exposes. Check the choice again after reconnecting. |

Share results in a [private compatibility report](https://github.com/owenawsong/parity/issues/new?template=adapter_report.yml). When a failure needs logs, attach a short excerpt with device addresses and personal names removed. Setup writes its app-install log under `%LOCALAPPDATA%\Pairside\setup.log`.

## Development verification

The package smoke test stages both payloads, verifies the desktop files and the bundled driver, and exercises desktop file removal. The administrator-approved Bluetooth changes and radio restoration need the physical Windows tests above.
