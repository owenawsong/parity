using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using WPods.Core;
using Day = WPods.Core.AppleHeadphoneHistory.Day;

const string headphone = "HKQuantityTypeIdentifierHeadphoneAudioExposure";
const string environmental = "HKQuantityTypeIdentifierEnvironmentalAudioExposure";
const string start = "2026-11-15 00:01:00 +1400";
const string finish = "2026-11-16 23:59:59.123 -1000";
string Record(string value, string date = start, string type = headphone, string unit = "dBASPL") =>
    $"""<Record type="{type}" sourceName="Private phone name" sourceVersion="17.0" device="Private device id" unit="{unit}" creationDate="{date}" startDate="{date}" endDate="{date}" value="{value}"/>""";
string Export(params string[] records) => """
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE HealthData [
    <!ELEMENT HealthData (ExportDate,Me,(Record|Correlation)*)>
    <!ATTLIST HealthData locale CDATA #REQUIRED>
    <!ELEMENT ExportDate EMPTY>
    <!ATTLIST ExportDate value CDATA #REQUIRED>
    <!ELEMENT Me EMPTY>
    <!ATTLIST Me HKCharacteristicTypeIdentifierDateOfBirth CDATA #IMPLIED>
    <!ELEMENT Record EMPTY>
    <!ATTLIST Record type CDATA #REQUIRED sourceName CDATA #REQUIRED
        sourceVersion CDATA #IMPLIED device CDATA #IMPLIED unit CDATA #IMPLIED
        creationDate CDATA #IMPLIED startDate CDATA #REQUIRED endDate CDATA #REQUIRED value CDATA #IMPLIED>
    <!ELEMENT Correlation (Record*)>
    ]>
    <HealthData locale="en_US"><ExportDate value="2026-11-20 10:00:00 +0000"/>
    <Me HKCharacteristicTypeIdentifierDateOfBirth="1987-07-01"/>
    """ + string.Concat(records) + "</HealthData>";
static void Check(bool condition, string failure)
{
    if (!condition) throw new InvalidOperationException(failure);
}
static void Reject<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}
static Day[] Read(string content, CancellationToken token = default)
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
    return AppleHeadphoneHistory.ReadXml(stream, token);
}
static void Archive(string path, params (string Name, string Text)[] entries)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (name, content) in entries)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
var first = new DateOnly(2026, 11, 15);
Day[] expected = [new(first, 42.5, 76), new(first.AddDays(1), 53, 53)];
string xml = Export(Record("53", finish), Record("76"), Record("42.5"), Record("NaN"), Record("Infinity"),
    Record("999", type: environmental), Record("10000", type: "HKQuantityTypeIdentifierStepCount", unit: "count"),
    Record("999", unit: "dB"), Record("888", "invalid"), $"<Correlation>{Record("777")}</Correlation>");
var directory = Directory.CreateTempSubdirectory("parity-history-tests-");
try
{
    string FileAt(string name) => Path.Combine(directory.FullName, name);
    File.WriteAllText(FileAt("export.xml"), xml);
    Archive(FileAt("legacy.ZIP"), ("apple_health_export/export.xml", xml), ("apple_health_export/export_cda.xml", "other"));
    foreach (var name in new[] { "export.xml", "legacy.ZIP" })
        Check(AppleHeadphoneHistory.Import(FileAt(name), default).SequenceEqual(expected), $"Import {name}");
    Console.WriteLine("PASS DTD, XML/ZIP, filters, aggregation and local dates");

    var records = Enumerable.Range(0, 110).OrderBy(i => (i * 37) % 110).Select(i =>
        Record("61", first.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 12:00:00 +0000")).ToArray();
    var newest = Read(Export(records));
    Check(newest.Select(d => d.Date).SequenceEqual(Enumerable.Range(20, 90).Select(i => first.AddDays(i))), "Retention order");
    Console.WriteLine("PASS unordered 90-day retention");

    Reject<FileNotFoundException>(() => AppleHeadphoneHistory.Import(FileAt("absent.xml"), default));
    Reject<InvalidDataException>(() => Read("<Other/>"));
    Reject<XmlException>(() => Read("<HealthData><Record"));
    Archive(FileAt("missing.zip"), ("export_cda.xml", xml));
    Archive(FileAt("duplicate.zip"), ("a/export.xml", xml), ("b/export.xml", xml));
    File.WriteAllText(FileAt("broken.zip"), "invalid");
    foreach (var name in new[] { "missing.zip", "duplicate.zip", "broken.zip" })
        Reject<InvalidDataException>(() => AppleHeadphoneHistory.Import(FileAt(name), default));
    Console.WriteLine("PASS malformed or ambiguous inputs");

    string saved = FileAt("history.json");
    var history = new AppleHeadphoneHistory(saved);
    Check(history.Load().Length == 0, "Missing history");
    history.Save(expected);
    Check(history.Load().SequenceEqual(expected), "Persistence roundtrip");
    var before = File.ReadAllBytes(saved);
    using (var json = JsonDocument.Parse(before))
        foreach (var item in json.RootElement.EnumerateArray())
            Check(item.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["Date", "Low", "High"]), "Extra private fields");
    using (var held = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool failed = false;
        try { history.Save(newest); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed, "Expected a Windows sharing failure");
    }
    Check(before.SequenceEqual(File.ReadAllBytes(saved)) && !Directory.EnumerateFiles(directory.FullName, "*.tmp").Any(), "Failed replace damaged history or left a temp file");
    history.Save(newest);
    Check(history.Load().SequenceEqual(newest), "Successful replace");
    File.WriteAllText(saved, """[{"Date":"2026-01-01","Low":99,"High":12}]""");
    Reject<InvalidDataException>(() => history.Load());
    history.Clear();
    Check(!File.Exists(saved), "Failed to clear");
    Console.WriteLine("PASS confidential summary, atomic replacement, rollback and clearing");

    var cancelled = new CancellationToken(canceled: true);
    Reject<OperationCanceledException>(() => Read(xml, cancelled));
    Reject<OperationCanceledException>(() => Read("<HealthData/>", cancelled));
    Check(Read(Export(Record("999").Replace("<Record ", "<Record xmlns='urn:unrelated' "))).Length == 0,
        "Foreign namespaced record accepted");
    Console.WriteLine("PASS cancellation and namespaced input");
}
finally
{
    directory.Delete(recursive: true);
}
