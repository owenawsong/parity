using System.Runtime.InteropServices;

namespace WPods.Audio;

internal static partial class AudioEndpoints
{
    internal sealed record MicrophoneUse(string App, string Endpoint, string Input);

#if PAIRSIDE_LIVE
    // Session metadata only. Opening capture streams here could switch the Bluetooth audio profile.
    internal static MicrophoneUse[] ActiveMicrophones()
    {
        var found = new HashSet<MicrophoneUse>();
        var enumerator = CreateEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(ECapture, 1, out IMMDeviceCollection inputs));
            try
            {
                Marshal.ThrowExceptionForHR(inputs.GetCount(out uint count));
                for (uint i = 0; i < count; ++i)
                {
                    if (inputs.Item(i, out var input) < 0) continue;
                    try
                    {
                        if (input.GetId(out string id) < 0) continue;
                        string? name = null;
                        VisitAudioSessions(input, activeOnly: true, (app, image, _) =>
                        {
                            string display = CaptureAppName(image, app);
                            if (display.Length == 0) return;
                            name ??= ReadFriendlyName(input);
                            if (!string.IsNullOrWhiteSpace(name)) found.Add(new(display, id, name));
                        });
                    }
                    // A headset can disappear or go idle between enumeration and session lookup.
                    catch (COMException) { }
                    finally { Marshal.ReleaseComObject(input); }
                }
            }
            finally { Marshal.ReleaseComObject(inputs); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return [.. found.OrderBy(use => use.App, StringComparer.OrdinalIgnoreCase)
            .ThenBy(use => use.Input, StringComparer.OrdinalIgnoreCase).ThenBy(use => use.Endpoint, StringComparer.OrdinalIgnoreCase)];
    }

    internal static string CaptureAppName(string image, string appId)
    {
        string name = Path.GetFileNameWithoutExtension(image);
        return name.ToLowerInvariant() switch
        {
            "msedge" => "Microsoft Edge", "chrome" => "Google Chrome", "firefox" => "Firefox",
            "ms-teams" or "teams" => "Microsoft Teams", "discord" => "Discord", "zoom" => "Zoom",
            "copilotapphost" => "Copilot app host", "snippingtool" => "Snipping Tool",
            "" => appId, _ => name,
        };
    }
#else
    internal static MicrophoneUse[] ActiveMicrophones() => [];
#endif
}
