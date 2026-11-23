using System.Text.Json;

namespace WPods.Core;

// Per-device local calendar totals. Only completed intervals are saved, so an
// app restart or PC shutdown can never extend the preceding connection.
internal sealed class ConnectionActivity
{
    private readonly string _file;
    private readonly TimeProvider _clock;
    private Dictionary<string, SortedDictionary<DateOnly, long>> _days = [];
    private ulong? _active;
    private DateTimeOffset _previous;
    private bool _changed;

    internal ConnectionActivity(string file, TimeProvider clock)
    {
        _clock = clock;
        _file = file;
        _previous = clock.GetUtcNow();
        try
        {
            if (File.Exists(file))
            {
                _days = JsonSerializer.Deserialize<Dictionary<string, SortedDictionary<DateOnly, long>>>(File.ReadAllText(file)) ?? [];
                foreach (var key in _days.Keys.ToArray())
                    if (_days[key] is null) _days.Remove(key);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("activity", "Could not load connection totals.");
        }
        Prune();
    }

    internal DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    internal bool Update(ulong? active)
    {
        var now = _clock.GetUtcNow();
        var changedDevice = active != _active;
        // The UI timer runs every minute. A suspended PC or a clock adjustment
        // can introduce a gap for which connection status is unknown.
        if (_active is { } address && now > _previous && now - _previous <= TimeSpan.FromSeconds(90))
        {
            var totals = _days[Mac.Format(address)];
            var startDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_previous, _clock.LocalTimeZone).DateTime);
            var endDay = Today;
            if (startDay != endDay)
            {
                var midnight = endDay.ToDateTime(TimeOnly.MinValue);
                var boundary = new DateTimeOffset(midnight, _clock.LocalTimeZone.GetUtcOffset(midnight)).ToUniversalTime();
                if (boundary > _previous && boundary <= now)
                {
                    totals[startDay] = totals.GetValueOrDefault(startDay) + (boundary - _previous).Ticks;
                    _previous = boundary;
                }
            }
            totals[endDay] = totals.GetValueOrDefault(endDay) + (now - _previous).Ticks;
            _changed = true;
        }
        _active = active;
        _previous = now;
        if (active is { } next)
        {
            string key = Mac.Format(next);
            if (!_days.TryGetValue(key, out var totals)) _days[key] = totals = [];
            if (totals.TryAdd(Today, 0)) _changed = true;
        }
        return changedDevice;
    }

    private void Prune()
    {
        var first = Today.AddDays(-6);
        foreach (var (key, days) in _days.ToArray())
        {
            foreach (var day in days.Keys.Where(day => day < first || day > Today).ToArray())
                _changed |= days.Remove(day);
            if (days.Count == 0 && (_active is null || key != Mac.Format(_active.Value))) _changed |= _days.Remove(key);
        }
    }

    internal void Save()
    {
        Prune();
        if (!_changed) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_days));
            File.Move(_file + ".tmp", _file, true);
            _changed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("activity", "Could not save connection totals.");
        }
    }

    internal void Write(Utf8JsonWriter writer)
    {
        var today = Today;
        writer.WriteString("day", today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteStartObject("devices");
        foreach (var (key, days) in _days)
        {
            if (days.Count == 0) continue;
            var first = days.First().Key;
            writer.WriteStartArray(key);
            for (int ago = 6; ago >= 0; ago--)
            {
                var day = today.AddDays(-ago);
                if (day < first) writer.WriteNullValue();
                else writer.WriteNumberValue(Math.Max(0, days.GetValueOrDefault(day) / TimeSpan.TicksPerSecond));
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
