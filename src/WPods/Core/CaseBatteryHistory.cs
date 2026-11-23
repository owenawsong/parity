using System.Text.Json;

namespace WPods.Core;

// Accessed on the tray UI thread, including when every window is closed.
internal sealed class CaseBatteryHistory
{
    private readonly string _path;
    private readonly Dictionary<string, CaseBatteryReading> _readings = [];
    private bool _dirty;

    internal CaseBatteryHistory(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var item in json.RootElement.EnumerateObject())
            {
                var value = item.Value;
                if (Mac.TryParse(item.Name, out var address) && value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("pct", out var pct) && pct.ValueKind == JsonValueKind.Number && pct.TryGetByte(out var percent) && percent <= 100 &&
                    value.TryGetProperty("seenAt", out var stamp) && stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var seenAt) &&
                    seenAt > 0 && seenAt <= now)
                    _readings[Mac.Format(address)] = new(percent, seenAt);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("battery", "Could not read case battery history.");
        }
    }

    internal CaseBatteryReading? Get(ulong address) => _readings.GetValueOrDefault(Mac.Format(address));

    internal void Observe(ulong address, CaseBatteryReading sample)
    {
        if (address == 0 || sample.Percent > 100 || sample.SeenAt <= 0) return;
        string key = Mac.Format(address);
        var previous = _readings.GetValueOrDefault(key);
        if (previous is not null && sample.SeenAt <= previous.SeenAt) return;
        _readings[key] = sample;
        _dirty = true;
        // Save first sightings and level changes immediately. Identical broadcasts only
        // advance the timestamp, checkpointed by the minute timer and graceful exit.
        if (previous?.Percent != sample.Percent) Save();
    }

    internal void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            using (var stream = File.Create(temp))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                foreach (var (key, sample) in _readings)
                {
                    writer.WriteStartObject(key);
                    writer.WriteNumber("pct", sample.Percent);
                    writer.WriteNumber("seenAt", sample.SeenAt);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.Flush();
            }
            File.Move(temp, _path, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("battery", "Could not save case battery history.");
        }
    }
}
