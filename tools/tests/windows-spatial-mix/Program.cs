using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WPods.Audio;

static void Check(bool pass, string issue) { if (!pass) throw new Exception(issue); }
if (args.Contains("--quiet-stream"))
{
    using var output = new WasapiOut(AudioClientShareMode.Shared, true, 30);
    output.Init(new SignalGenerator(48000, 2) { Gain = .0008, Frequency = 320, Type = SignalGeneratorType.Sin }.ToWaveProvider());
    output.Play();
    Thread.Sleep(450);
    return;
}
if (args is ["--virtual-tone", var frequencyText] && int.TryParse(frequencyText, out int frequency))
{
    using var devices = new MMDeviceEnumerator();
    using var endpoint = FindTestEndpoint(devices);
    using var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 40);
    output.Init(new SignalGenerator(SpatialStereo.Rate, 2)
        { Gain = .0008, Frequency = frequency, Type = SignalGeneratorType.Sin }.ToWaveProvider());
    output.Play();
    Thread.Sleep(1200);
    return;
}
if (args.Contains("--virtual-loopback"))
{
    // Send a quiet test signal only to the isolated virtual render device.
    // Keep every Windows default output exactly as it was.
    using var devices = new MMDeviceEnumerator();
    using var endpoint = FindTestEndpoint(devices);
    string?[] defaults = Enumerable.Range(0, 3).Select(AudioEndpoints.DefaultPlaybackRole).ToArray();
    Check(defaults.All(id => !WindowsSpatialMix.Same(id, endpoint.ID)), "Virtual device is a Windows default.");
    Check(Process.GetProcessesByName("AudioRelay").Length == 0, "AudioRelay is running.");
    var sessions = endpoint.AudioSessionManager.Sessions;
    for (int i = 0; i < sessions.Count; i++)
    {
        using var session = sessions[i];
        Check(session.AudioMeterInformation.MasterPeakValue == 0, "A player has audible output on the virtual device.");
    }
    using var input = new VirtualCapture(endpoint) { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SpatialStereo.Rate, 2) };
    int frames = 0;
    input.DataAvailable += (_, data) =>
    {
        var values = MemoryMarshal.Cast<byte, float>(data.Buffer.AsSpan(0, data.BytesRecorded));
        if (values.ContainsAnyExcept(0f)) Interlocked.Add(ref frames, values.Length / 2);
    };
    input.StartRecording();
    foreach (int tone in new[] { 320, 640, 960 })
    {
        int before = Volatile.Read(ref frames);
        using var app = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--virtual-tone {tone}") { UseShellExecute = false });
        Check(app != null && app.WaitForExit(6000) && app.ExitCode == 0, "Independent audio process failed.");
        Thread.Sleep(80);
        Check(Volatile.Read(ref frames) - before > SpatialStereo.Rate * .7, "An app's stream was lost after handoff.");
    }
    input.StopRecording();
    Check(defaults.SequenceEqual(Enumerable.Range(0, 3).Select(AudioEndpoints.DefaultPlaybackRole)), "The test changed a Windows default.");
    Console.WriteLine($"Three independent playback processes reached one uninterrupted WASAPI loopback ({frames} non-silent frames). Windows defaults verified.");
    return;
}
var saved = new WindowsSpatialMix.Saved("virtual", "buds", "buds", "call");
var queue = new SpatialPlaybackBuffer(100);
static float[] Frames(float value, int count) => Enumerable.Range(0, count * 2).Select(i => i % 2 == 0 ? value : -value).ToArray();
queue.Push(Frames(.1f, 100));
queue.Read(new float[100]);
queue.Push(Frames(.5f, 120));
var resumed = new float[340];
queue.Read(resumed);
Check(resumed[0] == .1f && resumed[1] == -.1f && resumed[100] == .5f && resumed[101] == -.5f,
    "An overrun must blend into the newest complete stereo frames");
Check(resumed[200] == .5f && resumed[201] == -.5f && !resumed.AsSpan(296).ContainsAnyExcept(0f),
    "An underrun must fade to silence");
