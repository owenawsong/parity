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
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Setup
{
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\WPodsService";
    private const string RuntimeKey = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64";
    private const string EdgeKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string WPodsUrl = "https://github.com/FPNDev/wpods/releases/download/v0.4.3/WPods-Setup.exe";
    private const string WPodsHash = "07352b8faae07204e8f754097ce33a193d559dff93bdb1dd8593401046050c25";
    private const string DotNetUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    private const string WebViewUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const int UntrustedMicrosoftInstaller = 22352;
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    internal static readonly string DesktopExe = Path.Combine(Local, @"Programs\Pairside\Pairside.exe");

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--verify-package")
        {
            try
            {
                string directory = Path.GetFullPath(args[1]);
                RequireEmptyStage(directory);
                InstallDesktop(directory, new Progress<string>());
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "parity-package-verification.log"), error.ToString());
                return 1;
            }
        }

        using (var mutex = new Mutex(false, @"Local\Parity.Setup"))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
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

    internal static bool HasWPods
    {
        get { return !String.IsNullOrEmpty(ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, ServiceKey, "ImagePath")); }
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

    private static bool ServiceRunning()
    {
        using (var service = new ServiceController("WPodsService"))
        {
            try { return service.Status == ServiceControllerStatus.Running; }
            catch (InvalidOperationException) { return false; }
        }
    }

    internal static string Install(IProgress<string> status)
    {
        string temp = NewTemp();
        try
        {
            if (!HasWPods)
            {
                status.Report("Downloading WPods Bluetooth setup…");
                string setup = Download(WPodsUrl, temp, "WPods-Setup.exe");
                status.Report("Complete the WPods setup window to prepare Bluetooth…");
                using (var file = File.Open(setup,FileMode.Open,FileAccess.Read,FileShare.Read))
                using (var sha = SHA256.Create())
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                    if (!String.Equals(actual,WPodsHash,StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The WPods download failed its integrity check. Try again.");
                    int result = Run(setup, "/SP- /NORESTART /MERGETASKS=\"!desktopicon,!startup,!autoupdate\"", true);
                    if (result != 0 && result != 3010)
                        throw new InvalidOperationException("WPods setup ended with code " + result + ". Check its setup window or retry.");
                }
                if (!HasWPods)
                    throw new InvalidOperationException("WPods Bluetooth setup did not finish. Open Parity Setup to try again.");
            }
            if (!HasDesktopRuntime())
            {
                status.Report("Installing the Microsoft .NET 10 Desktop Runtime…");
                InstallMicrosoft(Download(DotNetUrl, temp, "dotnet-desktop-x64.exe"), "/install /quiet /norestart");
                if (!HasDesktopRuntime())
                    throw new InvalidOperationException("The .NET 10 Desktop Runtime could not be detected after installation.");
            }
            if (!HasWebView())
            {
                status.Report("Installing Microsoft Edge WebView2…");
                InstallMicrosoft(Download(WebViewUrl, temp, "MicrosoftEdgeWebView2Setup.exe"), "/silent /install");
                if (!HasWebView())
                    throw new InvalidOperationException("The WebView2 Runtime could not be detected after installation.");
            }
            InstallDesktop(null,status);
            for (int attempt=0; attempt<8 && !ServiceRunning(); attempt++) Thread.Sleep(1000);
            return ServiceRunning()
                ? "Parity is installed. Pair your AirPods in Windows and open Parity from Start."
                : "Parity is installed. Restart Windows if WPods asked you to, then open Parity from Start.";
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

    private static void InstallMicrosoft(string file,string args)
    {
        // Hold the verified installer open until it exits so it cannot be swapped after validation.
        string script = "$f=[IO.File]::Open(" + PS(file) + ",'Open','Read','Read');try{" +
            "$s=Get-AuthenticodeSignature -LiteralPath " + PS(file) + ";" +
            "if($s.Status -ne 'Valid' -or ($s.SignerCertificate.SubjectName.Format($true) -split '\\r?\\n') " +
            "-cnotcontains 'O=Microsoft Corporation'){exit " + UntrustedMicrosoftInstaller + "};" +
            "$p=[Diagnostics.Process]::Start(" + PS(file) + "," + PS(args) + ");" +
            "$p.WaitForExit();exit $p.ExitCode}finally{$f.Dispose()}";
        int result = Run(PowerShell,"-NoProfile -NonInteractive -Command " + Quote(script),false);
        if (result == UntrustedMicrosoftInstaller)
            throw new IOException("The downloaded prerequisite could not be verified as Microsoft-signed.");
        if (result != 0 && result != 3010)
            throw new IOException("A Microsoft prerequisite installer ended with exit code " + result + ".");
    }

    private static int Run(string executable,string args,bool visible)
    {
        var start = new ProcessStartInfo(executable, args)
        {
            UseShellExecute=visible,
            CreateNoWindow=!visible,
            WindowStyle=visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
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

    internal static bool WPodsTrayRunning()
    {
        var processes=Process.GetProcessesByName("WPods");
        foreach (var process in processes) process.Dispose();
        return processes.Length>0;
    }
}

internal sealed class SetupWindow : Form
{
    private readonly Label status = new Label();
    private readonly Button next = new Button();
    private readonly ProgressBar progress = new ProgressBar();
    private bool busy;
    private bool installed;

    internal SetupWindow()
    {
        Text="Parity Setup";
        ClientSize=new Size(536,322);
        MinimumSize=new Size(512,350);
        StartPosition=FormStartPosition.CenterScreen;
        MaximizeBox=false;
        Font=new Font("Segoe UI",10);
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(25), ColumnCount=1, RowCount=5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading=new Label { Text="Install Parity", Font=new Font(Font.FontFamily,23,FontStyle.Bold),
            AutoSize=true, Margin=new Padding(0,0,0,12) };
        var info=new Label
        {
            AutoSize=true, MaximumSize=new Size(474,0), Margin=new Padding(0,0,0,16),
            Text=Setup.HasWPods
                ? "Setup will reuse the WPods Bluetooth service already on this PC. Quit Parity from the tray before installing an update."
                : "Setup will open WPods Setup to install the Bluetooth service and signed driver. It may ask for administrator approval or a restart."
        };
        status.Dock=DockStyle.Fill;
        status.Text="AirPods control for Windows";
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
        layout.Controls.Add(info);
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
            if (Setup.WPodsTrayRunning())
            {
                status.Text="Quit WPods from the system tray, then open Parity. Leave the Bluetooth service running.";
                return;
            }
            try { Process.Start(Setup.DesktopExe); Close(); }
            catch (Win32Exception error) { status.Text=error.Message; }
            return;
        }

        busy=true;
        next.Enabled=false;
        progress.Visible=true;
        status.ForeColor=SystemColors.ControlText;
        try
        {
            var reporter=new Progress<string>(message=> status.Text=message);
            status.Text=await Task.Run(()=>Setup.Install(reporter));
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
        }
    }
}
