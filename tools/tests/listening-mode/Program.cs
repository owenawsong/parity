using System.Security.Cryptography;
using System.Text;
using WPods.Core;
using WPods.Nearby;
using static WPods.Core.ListeningMode;

// Each report uses the same 0x0D value as the AAP packet. Explicit time keeps replay independent of scheduling.
static void Report(DeviceState state, ListeningMode mode, long at) =>
    state.ReportListeningMode((byte)mode, at);

static void Expect(DeviceState state, ListeningMode? shown, ListeningMode? reported, bool pending)
{
    if (state.ListeningModeSelection != new DeviceState.ModeSelection(shown, pending)
        || state.ReportedListeningMode != reported
        || state.GetPodSettings(PodSettings.None).ListeningMode != reported)
    {
        throw new InvalidOperationException(
            $"Expected shown={shown}, reported={reported}, pending={pending}. "
            + $"Actual {state.ListeningModeSelection}, reported={state.ReportedListeningMode}.");
    }
}

var state = new DeviceState(0, 0, PodSettings.None);
Aap.Apply(Aap.Setting(Aap.ListeningModeId, (byte)Transparency), state);
Expect(state, Transparency, Transparency, pending: false);

// An out-of-order reply to the older write can arrive over a second after the newest was confirmed.
state.PushListeningMode((byte)Adaptive, 100);
state.PushListeningMode((byte)NoiseCancellation, 1200);
Expect(state, NoiseCancellation, Transparency, pending: true);
Report(state, NoiseCancellation, 1700);
Expect(state, NoiseCancellation, NoiseCancellation, pending: false);
Report(state, Adaptive, 2750);
Expect(state, NoiseCancellation, NoiseCancellation, pending: false);
Report(state, Adaptive, 2800); // A physical change is accepted after the one expected late echo.
Expect(state, Adaptive, Adaptive, pending: false);

// Deferred reply budget expires, so physical stem changes to skipped modes still work.
state.PushListeningMode((byte)Transparency, 4000);
state.PushListeningMode((byte)NoiseCancellation, 5000);
Report(state, NoiseCancellation, 5500);
Report(state, Transparency, 7000);
Expect(state, Transparency, Transparency, pending: false);

// A report for an earlier write becomes the confirmed value without replacing the newer selection.
state.PushListeningMode((byte)Adaptive, 8000);
int failed = state.PushListeningMode((byte)NoiseCancellation, 9100);
Report(state, Adaptive, 9200);
Expect(state, NoiseCancellation, Adaptive, pending: true);
state.DropListeningMode(failed);
Expect(state, Adaptive, Adaptive, pending: false);

// A remote mode may report during a write. On timeout the page returns to the latest actual report.
state.PushListeningMode((byte)Transparency, 10000);
Report(state, Off, 10700);
Expect(state, Transparency, Off, pending: true);
if (state.ExpireListeningModes(12999) != (false, 1)
    || state.ExpireListeningModes(13000) != (true, 0))
    throw new InvalidOperationException("Dispatch expires at the wrong time.");
Expect(state, Off, Off, pending: false);

// A new session may remember a mode while checking, but cannot inherit the prior session's requests.
state.PushListeningMode((byte)Transparency, 14000);
var reattached = new DeviceState(0, 0, state.GetPodSettings(PodSettings.None));
reattached.CarryOver(state);
Expect(reattached, null, null, pending: false);
if (reattached.RememberedSettings.ListeningMode != Off)
    throw new InvalidOperationException("The pending selection leaked into the remembered settings.");

Console.WriteLine("Listening mode replays passed.");

static byte[] Identify(string model, string build) =>
    [4, 0, 4, 0, 0x1D, 0, 0, 0, 0, 0, 0,
        .. Encoding.UTF8.GetBytes($"Test AirPods\0{model}\0Vendor\0redacted\0{build}")];

var identified = new DeviceState(0, 0, PodSettings.None);
Aap.Apply(Identify("A3063", "91.3431046004000000.29\0"), identified);
if (identified.ModelName != "AirPods Pro 3" || identified.FirmwareBuild != "91.3431046004000000.29")
    throw new InvalidOperationException("The reported internal build should be parsed with its full precision.");
if (Aap.Apply(Identify("A3063", "91.3431046004000000.29\0"), identified) != DeviceChange.None)
    throw new InvalidOperationException("An identical firmware report should not redraw device information.");
if ((Aap.Apply(Identify("A3063", "92.02.1\0"), identified) & DeviceChange.Identity) == 0
    || identified.FirmwareBuild != "92.02.1")
    throw new InvalidOperationException("A firmware-only change should refresh device information.");
Aap.Apply(Identify("A3063", "garbage\0"), identified);
Aap.Apply(Identify("A3063", "93.01.1"), identified);
if (identified.FirmwareBuild != "92.02.1")
    throw new InvalidOperationException("A malformed or truncated build should keep the previous reading.");
Aap.Apply(Identify("A2084", "\0"), identified);
if (identified.FirmwareBuild is not null)
    throw new InvalidOperationException("The previous model's firmware must not leak to a new identity.");
Aap.Apply(Identify("A2084", "7B21\0"), identified);
if (identified.FirmwareBuild != "7B21")
    throw new InvalidOperationException("Short firmware revisions should be accepted.");
Console.WriteLine("Device identity replays passed.");

