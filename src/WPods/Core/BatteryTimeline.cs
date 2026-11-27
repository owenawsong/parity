#if PAIRSIDE_LIVE
using System.Text.Json;

namespace WPods.Core;

// One point per five minutes of actual reports. No timer creates or advances a reading.
internal sealed class BatteryTimeline(string path)
{
    internal const long Day = 86_400_000, Bucket = 300_000;
    internal static readonly string[] Parts = ["left", "right", "case", "max"];
    internal sealed record Point(long At, byte Pct, byte Low, byte High, bool? Charging, string Source, long? ChargedAt);
    private sealed record Alert(int Target, long After, bool Below);
    private sealed class Pair
    {
        public bool Paused { get; set; }
        public long Since { get; set; }
        public Dictionary<string, List<Point>> Traces { get; set; } = [];
        public Dictionary<string, Alert> Alerts { get; set; } = [];
    }

    private Dictionary<string, Pair> _pairs = [];
    private bool _dirty;
    private Task<bool> _pendingWrite = Task.FromResult(true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal void Load(long now)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 16 * 1024 * 1024) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize<Dictionary<string, Pair>>(file, Json);
            if (saved is null) return;
            foreach (var (id, pair) in saved)
            {
                if (!Mac.TryParse(id, out var address) || address == 0 || pair?.Traces == null) continue;
                pair.Alerts ??= [];
                foreach (var part in pair.Alerts.Keys.ToArray())
                    if (!Parts.Contains(part) || pair.Alerts[part] is not { Target: 80 or 90 or 100, After: > 0 })
                        pair.Alerts.Remove(part);
                if (pair.Paused) pair.Traces.Clear();
                foreach (var part in pair.Traces.Keys.ToArray())
                {
                    if (!Parts.Contains(part) || pair.Traces[part] is not { } trace)
                    {
                        pair.Traces.Remove(part);
                        continue;
                    }
                    // Validate before returning disk data to a chart.
                    long previous = -1;
                    trace.RemoveAll(point =>
                    {
                        if (point == null || point.At < now - Day || point.At > now || point.At < pair.Since ||
                            point.Low > point.Pct || point.Pct > point.High || point.High > 100 ||
                            point.Source is not ("device" or "case" or "nearby" or "saved") ||
                            point.ChargedAt is long charged && (charged > point.At || charged / Bucket != point.At / Bucket) ||
                            point.At / Bucket <= previous) return true;
                        previous = point.At / Bucket;
                        return false;
                    });
                }
                if (pair.Paused || pair.Since >= now - Day || pair.Alerts.Count != 0 || pair.Traces.Values.Any(trace => trace.Count > 0))
                    _pairs[Mac.Format(address)] = pair;
            }
            // A checkpoint will remove expired samples from disk even on a quiet day.
            _dirty = true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn("battery", "Could not read battery timeline.");
        }
    }

    internal bool Enabled(string id) => !_pairs.TryGetValue(id, out var pair) || !pair.Paused;

    internal void SetEnabled(string id, bool enabled, long now)
    {
        if (Enabled(id) == enabled) return;
        if (!_pairs.TryGetValue(id, out var pair)) _pairs[id] = pair = new Pair();
        pair.Paused = !enabled;
        pair.Since = now;
        pair.Traces.Clear();
        _dirty = true;
    }

    internal int AlertTarget(string id, string part) =>
        _pairs.TryGetValue(id, out var pair) && pair.Alerts.TryGetValue(part, out var a) ? a.Target : 0;

    internal void SetAlert(string id, string part, int target, long now, byte? current)
    {
        if (target is not (0 or 80 or 90 or 100) || !Parts.Contains(part) || AlertTarget(id, part) == target) return;
        if (!_pairs.TryGetValue(id, out var pair)) _pairs[id] = pair = new Pair();
        if (target == 0) pair.Alerts.Remove(part);
        else pair.Alerts[part] = new(target, now, current < target);
        _dirty = true;
    }

    // Qualify a future crossing with a below-target reading. Repeated or cached reports cannot fire a new alert.
    internal bool CrossedTarget(string id, string part, byte pct, long at, long now)
    {
        if (at > now || now - at > 90_000 || !_pairs.TryGetValue(id, out var pair) ||
            !pair.Alerts.TryGetValue(part, out var alert) || at <= alert.After) return false;
        bool below = pct < alert.Target;
        if (below || !alert.Below)
        {
            pair.Alerts[part] = alert with { After = at, Below = below };
            _dirty = true;
            return false;
        }
        pair.Alerts.Remove(part);
        _dirty = true;
        return true;
    }

    internal bool Observe(string id, string part, byte pct, long at, bool? charging, string source, long now)
    {
        if (pct > 100 || at <= 0 || at < now - Day || at > now) return false;
        if (!_pairs.TryGetValue(id, out var pair)) _pairs[id] = pair = new Pair();
        if (pair.Paused || at <= pair.Since) return false;
        if (!pair.Traces.TryGetValue(part, out var trace)) pair.Traces[part] = trace = [];
        var last = trace.Count == 0 ? null : trace[^1];
        if (last != null && last.At >= at) return false;
        bool newBucket = last == null || last.At / Bucket != at / Bucket;
        if (newBucket)
        {
            trace.RemoveAll(p => p.At < now - Day);
            trace.Add(new(at, pct, pct, pct, charging, source, charging == true ? at : null));
        }
        else
        {
            trace[^1] = new(at, pct, Math.Min(last!.Low, pct), Math.Max(last.High, pct), charging, source,
                charging == true ? at : last.ChargedAt);
        }
        _dirty = true;
        return newBucket || pct != last!.Pct || charging != last.Charging || source != last.Source;
    }

    internal IReadOnlyList<Point> Read(string id, string part, long now)
    {
        if (!_pairs.TryGetValue(id, out var pair) || !pair.Traces.TryGetValue(part, out var trace)) return [];
        if (trace.RemoveAll(p => p.At < now - Day) > 0) _dirty = true;
        return trace;
    }

    private void Prune(long now)
    {
        foreach (var pair in _pairs.Values)
            foreach (var trace in pair.Traces.Values)
                if (trace.RemoveAll(p => p.At < now - Day) > 0) _dirty = true;
        foreach (var (id, pair) in _pairs.ToArray())
            if (!pair.Paused && pair.Alerts.Count == 0 && pair.Since < now - Day && pair.Traces.Values.All(t => t.Count == 0))
            {
                _pairs.Remove(id);
                _dirty = true;
            }
    }

    // The tray thread owns the mutable lists. A checkpoint copies references to immutable points;
    // encoding, flushing and replacing the file run off the thread that paints the desktop window.
    internal void QueueSave(long now)
    {
        if (!_pendingWrite.IsCompleted) return;
        if (!_pendingWrite.GetAwaiter().GetResult()) _dirty = true;
        Prune(now);
        if (!_dirty) return;
        var snapshot = _pairs.ToDictionary(p => p.Key, p => new Pair
        {
            Since = p.Value.Since, Paused = p.Value.Paused,
            Alerts = new(p.Value.Alerts),
            Traces = p.Value.Traces.ToDictionary(t => t.Key, t => new List<Point>(t.Value)),
        });
        _dirty = false;
        _pendingWrite = Task.Run(() => Write(snapshot));
    }

    internal bool Save(long now)
    {
        // Explicit preference changes and graceful exit commit after any older checkpoint.
        if (!_pendingWrite.GetAwaiter().GetResult()) _dirty = true;
        _pendingWrite = Task.FromResult(true);
        Prune(now);
        if (_dirty) _dirty = !Write(_pairs);
        return !_dirty;
    }

    private bool Write(Dictionary<string, Pair> snapshot)
    {
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = File.Create(temp)) JsonSerializer.Serialize(file, snapshot, Json);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("battery", "Could not save battery timeline.");
            return false;
        }
    }
}
#endif
