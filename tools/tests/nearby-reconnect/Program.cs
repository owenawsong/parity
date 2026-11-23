using WPods.Nearby;

var pair = new NearbyReconnect();
void Check(bool held, string scenario)
{
    if (pair.Held != held) throw new InvalidOperationException(scenario);
    Console.WriteLine(scenario);
}

pair.ObserveLid(true);
Check(false, "Normal first discovery can auto-connect");
pair.Hold(knownClosed: false);
pair.ObserveLid(true);
pair.ObserveLid(true);
Check(true, "Rediscovering an open case after radio silence respects Disconnect");
pair.ObserveLid(false);
Check(true, "A closed case keeps the disconnect hold");
pair.ObserveLid(true);
Check(false, "A witnessed close and reopen allows the next connection");
pair.Hold(knownClosed: true);
pair.ObserveLid(true);
Check(false, "Opening a case known closed at disconnect allows the next connection");
pair.Hold(knownClosed: true);
pair.Hold(knownClosed: false);
pair.ObserveLid(true);
Check(true, "A newer Disconnect supersedes an older closing");
pair.EndHeadphoneVisit();
Check(false, "Lidless headphones can auto-connect on their next visit");

var signal = new CaseSignalTrend();
string? trend = null;
for (int i = 0; i < 9; i++)
    trend = signal.Update(1, true, i * 1000, i < 5 ? -80 : -70, i * 1000);
if (trend != "stronger" || signal.Update(1, true, 8000, -70, 13000) != null)
    throw new Exception("Signal trend or expiry");
for (int i = 20; i < 29; i++)
    trend = signal.Update(1, true, i * 1000, i < 25 ? -62 : -76, i * 1000);
if (trend != "weaker" || signal.Update(1, false, 29000, -95, 29000) != null)
    throw new Exception("Trend mixed the case with an earbud");
for (int i = 0; i < 15; i++)
    if (signal.Update(2, false, 30000, -95, 30000 + i*200) != null)
        throw new Exception("Repeated polls counted as packets");
Console.WriteLine("Case signal trends distinguish fresh sustained changes, signal source and duplicate polls.");
