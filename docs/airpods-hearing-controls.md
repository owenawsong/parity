# Custom Transparency, Conversation Boost, and playback level

## What the earbuds control

Custom Transparency changes how the AirPods reproduce their surroundings. Conversation Boost focuses that processing on someone speaking in front of the wearer. Apple's separate Conversation Awareness feature reacts when the wearer speaks. The Windows microphone monitor in Parity routes a selected PC input to the earbuds and serves a different purpose. [Apple's Headphone Accommodations guide](https://support.apple.com/en-us/102663)

LibrePods' Android implementation reads and writes a settings record containing per-ear amplification, tone, ambient noise reduction and Conversation Boost. Its documented transport is the Bluetooth Classic Attribute Protocol. In the layouts Parity's trace reader recognizes, the record is 100 or 104 bytes. The existing Parity service opens a channel for ordinary AirPods controls. The Transparency record needs its own transport. [LibrePods settings implementation](https://github.com/librepods-org/librepods/blob/89356c08cea3db0b6b54792d9329d09a87cc85fa/android/app/src/main/java/me/kavishdevar/librepods/data/Transparency.kt), [podsd transport implementation](https://github.com/madeitdisfar/podsd/blob/d5e17679b4ecbef01b6034b8153f28aa9aa86516/src/bluetooth/att.rs)

Run the read-only Windows inventory from the checkout with:

```powershell
python tools/inspect-transparency-services.py
```

The inventory prints each matching Bluetooth address. Use the address for the intended pair to request fresh SDP records. The tool verifies the Classic pairing and connection before and after discovery.

```powershell
python tools/inspect-transparency-services.py --live <paired-address>
```

On the test Pro 3 pairing, both Windows service caches contained Classic ATT handle ranges 0x004F–0x0052 and 0x0057–0x0057. LibrePods' known profile handle is 0x0018. These are cached advertisements and can omit private services. An overlapping handle on some other device would also need independent identification. The release condition for Windows editing is a recognized live profile, one deliberate change acknowledged and read back from the same pair, and restoration of the original setting with the other fields preserved.

### Connected Pro 3 check

Windows confirmed the paired AirPods were connected over Bluetooth Classic. The device container had 14 related devices and 21 interfaces, with zero Windows GATT service interfaces and zero paired LE endpoints in that container. Trying the paired Bluetooth address directly also returned no LE device.

A Winsock SDP lookup on that pair, requesting fresh records with `LUP_FLUSHCACHE`, returned eight service records. The two Classic ATT ranges were 0x004F–0x0052 and 0x0057–0x0057, the same ranges as in the cache. Neither included 0x0018. The earbuds’ current Conversation Boost value is therefore unknown through the tested Windows routes. The installed Parity service exposes the ordinary AAP channel and has no Classic ATT read operation. [Microsoft's Bluetooth service discovery documentation](https://learn.microsoft.com/en-us/windows/win32/bluetooth/bluetooth-and-wsalookupservicebegin-for-service-discovery)

The Windows Winsock catalog on this host also lists Microsoft's L2CAP stream provider (address family 32, protocol 256). Socket creation succeeded. A direct connection to the paired Pro 3 at ATT PSM 0x001F returned Winsock error 10050 (WSAENETDOWN) before any ATT request. During the same check, fresh service discovery succeeded and identified service 1801 at 0x0057 and service 4715650b-5e9d-4ac2-b898-a4fc0aa5df78 at 0x004F–0x0052. Error 10050 was returned by the local Winsock connection attempt. No remote ATT error was received, and the availability of the private attribute remains unknown.

The referenced clients identify Transparency by attribute handle. To obtain its service and characteristic UUIDs from an authorized PacketLogger capture, include connection setup and GATT discovery, then run:

```powershell
python tools/inspect-seal-trace.py capture.pklg --features transparency
```

The decoder pairs ATT Read By Group Type and Read By Type requests with their responses on the same Classic bearer. It reports the primary service enclosing handle 0x0018 and the characteristic declaration naming that value handle when present. A capture may omit discovery when the host already knows the handle. UUID output identifies the declaration; the profile read and setting change still require independent verification.

## What Windows can measure

The tool below watches one explicitly selected stereo playback endpoint using WASAPI loopback. It reports separate left and right one-second-window digital RMS values with A-weighting. It also reports Windows' endpoint gain and mute state separately. Audio stays in memory. The tool exits if the selected endpoint is invalidated. [Microsoft's loopback documentation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)

```powershell
$sdk = '.build/dotnet/dotnet.exe'
& $sdk run --project tools/headphone-level -c Release -- --self-test
& $sdk run --project tools/headphone-level -c Release -- --list
& $sdk run --project tools/headphone-level -c Release -- --endpoint '<render endpoint ID from the list>' --seconds 10
```

A 1 kHz digital sine with 0.01 peak amplitude has an expected RMS level of −43.01 dB relative to full-scale amplitude. The Windows virtual-endpoint test returned −43.0 on both channels. Frequency-response checks at 100 Hz, 1 kHz and 4 kHz passed at 44.1, 48 and 96 kHz. These tests establish the digital measurement only.

Apple shows headphone playback levels and exposure history. To convert Parity's digital measurements to an acoustic estimate, a reference must cover the exact earbud model, Windows output path, volume settings, and processing mode. A next experiment would measure a Pro 3 on an ear simulator at several normal playback levels and reserve a different signal and volume for validation. Confirm where endpoint attenuation is applied in the measurement path before adding it to any estimate. Changing the Windows volume could otherwise count the same attenuation twice. [Apple's headphone level meter](https://support.apple.com/guide/iphone/iph0596a9152/ios), [WHO and ITU safe-listening measurement framework](https://www.itu.int/dms_pub/itu-t/opb/joint/T-JOINT-WHO-2019-1-PDF-E.pdf)

### Bluetooth playback-level telemetry on the tested Pro 3

No supported Windows endpoint in the connected-pair inventory supplied an earbud-reported acoustic playback level. The checked LibrePods and podsd implementations exposed no identified headphone sound-pressure readout. Private device telemetry remains unknown.

Three standard LE Audio services have distinct meanings. Hearing Access identifies hearing aids and controls presets. Audio Input Control reports gain and mute for a particular input. Volume Control represents output volume on a scale of 0 to 255. None of these values represents sound pressure at the ear. An Apple Health headphone exposure sample records equivalent continuous A-weighted sound pressure over its sampling interval. A Windows loopback sample measures the digital playback signal. Neither establishes a live SPL-reporting characteristic on this Bluetooth pairing. [Bluetooth SIG Hearing Access](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/HAS_v1.0.1/out/en/index-en.html), [Audio Input Control](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/AICS_v1.0.1/out/en/index-en.html), [Volume Control](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/77401-VCS-html5/out/en/index-en.html), [Apple HealthKit headphone exposure](https://developer.apple.com/documentation/healthkit/hkquantitytypeidentifier/headphoneaudioexposure)

### Endpoint attenuation experiment and acoustic validation

On an idle, non-default AudioRelay virtual render endpoint, a stereo 48 kHz, 1 kHz sine at 0.01 peak amplitude played through shared-mode WASAPI. The relay process was closed. For each gain below, the meter captured three consecutive windows. Only the virtual endpoint gain changed, and the original gain was read back after restoration.

| Virtual endpoint gain | Left digital RMS | Right digital RMS |
| --- | --- | --- |
| 0 dB | −43.0 dBFS(A) | −43.0 dBFS(A) |
| −12 dB | −43.0 dBFS(A) | −43.0 dBFS(A) |
| 0 dB, restored | −43.0 dBFS(A) | −43.0 dBFS(A) |

On this virtual endpoint, the loopback signal excluded the endpoint gain change. Bluetooth absolute volume, firmware processing and the output transducer remain outside this experiment. Apple's HealthKit API provides equivalent continuous A-weighted sound pressure level samples on supported Apple platforms. Apple's Pro 3 technical specifications describe its adaptive processing but give no transfer function linking Windows render levels and gains to sound pressure. [Apple HealthKit exposure quantity](https://developer.apple.com/documentation/healthkit/hkquantitytypeidentifier/headphoneaudioexposure), [AirPods Pro 3 specifications](https://www.apple.com/airpods-pro/specs/)

A bench measurement on the Windows route can establish a calibration for a specified pair and operating range:

1. Use a calibrated occluded-ear simulator appropriate for insert earphones. Record the simulator, microphone, calibrator, tip and insertion method. Check the microphone calibration before and after the session. Use a quiet fixture and measure the background in each operating mode.
2. Record the AirPods model, firmware, Windows endpoint, Bluetooth playback profile, sample rate, left and right channel balance, endpoint gain and processing settings. Seat each earbud in the fixture. Keep automatic processing settings and nearby sound conditions reproducible.
3. With the earbuds in the fixture, play a fixed 1 kHz tone at conservative levels. At a fixed Windows gain, change the signal amplitude by a known amount. Then hold the signal amplitude fixed and change the Windows gain. Log each ear’s loopback RMS and simultaneous microphone LAeq over the same steady interval. Repeat across at least three intended gains. Use the measured paired changes to identify where gain enters the calculation.
4. Repeat with a documented broadband signal and independent reseatings. Keep a separate signal, gain and reseating out of the calibration fit. Compare the estimate against the simulator separately for each ear on these held-out trials. Report the maximum absolute discrepancy, reseating spread, calibration drift, spectrum and tested gain range.
5. Identify the acoustic reference plane. An ear-simulator measurement gives sound pressure at the simulator microphone. For a safe-listening exposure estimate, apply and document the appropriate reference-field correction and validate it on broadband material. Leave any unmeasured mode, playback profile or volume range uncalibrated. [ITU-T H.870 safe listening guidelines](https://www.itu.int/rec/T-REC-H.870/en)

### Pro 3 playback probe and calibrated prototype

A read-only probe of the active Pro 3 endpoint captured both channels of existing playback. In the observed windows it measured roughly −28 to −23 dBFS(A), with Windows reporting about −10.9 dB of master gain. There was no simultaneous acoustic reference, so these observations establish signal and gain coverage on the physical Bluetooth route.

The most recent Windows control session also reported settings `0x37 = 0x00` and `0x38 = 0x52`. CAPod's Pro 3 captures identify these as the limit switch and configured level. `0x52` represents a configured 82 dBA limit, which is independent of the changing playback level. A separate eight-second probe included a transition from idle to playback and logged one distinct control packet, `0x000E`, which public protocol notes identify as an audio-source response. The session log deduplicates identical control traffic. These checks cover the observed Windows session and leave private Apple-host telemetry unresolved. [CAPod's captured setting analysis](https://git.tysstech.com/TySS-Dev/capod/commit/26b04854c470fa7c7dc1754f38c7c754c8a935db) · [AACP opcode research](https://github.com/arctumn/ntpods/blob/main/docs/opcodes.md)

The probe accepts paired Windows and acoustic measurements from a calibrated bench. Record A-weighted equivalent levels on both ears for the same steady playback windows. The uncalibrated probe prints timestamps, stereo signal, sample rate and master and per-channel gain. Use steady windows with continuous playback, unchanged gains and mute off. Record the model, firmware, output route, fixture and processing state in `setup`. Use `ear-simulator` for levels measured at the fixture and `diffuse-field-corrected` only for measurements with the documented correction applied.

```powershell
$sdk = '.build/dotnet/dotnet.exe'
& $sdk run --project tools/headphone-level -c Release -- --validate-calibration measurements.json
& $sdk run --project tools/headphone-level -c Release -- --endpoint '<same render endpoint ID>' --seconds 10 --calibration measurements.json
```

Example structure with **synthetic measurements**, for checking the input format only.

```json
{
  "endpointId": "example",
  "sampleRate": 48000,
  "referencePlane": "ear-simulator",
  "setup": "Synthetic example",
  "trials": [
    {"id":"fit-1","signal":"tone","holdout":false,"gain":{"masterDb":-10,"leftDb":-10,"rightDb":-13},"leftDbFs":-40,"rightDbFs":-44,"leftDba":53,"rightDba":45},
    {"id":"fit-2","signal":"tone","holdout":false,"gain":{"masterDb":-10,"leftDb":-10,"rightDb":-13},"leftDbFs":-10,"rightDbFs":-14,"leftDba":83,"rightDba":75},
    {"id":"test-1","signal":"program simulation","holdout":true,"gain":{"masterDb":-10,"leftDb":-10,"rightDb":-13},"leftDbFs":-35,"rightDbFs":-39,"leftDba":59,"rightDba":49},
    {"id":"test-2","signal":"speech","holdout":true,"gain":{"masterDb":-10,"leftDb":-10,"rightDb":-13},"leftDbFs":-15,"rightDbFs":-19,"leftDba":77,"rightDba":71}
  ]
}
```

Each gain state gets a separate offset per ear. The bench comparison therefore accounts for attenuation already in the loopback signal. A state must have at least two fitting trials and two held-out trials using signals excluded from the fit. Validation must span at least 6 dB within the fit range on each side. The tool reports the worst held-out error and rejects a constant-offset model if any fit or validation trial differs by more than 3 dB. That threshold is a research gate for the supplied trials. A live estimate requires the same endpoint, sample rate, master and per-channel gains, and a signal level inside the held-out range. The bench operator is responsible for matching the firmware, fit and processing state. To evaluate broader release use, repeat the tests across content, reseatings, processing modes, physical units and playback routes. [ITU-T H.870 measurement method](https://www.itu.int/rec/T-REC-H.870/en)
