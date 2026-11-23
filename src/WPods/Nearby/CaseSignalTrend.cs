namespace WPods.Nearby;

internal sealed class CaseSignalTrend
{
    private readonly List<(long At, double Dbm)> _samples = [];
    private (ulong Device, bool Direct)? _source;

    internal void Reset()
    {
        _samples.Clear();
        _source = null;
    }

    internal string? Update(ulong device, bool direct, long seen, double level, long now)
    {
        if (seen > now || now - seen > 4_000)
        {
            Reset();
            return null;
        }
        if (_source != (device, direct) || (_samples.Count > 0 && seen - _samples[^1].At > 4_000))
            Reset();
        _source = (device, direct);
        _samples.RemoveAll(p => p.At < seen - 10_000);
        if (_samples.Count == 0 || seen > _samples[^1].At)
            _samples.Add((seen, level));
        if (_samples.Count < 6 || seen - _samples[0].At < 6_000) return null;

        // Two separated windows keep a single packet or a momentary obstruction
        // from turning into a movement hint. RSSI describes signal, not distance.
        var early = _samples.Where(p => p.At <= seen - 4_000).Select(p => p.Dbm).Order().ToArray();
        var late = _samples.Where(p => p.At >= seen - 3_000).Select(p => p.Dbm).Order().ToArray();
        if (early.Length < 3 || late.Length < 3) return null;
        static double Median(double[] a) => (a[(a.Length-1)/2] + a[a.Length/2])/2;
        var change = Median(late) - Median(early);
        return change >= 5 ? "stronger" : change <= -5 ? "weaker" : null;
    }
}
