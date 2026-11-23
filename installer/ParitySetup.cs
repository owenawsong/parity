using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Setup
{
    private const string RuntimeKey = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64";
    private const string EdgeKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string DotNetUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    private const string WebViewUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const int UntrustedInstaller = 22352;
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    internal static readonly string DesktopExe = Path.Combine(Local, @"Programs\Pairside\Pairside.exe");

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 1 && args[0] == "--install-bluetooth")
        {
            try { return BluetoothProvisioning.Install(); }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "Parity Bluetooth", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
        if (args.Length == 2 && (args[0] == "--verify-package" || args[0] == "--verify-bluetooth-package"))
        {
            try
            {
                string directory = Path.GetFullPath(args[1]);
                RequireEmptyStage(directory);
                if (args[0] == "--verify-package") InstallDesktop(directory, new Progress<string>());
                else BluetoothProvisioning.Unpack(directory);
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "parity-package-verification.log"), error.ToString());
                return 1;
            }
        }

        using (var mutex = new System.Threading.Mutex(false, @"Local\Parity.Setup"))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (System.Threading.AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                MessageBox.Show("Parity Setup is already open.", "Parity Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            try
            {
                using (var wizard = new SetupWindow()) Application.Run(wizard);
            }
            finally { mutex.ReleaseMutex(); }
        }
        return 0;
    }

    private static void RequireEmptyStage(string directory)
    {
        string root = Path.GetPathRoot(directory);
        if (String.Equals(root, directory, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(directory) || (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()))
            throw new IOException("Package verification requires an empty staging directory.");
    }

    private static string ReadRegistry(RegistryHive hive, RegistryView view, string key, string property)
    {
        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
        using (var valueKey = baseKey.OpenSubKey(key))
            return valueKey == null ? null : valueKey.GetValue(property) as string;
    }

    private static bool HasDesktopRuntime()
    {
        string location = ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, RuntimeKey, "InstallLocation");
        if (String.IsNullOrEmpty(location))
            location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        string shared = Path.Combine(location, @"shared\Microsoft.WindowsDesktop.App");
        return Directory.Exists(shared) && Directory.EnumerateDirectories(shared, "10.*").Any(directory =>
        {
            Version version;
            return Version.TryParse(Path.GetFileName(directory), out version) && version.Major == 10
                && File.Exists(Path.Combine(directory, "Microsoft.WindowsDesktop.App.deps.json"));
        });
    }

    private static bool HasWebView()
    {
        var machine = ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, EdgeKey, "pv");
        var user = ReadRegistry(RegistryHive.CurrentUser, RegistryView.Default, EdgeKey, "pv");
        Version version;
        return (Version.TryParse(machine, out version) && version.Major > 0) ||
               (Version.TryParse(user, out version) && version.Major > 0);
    }

    internal static void StopParityService()
    {
        using (var service = new System.ServiceProcess.ServiceController(BluetoothProvisioning.ServiceName))
        {
            if (service.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) return;
            if (service.Status != System.ServiceProcess.ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }
    }

    internal static string Install(bool bluetooth, IProgress<string> status)
    {
        string temp = NewTemp();
        try
        {
            if (!HasDesktopRuntime())
            {
                status.Report("Installing the Microsoft .NET 10 Desktop Runtime…");
                RunVerifiedInstaller(Download(DotNetUrl, temp, "dotnet-desktop-x64.exe"), "/install /quiet /norestart", "Microsoft Corporation");
                if (!HasDesktopRuntime())
                    throw new InvalidOperationException("The .NET 10 Desktop Runtime could not be detected after installation.");
            }
            if (!HasWebView())
            {
                status.Report("Installing Microsoft Edge WebView2…");
                RunVerifiedInstaller(Download(WebViewUrl, temp, "MicrosoftEdgeWebView2Setup.exe"), "/silent /install", "Microsoft Corporation");
                if (!HasWebView())
                    throw new InvalidOperationException("The WebView2 Runtime could not be detected after installation.");
            }
            InstallDesktop(null, status);
            if (!bluetooth)
                return BluetoothProvisioning.Installed
                    ? "Parity is installed. You can open it now."
                    : "Parity desktop is installed. Run Setup again to add full AirPods controls.";
            status.Report("Waiting for administrator approval for AirPods controls…");
            int result;
            try
            {
                var start = new ProcessStartInfo(Application.ExecutablePath, "--install-bluetooth")
                {
                    UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory
                };
                using (var process = Process.Start(start))
                {
                    process.WaitForExit();
                    result = process.ExitCode;
                }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode != 1223) throw;
                return "Parity desktop is installed. AirPods controls were skipped when administrator approval was canceled.";
            }
            if (result == BluetoothProvisioning.RestartRequired)
                return "Parity is installed. Restart Windows when convenient to finish enabling AirPods controls.";
            if (result != 0)
                return "Parity desktop is installed. Run Setup again to retry the optional AirPods controls.";
            return "Parity is installed. Pair your AirPods through Windows Bluetooth and open Parity.";
        }
        finally { CleanTemp(temp); }
    }

    private static string NewTemp()
    {
        string path = Path.Combine(Path.GetTempPath(), "ParitySetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanTemp(string path)
    {
        try { Directory.Delete(path,true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Download(string uri,string directory,string name)
    {
        string file = Path.Combine(directory,name);
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        using (var client = new WebClient()) client.DownloadFile(uri,file);
        return file;
    }

    private static string PS(string text) { return "'" + text.Replace("'","''") + "'"; }
    private static string Quote(string path) { return "\"" + path + "\""; }

    private static string VerifyPublisher(string file, string publisher)
    {
        return "$s=Get-AuthenticodeSignature -LiteralPath " + PS(file) + ";" +
            "if($s.Status -ne 'Valid' -or ($s.SignerCertificate.SubjectName.Format($true) -split '\\r?\\n') " +
            "-cnotcontains " + PS("O=" + publisher) + "){exit " + UntrustedInstaller + "};";
    }

    private static int RunPowerShell(string script)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';" + script));
        return Run(PowerShell, "-NoProfile -NonInteractive -EncodedCommand " + encoded, false);
    }

    internal static bool TrustedPublisher(string file, string publisher)
    {
        return RunPowerShell(VerifyPublisher(file, publisher) + "exit 0") == 0;
    }

    internal static int RunVerifiedInstaller(string file, string args, string publisher, string hash = null)
    {
        // Keep the executable locked against replacement throughout verification and launch.
        using (var input = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (hash != null)
            {
                using (var sha = SHA256.Create())
                    if (!String.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""), hash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("A bundled component failed its integrity check.");
            }
            string start = "$p=[Diagnostics.Process]::Start(" + PS(file) + "," + PS(args) + ");$p.WaitForExit();exit $p.ExitCode";
            int result = RunPowerShell(VerifyPublisher(file, publisher) + start);
            if (result == UntrustedInstaller) throw new IOException("Setup could not verify the publisher of " + Path.GetFileName(file) + ".");
            if (result != 0 && result != BluetoothProvisioning.RestartRequired)
                throw new IOException(Path.GetFileName(file) + " returned setup code " + result + ".");
            return result;
        }
    }

    internal static int Run(string executable,string args,bool visible)
    {
        var start = new ProcessStartInfo(executable, args)
        {
            UseShellExecute=visible,
            CreateNoWindow=!visible,
            WindowStyle=visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            WorkingDirectory=Environment.SystemDirectory,
        };
        // A process launched from PowerShell 7 can inherit a module path that has no Windows PowerShell modules.
        if (!visible && String.Equals(executable, PowerShell, StringComparison.OrdinalIgnoreCase))
            start.EnvironmentVariables["PSModulePath"] = Path.Combine(Path.GetDirectoryName(PowerShell), "Modules");
        using (var process=Process.Start(start))
        {
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static void InstallDesktop(string stage,IProgress<string> status)
    {
        status.Report("Verifying the Parity package and installing app files…");
        string temp=NewTemp();
        try
        {
            string archive=Path.Combine(temp,"desktop.zip");
            using (var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("ParityDesktop.zip"))
            using (var zip=File.Create(archive))
                resource.CopyTo(zip);
            string contents=Path.Combine(temp,"contents");
            ZipFile.ExtractToDirectory(archive,contents);
            string script=Path.Combine(contents,"Install-Parity.ps1");
            string log=Path.Combine(Local,@"Pairside\setup.log");
            if (stage!=null) log=Path.Combine(Path.GetDirectoryName(stage),"parity-stage-setup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            string stageSwitch=stage==null ? "" : " -StageDirectory " + PS(stage);
            string command="$ErrorActionPreference='Stop';try{ & " + PS(script) + " -RegisterUninstall" +
                stageSwitch + " *>> " + PS(log) + ";exit 0}catch{$_ | Out-String | Add-Content -LiteralPath " + PS(log) + ";exit 1}";
            int result=Run(PowerShell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Quote(command),false);
            string target = stage==null ? DesktopExe : Path.Combine(stage,"Pairside.exe");
            string uninstall = Path.Combine(Path.GetDirectoryName(target),"Uninstall-Parity.ps1");
            if (result!=0 || !File.Exists(target) || !File.Exists(uninstall))
                throw new IOException("Parity could not be installed. Details are in " + log + ".");
        }
        finally { CleanTemp(temp); }
    }

}

internal sealed class SetupWindow : Form
{
    private readonly Label status = new Label();
    private readonly Button next = new Button();
    private readonly CheckBox bluetooth = new CheckBox();
    private readonly ProgressBar progress = new ProgressBar();
    private bool busy;
    private bool installed;

    internal SetupWindow()
    {
        Text="Parity Setup";
        ClientSize=new Size(544,365);
        MinimumSize=new Size(544,404);
        StartPosition=FormStartPosition.CenterScreen;
        MaximizeBox=false;
        Font=new Font("Segoe UI",10);
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(25), ColumnCount=1, RowCount=7 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading=new Label { Text="Install Parity", Font=new Font(Font.FontFamily,23,FontStyle.Bold),
            AutoSize=true, Margin=new Padding(0,0,0,12) };
        bluetooth.Text = "Add full AirPods controls";
        bluetooth.AutoSize = true;
        bool hasWPods = BluetoothProvisioning.WPodsPresent;
        bluetooth.Enabled = !hasWPods;
        bluetooth.Margin = new Padding(0, 0, 0, 6);
        var info=new Label
        {
            AutoSize=true, MaximumSize=new Size(474,0), Margin=new Padding(0,0,0,16),
            Text=hasWPods
                ? "You can install the desktop app now. Remove WPods through Installed apps before adding Parity's AirPods controls."
                : "Adds a Bluetooth service and a signed USB/IP driver for listening modes, EQ and other earbud settings. Windows might briefly lose Bluetooth and may ask you to restart."
        };
        var settings = new LinkLabel
        {
            Text = "Open Installed apps", Visible = hasWPods, AutoSize = true, Margin = new Padding(0, 0, 0, 12)
        };
        settings.LinkClicked += delegate { Process.Start("ms-settings:appsfeatures"); };
        status.Dock=DockStyle.Fill;
        status.Text="Parity installs for your Windows account.";
        status.ForeColor=SystemColors.GrayText;
        progress.Style=ProgressBarStyle.Marquee;
        progress.Dock=DockStyle.Top;
        progress.Height=6;
        progress.Margin=new Padding(0,0,0,13);
        progress.Visible=false;
        next.Text="Install";
        next.AutoSize=true;
        next.MinimumSize=new Size(112,34);
        next.Anchor=AnchorStyles.Right;
        next.Click+=Begin;
        layout.Controls.Add(heading);
        layout.Controls.Add(bluetooth);
        layout.Controls.Add(info);
        layout.Controls.Add(settings);
        layout.Controls.Add(status);
        layout.Controls.Add(progress);
        layout.Controls.Add(next);
        Controls.Add(layout);
        AcceptButton=next;
        FormClosing+=delegate(object sender,FormClosingEventArgs args) { if (busy) args.Cancel=true; };
    }

    private async void Begin(object sender,EventArgs args)
    {
        if (installed)
        {
            try { Process.Start(Setup.DesktopExe); Close(); }
            catch (Win32Exception error) { status.Text=error.Message; }
            return;
        }

        busy=true;
        next.Enabled=false;
        bool addControls = bluetooth.Checked && bluetooth.Enabled;
        bluetooth.Enabled=false;
        progress.Visible=true;
        status.ForeColor=SystemColors.ControlText;
        try
        {
            var reporter=new Progress<string>(message=> status.Text=message);
            status.Text=await Task.Run(()=>Setup.Install(addControls, reporter));
            installed=true;
            next.Text="Open Parity";
        }
        catch (Exception error)
        {
            status.Text=error.Message;
            next.Text="Try again";
        }
        finally
        {
            busy=false;
            progress.Visible=false;
            next.Enabled=true;
            if (!installed) bluetooth.Enabled=!BluetoothProvisioning.WPodsPresent;
        }
    }
}
