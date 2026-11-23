namespace WPods.Core;

/// <summary>Reported call-management layout on compatible AirPods. Preserve unknown firmware layouts.</summary>
internal static class CallGesture
{
    internal const byte SettingId = 0x24;

    internal static byte? Read(ReadOnlySpan<byte> packet) =>
        packet is [0x04, 0x00, 0x04, 0x00, 0x09, 0x00, SettingId, 0x00, 0x03, 0x00, 0x00] ? (byte)1 :
        packet is [0x04, 0x00, 0x04, 0x00, 0x09, 0x00, SettingId, 0x00, 0x02, 0x00, 0x00] ? (byte)2 : null;

    internal static byte[] Configure(byte presses) => presses switch
    {
        1 => Aap.Setting(SettingId, 0, 3),
        2 => Aap.Setting(SettingId, 0, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(presses)),
    };
}
