using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

internal static class BluetoothProvisioning
{
    internal const string ServiceName = "ParityBluetoothService";
    internal const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;
    internal const string BluetoothState = @"SOFTWARE\Parity\Bluetooth";
    internal const int RestartRequired = 3010;
    private const string DriverKey = @"SYSTEM\CurrentControlSet\Services\usbip2_ude";
    private const string InstallState = @"SOFTWARE\Parity\Installer";
    private const string DriverHash = "38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a";
    private const string DriverPublisher = "Cloudyne Systems (Scheibling Consulting AB)";
    private const string DriverArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
    private static readonly string Programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    internal static readonly string Folder = Path.Combine(Programs, "Parity Bluetooth");
    private static readonly string ServiceExe = Path.Combine(Folder, "Parity.Bluetooth.exe");
    private static readonly string DriverFolder = Path.Combine(Programs, "USBip");
    private static readonly string DriverExe = Path.Combine(DriverFolder, "usbip.exe");

    internal static bool WPodsPresent { get { return KeyExists(@"SYSTEM\CurrentControlSet\Services\WPodsService"); } }
    internal static bool Installed { get { return KeyExists(ServiceKey); } }
    internal static bool PendingRestart { get { return Flag(BluetoothState, "WaitsForRestart"); } }

    private static bool KeyExists(string key)
    {
        using (var value = Registry.LocalMachine.OpenSubKey(key)) return value != null;
    }

    private static bool Flag(string key, string name)
    {
        using (var value = Registry.LocalMachine.OpenSubKey(key))
            return value != null && (int?)value.GetValue(name) == 1;
    }

    private static void SetFlag(string name, bool enabled)
    {
        using (var key = Registry.LocalMachine.CreateSubKey(InstallState))
            if (enabled) key.SetValue(name, 1, RegistryValueKind.DWord);
            else key.DeleteValue(name, false);
    }

