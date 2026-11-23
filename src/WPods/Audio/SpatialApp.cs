#if PAIRSIDE_LIVE
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WPods.Core;

namespace WPods.Audio;

internal sealed record SpatialApp(string Key, string ProcessName, string? Identity = null)
{
    internal static readonly SpatialApp Spotify = new("spotify", "Spotify");
    internal static readonly SpatialApp WindowsMix = new("windowsMix", "");
    internal bool IsWindowsMix => Key == WindowsMix.Key;
    internal static readonly SpatialApp[] Supported = [Spotify, new("mediaPlayer", "Microsoft.Media.Player"), new("vlc", "vlc"), new("appleMusic", "AppleMusic")];
    private static readonly object CatalogLock = new();
    private static Dictionary<string, SpatialApp> _available = [];
    private static readonly string SelectionFile = Path.Combine(AppIdentity.Profile, "spatial-source.json");
    private static SpatialApp? _remembered;
    private static bool _loadedSelection;

    internal string DisplayName => Key switch
    {
        "windowsMix" => "Windows playback", "spotify" => "Spotify", "vlc" => "VLC",
        "mediaPlayer" => "Windows Media Player", "appleMusic" => "Apple Music",
        _ => ProcessName.ToLowerInvariant() switch
        {
            "msedge" => "Microsoft Edge", "chrome" => "Google Chrome", "firefox" => "Firefox",
            "brave" => "Brave", "opera" => "Opera", "ms-teams" => "Microsoft Teams",
            "itunes" => "iTunes", _ => ProcessName
        }
    };

    internal static SpatialApp? FromKey(string? key)
    {
        if (key == WindowsMix.Key) return WindowsMix;
        var known = Array.Find(Supported, app => app.Key == key);
        if (known != null || !IsDynamicKey(key)) return known;
        lock (CatalogLock)
        {
            if (_available.TryGetValue(key!, out var app)) return app;
            if (!_loadedSelection)
            {
                _loadedSelection = true;
                try
                {
                    if (File.Exists(SelectionFile))
                    {
                        var saved = JsonSerializer.Deserialize<SpatialApp>(File.ReadAllText(SelectionFile));
                        _remembered = RestoreDescriptor(saved?.Key, saved?.ProcessName, saved?.Identity);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            }
            return _remembered?.Key == key ? _remembered : null;
        }
    }

    // Save just the explicitly selected app. Merely playing audio leaves no history on disk.
    internal static void RememberSelection(SpatialApp app)
    {
        lock (CatalogLock)
        {
            _remembered = app.Identity != null ? app : null;
            _loadedSelection = true;
            if (_remembered == null)
            {
                File.Delete(SelectionFile);
                return;
            }
            string temporary = SelectionFile + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(app));
            File.Move(temporary, SelectionFile, true);
        }
    }

    internal static SpatialApp[] DiscoverPlaybackApps() =>
        AppsFromSessions(AudioEndpoints.ReadSpatialSessions());

    internal sealed record PlaybackPath(string Key, string App, string Output, string Path);

    internal static PlaybackPath[] PlaybackPaths(IEnumerable<AudioEndpoints.SpatialSession> sessions, string sink, string pair) =>
        [.. sessions.Select(session => (Session: session, App: SessionApp(session)))
            .Where(item => item.App != null)
            .Select(item => new PlaybackPath(item.App!.Key, item.App.DisplayName, item.Session.Output,
                string.Equals(item.Session.Endpoint, sink, StringComparison.OrdinalIgnoreCase) ? "spatial" :
                string.Equals(item.Session.Endpoint, pair, StringComparison.OrdinalIgnoreCase) ? "direct" : "other"))
            .Distinct()
            .OrderBy(path => path.Path switch { "spatial" => 0, "direct" => 1, _ => 2 })
            .ThenBy(path => path.App, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(path => path.Output, StringComparer.CurrentCultureIgnoreCase)];

    internal static SpatialApp[] AppsFromSessions(IEnumerable<AudioEndpoints.SpatialSession> sessions)
    {
        var found = new Dictionary<string, SpatialApp>();
        foreach (var session in sessions)
        {
            var app = SessionApp(session);
            if (app?.Identity != null) found.TryAdd(app.Key, app);
        }
        lock (CatalogLock) _available = found;
        return [.. Supported, .. found.Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Key)];
    }

    internal static SpatialApp? SessionApp(AudioEndpoints.SpatialSession session)
    {
        string process = Path.GetFileNameWithoutExtension(session.Image);
        if (string.IsNullOrWhiteSpace(process) || process.ToLowerInvariant() is
            "audiodg" or "svchost" or "system" or "msedgewebview2" or "audiorelay" or "wpods" or "pairside") return null;
        var known = Array.Find(Supported, app => app.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase));
        if (known != null) return known;
        string identity = string.IsNullOrEmpty(session.AppId) ? session.Image : session.AppId;
        return new SpatialApp(DynamicKey(identity), process, identity);
    }

