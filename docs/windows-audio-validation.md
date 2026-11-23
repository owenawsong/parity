# Windows audio validation

## Service and driver

On November 22 the development PC reported WPods.Service 0.4.2 over WinUSB with its adapter guard clear. Parity has its own desktop executable and preferences. Both clients depend on the installed Bluetooth service.

WPods 0.4.3 adds experimental HD voice for older AirPods and improves how its service recognizes Windows Hands-Free channels. Builds of both tags passed in separate directories. I replayed a normal compound L2CAP signaling frame inside the 0.4.3 source and its parent before the split signaling fix. Only the fixed build recognized the embedded incoming RFCOMM connection, kept it through a pending response, and consumed it on success. That verifies the parser path without an adapter takeover. [WPods 0.4.3 release](https://github.com/FPNDev/wpods/releases/tag/v0.4.3) · [Split signaling fix](https://github.com/FPNDev/wpods/commit/6a406c3d94e944ce9c6fc2b5aefce0875703af64)

A second replay varied the signaling ID, channel ID and position in the command list. The fixed build passed 64 single-command and 64 compound exchanges. The parent revision missed those requests on the tested receive path. The replay verified parser state without starting a Bluetooth service.

Generic Windows VoIP call events belong to the calling application. The AirPods service tells Parity when Hands-Free audio starts, which is useful for routing but arrives too late to identify an unanswered call. A nod-to-answer feature needs a verified incoming-call signal and a verified call action for the chosen Windows calling app before it can be offered in the UI. [Windows VoipPhoneCall](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.voipphonecall)

## Media handoff

Parity snapshots the current playback choices before enabling Spatial Audio. It restores those choices when Hands-Free audio needs the earbuds. It resumes the chosen mode when the same AirPods are still attached, no conflicting capture is active, and the original output choices are back. A change in Windows or the player's output wins. **Stay in Stereo** cancels recovery. Repeated audio initialization failures also stop automatic recovery.

After an unexpected loss of the control channel, Parity gives the same pair up to 25 seconds to reconnect. An explicit Disconnect clears the request. The playback queue holds at most 100 milliseconds of PCM at 48 kHz. If the reader stalls, the queue retains the newest frames and applies a short ramp over the discontinuity. This bounds queue backlog independently of Bluetooth transport latency.

Playback diagnostics identify current streams by Core Audio endpoint ID. **Through Parity** means the app is routed to the virtual input of the renderer. **Direct to AirPods** means that app is using the earbuds' physical output. The status reflects active sessions, including sessions between audible samples. Whole-PC playback shows track details when the media session matches a player with an unambiguous route through Parity. [Windows audio sessions](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audio-sessions)

On Bluetooth stacks with separate stereo and mono playback endpoints, Parity checks channel counts before choosing an endpoint for Spatial Audio. An exact existing default is preferred when it is suitable. With several candidates and no suitable default, the stereo endpoint must be unambiguous.

On this PC the AudioRelay render endpoint currently mixes two channels at 48 kHz. This is sufficient for Parity's binaural stereo rendering. Live discrete surround would need a playback endpoint that exposes the original multichannel samples. The file-based Sound check can already use separately decoded 5.1 channels.

I tested Windows process loopback against a synthetic stream that belonged to the probe's own process. It captured cleanly, but muting that process's Windows audio session dropped captured RMS from 0.001414 to 0.000015. Unmuting returned it to 0.001414. All playback defaults stayed in place. So capturing a player and muting its session would also silence the audio Parity needs to render. Microsoft documents a per-device audio processing effect or separate virtual endpoint as possible foundations for deeper integration. A virtual endpoint would need to expose discrete input channels and implement real loopback. The SysVAD sample simulates loopback with a tone and would need additional work plus a signed driver package. [Process loopback sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/) · [Componentized audio effects](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects) · [SysVAD sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/sysvad-virtual-audio-device-driver-sample/)

This Windows build accepted process-loopback requests for two, six and eight channels. To check the actual content, I sent six distinct tones to the two-channel virtual endpoint and measured 168,000 frames from this process alone. Windows mixed all six tones into the first two channels and returned silence on channels three through six. A wider capture format therefore still received stereo content on this path. Windows kept all default playback roles during the experiment.

The packaged Sound check preserved all six tones on separate channels when decoding a 5.1 WAV file in the WebView. Switching from Fixed to Head Tracked kept the same file playback instance. This verifies that the file decoder and channel graph can supply discrete surround to Parity's binaural renderer.

For a sample of 39 unambiguous listening-mode writes followed by a matching AirPods report, the median was about 0.95 seconds. These are device report timings measured from the existing log. They do not measure when the audio changed inside the earbuds or how soon the app displayed the pending selection.

## Verification

- Run `python tools/tests/run-regressions.py` for head pose and rendering, case signals, role restoration, UI behavior and shared-feature regressions.
- Run the Windows spatial mix test with `--virtual-loopback` for a short, quiet test solely on the existing AudioRelay virtual endpoint. It checks that Windows delivers an independent loopback signal and verifies all default output roles after the test.
- An isolated probe set both per-app playback roles on its own executable, exited without restoring them, then relaunched. The replacement process found the saved journal and restored both roles. The Windows playback defaults stayed in place.
- Run the spatial render test with `--soak-minutes 60` to continuously vary speaker spacing, head pose and signal presence. It checks allocation, peak limits and timing per audio buffer.
- The November 22 run processed 98,851 40 ms buffers over 75 minutes without rendering-thread allocation or limiter violations. The longest measured render was 14.70 ms. The run exercised the renderer in isolation from Bluetooth.
- Parity 0.17.2 passed 332 browser-fixture navigation and routing cycles over 45 minutes from 320 to 1,024 px. The DOM count held at 2,152 after the pages loaded. A separate model fixture checked nine profiles across three widths.
- Live call acceptance requires an incoming call to the test Windows app. Play audio in Fixed or Head Tracked, accept and end the call, then verify the actual mic in use and that the previous media returns. Repeat with a manual output change during the call and a second call a moment after the first.
