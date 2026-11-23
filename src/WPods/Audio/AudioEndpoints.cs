using System.Runtime.InteropServices;

namespace WPods.Audio;

/// <summary>Metadata for the Windows default multimedia endpoints. Looking up a name opens no audio stream.</summary>
internal static partial class AudioEndpoints
{
#if PAIRSIDE_LIVE
    internal sealed record SpatialRoute(ulong Address, string Endpoint, string ProcessName, bool WholeMix = false);
    internal static volatile SpatialRoute? SpotifyRoute;
#endif
    internal const int ERender = 0;
    internal const int ECapture = 1;
    internal const int EMultimedia = 1;
    private const int ECommunications = 2;
    private static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    private static readonly PropertyKey ContainerId = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    internal sealed record Route(string Id, string? Name, Guid? Container = null, bool? Muted = null);
    internal sealed record Routes(Route? Input, Route? CommunicationsInput, Route? Output, bool HardwareCallInput,
        Route? CommunicationsOutput);

    internal static Routes ReadDefaults(ulong pairAddress = 0)
    {
        var enumerator = CreateEnumerator();
        try
        {
            Route? calls = ReadRoute(enumerator, ECapture, ECommunications, readContainer: pairAddress != 0);
            bool hardwareCallInput = pairAddress != 0 && calls?.Container is Guid container && container != Guid.Empty &&
                container == PairContainer(pairAddress);
            return new Routes(ReadRoute(enumerator, ECapture, EMultimedia), calls,
                ReadRoute(enumerator, ERender, EMultimedia), hardwareCallInput, ReadRoute(enumerator, ERender, ECommunications));
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    internal static bool IsDefaultCallsInput(IMMDeviceEnumerator enumerator, string endpoint)
    {
        if (enumerator.GetDefaultAudioEndpoint(ECapture, ECommunications, out IMMDevice device) < 0)
            return false;
        try
        {
            return device.GetId(out string id) >= 0 && string.Equals(id, endpoint, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static Route? ReadRoute(IMMDeviceEnumerator enumerator, int flow, int role, bool readContainer = false)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out IMMDevice device) < 0)
        {
            return null;
        }
        try
        {
            if (device.GetId(out string id) < 0) return null;
            if (device.OpenPropertyStore(0, out IPropertyStore store) < 0)
            {
                return new Route(id, null);
            }
            try
            {
                int hr = store.GetValue(in FriendlyName, out PropVariant value);
                try
                {
                    return new Route(id, hr >= 0 && value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null,
                        readContainer ? ReadContainer(store) : null,
                        flow == ECapture && role == ECommunications ? ReadMute(device) : null);
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    internal static bool? ReadMute(IMMDevice device)
    {
        var volume = OpenEndpointVolume(device);
        if (volume is null) return null;
        try
        {
            return volume.GetMute(out int muted) >= 0 ? muted != 0 : null;
        }
        finally { Marshal.ReleaseComObject(volume); }
    }

    internal static string? ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out IPropertyStore store) < 0) return null;
        try
        {
            int hr = store.GetValue(in FriendlyName, out PropVariant value);
            try { return hr >= 0 && value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    internal static IAudioEndpointVolume? OpenEndpointVolume(IMMDevice device)
    {
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        return device.Activate(ref iid, 0x17, 0, out object volume) >= 0 ? (IAudioEndpointVolume)volume : null;
    }

    internal static bool? ReadInputMute(string endpoint)
    {
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(endpoint, out var device) < 0) return null;
            try { return ReadMute(device); }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        // Preserve the IAudioEndpointVolume vtable order. The two getters are read-only.
        void RegisterControlChangeNotify();
        void UnregisterControlChangeNotify();
        void GetChannelCount();
        void SetMasterVolumeLevel();
        void SetMasterVolumeLevelScalar();
        void GetMasterVolumeLevel();
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel();
        void SetChannelVolumeLevelScalar();
        void GetChannelVolumeLevel();
        void GetChannelVolumeLevelScalar();
        void SetMute();
        [PreserveSig] int GetMute(out int muted);
    }

    private static Guid? ReadContainer(IPropertyStore store)
    {
        int hr = store.GetValue(in ContainerId, out PropVariant value);
        try
        {
            return hr >= 0 && value.Type == 72 && value.Pointer != 0 ? Marshal.PtrToStructure<Guid>(value.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    // Compare the multimedia output to the physical Bluetooth device. User-editable names
    // and the console or communications defaults cannot identify this route.
    internal sealed record PlaybackOutput(string Id, string? Name, bool? OnPair)
    {
        internal string? OtherOutput => OnPair == false ? Name : null;
    }

    internal static PlaybackOutput? ReadPlaybackOutput(ulong address)
    {
        var enumerator = CreateEnumerator();
        try
        {
            var output = ReadRoute(enumerator, ERender, EMultimedia, readContainer: true);
            if (output is null) return null;
#if PAIRSIDE_LIVE
            if (SpotifyRoute is { WholeMix: true } mix && mix.Address == address && mix.Endpoint == output.Id)
                return new PlaybackOutput(output.Id, output.Name, true);
#endif
            bool? onPair = null;
            if (address != 0 && output.Container is Guid container && container != Guid.Empty &&
                PairContainer(address) is Guid pair && pair != Guid.Empty)
            {
                onPair = container == pair;
            }
            return new PlaybackOutput(output.Id, string.IsNullOrWhiteSpace(output.Name) ? null : output.Name, onPair);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    internal static bool? PlaybackBelongsToPair(string endpointId, ulong address)
    {
        if (address == 0 || PairContainer(address) is not Guid pair || pair == Guid.Empty) return null;
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(endpointId, out var endpoint) < 0) return null;
            try
            {
                if (endpoint.OpenPropertyStore(0, out var store) < 0) return null;
                try { return ReadContainer(store) is Guid container && container != Guid.Empty ? container == pair : null; }
                finally { Marshal.ReleaseComObject(store); }
            }
            finally { Marshal.ReleaseComObject(endpoint); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    internal enum PlaybackChange { Pending, Selected, Superseded, Unavailable }

    internal static PlaybackChange ComparePlaybackChoice(string original, string target, string? current)
    {
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return PlaybackChange.Selected;
        return string.Equals(current, original, StringComparison.OrdinalIgnoreCase) ? PlaybackChange.Pending : PlaybackChange.Superseded;
    }

#if PAIRSIDE_LIVE
    // The device container has already identified the pair. On older Bluetooth
    // stacks it can expose stereo playback and a separate mono call endpoint.
    internal static string? SelectPairRenderEndpoint(IReadOnlyList<string> matching, string? preferred,
        Func<string, int?> channels, bool requireStereo)
    {
        string? selected = matching.FirstOrDefault(id => id.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (selected != null && (!requireStereo || channels(selected) is null or >= 2)) return selected;
        if (matching.Count == 1)
            return !requireStereo || channels(matching[0]) is null or >= 2 ? matching[0] : null;

        string? stereo = null;
        foreach (string endpoint in matching)
        {
            int? count = channels(endpoint);
            if (count == null) return null;
            if (count < 2) continue;
            if (stereo != null) return null;
            stereo = endpoint;
        }
        return stereo;
    }
#endif

    private static Guid? PairContainer(ulong address)
    {
        const uint presentBluetooth = 0x101; // CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT
        string prefix = $"BTHENUM\\DEV_{address:X12}\\";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_Size(out uint length, "BTHENUM", presentBluetooth) != 0 || length < 2) return null;
            char[] ids = new char[length];
            uint result = CM_Get_Device_ID_List("BTHENUM", ids, length, presentBluetooth);
            if (result == 0x1A) continue; // CR_BUFFER_SMALL when devices change between calls
            if (result != 0) return null;
            for (int start = 0; start < ids.Length && ids[start] != '\0';)
            {
                int end = Array.IndexOf(ids, '\0', start);
                if (end < 0) break;
                var id = ids.AsSpan(start, end - start);
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    CM_Locate_DevNode(out uint node, id.ToString(), 0) == 0)
                {
                    uint size = 16;
                    if (CM_Get_DevNode_Property(node, in ContainerId, out uint type, out Guid guid, ref size, 0) == 0 &&
                        type == 13 && size == 16) return guid;
                }
                start = end + 1;
            }
            break;
        }
        return null;
    }

    // Prefer the pair's multimedia endpoint. Away from that default, use its
    // active render endpoint only when the physical device has a unique match.
    internal static string? FindPairPlaybackEndpoint(IMMDeviceEnumerator enumerator, ulong address)
    {
        if (PairContainer(address) is not Guid pair || pair == Guid.Empty) return null;
        const int active = 1;
        Route? output = ReadRoute(enumerator, ERender, EMultimedia, readContainer: true);
        if (output?.Container == pair) return output.Id;

        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(ERender, active, out IMMDeviceCollection devices));
        try
        {
            Marshal.ThrowExceptionForHR(devices.GetCount(out uint count));
            string? match = null;
            for (uint index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(devices.Item(index, out IMMDevice device));
                try
                {
                    if (!IsActivePairPlaybackEndpoint(device, pair) || device.GetId(out string id) < 0) continue;
                    if (match is not null) return null;
                    match = id;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            return match;
        }
        finally { Marshal.ReleaseComObject(devices); }
    }

    internal static bool IsActivePairPlaybackEndpoint(IMMDevice device, ulong address) =>
        PairContainer(address) is Guid pair && pair != Guid.Empty && IsActivePairPlaybackEndpoint(device, pair);

    private static bool IsActivePairPlaybackEndpoint(IMMDevice device, Guid pair)
    {
        if (device is not IMMEndpoint endpoint || endpoint.GetDataFlow(out int flow) < 0 || flow != ERender ||
            device.GetState(out int state) < 0 || state != 1 || device.OpenPropertyStore(0, out var store) < 0) return false;
        try { return ReadContainer(store) == pair; }
        finally { Marshal.ReleaseComObject(store); }
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List(string filter, [Out] char[] ids, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CM_Get_DevNode_Property(uint node, in PropertyKey key, out uint type, out Guid value, ref uint size, uint flags);

    internal static int? PlaybackPeakDb()
    {
        var meter = OpenPeakMeter(null);
        if (meter is null) return null;
        try { return ReadPeak(meter); }
        finally { Marshal.ReleaseComObject(meter); }
    }

    // Meter the selected Windows input. The meter reads signal strength and never opens
    // a capture stream or buffers microphone samples. The caller owns the COM reference.
    internal static IAudioMeterInformation? OpenPeakMeter(string? endpoint)
    {
        var enumerator = CreateEnumerator();
        try
        {
            IMMDevice device;
            int hr = endpoint is null ? enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device)
                                      : enumerator.GetDevice(endpoint, out device);
            if (hr < 0) return null;
            try
            {
                Guid id = typeof(IAudioMeterInformation).GUID;
                return device.Activate(ref id, 0x17, 0, out object instance) >= 0 ? (IAudioMeterInformation)instance : null;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    internal static int? ReadPeak(IAudioMeterInformation meter) =>
        meter.GetPeakValue(out float peak) >= 0 ? PeakDb(peak) : null;

    internal static int PeakDb(double peak) => (int)Math.Round(Math.Clamp(20 * Math.Log10(Math.Max(peak, .001)), -60, 0));

    // Only change the Communications role, and verify it by reading Windows' default back.
    internal static bool SetPairCallsInput(string endpoint, ulong address)
    {
        if (!IsPairMicrophone(endpoint, address)) return false;
        var policy = (IEndpointPolicy)(object)new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(endpoint, ECommunications));
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (ReadDefaults().CommunicationsInput?.Id == endpoint) return true;
                if (attempt < 5) Thread.Sleep(80);
            }
            return false;
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointPolicy
    {
        // Preserve the ten preceding IPolicyConfig slots. Only SetDefaultEndpoint is invoked.
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string endpoint, int role);
    }

    internal static bool IsPairMicrophone(string endpoint, ulong address)
    {
        if (address == 0) return false;
        if (IsVirtualPairMicrophone(endpoint, address)) return true;
        if (PairContainer(address) is not Guid pair || pair == Guid.Empty) return false;
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(endpoint, out var device) < 0) return false;
            try
            {
                if (device is not IMMEndpoint endpointInfo || endpointInfo.GetDataFlow(out int flow) < 0 ||
                    flow != ECapture || device.OpenPropertyStore(0, out var store) < 0) return false;
                try { return ReadContainer(store) == pair; }
                finally { Marshal.ReleaseComObject(store); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMEndpoint
    {
        [PreserveSig] int GetDataFlow(out int flow);
    }

    internal static bool IsVirtualPairMicrophone(string endpoint, ulong address)
    {
        string instance = "SWD\\MMDEVAPI\\" + endpoint;
        string serial = $"\\WPODS{address:X12}";
        if (CM_Locate_DevNode(out uint node, instance, 0) != 0) return false;
        char[] id = new char[256];
        for (int depth = 0; depth < 12 && CM_Get_Parent(out node, node, 0) == 0; depth++)
        {
            if (CM_Get_Device_ID(node, id, id.Length, 0) == 0 &&
                id.AsSpan(0, Array.IndexOf(id, '\0')).EndsWith(serial, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint node, string instance, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID(uint node, [Out] char[] buffer, int length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PropertyKey(Guid Format, uint Id);

    // PROPVARIANT on x64, including the counted-array union member.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal nint Pointer;
    }

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
    }

    private static readonly Guid EnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorInterfaceId = typeof(IMMDeviceEnumerator).GUID;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    internal static IMMDeviceEnumerator CreateEnumerator()
    {
        // NAudio activates the same COM class. A shared RCW can be typed as its
        // coclass, and either library releasing that wrapper can affect the other.
        Marshal.ThrowExceptionForHR(CoCreateInstance(in EnumeratorClassId, 0, 1, in EnumeratorInterfaceId, out nint instance));
        try { return (IMMDeviceEnumerator)Marshal.GetUniqueObjectForIUnknown(instance); }
        finally { Marshal.Release(instance); }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, nint format, nint session);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int frames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong position, out ulong qpc);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
