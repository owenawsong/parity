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

namespace WPods.Core
{
    internal static class Log
    {
        public static void Info(string category, string message) { }
        public static void Warn(string category, string message) { }
    }
}
