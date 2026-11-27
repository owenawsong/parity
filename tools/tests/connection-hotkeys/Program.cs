using WPods.Core;
using WPods.Ui;

string[] on = ["connection.on"], off = ["connection.off"], both = [..on, ..off];
void Check(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException(scenario);
    Console.WriteLine(scenario);
}

Check(ConnectionShortcut.Target(2, 1, [3], [1, 2]) == 2, "Disconnect targets the attached pair");
Check(ConnectionShortcut.Target(0, 1, [], [2, 1]) == 1, "A remembered offline pair is selected before the first listed pair");
Check(ConnectionShortcut.Target(0, 9, [], [2, 1]) == 2, "An unpaired device is skipped");
Check(ConnectionShortcut.Target(0, 1, [3], [1, 3]) == 3, "Disconnect targets the pending pair before the previously used one");
Check(ConnectionShortcut.Target(0, 0, [], []) == 0, "No paired AirPods yields no target");
var timeline = new ConnectionTimeline();
timeline.Record(1, ConnectionEvent.LinkUp);
timeline.Record(1, ConnectionEvent.MotionMissing);
timeline.Record(2, ConnectionEvent.ControlsReady);
timeline.Record(1, ConnectionEvent.MotionMissing);
timeline.Record(1, ConnectionEvent.CalibrationTimedOut);
var events = timeline.Read(1);
Check(events.Length == 3 && events[0].Kind == ConnectionEvent.CalibrationTimedOut &&
    events[1].Kind == ConnectionEvent.MotionMissing && events[1].Count == 2 && events[2].Kind == ConnectionEvent.LinkUp,
    "Connection history groups repeated failures for the selected pair and preserves milestone order");
Check(timeline.Read(2).Single().Kind == ConnectionEvent.ControlsReady && timeline.Read(0).Length == 0 &&
    events.All(entry => entry.AgeSeconds >= 0), "Connection history isolates pairs and returns relative event ages");
for (int i = 0; i < 100; i++) timeline.Record(1, i % 2 == 0 ? ConnectionEvent.LinkUp : ConnectionEvent.LinkDown);
Check(timeline.Read(1).Length == 8 && timeline.Read(2).Length == 0, "Connection history bounds storage and the displayed result");
Check(ConnectionShortcut.Resolve(on, false, false) == ConnectionIntent.Connect, "Connect works while offline");
Check(ConnectionShortcut.Resolve(on, true, false) == ConnectionIntent.AlreadyConnected, "A second connect is idempotent");
Check(ConnectionShortcut.Resolve(off, false, false) == ConnectionIntent.AlreadyDisconnected, "A second disconnect is idempotent");
Check(ConnectionShortcut.Resolve(off, false, true) == ConnectionIntent.Cancel, "Explicit disconnect cancels the current request");
Check(ConnectionShortcut.Resolve(both, false, false) == ConnectionIntent.Connect, "Shared shortcut connects");
Check(ConnectionShortcut.Resolve(both, false, true) == ConnectionIntent.Wait, "Repeated shared shortcut never starts another pending request");
Check(ConnectionShortcut.Resolve(both, true, false) == ConnectionIntent.Disconnect, "Shared shortcut disconnects");

var host = new HostWindow();
var keys = new Hotkeys(host);
var chord = new Shortcut(82, 3);
var existing = new Shortcut(75, 2);
var bindings = Hotkeys.Effective(new HotkeyConfig(new Dictionary<string, Shortcut>
{
    ["window.toggle"] = existing, ["connection.off"] = chord, ["connection.on"] = chord,
}, false));
keys.Apply(bindings);
Check(bindings.Count == 3 && keys.Bindings["window.toggle"] == existing, "Custom shortcuts survive");
Check(host.Registered.Count == 2 && keys.StatusOf(on[0]) == HotkeyStatus.Toggle && keys.StatusOf(off[0]) == HotkeyStatus.Toggle, "Windows registers a shared key only once");
int id = host.Registered.Single(kv => kv.Value == chord).Key;
var groups = keys.Resolve(id, out bool conflicts);
Check(!conflicts && groups.Count == 1 && groups[0].SequenceEqual(both), "The registered key dispatches a single connection group");
var defaults = Hotkeys.Effective(new HotkeyConfig(null, true));
Check(!defaults.ContainsKey(on[0]) && !defaults.ContainsKey(off[0]) && defaults["lowLatencyMic.on"] == defaults["lowLatencyMic.off"] && !defaults.ContainsKey("lowLatency.on"), "New connection keys are opt-in and the default latency chord targets the mic");
const string recenter = "spatial.recenter";
Check(!defaults.ContainsKey(recenter), "Recenter never reserves a global shortcut by default");
var spatialBindings = Hotkeys.Effective(new HotkeyConfig(new Dictionary<string, Shortcut>
{
    [recenter] = chord,
}, false));
keys.Apply(spatialBindings);
int spatialId = host.Registered.Single(kv => kv.Value == chord).Key;
var spatialActions = keys.Resolve(spatialId, out bool spatialConflict);
Check(!spatialConflict && spatialActions.Count == 1 && spatialActions[0].SequenceEqual([recenter]),
    "An assigned recenter shortcut dispatches the spatial action");
