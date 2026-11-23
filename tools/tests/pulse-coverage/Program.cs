using WPods.Core;

static void Require(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
var history = new PulseHistory(path);
try
{
    var today = new DateTime(2026, 7, 11);
    long start = new DateTimeOffset(today).ToUnixTimeMilliseconds();
    history.Start(1, start);
    foreach (long offset in new long[] { 0, 1_000, 1_000, 9_000, 17_001, 317_001, 318_001 })
        history.Add(start + offset, 70);
    var live = history.Live()!;
    Require(live.CoveredMs == 10_000 && live.Count == 7, "Duplicate timestamps and gaps above eight seconds must add no measured time.");
    history.End();
    var archive = history.Read(1, start)!;
    Require(archive.CoveredMs == live.CoveredMs && archive.Points.Count(p => p.Bpm == 0) == 2,
        "Archived and live coverage must agree and expose both missing intervals.");
    history.Start(2, start - 3_000);
    history.Add(start - 3_000, 60);
    history.Add(start + 2_000, 65);
    history.End();
    var reopened = new PulseHistory(path);
    var saved = reopened.List(2).Single();
    var overnight = reopened.Read(2, saved.Key)!;
    Require(saved.Mean == overnight.Mean && saved.Count == overnight.Count && reopened.List(2).Single() == saved,
        "The session list must display the same average on its first and subsequent reads.");
    Require(overnight.First == start - 3_000 && overnight.Last == start + 2_000 &&
        overnight.CoveredMs == 5_000 && overnight.Low == 60 && overnight.High == 65,
        "A saved session crossing midnight must retain its timeline and statistics.");

    history.Start(3, start);
    for (int i = 0; i <= 1200; i++)
        if (i < 305 || i >= 320) history.Add(start + i * 1000L, (byte)(70 + i % 10));
    var shortView = history.Live(120)!;
    var longView = history.Live(900)!;
    Require(shortView.Points[0].Time == start + 1_080_000 &&
        longView.Points[0].Time == start + 300_000 && longView.Points[^1].Time == start + 1_200_000 &&
        shortView.Count == longView.Count && shortView.Mean == longView.Mean,
        "Extended live windows need older readings while preserving session statistics.");
    history.End();
    var window = reopened.ReadWindow(3, start, start + 300_000, start + 330_000)!;
    Require(window[0].Time == start + 300_000 && window[^1].Time == start + 330_000 &&
        window.Count == 17 && window.Single(p => p.Bpm == 0).Time == start + 320_000,
        "A chart window must retrieve original readings at included bounds and preserve the internal gap.");
    Require(reopened.ReadWindow(3, start, start + 310_000, start + 315_000) is { Count: 0 } &&
        reopened.ReadWindow(3, start + 1, start, start + 15_000) is null,
        "A silent interval and a missing session need separate results.");
    Console.WriteLine("Pulse coverage passes for live capture, archives, signal gaps, midnight and detailed chart windows.");
}
finally
{
    history.End();
    if (Directory.Exists(path)) Directory.Delete(path, true);
}