queue.Push(Frames(-.5f, 60));
queue.Read(resumed);
Check(resumed[0] == 0 && resumed[100] == -.5f && resumed[101] == .5f, "Audio must ramp back in after a gap");
long? quiet = SpotifyBridge.ResumeWindow(1000, null, callAudio: true, captureActive: false);
Check(quiet is null, "Call audio must prevent spatial resume");
quiet = SpotifyBridge.ResumeWindow(2000, quiet, callAudio: false, captureActive: true);
Check(quiet is null, "Microphone still in use must prevent spatial resume");
quiet = SpotifyBridge.ResumeWindow(3000, quiet, callAudio: false, captureActive: false);
Check(quiet == 3000 && SpotifyBridge.ResumeWindow(4000, quiet, false, false) == quiet, "Quiet window must begin after both inputs clear");
quiet = SpotifyBridge.ResumeWindow(4100, quiet, callAudio: true, captureActive: false);
Check(quiet is null, "A second call must restart the quiet window");
Check(SpotifyBridge.CanRetryResume(2000, 3000, 1000), "A missing endpoint must leave room for another reconnect attempt");
Check(!SpotifyBridge.CanRetryResume(3000, 3000, 2200), "Reconnect retries must stop at the original deadline");
Check(SpotifyBridge.CanRetryResume(4000, 0, 3000), "A call can resume after its quiet window without a device reconnect deadline");
Check(!SpotifyBridge.CanRetryResume(13000, 0, 3000) && !SpotifyBridge.CanRetryResume(4000, 0, null),
    "An unavailable endpoint must stop retrying, and an active call must block automatic startup");
var paths = SpatialApp.PlaybackPaths([
    new("", @"C:\Audio\Spotify.exe", "Virtual output", "sink"),
    new("", @"C:\Audio\Spotify.exe", "Virtual output", "sink"),
    new("", @"C:\Audio\Spotify.exe", "Headphones", "Pair"),
    new("", @"C:\Audio\msedge.exe", "Headphones", "desk"),
    new("", @"C:\Audio\Pairside.exe", "Headphones", "Pair")
], "sink", "pair");
Check(paths.Length == 3 && paths[0].Path == "spatial" && paths[1].Path == "direct" && paths[2].Path == "other",
    "Route diagnostics must use endpoint IDs, distinguish duplicates and exclude Parity");
Check(WindowsSpatialMix.MatchesMedia(saved, r => r == 2 ? "new call headset" : "buds") &&
    !WindowsSpatialMix.MatchesMedia(saved, r => r == 1 ? "new music output" : "buds"), "Resume must respect changes to media roles");
var appSnapshot = new SpotifyRouting.Saved("sink", null, "before");
Check(SpotifyRouting.MatchesMedia(appSnapshot, r => r == 1 ? "BEFORE" : null) &&
    !SpotifyRouting.MatchesMedia(appSnapshot, r => r == 0 ? "user choice" : "before"), "Resume must respect changes to application roles");
