#if PAIRSIDE_LIVE
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
using WPods.Core;

namespace WPods.Nearby;

public sealed partial class NearbyService
{
    private const ushort AppleCompanyId = 0x004c;
    private BluetoothLEAdvertisementWatcher? _windowsWatcher;
    private int _finderRequested;
    private bool _windowsFinderActive;
    private int _scanFailures;
    private bool _legacyAdvertisementsOnly;
    private bool _passiveOnly;
    private bool _nativeWatcherStarted;

    internal string DiscoveryStatus => Volatile.Read(ref _windowsWatcher)?.Status == BluetoothLEAdvertisementWatcherStatus.Started
        ? "watching" : Volatile.Read(ref _nativeWatcherStarted) ? "legacy" : "waiting";

    internal void SetFinderActive(bool active)
    {
        int desired = active ? 1 : 0;
        if (Interlocked.Exchange(ref _finderRequested, desired) != desired)
        {
            ThreadPool.QueueUserWorkItem(static service => service.RestartScan(finderChange: true), this, preferLocal: false);
        }
    }

    // Called under _scanLock. Match the company only: a case record can follow an unrelated Continuity field.
    private bool TryStartWindowsWatcher(bool finderActive)
    {
        try
        {
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = finderActive ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive,
                AllowExtendedAdvertisements = !_legacyAdvertisementsOnly,
            };
            if (finderActive)
            {
                watcher.SignalStrengthFilter.SamplingInterval = TimeSpan.Zero;
            }
            using var company = new DataWriter();
            company.WriteBytes([0x4c, 0x00]);
            watcher.AdvertisementFilter.BytePatterns.Add(new BluetoothLEAdvertisementBytePattern(
                BluetoothLEAdvertisementDataTypes.ManufacturerSpecificData, 0, company.DetachBuffer()));
            Volatile.Write(ref _windowsWatcher, watcher);
            watcher.Received += OnWindowsAdvert;
            watcher.Stopped += OnWindowsWatcherStopped;
            watcher.Start();
            _windowsFinderActive = finderActive;
            Log.Info("nearby", finderActive ? "case finder foreground BLE scan started" : "BLE watcher started for AirPods and charging-case broadcasts");
            return true;
        }
        catch (Exception e) when (e is COMException or ArgumentException)
        {
            StopWindowsWatcher();
            Log.Warn("nearby", $"BLE watcher could not start (finder={finderActive}): 0x{e.HResult:X8}");
            return false;
        }
    }

    private void StopWindowsWatcher()
    {
        if (Interlocked.Exchange(ref _windowsWatcher, null) is { } watcher)
        {
            watcher.Received -= OnWindowsAdvert;
            watcher.Stopped -= OnWindowsWatcherStopped;
            watcher.Stop();
        }
    }

    // Bluetooth can abort a successfully started watcher without a radio arrival or a link change.
    // Retry only its own generation, so a late stop cannot replace a newer, working watcher.
    private async void OnWindowsWatcherStopped(BluetoothLEAdvertisementWatcher watcher, BluetoothLEAdvertisementWatcherStoppedEventArgs reason)
    {
        if (!ReferenceEquals(watcher, Volatile.Read(ref _windowsWatcher))) return;
        BluetoothError error = reason.Error;
        int failures = Math.Min(Interlocked.Increment(ref _scanFailures), 7);
        int delayMs = Math.Min(60_000, 750 << (failures - 1));
        Log.Warn("nearby", $"BLE watcher stopped ({error}); retry in {delayMs} ms");
        await Task.Delay(delayMs).ConfigureAwait(false);
        lock (_scanLock)
        {
            if (_disposed || !ReferenceEquals(watcher, _windowsWatcher)) return;
            if (error == BluetoothError.NotSupported)
            {
                if (_legacyAdvertisementsOnly && _windowsFinderActive) _passiveOnly = true;
                _legacyAdvertisementsOnly = true;
            }
            RestartScan();
        }
    }

    private void OnWindowsAdvert(BluetoothLEAdvertisementWatcher watcher, BluetoothLEAdvertisementReceivedEventArgs report)
    {
        try
        {
            short rssi = report.RawSignalStrengthInDBm;
            if (rssi == LostRssi || !ReferenceEquals(Volatile.Read(ref _windowsWatcher), watcher))
            {
                return;
            }
            Interlocked.Exchange(ref _scanFailures, 0);
            foreach (BluetoothLEManufacturerData section in report.Advertisement.GetManufacturerDataByCompanyId(AppleCompanyId))
            {
                using var reader = DataReader.FromBuffer(section.Data);
                var data = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(data);
                byte type = report.BluetoothAddressType == BluetoothAddressType.Random ? (byte)1 : (byte)0;
                HandleAdvert(report.BluetoothAddress, type, rssi, data);
            }
        }
        catch (Exception e)
        {
            Log.Error("nearby", "advert handler failed", e);
        }
    }
}
#endif
