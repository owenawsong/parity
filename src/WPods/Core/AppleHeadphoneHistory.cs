#if PAIRSIDE_LIVE
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;

namespace WPods.Core;

/// <summary>Only daily ranges of headphone playback levels are retained from a user-selected Health export.</summary>
internal sealed class AppleHeadphoneHistory(string path)
{
    internal const int RetainedDays = 90;
    private const long MaxXmlSize = 8L * 1024 * 1024 * 1024;
    private const string HealthType = "HKQuantityTypeIdentifierHeadphoneAudioExposure";
    internal sealed record Day(DateOnly Date, double Low, double High);

    internal static Day[] Import(string file, CancellationToken cancel)
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var candidates = archive.Entries.Where(e => e.Name.Equals("export.xml", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (candidates.Length != 1 || candidates[0].Length > MaxXmlSize)
                throw new InvalidDataException("Expected one Apple Health export.xml.");
            using var stream = candidates[0].Open();
            return ReadXml(stream, cancel);
        }
        if (input.Length > MaxXmlSize) throw new InvalidDataException("Export exceeds supported size.");
        return ReadXml(input, cancel);
    }

    internal static Day[] ReadXml(Stream stream, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        // Apple includes a DOCTYPE in genuine exports. Ignore declarations and never resolve external resources.
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true,
            IgnoreWhitespace = true, MaxCharactersInDocument = MaxXmlSize,
        });
        if (xml.MoveToContent() != XmlNodeType.Element || xml.Name != "HealthData" || xml.NamespaceURI.Length != 0)
            throw new InvalidDataException("Expected a HealthData export.");

        var days = new SortedDictionary<DateOnly, Day>();
        while (xml.Read())
        {
            cancel.ThrowIfCancellationRequested();
            if (xml.Depth != 1 || xml.NodeType != XmlNodeType.Element || xml.Name != "Record" || xml.NamespaceURI.Length != 0 ||
                xml.GetAttribute("type") != HealthType || xml.GetAttribute("unit") != "dBASPL")
                continue;
            var stamp = xml.GetAttribute("endDate");
            if (stamp is null || !TrySampleDate(stamp, out var day) ||
                !double.TryParse(xml.GetAttribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out double level) ||
                !double.IsFinite(level))
                continue;

            if (days.TryGetValue(day, out var existing))
            {
                if (level < existing.Low || level > existing.High)
                    days[day] = new(day, Math.Min(existing.Low, level), Math.Max(existing.High, level));
            }
            else if (days.Count < RetainedDays || day > days.First().Key)
            {
                if (days.Count == RetainedDays) days.Remove(days.First().Key);
                days.Add(day, new(day, level, level));
            }
        }
        return days.Values.ToArray();
    }

    private static bool TrySampleDate(string stamp, out DateOnly day)
    {
        // Group by the date at the sample's recorded offset, including on another PC or after travel.
        if (stamp.Length >= 5 && stamp[^5] is '+' or '-') stamp = stamp.Insert(stamp.Length - 2, ":");
        bool ok = DateTimeOffset.TryParseExact(stamp, ["yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss.FFFFFFF zzz"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var end);
        day = DateOnly.FromDateTime(end.DateTime);
        return ok;
    }

    internal Day[] Load()
    {
        if (!File.Exists(path)) return [];
        using var input = File.OpenRead(path);
        if (input.Length > 65536) throw new InvalidDataException("Invalid saved day summaries.");
        var days = JsonSerializer.Deserialize<Day[]>(input);
        if (days == null || days.Length > RetainedDays ||
            days.Any(d => d == null || !double.IsFinite(d.Low) || !double.IsFinite(d.High) || d.Low > d.High) ||
            days.Zip(days.Skip(1)).Any(pair => pair.First.Date >= pair.Second.Date))
            throw new InvalidDataException("Invalid saved day summaries.");
        return days;
    }

    internal void Save(Day[] days)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(tmp)) JsonSerializer.Serialize(stream, days);
            File.Move(tmp, path, overwrite: true);
        }
        finally { File.Delete(tmp); }
    }

    internal void Clear() => File.Delete(path);
}
#endif