var check = new SpotifyBridge.SpeakerCheck();
var testSignal = new float[SpatialStereo.Rate * 3];
check.Start();
for (int i = 0; i < testSignal.Length; i += 480)
{
    if (i == 480) check.Start(); // Repeated clicks must not interrupt a playing tone.
    check.Mix(testSignal.AsSpan(i, Math.Min(480, testSignal.Length - i)));
}
for (int pos = 0; pos < 3; pos++)
{
    var segment = testSignal.AsSpan(pos * SpatialStereo.Rate, 16000);
    double left = 0, right = 0;
    for (int i = 0; i < segment.Length; i += 2)
    {
        left += segment[i] * segment[i];
        right += segment[i + 1] * segment[i + 1];
    }
    Check(pos switch { 0 => left > 1 && right == 0, 1 => left > 1 && left == right, _ => right > 1 && left == 0 }, "Speaker check channel order");
}
Check(!testSignal.AsSpan(2 * SpatialStereo.Rate + 16000).ContainsAnyExcept(0f), "Speaker check did not end");
var untouched = Enumerable.Repeat(.05f, 240).ToArray();
check.Mix(untouched);
Check(!untouched.AsSpan().ContainsAnyExcept(.05f), "Idle speaker check modified playback");
string?[] roles = ["buds", "buds", "call"];
WindowsSpatialMix.Activate(saved, r => roles[r], (r, v) => roles[r] = v);
Check(roles.SequenceEqual(new[] {"virtual", "virtual", "call"}), "Default media activation changed calls");
roles[0] = "speakers";
WindowsSpatialMix.RestoreOwned(saved, r => roles[r], (r, v) => roles[r] = v, ["speakers","buds","virtual","call"]);
Check(roles.SequenceEqual(new[] {"speakers","buds","call"}), "User's output change lost");
roles = ["speakers", "virtual", "call"];
WindowsSpatialMix.RestoreOwned(saved, r => roles[r], (r, v) => {
    roles[r] = v;
    if (r == 1) roles[0] = roles[2] = v;
}, ["speakers","buds","virtual","call"]);
Check(roles.SequenceEqual(new[] {"speakers","buds","call"}), "A grouped media write overwrote independent output choices");
roles = ["speakers", "virtual", "call"];
bool wroteCalls = false;
WindowsSpatialMix.RestoreOwned(saved, r => roles[r], (r, v) => {
    roles[r] = v;
    if (r == 1 && !wroteCalls) roles[2] = v;
    if (r == 2 && !wroteCalls) { wroteCalls = true; roles[1] = v; }
}, ["speakers","buds","virtual","call"]);
Check(roles.SequenceEqual(new[] {"speakers","buds","call"}), "A later grouped call write changed an already restored media role");
roles = ["virtual", "virtual", "call"];
WindowsSpatialMix.RestoreOwned(saved, r => roles[r], (r, v) => roles[r] = v, ["virtual","call"]);
Check(roles.All(v => v == "call"), "An unplug left Windows pointing at a silent virtual endpoint");
roles = ["buds", "changed", "call"];
try { WindowsSpatialMix.Activate(saved, r => roles[r], (_, _) => throw new Exception("Changed a superseded default")); }
catch (InvalidOperationException e) when (e.Message == "usePairDefault") { }
Console.WriteLine("Passed media-only routing, communications isolation, user change, disconnect and superseded output checks.");

if (args.Contains("--live-mix"))
{
    if (WindowsSpatialMix.Pending) throw new Exception("Existing mix route owns the Windows output.");
    string?[] before = Enumerable.Range(0, 3).Select(AudioEndpoints.DefaultPlaybackRole).ToArray();
    var original = AudioEndpoints.ReadDefaults().Output ?? throw new Exception("No Windows playback output.");
    Console.WriteLine($"Testing live shared playback on {original.Name}.");
    using (var bridge = new SpotifyBridge(SpatialApp.WindowsMix, original.Id, 0))
    {
        bridge.Metering = true;
        Check(WindowsSpatialMix.Same(AudioEndpoints.DefaultPlaybackRole(1), bridge.SinkId), "Media role did not move.");
        Check(AudioEndpoints.DefaultPlaybackRole(2) == before[2], "Communications role changed.");
        using var player = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--quiet-stream") {UseShellExecute = false});
        Check(player != null && player.WaitForExit(4000) && player.ExitCode == 0, "Independent audio process failed");
        Check(bridge.HasSignal && !bridge.Failed, "The spatial renderer did not receive audio.");
    }
    Check(before.SequenceEqual(Enumerable.Range(0, 3).Select(AudioEndpoints.DefaultPlaybackRole)) && !WindowsSpatialMix.Pending,
        "Default outputs were not fully restored.");
    Console.WriteLine("Live Windows playback, a separate app's signal, and exact restoration of all three output roles passed.");
}

static MMDevice FindTestEndpoint(MMDeviceEnumerator devices) =>
    devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
        .FirstOrDefault(device => device.FriendlyName.Contains("Virtual Speakers for AudioRelay", StringComparison.OrdinalIgnoreCase))
        ?? throw new Exception("Spatial virtual playback device unavailable.");

sealed class VirtualCapture(MMDevice endpoint) : WasapiCapture(endpoint, true, 24)
{
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
}
