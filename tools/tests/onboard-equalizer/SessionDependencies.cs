namespace WPods.Core;

// The replay has no installed service, Bluetooth adapter or user profile.
internal static class ServicePipes
{
    public const byte AapPacket = 0;
    public const byte AapEnded = 1;
    public const byte AapCallAudio = 3;
}

internal static class Native
{
    public const int ErrorDeviceNotConnected = 1167;
}

internal static class Log
{
    public static void Info(string category, string message) { }
    public static void Warn(string category, string message) { }
    public static void Error(string category, string message, Exception error) { }
    public static void Packet(bool outbound, ReadOnlySpan<byte> packet) { }
}

internal sealed record DeviceModel(string Number);
internal sealed record HostSettings(bool SleepPause = false);

internal sealed class SettingsStore
{
    public const StemModes DefaultStemModes = StemModes.NoiseCancellation | StemModes.Transparency;

    public DeviceModel? GetModel(ulong address) => null;
    public PodSettings GetPodSettings(ulong address) => PodSettings.None;
    public DeviceCapabilities GetCapabilities(ulong address) => DeviceCapabilities.None;
    public bool GetEarDetection(ulong address) => true;
    public MicSide GetMicSide(ulong address) => default;
    public bool GetAllowOff(ulong address) => false;
    public StemModes GetStemModes(ulong address) => DefaultStemModes;
    public HostSettings Get(ulong address) => new();
    public bool SetKeys(ulong address, ProximityKeys keys) => true;
    public void SetEarDetection(ulong address, bool enabled) { }
    public void SetMicSide(ulong address, MicSide side) { }
    public void SetAllowOff(ulong address, bool enabled) { }
    public void SetStemModes(ulong address, StemModes modes) { }
    public void SetPodSettings(ulong address, PodSettings settings) { }
    public void SetCapabilities(ulong address, DeviceCapabilities capabilities) { }
}
