namespace WPods;

// Pipe and registry identities must match in the desktop client and its Bluetooth service.
internal static class BluetoothServiceIdentity
{
#if PARITY_BLUETOOTH
    public static readonly bool IsParity = true;
    public const string Product = "Parity";
    public const string ServiceName = "ParityBluetoothService";
    public const string PipePrefix = "Parity.Bluetooth";
    public const string StateKey = @"SOFTWARE\Parity\Bluetooth";
    public const string MicSerialPrefix = "PARITY";
#else
    public static readonly bool IsParity = false;
    public const string Product = "WPods";
    public const string ServiceName = "WPodsService";
    public const string PipePrefix = "WPods";
    public const string StateKey = @"SOFTWARE\WPods\Service";
    public const string MicSerialPrefix = "WPODS";
#endif
    public const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;
}
