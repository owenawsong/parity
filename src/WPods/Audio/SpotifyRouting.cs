#if PAIRSIDE_LIVE
using System.Runtime.InteropServices;
using System.Text.Json;
using WPods.Core;

namespace WPods.Audio;

// The Windows volume mixer persists app output choices by app identity, using a
// live process ID. Save both roles before touching either. Restore only our own
// choices, including after a restart, so an intervening user change wins.
internal sealed class SpotifyRouting
{
    internal sealed record Saved(string Sink, string? Console, string? Multimedia, string? Identity = null, string? ProcessName = null);
    private static string Journal(SpatialApp app) => Path.Combine(AppIdentity.Profile, app.Key + "-output.json");
    private const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private static string Interface(string endpoint) => @"\\?\SWD#MMDEVAPI#" + endpoint + RenderInterface;
    internal static bool Pending(SpatialApp app) => File.Exists(Journal(app));
    internal static bool AnyPending => PendingApps().Length != 0;

    internal static SpatialApp[] PendingApps()
    {
        var apps = SpatialApp.Supported.Where(Pending).ToList();
        if (!Directory.Exists(AppIdentity.Profile)) return [.. apps];
        const string suffix = "-output.json";
        foreach (string path in Directory.EnumerateFiles(AppIdentity.Profile, "app-*" + suffix))
        {
            string key = Path.GetFileName(path)[..^suffix.Length];
            if (!SpatialApp.IsDynamicKey(key)) continue;
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
                if (SpatialApp.RestoreDescriptor(key, saved?.ProcessName, saved?.Identity) is { } app) apps.Add(app);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return [.. apps];
    }

    internal static Saved Apply(SpatialApp app, int pid, string endpoint, Saved? expected = null)
    {
        if (app.IsWindowsMix) throw new InvalidOperationException("Windows playback has no per-app mixer route.");
        if (Pending(app) && !RestoreAvailable(app)) throw new InvalidOperationException("restore");
        string identity = SpatialApp.IdentityOf(pid) ?? throw new InvalidOperationException("noPlayer");
        if (app.Identity != null && !SpatialApp.SameIdentity(app.Identity, identity)) throw new InvalidOperationException("noPlayer");
        string sink = Interface(endpoint);
        using var policy = new AppOutputPolicy();
        var before = new Saved(sink, policy.Read(pid, 0), policy.Read(pid, 1), identity, app.ProcessName);
        if (expected != null && (!SpatialApp.SameIdentity(expected.Identity, identity) ||
            !MatchesMedia(expected, role => policy.Read(pid, role))))
            throw new InvalidOperationException("outputChanged");
        string temporary = Journal(app) + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(before));
        File.Move(temporary, Journal(app), true);
        try
        {
            policy.Write(pid, 1, sink);
            policy.Write(pid, 0, sink);
            if (!Same(policy.Read(pid, 1), sink) || !Same(policy.Read(pid, 0), sink))
                throw new InvalidOperationException("Windows did not confirm the app output.");
        }
        catch
        {
            Restore(app, pid);
            throw;
        }
        return before;
    }

    internal static bool ReadyToResume(SpatialApp app, Saved expected)
    {
        int pid = app.FindPid(expected.Identity);
        if (pid == 0) return false;
        using var policy = new AppOutputPolicy();
        return MatchesMedia(expected, role => policy.Read(pid, role));
    }

    internal static bool MatchesMedia(Saved expected, Func<int, string?> read) =>
        Same(expected.Console, read(0)) && Same(expected.Multimedia, read(1));

    internal static bool Owns(int pid, string endpoint)
    {
        using var policy = new AppOutputPolicy();
        return Same(policy.Read(pid, 1), Interface(endpoint)) && Same(policy.Read(pid, 0), Interface(endpoint));
    }

    private static Saved ReadSaved(SpatialApp app) =>
        JsonSerializer.Deserialize<Saved>(File.ReadAllText(Journal(app))) ?? throw new IOException("Invalid app output backup.");

    internal static bool RestoreAvailable(SpatialApp app)
    {
        if (!Pending(app)) return true;
        var saved = ReadSaved(app);
        int pid = app.FindPid(saved.Identity);
        if (pid == 0) return false;
        Restore(app, pid);
        return true;
    }

    internal static void Restore(SpatialApp app, int pid)
    {
        if (!Pending(app)) return;
        var saved = ReadSaved(app);
        if (saved.Identity != null && !SpatialApp.SameIdentity(saved.Identity, SpatialApp.IdentityOf(pid)))
            throw new IOException("The original player is unavailable for output recovery.");
        using var policy = new AppOutputPolicy();
        RestoreOwnedRoles(saved.Sink, saved.Console, saved.Multimedia, role => policy.Read(pid, role),
            (role, endpoint) => policy.Write(pid, role, endpoint));
        File.Delete(Journal(app));
    }

    internal static void RestoreOwnedRoles(string sink, string? console, string? multimedia,
        Func<int, string?> read, Action<int, string?> write)
    {
        void RestoreRole(int role, string? original)
        {
            if (!Same(read(role), sink)) return;
            write(role, original);
            if (!Same(read(role), original))
                throw new IOException("App output restoration is pending.");
        }
        RestoreRole(1, multimedia);
        RestoreRole(0, console);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed unsafe class AppOutputPolicy : IDisposable
    {
        // IAudioPolicyConfigFactory, Win10 21H2 / Windows 11. IInspectable plus
        // nineteen methods before Set/GetPersistedDefaultAudioEndpoint.
        private nint _factory;
        internal AppOutputPolicy()
        {
            nint name = MakeString("Windows.Media.Internal.AudioPolicyConfig");
            try
            {
                Guid iid = new("ab3d4648-e242-459f-b02f-541c70306324");
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in iid, out _factory));
            }
            finally { WindowsDeleteString(name); }
        }
        internal string? Read(int pid, int role)
        {
            nint value = 0;
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, int, int, nint*, int>)(*(nint**)_factory)[26];
            int hr = fn(_factory, (uint)pid, 0, role, &value);
            try
            {
                if (hr == unchecked((int)0x80070490)) return null;
                Marshal.ThrowExceptionForHR(hr);
                if (value == 0) return null;
                nint data = WindowsGetStringRawBuffer(value, out uint length);
                return length == 0 ? null : Marshal.PtrToStringUni(data, checked((int)length));
            }
            finally { WindowsDeleteString(value); }
        }
        internal void Write(int pid, int role, string? endpoint)
        {
            nint value = endpoint is null ? 0 : MakeString(endpoint);
            try
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, uint, int, int, nint, int>)(*(nint**)_factory)[25];
                Marshal.ThrowExceptionForHR(fn(_factory, (uint)pid, 0, role, value));
            }
            finally { WindowsDeleteString(value); }
        }
        private static nint MakeString(string s)
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(s, (uint)s.Length, out nint value));
            return value;
        }
        public void Dispose() { if (_factory != 0) { Marshal.Release(_factory); _factory = 0; } }
        [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WindowsCreateString(string value, uint length, out nint hstring);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
        [DllImport("combase.dll")] private static extern nint WindowsGetStringRawBuffer(nint value, out uint length);
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint className, in Guid iid, out nint factory);
    }
}
#endif