    private static void RequireAdmin()
    {
        using (var identity = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Administrator approval is required to change the Bluetooth component.");
    }

    private static void AssertRegular(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A link is blocking the Bluetooth installation at " + path + ".");
    }

    private static void VerifyTree(string path)
    {
        AssertRegular(path);
        foreach (string file in Directory.EnumerateFiles(path)) AssertRegular(file);
        foreach (string directory in Directory.EnumerateDirectories(path)) VerifyTree(directory);
    }

    private static void VerifyOwnedInstall()
    {
        if (Directory.Exists(Folder))
        {
            if (!Flag(InstallState, "OwnsBluetoothFolder"))
                throw new IOException("A folder already exists at " + Folder + ". Setup has left it in place.");
            VerifyTree(Folder);
        }
        using (var key = Registry.LocalMachine.OpenSubKey(ServiceKey))
        {
            if (key != null && (!String.Equals(key.GetValue("ImagePath") as string, "\"" + ServiceExe + "\"", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(ServiceExe)))
                throw new IOException("The registered Parity Bluetooth service points to an unexpected file. Its configuration was preserved.");
        }
    }

    internal static void Unpack(string directory)
    {
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("ParityBluetooth.zip"))
        {
            if (payload == null) throw new IOException("The Parity Bluetooth package is missing.");
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read)) zip.ExtractToDirectory(directory);
        }
    }

    private static string TemporaryFolder()
    {
        AssertRegular(Programs);
        string path = Path.Combine(Programs, ".ParityBluetooth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteOwnedTree(string path)
    {
        if (!Directory.Exists(path)) return;
        VerifyTree(path);
        Directory.Delete(path, true);
    }

    internal static int Install()
    {
        return WithLock(delegate
        {
            if (WPodsPresent)
                throw new InvalidOperationException("WPods still manages Bluetooth on this PC. Remove it through Installed apps before adding Parity's AirPods controls.");
            VerifyOwnedInstall();
            string work = TemporaryFolder();
            string incoming = Path.Combine(work, "package");
            string previous = Path.Combine(work, "previous");
            bool startAfterRestart = PendingRestart || Flag(InstallState, "UsbIpNeedsRestart");
            bool wasInstalled = Installed;
            bool swapped = false;
            bool retainBackup = false;
            byte[] remover = null;
            try
            {
                Unpack(incoming);
                remover = File.ReadAllBytes(Path.Combine(incoming, "Remove-ParityBluetooth.ps1"));
                string next = Path.Combine(incoming, "Parity.Bluetooth.exe");
                if (!wasInstalled)
                {
                    int check = Setup.Run(next, "--check-adapter", false);
                    if (check == 20)
                        throw new InvalidOperationException("No compatible USB Bluetooth adapter was detected. Windows Bluetooth is still in charge.");
                    if (check != 0) throw new IOException("The Bluetooth adapter check ended with code " + check + ".");
                }

                if (!File.Exists(DriverExe) && !KeyExists(DriverKey) && !Directory.Exists(DriverFolder))
                {
                    string driver = Path.Combine(incoming, "USBip-0.9.8.1-x64.exe");
                    try
                    {
                        Setup.RunVerifiedInstaller(driver, DriverArgs +
                            " /TYPE=compact /NOICONS /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /MERGETASKS=\"!desktopicon\" /RESTARTEXITCODE=3010",
                            DriverPublisher, DriverHash);
                    }
                    finally
                    {
                        if (File.Exists(DriverExe))
                        {
                            SetFlag("OwnsUsbIp", true);
                            SetFlag("UsbIpNeedsRestart", true);
                        }
                    }
                    startAfterRestart = true;
                }
                if (!File.Exists(DriverExe) || !KeyExists(DriverKey))
                    throw new IOException("USB/IP setup is incomplete. Restart Windows if the driver installer requested it, then retry.");
                AssertRegular(DriverFolder);
                AssertRegular(DriverExe);
                if (!Setup.TrustedPublisher(DriverExe, DriverPublisher))
                    throw new IOException("The installed USB/IP client could not be verified. Its files were preserved.");
                File.Delete(Path.Combine(incoming, "USBip-0.9.8.1-x64.exe"));

                if (wasInstalled)
                {
                    Setup.StopParityService();
                    if (Setup.Run(ServiceExe, "--restore-native", false) != 0)
                        throw new IOException("Windows could not take back the Bluetooth adapter. The previous Bluetooth files were retained.");
                }
                SetFlag("OwnsBluetoothFolder", true);
                if (Directory.Exists(Folder)) Directory.Move(Folder, previous);
                Directory.Move(incoming, Folder);
                swapped = true;
                int result = Setup.Run(ServiceExe, startAfterRestart ? "--install --no-start" : "--install", false);
                if (result != 0) throw new IOException("Parity Bluetooth returned setup code " + result + ".");
                SetFlag("UsbIpNeedsRestart", false);
                return startAfterRestart ? RestartRequired : 0;
            }
            catch
            {
                try
                {
                    if (swapped && Installed && Setup.Run(ServiceExe, "--uninstall", false) != 0)
                        throw new IOException("Restart Windows and rerun Parity Setup to repair Bluetooth.");
                    if (swapped) DeleteOwnedTree(Folder);
                    if (Directory.Exists(previous))
                    {
                        Directory.Move(previous, Folder);
                        if (wasInstalled && Setup.Run(ServiceExe, startAfterRestart ? "--install --no-start" : "--install", false) != 0)
                            throw new IOException("Restart Windows and rerun Parity Setup to repair Bluetooth.");
                    }
                    if (!wasInstalled && !Directory.Exists(Folder))
                    {
                        // A retry or uninstall can still remove a driver that was installed before the service failed.
                        bool driverOnly = remover != null && Flag(InstallState, "OwnsUsbIp");
                        SetFlag("OwnsBluetoothFolder", driverOnly);
                        if (driverOnly)
                        {
                            Directory.CreateDirectory(Folder);
                            File.WriteAllBytes(Path.Combine(Folder, "Remove-ParityBluetooth.ps1"), remover);
                        }
                    }
                }
                catch
                {
                    retainBackup = Directory.Exists(previous);
                    throw;
                }
                throw;
            }
            finally
            {
                if (!retainBackup)
                {
                    try { DeleteOwnedTree(work); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        });
    }

    private static int WithLock(Func<int> action)
    {
        RequireAdmin();
        using (var gate = new Mutex(false, @"Global\Parity.Bluetooth.Setup"))
        {
            bool acquired;
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Parity Bluetooth setup is already running.");
            try { return action(); }
            finally { gate.ReleaseMutex(); }
        }
    }
}
