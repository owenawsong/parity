#if PAIRSIDE_LIVE
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WPods.Audio;

internal static partial class AudioEndpoints
{

    internal sealed record PlayerStream(string AppId, string Image, bool OnPair, bool Active);

    internal sealed record SpatialSession(string AppId, string Image, string Output, string Endpoint = "");

    // Discover actual playing clients even when they use another endpoint or expose no media keys.
    internal static List<SpatialSession> ReadSpatialSessions()
    {
        List<SpatialSession> found = [];
        var enumerator = CreateEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(ERender, 1, out var outputs));
            try
            {
                Marshal.ThrowExceptionForHR(outputs.GetCount(out uint count));
                for (uint i = 0; i < count; i++)
                {
                    if (outputs.Item(i, out var device) < 0) continue;
                    try
                    {
                        string? name = null;
                        if (device.GetId(out var endpoint) < 0) continue;
                        VisitAudioSessions(device, activeOnly: true, (app, image, _) =>
                        {
                            if (string.IsNullOrEmpty(image)) return;
                            name ??= ReadFriendlyName(device) ?? "";
                            found.Add(new(app, image, name, endpoint));
                        });
                    }
                    catch (COMException) { /* An endpoint can be unplugged during this read. */ }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(outputs); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return found;
    }

    // GSMTC has a player identity but no output device. WASAPI has the actual
    // endpoint and process. Join only identities Windows exposes on both sides.
    internal static List<PlayerStream> PlayerStreams(ulong address)
    {
        List<PlayerStream> result = [];
        if (PairContainer(address) is not Guid pair || pair == Guid.Empty) return result;
        var enumerator = CreateEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(ERender, 1, out IMMDeviceCollection outputs));
            try
            {
                Marshal.ThrowExceptionForHR(outputs.GetCount(out uint count));
                for (uint i = 0; i < count; i++)
                {
                    Marshal.ThrowExceptionForHR(outputs.Item(i, out var device));
                    try
                    {
                        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out var props));
                        bool onPair;
                        try { onPair = ReadContainer(props) == pair; }
                        finally { Marshal.ReleaseComObject(props); }
                        var route = SpotifyRoute;
                        bool spatial = route != null && route.Address == address &&
                            device.GetId(out string id) >= 0 && id.Equals(route.Endpoint, StringComparison.OrdinalIgnoreCase);
                        VisitAudioSessions(device, activeOnly: false, (app, image, active) => result.Add(new(app, image,
                            onPair || spatial && (route!.WholeMix || Path.GetFileNameWithoutExtension(image).Equals(route.ProcessName, StringComparison.OrdinalIgnoreCase)), active)));
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(outputs); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return result;
    }

    private static void VisitAudioSessions(IMMDevice device, bool activeOnly, Action<string, string, bool> visit)
    {
        Guid iid = typeof(IAudioSessionManager2).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 0x17, 0, out object manager));
        try
        {
            Marshal.ThrowExceptionForHR(((IAudioSessionManager2)manager).GetSessionEnumerator(out var sessions));
            try
            {
                Marshal.ThrowExceptionForHR(sessions.GetCount(out int count));
                for (int i = 0; i < count; i++)
                {
                    Marshal.ThrowExceptionForHR(sessions.GetSession(i, out object instance));
                    try
                    {
                        var control = (IAudioSessionControl2)instance;
                        if (control.GetState(out int state) != 0 || state == 2 ||
                            control.GetProcessId(out uint pid) != 0 || pid == 0 || (activeOnly && !IsExternalActiveSession(state, pid))) continue;
                        using var process = OpenProcess(0x1000, false, pid);
                        if (process.IsInvalid) continue;
                        uint length = 512;
                        var buffer = new StringBuilder((int)length);
                        string app = GetApplicationUserModelId(process, ref length, buffer) == 0 ? buffer.ToString() : "";
                        length = 1024;
                        buffer = new StringBuilder((int)length);
                        string image = QueryFullProcessImageName(process, 0, buffer, ref length) ? buffer.ToString() : "";
                        if (app.Length != 0 || image.Length != 0) visit(app, image, state == 1);
                    }
                    finally { Marshal.ReleaseComObject(instance); }
                }
            }
            finally { Marshal.ReleaseComObject(sessions); }
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    internal static bool IsExternalActiveSession(int state, uint processId) =>
        state == 1 && processId != 0 && processId != Environment.ProcessId;

    // Include the inactive stream when checking the route of a paused player.
    // Competing outputs for the same app make that player ambiguous.
    internal static bool PlaysOnPair(IReadOnlyList<PlayerStream> streams, string appId, bool paused)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        bool found = false;
        foreach (var stream in streams)
        {
            if (!paused && !stream.Active) continue;
            if (!string.Equals(stream.AppId, appId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(stream.Image, appId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFileName(stream.Image), appId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFileNameWithoutExtension(stream.Image), appId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!stream.OnPair) return false;
            found = true;
        }
        return found;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder value);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder value, ref uint length);

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void GetAudioSessionControl(); void GetSimpleAudioVolume();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        void GetDisplayName(); void SetDisplayName(); void GetIconPath(); void SetIconPath();
        void GetGroupingParam(); void SetGroupingParam(); void RegisterAudioSessionNotification();
        void UnregisterAudioSessionNotification(); void GetSessionIdentifier(); void GetSessionInstanceIdentifier();
        [PreserveSig] int GetProcessId(out uint pid);
    }
}
#endif
