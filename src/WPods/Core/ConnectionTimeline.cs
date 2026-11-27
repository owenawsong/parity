namespace WPods.Core;

internal enum ConnectionEvent
{
    LinkUp, LinkDown, ControlsOpening, ControlsReady, ControlsUnavailable,
    ControlsClosed, HandshakeTimedOut, RetriesExhausted,
    TrackingReady, MotionMissing, CalibrationTimedOut, MotionInterrupted
}

// Bounded to this app session. Only the selected pair's event types, times,
// and counts cross into the UI. The address is an internal lookup key.
internal sealed class ConnectionTimeline
{
    private const int Capacity = 96;
    private const int VisibleEntries = 8;
    private readonly Lock _lock = new();
    private readonly List<(ulong Address, ConnectionEvent Kind, long Time, long Tick, int Count)> _entries = [];

    internal sealed record Entry(ConnectionEvent Kind, long Time, long AgeSeconds, int Count);

    public void Record(ulong address, ConnectionEvent kind)
    {
        if (address == 0) return;
        lock (_lock)
        {
            int last = _entries.FindLastIndex(item => item.Address == address);
            int count = 1;
            if (last >= 0 && _entries[last].Kind == kind)
            {
                count += _entries[last].Count;
                _entries.RemoveAt(last);
            }
            if (_entries.Count == Capacity) _entries.RemoveAt(0);
            _entries.Add((address, kind, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Environment.TickCount64, count));
        }
    }

    public Entry[] Read(ulong address)
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            return _entries.AsEnumerable().Reverse().Where(item => item.Address == address)
                .Take(VisibleEntries).Select(item => new Entry(item.Kind, item.Time, (now - item.Tick) / 1000, item.Count)).ToArray();
        }
    }
}
