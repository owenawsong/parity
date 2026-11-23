#if PAIRSIDE_LIVE
using System.Runtime.InteropServices;
using System.Text.Json;
using WPods.Core;

namespace WPods.Audio;

internal static class WindowsSpatialMix
{
    internal sealed record Saved(string Sink, string Console, string Media, string? Calls);
    private static readonly string Journal = Path.Combine(AppIdentity.Profile, "windows-spatial-mix.json");
    internal static bool Pending => File.Exists(Journal);
    internal static bool Same(string? a, string? b) => a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static Saved Start(string sink, string pair, Saved? expected = null)
    {
        if (Pending) Restore();
        string? console = AudioEndpoints.DefaultPlaybackRole(0), media = AudioEndpoints.DefaultPlaybackRole(1);
        string? calls = AudioEndpoints.DefaultPlaybackRole(2);
        if (!Same(media, pair) || console == null) throw new InvalidOperationException("usePairDefault");
        if (Same(console, sink) || Same(media, sink) || Same(calls, sink)) throw new InvalidOperationException("virtualBusy");
        var saved = new Saved(sink, console, media!, calls);
        if (expected != null && !MatchesMedia(expected, AudioEndpoints.DefaultPlaybackRole))
            throw new InvalidOperationException("outputChanged");
        File.WriteAllText(Journal + ".tmp", JsonSerializer.Serialize(saved));
        File.Move(Journal + ".tmp", Journal, true);
        try { Activate(saved, AudioEndpoints.DefaultPlaybackRole, AudioEndpoints.SetPlaybackRole); }
        catch { Restore(); throw; }
        return saved;
    }

    internal static bool MatchesMedia(Saved expected, Func<int, string?> read) =>
        Same(expected.Console, read(0)) && Same(expected.Media, read(1));

    internal static void Activate(Saved saved, Func<int, string?> read, Action<int, string> write)
    {
        if (!Same(read(1), saved.Media) || !Same(read(0), saved.Console)) throw new InvalidOperationException("usePairDefault");
        write(1, saved.Sink);
        if (Same(saved.Console, saved.Media) && Same(read(0), saved.Console)) write(0, saved.Sink);
        // Windows can group roles which previously shared one output.
        if (saved.Calls != null && Same(read(2), saved.Sink)) write(2, saved.Calls);
        if (!Same(read(1), saved.Sink) || (Same(saved.Console, saved.Media) && !Same(read(0), saved.Sink)) ||
            Same(read(2), saved.Sink)) throw new IOException("Windows did not confirm the media route.");
    }

    internal static void Restore()
    {
        if (!Pending) return;
        var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(Journal)) ?? throw new IOException("Invalid Windows output backup.");
        RestoreOwned(saved, AudioEndpoints.DefaultPlaybackRole, AudioEndpoints.SetPlaybackRole, AudioEndpoints.ActivePlaybackIds());
        File.Delete(Journal);
    }

    internal static void RestoreOwned(Saved saved, Func<int, string?> read, Action<int, string> write, string[] active)
    {
        var originals = new[] { saved.Console, saved.Media, saved.Calls };
        // Snapshot all roles so a user change wins even when Windows groups role writes.
        var before = Enumerable.Range(0, 3).Select(read).ToArray();
        var changedByWrite = new bool[3];
        var fallback = before.Concat(active).FirstOrDefault(id => id != null && !Same(id, saved.Sink) && active.Any(x => Same(x, id)));
        int[] order = [1, 0, 2];
        // A later write can change a role we restored earlier. Verify the final
        // defaults before deleting the journal, then make one bounded repair.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            foreach (int role in order)
            {
                var current = read(role);
                if (!Same(current, saved.Sink) && !changedByWrite[role]) continue;
                var desired = Target(role) ?? throw new IOException("Waiting for a playback device.");
                if (Same(current, desired)) continue;
                var prior = Enumerable.Range(0, 3).Select(read).ToArray();
                write(role, desired);
                changedByWrite[role] = true;
                foreach (int other in order)
                    if (other != role && !string.Equals(prior[other], read(other), StringComparison.OrdinalIgnoreCase))
                        changedByWrite[other] = true;
            }
            if (order.All(role => !Same(read(role), saved.Sink) &&
                (!changedByWrite[role] || Same(read(role), Target(role))))) return;
        }
        throw new IOException("Windows output recovery pending.");

        string? Target(int role)
        {
            var desired = Same(before[role], saved.Sink) ? originals[role] : before[role];
            return active.Any(id => Same(id, desired)) ? desired : fallback;
        }
    }
}

internal static partial class AudioEndpoints
{
    internal static string? DefaultPlaybackRole(int role)
    {
        var devices = CreateEnumerator();
        try
        {
            if (devices.GetDefaultAudioEndpoint(ERender, role, out var device) < 0) return null;
            try { return device.GetId(out var id) >= 0 ? id : null; }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(devices); }
    }

    internal static void SetPlaybackRole(int role, string id)
    {
        var policy = (IEndpointPolicy)(object)new PolicyConfigClient();
        try { Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role)); }
        finally { Marshal.ReleaseComObject(policy); }
    }

    internal static string[] ActivePlaybackIds()
    {
        List<string> result = [];
        var devices = CreateEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(devices.EnumAudioEndpoints(ERender, 1, out var list));
            try
            {
                Marshal.ThrowExceptionForHR(list.GetCount(out var count));
                for (uint i = 0; i < count; i++)
                {
                    if (list.Item(i, out var device) < 0) continue;
                    try { if (device.GetId(out var id) >= 0) result.Add(id); }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(list); }
        }
        finally { Marshal.ReleaseComObject(devices); }
        return result.ToArray();
    }
}
#endif
