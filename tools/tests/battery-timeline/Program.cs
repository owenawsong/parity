using WPods.Core;

const string pair = "000000000001", other = "000000000002";
const long start = BatteryTimeline.Day * 20000;
var file = Path.Combine(Path.GetTempPath(), "parity-battery-" + Guid.NewGuid().ToString("N") + ".json");
void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
}
try
{
    var history = new BatteryTimeline(file);
    history.Observe(pair, "left", 70, start, false, "device", start);
    history.Observe(pair, "left", 72, start + 1, true, "device", start + 1);
    history.Observe(pair, "left", 71, start + 5, false, "device", start + 5);
    history.Observe(pair, "right", 47, start + 7, false, "device", start + 7);
    history.Observe(other, "left", 8, start + 2, null, "device", start + 8);
    Check(!history.Observe(pair,"left",20,start + 3, false, "device",start + 9), "old sample replaced newer data");
    var p = history.Read(pair, "left", start + 9).Single();
    Check(p is { Pct:71, Low:70, High:72, Charging:false } && p.ChargedAt == start + 1, "bucket range / last charging report");
    Check(history.Read(other, "left", start + 9).Single().Pct == 8, "pair isolation");
    long later = start + BatteryTimeline.Bucket * 17;
    history.Observe(pair,"left",40,later,false,"device",later);
    history.Save(later);
    var restored = new BatteryTimeline(file);
    restored.Load(later);
    var points = restored.Read(pair,"left",later);
    Check(points.Count == 2 && points[1].At - points[0].At > 3600000, "invented observations in a gap or failed restart");
    restored.SetAlert(pair,"left",80,later,90);
    Check(!restored.CrossedTarget(pair,"left",95,later+1,later+1), "immediate notification while already over target");
    Check(!restored.CrossedTarget(pair,"left",65,later+2,later+2), "below alert threshold");
    Check(!restored.CrossedTarget(pair,"left",86,later+1,later+3), "out-of-order reading triggered alert");
    restored.SetEnabled(pair,false,later+3);
    Check(restored.Read(pair,"left",later+3).Count==0 && restored.AlertTarget(pair,"left")==80, "turning off erases only history");
    restored.Save(later+3);
    var restarted = new BatteryTimeline(file);
    restarted.Load(later+4);
    Check(!restarted.Enabled(pair) && restarted.Read(other,"left",later+4).Count==1, "per-device opt-out");
    Check(!restarted.CrossedTarget(other,"left",90,later+5,later+5), "other pair triggered alert");
    Check(!restarted.CrossedTarget(pair,"left",90,later+5,later+100000), "stale reading triggered alert");
    Check(restarted.CrossedTarget(pair,"left",80,later+6,later+6), "fresh crossing after process restart");
    Check(!restarted.CrossedTarget(pair,"left",81,later+7,later+7), "duplicate alert");
    restarted.SetEnabled(pair,true,later+8);
    Check(!restarted.Observe(pair,"case",80,start,false,"saved",later+8), "backfill after history opt-out");
    restarted.Observe(pair,"case",80,later+9,null,"saved",later+9);
    Check(restarted.Read(pair,"case",later+10).Single().Source == "saved", "saved reading provenance");
    restarted.Save(later + BatteryTimeline.Day + 10);
    Check(restarted.Read(pair,"case",later + BatteryTimeline.Day + 10).Count==0, "24-hour expiry");
    Console.WriteLine("PASS buckets, gaps, source, retention, pair isolation, opt-out, restoration, one-time alerts and stale reports.");
}
finally { File.Delete(file); File.Delete(file+".tmp"); }

namespace WPods.Core
{
    internal static class Log
    {
        internal static void Warn(string area, string text) => Console.Error.WriteLine(area + ": " + text);
    }
}