    private static string DynamicKey(string identity) => "app-" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())).AsSpan(0, 16));

    internal static bool IsDynamicKey(string? key) => key is { Length: 36 } && key.StartsWith("app-", StringComparison.Ordinal)
        && key.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static SpatialApp? RestoreDescriptor(string? key, string? process, string? identity) =>
        IsDynamicKey(key) && !string.IsNullOrWhiteSpace(process) && process.Length <= 260 && process.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !string.IsNullOrWhiteSpace(identity) && identity.Length <= 32768 && DynamicKey(identity) == key
        ? new(key!, process, identity) : null;

    internal static string? FromMediaSession(string appId)
    {
        if (appId.StartsWith("Microsoft.ZuneMusic_8wekyb3d8bbwe!", StringComparison.OrdinalIgnoreCase)) return "mediaPlayer";
        if (appId.StartsWith("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!", StringComparison.OrdinalIgnoreCase)) return "spotify";
        if (appId.StartsWith("AppleInc.AppleMusicWin_nzyj5cx40ttqa!", StringComparison.OrdinalIgnoreCase)) return "appleMusic";
        var name = Path.GetFileName(appId);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var known = Array.Find(Supported, app => app.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (known != null) return known.Key;
        lock (CatalogLock)
        {
            foreach (var app in _available.Values)
                if (SameIdentity(app.Identity, appId)) return app.Key;
            return SameIdentity(_remembered?.Identity, appId) ? _remembered!.Key : null;
        }
    }

    internal static string? IdentityOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return IdentityOf(process);
        }
        catch (ArgumentException) { return null; }
    }

    private static string? IdentityOf(Process process)
    {
        try
        {
            // Mixer choices follow the package identity across Store updates. Classic
            // portable and installed copies can share a name but have different routes.
            using var handle = OpenProcess(0x1000, false, process.Id);
            if (handle.IsInvalid) return null;
            uint length = 512;
            var name = new StringBuilder((int)length);
            if (GetApplicationUserModelId(handle, ref length, name) == 0 && name.Length != 0) return name.ToString();
            length = (uint)name.Capacity;
            if (QueryFullProcessImageName(handle, 0, name, ref length)) return name.ToString();
            if (Marshal.GetLastWin32Error() != 122) return null;
            length = 32768;
            name = new StringBuilder((int)length);
            return QueryFullProcessImageName(handle, 0, name, ref length) ? name.ToString() : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    internal int FindPid(string? identity = null)
    {
        if (IsWindowsMix) return 0;
        if (Identity != null && identity != null && !SameIdentity(Identity, identity)) return 0;
        identity ??= Identity;
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            string? matched = null;
            int candidate = 0;
            foreach (var process in processes)
            {
                var current = IdentityOf(process);
                if (current == null || (identity != null && !SameIdentity(current, identity))) continue;
                if (matched != null && !SameIdentity(matched, current)) return 0;
                matched = current;
                try
                {
                    if (!process.HasExited && (candidate == 0 || process.MainWindowHandle != 0)) candidate = process.Id;
                }
                catch (InvalidOperationException) { }
            }
            return candidate;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static bool SameIdentity(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder value, ref uint length);
}
#endif
