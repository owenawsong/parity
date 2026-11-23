using System.Text.Json;
using System.Text.Json.Serialization;
using WPods.Core;

namespace WPods.Ui;

/// <summary>One locales\&lt;iso&gt;.json file.</summary>
internal sealed class LocaleFile
{
    public string OriginalName { get; set; } = "";
    public string EngName { get; set; } = "";
    public Dictionary<string, string> Labels { get; set; } = [];
}

/// <summary>The locale in use: code, labels (en-US underneath for missing keys) and direction.</summary>
internal sealed record ResolvedLocale(string Code, Dictionary<string, string> Labels, bool Rtl)
{
    public string Text(string key) => Labels.GetValueOrDefault(key, key);
}

/// <summary>
/// Locale files installed as locales\&lt;iso&gt;.json (filename = ISO code). Resolution: chosen code (or the Windows UI
/// language for "system") -> a file with the same language subtag -> en-US -> another en-* -> any file.
/// </summary>
internal sealed class Localization
{
    private const string Fallback = "en-US";

    private readonly SortedDictionary<string, LocaleFile> _files;

    private Localization(SortedDictionary<string, LocaleFile> files)
    {
        _files = files;
    }

    /// <summary>All installed locales by code (sorted).</summary>
    public IReadOnlyDictionary<string, LocaleFile> Files => _files;

    /// <summary>Loads every readable locale file; null when there is none.</summary>
    public static Localization? Load(string directory)
    {
        var files = new SortedDictionary<string, LocaleFile>(StringComparer.OrdinalIgnoreCase);
        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("ui", $"locales folder unreadable: {directory}", e);
            return null;
        }
        foreach (string path in paths)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize(stream, LocaleJson.Default.LocaleFile) is { } file)
                {
                    files[Path.GetFileNameWithoutExtension(path)] = file;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Skip a broken file; the others still work.
                Log.Warn("ui", $"locale file skipped: {path}: {e.Message}");
            }
        }
        return files.Count == 0 ? null : new Localization(files);
    }

    /// <summary><paramref name="choice"/> = "system" or an ISO code.</summary>
    public ResolvedLocale Resolve(string choice)
    {
        string wanted = choice == "system" ? NativeUi.UserUiLanguage() : choice;
        string code = Pick(wanted);
        LocaleFile file = _files[code];
        var labels = new Dictionary<string, string>(file.Labels, StringComparer.Ordinal);
        if (!code.Equals(Fallback, StringComparison.OrdinalIgnoreCase) && _files.TryGetValue(Fallback, out LocaleFile? english))
        {
            foreach ((string key, string text) in english.Labels)
            {
                labels.TryAdd(key, text);
            }
        }
        labels["app.name"] = AppIdentity.Name;
        if (AppIdentity.IsPairside)
        {
            // Keep the upstream project and service names in their legal and service messages.
            foreach (string key in labels.Keys.ToArray())
            {
                if (key.StartsWith("firstRun.", StringComparison.Ordinal) ||
                    key.StartsWith("pair.", StringComparison.Ordinal) ||
                    key.StartsWith("adapter.", StringComparison.Ordinal) ||
                    key.StartsWith("hotkeys.", StringComparison.Ordinal) ||
                    key.StartsWith("update.", StringComparison.Ordinal) ||
                    key is "tray.open" or "tray.quit" or "legal.firstRunBody" or "legal.notAppleBody" or
                        "legal.trademarksBody" or "legal.ownFeaturesTitle" or "legal.ownFeaturesBody" or
                        "legal.thirdPartyBody" or "legal.warrantyBody" ||
                    key is "mic.note" or "restart.body" or "windows.handsFreeDesc" or "osd.conflict" or
                        "toast.micModelFailed" or "toast.settingsReset" or "toast.webviewFailed" or "toast.restartToFinish")
                {
                    labels[key] = labels[key].Replace("WPods", AppIdentity.Name, StringComparison.Ordinal);
                }
            }
        }
        string language = Subtag(code);
        bool rtl = language.Equals("ar", StringComparison.OrdinalIgnoreCase) || language.Equals("he", StringComparison.OrdinalIgnoreCase)
            || language.Equals("fa", StringComparison.OrdinalIgnoreCase) || language.Equals("ur", StringComparison.OrdinalIgnoreCase);
        return new ResolvedLocale(code, labels, rtl);
    }

    private string Pick(string wanted)
    {
        if (wanted.Length > 0)
        {
            if (_files.ContainsKey(wanted))
            {
                return wanted;
            }
            if (FirstWithLanguage(Subtag(wanted)) is { } sameLanguage)
            {
                return sameLanguage;
            }
        }
        if (_files.ContainsKey(Fallback))
        {
            return Fallback;
        }
        if (FirstWithLanguage("en") is { } english)
        {
            return english;
        }
        return _files.Keys.First();
    }

    private string? FirstWithLanguage(string language)
    {
        foreach (string code in _files.Keys)
        {
            if (Subtag(code).Equals(language, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }
        return null;
    }

    private static string Subtag(string code)
    {
        int dash = code.IndexOf('-');
        return dash < 0 ? code : code[..dash];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LocaleFile))]
internal sealed partial class LocaleJson : JsonSerializerContext;
