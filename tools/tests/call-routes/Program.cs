using WPods.Audio;

static void Check(bool result, string message)
{
    if (!result) throw new InvalidOperationException(message);
}

AudioEndpoints.SpotifyRoute = null;
Check(SpatialApp.FromKey("unknown") == null, "Unrecognized spatial player accepted.");
Check(SpatialApp.FromMediaSession("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic") == "mediaPlayer", "Packaged Media Player metadata missing.");
Check(SpatialApp.FromMediaSession(@"C:\Apps\VLC\vlc.exe") == "vlc", "Classic VLC metadata missing.");
Check(SpatialApp.FromMediaSession("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == "spotify", "Packaged Spotify missing.");
Check(SpatialApp.FromMediaSession("Microsoft.ZuneMusic_somethingElse!Fake") == null, "Unrelated player metadata leaked.");
Check(SpatialApp.FromMediaSession("chrome.exe") == null, "Browser metadata leaked into a selected player's room.");
Check(SpatialApp.FromKey("windowsMix")?.IsWindowsMix == true && !SpatialApp.Supported.Any(a => a.IsWindowsMix), "Windows mix treated as a per-app route.");
Check(SpatialApp.FromMediaSession("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App") == "appleMusic", "Apple Music metadata was lost.");
var players = SpatialApp.AppsFromSessions([
    new("", @"C:\Apps\Edge\msedge.exe", "USB speakers"),
    new("", @"c:\apps\Edge\MSEDGE.EXE", "Other speakers"),
    new("", @"D:\Portable\msedge.exe", "USB speakers"),
    new("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", @"C:\WindowsApps\AppleMusic.exe", "Headphones"),
    new("", @"C:\Windows\system32\svchost.exe", "Headphones"),
    new("", @"C:\Apps\Pairside.exe", "Headphones")
]);
var edges = players.Where(p => p.DisplayName == "Microsoft Edge").ToArray();
Check(edges.Length == 2 && edges[0].Key != edges[1].Key, "Browser processes were merged by name or duplicated by output.");
Check(players.Count(a => a.Key == "appleMusic") == 1 && players.All(a => a.ProcessName != "svchost" && a.ProcessName != "Pairside"), "Duplicate or excluded source offered.");
Check(SpatialApp.FromKey(edges[0].Key) == edges[0] && SpatialApp.FromKey(@"C:\Apps\Arbitrary.exe") == null, "Selection failed to enforce the source registry.");
Check(SpatialApp.RestoreDescriptor(edges[0].Key, edges[0].ProcessName, edges[0].Identity) == edges[0], "Restart lost the selected portable player.");
Check(SpatialApp.RestoreDescriptor(edges[0].Key, edges[0].ProcessName, edges[1].Identity) == null, "Recovery accepted another executable's identity.");
Console.WriteLine("Source discovery passed for packaged media, multi-process browsers, portable copies and system exclusions.");
uint external = (uint)Environment.ProcessId + 1;
Check(AudioEndpoints.IsExternalActiveSession(1, external), "An active external input was lost.");
Check(!AudioEndpoints.IsExternalActiveSession(0, external), "An idle app was presented as recording.");
Check(!AudioEndpoints.IsExternalActiveSession(2, external), "An expired session was presented as recording.");
Check(!AudioEndpoints.IsExternalActiveSession(1, (uint)Environment.ProcessId), "Parity listed its own test.");
Check(!AudioEndpoints.IsExternalActiveSession(1, 0), "A system session was identified as a call app.");
Check(AudioEndpoints.CaptureAppName(@"C:\Apps\ms-teams.exe", "") == "Microsoft Teams", "Unexpected call app label.");
Check(AudioEndpoints.CaptureAppName(@"C:\Apps\Audio Lab.exe", "") == "Audio Lab", "Executable path escaped into the UI.");
Check(AudioEndpoints.CaptureAppName("", "Example.App") == "Example.App", "Packaged app identity was lost.");
Console.WriteLine("Passed active, idle, expired, self and system session checks and capture app labels.");

const string sink = "spatial bridge", speakers = "desk speakers", buds = "pair output", hdmi = "HDMI";
string?[] roles = [sink, sink];
int writes = 0;
void Restore(string? originalConsole, string? originalMedia) =>
    SpotifyRouting.RestoreOwnedRoles(sink, originalConsole, originalMedia, r => roles[r],
        (r, value) => { writes++; roles[r] = value; });
Restore(null, null);
Check(roles.All(r => r == null) && writes == 2, "Call handoff did not return both player roles to follow Windows.");
roles = [sink, hdmi];
writes = 0;
Restore(speakers, buds);
Check(roles[0] == speakers && roles[1] == hdmi && writes == 1, "A changed media output was overwritten at handoff.");
roles = [hdmi, buds];
writes = 0;
Restore(speakers, buds);
Check(roles[0] == hdmi && roles[1] == buds && writes == 0, "Retry overwrote a later selection.");

Check(AudioEndpoints.ComparePlaybackChoice(speakers, buds, speakers) == AudioEndpoints.PlaybackChange.Pending, "Fresh selection was denied.");
Check(AudioEndpoints.ComparePlaybackChoice(speakers, buds, buds) == AudioEndpoints.PlaybackChange.Selected, "AirPods were already selected.");
Check(AudioEndpoints.ComparePlaybackChoice(speakers, buds, hdmi) == AudioEndpoints.PlaybackChange.Superseded, "Stale request overwrote a new media output.");
Check(AudioEndpoints.ComparePlaybackChoice(speakers, buds, null) == AudioEndpoints.PlaybackChange.Superseded, "Disconnected output was treated as the original.");
Check(AudioEndpoints.ComparePlaybackChoice(null, buds, null) == AudioEndpoints.PlaybackChange.Pending, "A call mic cannot be chosen when Windows has no call default.");
Check(AudioEndpoints.ComparePlaybackChoice(null, buds, speakers) == AudioEndpoints.PlaybackChange.Superseded, "A call mic choice would replace a newer valid default.");
int? Channels(string id) => id == buds ? 2 : id == "call" ? 1 : null;
Check(AudioEndpoints.SelectPairRenderEndpoint([buds, "call"], "call", Channels, false) == "call", "The user's default playback should remain selected for general audio.");
Check(AudioEndpoints.SelectPairRenderEndpoint([buds, "call"], "call", Channels, true) == buds, "Spatial playback chose a mono Hands-Free endpoint.");
Check(AudioEndpoints.SelectPairRenderEndpoint([buds, "call"], null, Channels, false) == buds, "The stereo endpoint was lost when Windows used another device.");
Check(AudioEndpoints.SelectPairRenderEndpoint([buds, "unknown"], null, Channels, true) == null, "An ambiguous endpoint was selected automatically.");
Check(AudioEndpoints.SelectPairRenderEndpoint(["call"], null, Channels, true) == null, "A mono-only device was offered spatial playback.");
Check(AudioEndpoints.OutputSilence(true, 0f) == AudioEndpoints.PlaybackSilence.Muted, "Mute should explain a zero volume on a muted output.");
Check(AudioEndpoints.OutputSilence(false, 0f) == AudioEndpoints.PlaybackSilence.ZeroVolume, "Zero Windows master volume was lost.");
Check(AudioEndpoints.OutputSilence(false, .001f) == AudioEndpoints.PlaybackSilence.None, "Quiet playback was mistaken for silence.");
Console.WriteLine("Passed call handoff, per-role recovery, repeated restoration, and post-call output reselection.");

var music = new AudioEndpoints.MediaPlayerRoute("Spotify", true, false);
var browser = new AudioEndpoints.MediaPlayerRoute("msedge", true, false);
AudioEndpoints.PlayerStream[] split = [new("", @"C:\Apps\Spotify.exe", true, true), new("", @"C:\Apps\msedge.exe", false, true)];
Check(AudioEndpoints.SelectPairPlayer(split, [browser, music], true) == 1, "A gesture selected a player on different speakers.");
Check(AudioEndpoints.SelectPairPlayer(split, [music, music with { Playing = false, Paused = true }], true) == -1,
    "Two player sessions with the same app identity were treated as one.");
var paused = music with { Playing = false, Paused = true };
AudioEndpoints.PlayerStream[] parked = [split[0] with { Active = false }, split[1]];
Check(AudioEndpoints.SelectPairPlayer(parked, [paused, browser], false) == -1 &&
    AudioEndpoints.SelectPairPlayer(parked, [paused, browser], true) == 0, "Paused playback was selected for the wrong action.");
Check(AudioEndpoints.SelectPairPlayer([split[0], split[1] with { OnPair = true }], [music, browser], true) == -1,
    "A gesture targeted one of two streams playing through the AirPods.");
Check(AudioEndpoints.SelectPairPlayer([split[0], split[0] with { OnPair = false }], [music], true) == -1,
    "A player with conflicting active outputs was treated as safe to control.");
Console.WriteLine("Passed player routing for head gestures and in-ear playback, including ambiguous and paused sessions.");

if (args.Contains("--windows-routes"))
{
    var routes = AudioEndpoints.ReadDefaults();
    var apps = AudioEndpoints.ActiveMicrophones();
    Check(apps.Distinct().Count() == apps.Length, "Duplicate app and endpoint pair.");
    Check(apps.All(a => a.App.Length > 0 && a.Endpoint.Length > 0 && a.Input.Length > 0), "Incomplete active route.");
    Console.WriteLine($"Windows endpoint metadata: media output={routes.Output != null}, call output={routes.CommunicationsOutput != null}, call input={routes.CommunicationsInput != null}, active app/input pairs={apps.Length}.");
    var discovered = SpatialApp.DiscoverPlaybackApps();
    Console.WriteLine("Windows spatial playback choices: " + string.Join(", ", discovered.Select(s => s.DisplayName)));
}

if (args.Contains("--spatial-policy"))
{
    // Touch only this test executable's per-app Windows mixer settings.
    var app = new SpatialApp("parity-route-regression", "CallRoutes");
    var output = AudioEndpoints.ReadDefaults().Output ?? throw new Exception("No playback device.");
    int pid = Environment.ProcessId;
    try
    {
        SpotifyRouting.Apply(app, pid, output.Id);
        Check(SpotifyRouting.Owns(pid, output.Id) && SpotifyRouting.Pending(app), "Windows did not apply the selected output.");
        try
        {
            SpotifyRouting.Restore(app, -1);
            throw new Exception("A different process cleared the recovery journal.");
        }
        catch (IOException) { }
        Check(SpotifyRouting.Pending(app), "Wrong identity discarded the recovery journal.");
        Check(SpotifyRouting.RestoreAvailable(app), "Recovery failed to find the original app.");
    }
    finally { SpotifyRouting.Restore(app, pid); }
    Check(!SpotifyRouting.Pending(app), "Route recovery is incomplete.");
    Console.WriteLine("Windows confirmed both per-app output roles and their restoration on the isolated test executable.");
}