var modeSets = new (string Model, ListeningMode[] Supported)[]
{
    ("A1523", []), ("A2032", []), ("A2565", []), ("A3053", []),
    ("A2084", [Off, Transparency, NoiseCancellation]), ("A2096", [Off, Transparency, NoiseCancellation]),
    ("A3056", [Off, Transparency, NoiseCancellation, Adaptive]), ("A3531", [Off, Transparency, NoiseCancellation, Adaptive]),
    ("A3439", [Off, Transparency, NoiseCancellation, Adaptive]), ("A3454", [Off, Transparency, NoiseCancellation, Adaptive]),
    ("A2931", [Transparency, NoiseCancellation, Adaptive]), ("A3047", [Transparency, NoiseCancellation, Adaptive]),
    ("A3063", [Transparency, NoiseCancellation, Adaptive]),
};
foreach (var (model, supported) in modeSets)
{
    var pair = new DeviceState(0, Models.FromModelNumber(model)!.Capabilities, PodSettings.None);
    pair.SetSetting(Aap.AllowOffId, Aap.Bool(false));
    var actual = Enum.GetValues<ListeningMode>().Where(pair.SupportsListeningMode).ToHashSet();
    if (!actual.SetEquals(supported))
        throw new InvalidOperationException($"Incorrect modes for {model}: {string.Join(", ", actual)}");
    if ((pair.Capabilities & DeviceCapabilities.AllowOff) == 0) continue;
    pair.SetSetting(Aap.AllowOffId, Aap.Bool(true));
    if (!pair.SupportsListeningMode(Off))
        throw new InvalidOperationException($"Opt-in to Off failed for {model}");
}
Console.WriteLine("Listening mode capability matrix passed for 13 model variants.");

var advertisement = new byte[27];
var leftDocked = new PodBatteries(new Battery(88, BatteryStatus.Charging), new Battery(87, BatteryStatus.Discharging), default, default);
advertisement[5] = 0x50 | 0x20;
if (!Proximity.HasDockedEarbudSignal(advertisement, leftDocked))
    throw new InvalidOperationException("The single transmitting bud in its slot must give a case signal.");
advertisement[5] = 0x10 | 0x20;
if (Proximity.HasDockedEarbudSignal(advertisement, leftDocked))
    throw new InvalidOperationException("An advert about the other bud must not locate the case.");
advertisement[5] = 0x50;
if (Proximity.HasDockedEarbudSignal(advertisement, leftDocked))
    throw new InvalidOperationException("A contradictory report must not locate the case.");
var rightDocked = leftDocked with { Right = new Battery(100, BatteryStatus.Discharging) };
if (!Proximity.HasDockedEarbudSignal(advertisement, rightDocked))
    throw new InvalidOperationException("A fully charged transmitting bud can still be docked.");
Console.WriteLine("Verified the case signal accepts a known docked transmitter without attributing the undocked bud's signal to the case.");

// Payloads from the original Pro 3 case measurements, re-encrypted with dummy keys.
// https://github.com/stuffz/airpods/blob/main/docs/ble-research.md#case-controlled-changes
using var caseKey = Proximity.CreateCipher(new byte[16]);
using var unrelatedKey = Proximity.CreateCipher(Enumerable.Repeat((byte)0x11, 16).ToArray());
byte[] CasePacket(string hex)
{
    var packet = new byte[19];
    packet[0] = 0x07;
    packet[1] = 0x11;
    packet[2] = 0x06;
    caseKey.EncryptEcb(Convert.FromHexString(hex), packet.AsSpan(3), PaddingMode.None);
    return packet;
}
void ExpectCase(string hex, Battery left, Battery right, Battery @case)
{
    byte[] packet = CasePacket(hex);
    if (!Proximity.TryReadCaseBroadcast(packet, caseKey, out var observed, out bool header) || !header ||
        observed != new PodBatteries(left, right, @case, default))
        throw new InvalidOperationException($"Case packet decoded the wrong battery levels: {hex} -> {observed}.");
    if (Proximity.TryReadCaseBroadcast(packet, unrelatedKey, out _, out _))
        throw new InvalidOperationException("A different paired key decoded the case packet.");
}
var l94 = new Battery(94, BatteryStatus.Charging);
var r93 = new Battery(93, BatteryStatus.Charging);
var c61 = new Battery(61, BatteryStatus.Discharging);
ExpectCase("29200A3DDEDD510B000E0000B6841600", l94, r93, c61); // Closed lid.
ExpectCase("292008BDDEDD510B0000000025831600", l94, r93, c61 with { Status = BatteryStatus.Charging });
ExpectCase("29200B3DDEFF510B00050000DA841600", l94, default, c61); // Only the right bud was removed.
ExpectCase("2920093DFFFF510B0000000040831600", default, default, c61);
ExpectCase("29200A64E4E45A8600000000F2720000", new(100, BatteryStatus.Charging), new(100, BatteryStatus.Charging), new(100, BatteryStatus.Discharging));
ExpectCase("292009FF8000510B0000000045831600", new(0, BatteryStatus.Charging), new(0, BatteryStatus.Discharging), default);
if (Proximity.TryReadCaseBroadcast(CasePacket("2920093D65DD510B0000000045831600"), caseKey, out var invalid, out _) || invalid != default)
    throw new InvalidOperationException("An invalid earbud percent escaped the case layout checks.");
if (Proximity.TryReadCaseBroadcast([0x07, 0x11, 0x06], caseKey, out _, out _))
    throw new InvalidOperationException("A truncated case broadcast was accepted.");
Console.WriteLine("Verified Pro 3 case batteries from encrypted captures, including closed lid, empty slots, and pair isolation.");

namespace WPods.Core
{
    internal static class Log
    {
        public static void Info(string category, string message) { }
        public static void Warn(string category, string message) { }
    }
}
