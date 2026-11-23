using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace WPods.Core;

// Each accepted sample is one UTC timestamp and one BPM byte. No placeholder
// records are written during gaps. An interrupted last record is ignored on read.
internal sealed class PulseHistory(string root)
{
    private const int RecordSize = 9;
    private const int MaxSampleIntervalMs = 8000;
    internal const int MaxChartSeconds = 15 * 60;
    internal static bool ValidChartWindow(int seconds) => seconds is 15 or 30 or 60 or 120 or 300 or MaxChartSeconds;
    private static bool Continuous(long previous, long time) =>
        previous > 0 && time >= previous && time - previous <= MaxSampleIntervalMs;
    internal sealed record Point(long Time, int Bpm);
    internal sealed record Entry(long Key, long First, long Last, int Count, int Mean);
    internal sealed record Trace(long Key, long First, long Last, int Count, int Low, int High, int Mean, long CoveredMs, IReadOnlyList<Point> Points);

    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, Entry Summary)> _summaries = new();
    private FileStream? _writer;
    private readonly Queue<Point> _recent = new();
    private long _key, _first, _last, _sum, _covered;
    private int _count, _low, _high;
    internal ulong? Address { get; private set; }
    internal long? CurrentKey => Address.HasValue ? _key : null;

    private string DirectoryFor(ulong address) => Path.Combine(root, address.ToString("X12"));
    private string FileFor(ulong address, long key) => Path.Combine(DirectoryFor(address), key.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".pulse");

    internal void Start(ulong address, long now)
    {
        if (Address.HasValue) throw new InvalidOperationException("A pulse session is already open.");
        Directory.CreateDirectory(DirectoryFor(address));
        _key = now;
        while (File.Exists(FileFor(address, _key))) _key++;
        _count = 0;
        _sum = _first = _last = _covered = 0;
        _low = int.MaxValue;
        _high = 0;
        _recent.Clear();
        Address = address;
    }

    internal bool Add(long time, byte bpm)
    {
        if (Address is not { } address || bpm is < 30 or > 220 || time <= 0 || time < _last ||
            time > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) return false;
        if (_writer is null)
            _writer = new FileStream(FileFor(address, _key), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        Span<byte> data = stackalloc byte[RecordSize];
        BinaryPrimitives.WriteInt64LittleEndian(data, time);
        data[8] = bpm;
        _writer.Write(data);
        if (_count == 0 || _count % 10 == 0) _writer.Flush();
        if (_count++ == 0) _first = time;
        if (Continuous(_last, time)) _covered += time - _last;
        _last = time;
        _sum += bpm;
        _low = Math.Min(_low, bpm);
        _high = Math.Max(_high, bpm);
        _recent.Enqueue(new(time, bpm));
        while (_recent.Count > 4096 || _recent.Peek().Time < time - MaxChartSeconds * 1000L) _recent.Dequeue();
        return true;
    }

    internal Trace? Live(int windowSeconds = 120) => _count == 0 || Address is null ? null :
        new(_key, _first, _last, _count, _low, _high, (int)Math.Round((double)_sum / _count), _covered,
            _recent.Where(point => point.Time >= _last - windowSeconds * 1000L).ToArray());

    internal void End()
    {
        Address = null;
        _recent.Clear();
        try { _writer?.Dispose(); }
        finally { _writer = null; }
    }

    internal List<Entry> List(ulong address, long? excludeKey = null)
    {
        List<Entry> result = [];
        string dir = DirectoryFor(address);
        if (!Directory.Exists(dir)) return result;
        foreach (string file in Directory.EnumerateFiles(dir, "*.pulse"))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file), out long key) ||
                excludeKey == key) continue;
            try
            {
                var info = new FileInfo(file);
                long size = info.Length;
                if (size / RecordSize is 0 or > int.MaxValue) continue;
                DateTime modified = info.LastWriteTimeUtc;
                if (_summaries.TryGetValue(file, out var cached) && cached.Size == size && cached.Modified == modified)
                {
                    result.Add(cached.Summary);
                    continue;
                }
                using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var trace = ReadTrace(stream, key, 0, stream.Length / RecordSize, includePoints: false);
                if (trace == null) continue;
                var entry = new Entry(key, trace.First, trace.Last, trace.Count, trace.Mean);
                _summaries[file] = (size, modified, entry);
                result.Add(entry);
            }
            catch (FileNotFoundException) { /* Deleted while refreshing. */ }
            catch (DirectoryNotFoundException) { /* The history folder was removed. */ }
        }
        result.Sort((a,b) => b.Key.CompareTo(a.Key));
        return result;
    }

    internal void Delete(ulong address, long key)
    {
        if (key <= 0 || Address == address && CurrentKey == key) return;
        string path = FileFor(address, key);
        try { File.Delete(path); _summaries.TryRemove(path, out _); }
        catch (DirectoryNotFoundException) { /* Already removed. */ }
    }

    internal IReadOnlyList<Point>? ReadWindow(ulong address, long key, long from, long to)
    {
        if (key <= 0 || from <= 0 || to < from) return null;
        try
        {
            using var stream = File.OpenRead(FileFor(address, key));
            long records = stream.Length / RecordSize;
            long first = FindBound(stream, records, from, includeEqual: false);
            long end = FindBound(stream, records, to, includeEqual: true);
            return ReadTrace(stream, key, first, end)?.Points ?? [];
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    // Files are written in timestamp order. Seek to the window instead of
    // rereading the entire session on each pan.
    private static long FindBound(FileStream stream, long end, long time, bool includeEqual)
    {
        Span<byte> stamp = stackalloc byte[sizeof(long)];
        long first = 0;
        while (first < end)
        {
            long middle = first + (end - first) / 2;
            stream.Position = middle * RecordSize;
            stream.ReadExactly(stamp);
            long candidate = BinaryPrimitives.ReadInt64LittleEndian(stamp);
            if (candidate < time || (includeEqual && candidate == time)) first = middle + 1;
            else end = middle;
        }
        return first;
    }

    internal Trace? Read(ulong address, long key)
    {
        if (key <= 0) return null;
        string file = FileFor(address, key);
        if (!File.Exists(file)) return null;
        using var stream = File.OpenRead(file);
        return ReadTrace(stream, key, 0, stream.Length / RecordSize);
    }

    private static Trace? ReadTrace(FileStream stream, long key, long start, long end, bool includePoints = true)
    {
        stream.Position = start * RecordSize;
        Span<byte> record = stackalloc byte[RecordSize];
        // Keep extrema in source order to preserve isolated peaks at chart resolution.
        long bucketSize = Math.Max(1, (end - start + 119) / 120);
        List<Point> points = [];
        Point? min = null, max = null, previous = null;
        long first = 0, sum = 0, covered = 0;
        int count = 0, lo = int.MaxValue, hi = 0;
        void Emit(Point p) { if (points.Count == 0 || points[^1] != p) points.Add(p); }
        void EmitBucket()
        {
            if (min is null || max is null) return;
            if (min.Time <= max.Time) { Emit(min); Emit(max); }
            else { Emit(max); Emit(min); }
            min = max = null;
        }
        for (long i = start; i < end; i++)
        {
            stream.ReadExactly(record);
            var p = new Point(BinaryPrimitives.ReadInt64LittleEndian(record), record[8]);
            if (p.Bpm is < 30 or > 220 || p.Time <= 0 || p.Time > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() || p.Time < previous?.Time) continue;
            if (count == 0) { first = p.Time; if (includePoints) Emit(p); }
            bool gap = previous is not null && !Continuous(previous.Time, p.Time);
            if (previous is not null && !gap) covered += p.Time - previous.Time;
            if (includePoints)
            {
                if ((i - start) % bucketSize == 0 || gap) EmitBucket();
                if (gap) { Emit(previous!); points.Add(new(p.Time, 0)); Emit(p); }
                if (min is null || p.Bpm < min.Bpm) min = p;
                if (max is null || p.Bpm >= max.Bpm) max = p;
            }
            previous = p;
            count++;
            sum += p.Bpm;
            lo = Math.Min(lo, p.Bpm);
            hi = Math.Max(hi, p.Bpm);
        }
        EmitBucket();
        if (previous is null) return null;
        if (includePoints) Emit(previous);
        return new(key, first, previous.Time, count, lo, hi, (int)Math.Round((double)sum / count), covered, points);
    }
}
