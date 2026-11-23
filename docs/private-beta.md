# Parity 0.19 private beta

Use the x64 Setup EXE from the draft release. The installer checks for the existing WPods Bluetooth service and for the Microsoft runtimes. On a fresh PC it opens WPods Setup for the administrator-approved Bluetooth install. Parity itself installs for the current user.

## Test setups

| AirPods | Adapter | What to learn |
| --- | --- | --- |
| Pro 3 | Main development adapter | Confirm the package behaves like the current local installation. |
| Pro 2, either case | A different Bluetooth adapter | Check discovery, battery sources, listening modes, and the microphone route on another hardware combination. |
| Pro 1 or a standard pair | A second adapter if available | Check the controls the model exposes, including Off on Pro 1 and basic playback on a standard pair. |

Record the AirPods firmware, Bluetooth adapter name and USB hardware ID, Windows build, Parity version, and WPods service version. Mark each check **Pass**, **Fail**, or **Skipped**.

## Setup and removal

1. On a fresh Windows x64 PC, run Setup and complete the WPods prompts. If WPods asks for a restart, restart before pairing.
2. On a PC with WPods configured, run Setup and confirm it reuses the Bluetooth service.
3. Pair in Windows, quit the WPods tray app, and launch Parity from Start. Check the tray icon, Overview, and the pairing guide.
4. Update an existing Parity install and verify that its preferences and Start shortcut carry over.
5. Uninstall Parity from Installed apps. Confirm Windows removes the app entry and WPods can still open. Reinstall Parity and check that its preferences return.

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

The package smoke test extracts the Setup EXE payload into an isolated directory, verifies all app files, and exercises the uninstaller against the staged copy. Fresh-PC prerequisite installation and Bluetooth behavior need the tests above.
