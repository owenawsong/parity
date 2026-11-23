$ErrorActionPreference = 'Stop'
$env:PSModulePath = Join-Path $PSHOME 'Modules'
Add-Type -AssemblyName System.Windows.Forms
$gate = [Threading.Mutex]::new($false, 'Global\Parity.Bluetooth.Setup')
$acquired = $false
function Assert-Regular([string]$Path) {
    if ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) {
        throw "A link is blocking removal at $Path."
    }
}
function Get-ComponentTree([string]$Path) {
    Assert-Regular $Path
    foreach ($file in [IO.Directory]::EnumerateFiles($Path)) {
        Assert-Regular $file
        $file
    }
    foreach ($folder in [IO.Directory]::EnumerateDirectories($Path)) { Get-ComponentTree $folder }
    $Path
}
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole('Administrator')) {
        throw 'Administrator approval is required to restore the Bluetooth adapter.'
    }
    try { $acquired = $gate.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (!$acquired) { throw 'Parity Bluetooth setup is already running.' }
    $programs = [Environment]::GetFolderPath('ProgramFiles')
    $folder = Join-Path $programs 'Parity Bluetooth'
    $ownership = 'HKLM:\SOFTWARE\Parity\Installer'
    $record = Get-ItemProperty -LiteralPath $ownership -ErrorAction SilentlyContinue
    if ($PSScriptRoot -ne $folder -or $record.OwnsBluetoothFolder -ne 1) {
        throw 'Parity could not verify the Bluetooth component folder.'
    }
    Assert-Regular $programs
    Get-ComponentTree $folder | Out-Null
    $program = Join-Path $folder 'Parity.Bluetooth.exe'
    $serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\ParityBluetoothService'
    $service = Get-ItemProperty -LiteralPath $serviceKey -ErrorAction SilentlyContinue
    if ($service -and $service.ImagePath -ne ('"{0}"' -f $program)) {
        throw 'Parity could not verify the Bluetooth service path.'
    }
    if (Test-Path -LiteralPath $program -PathType Leaf) {
        $result = Start-Process -FilePath $program -ArgumentList '--uninstall' -WorkingDirectory $programs -WindowStyle Hidden -Wait -PassThru
        if ($result.ExitCode) {
            throw 'Windows could not take back the Bluetooth adapter. Restart Windows and try removing Parity again.'
        }
    } elseif ($service -or (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Parity\Bluetooth')) {
        throw 'Repair Parity Setup before removing the Bluetooth component.'
    }
    $driverKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\usbip2_ude'
    $wpodsInstalled = Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\WPodsService'
    $restart = $false
    if ($record.OwnsUsbIp -eq 1 -and !$wpodsInstalled -and (Test-Path -LiteralPath $driverKey) -and
        [Windows.Forms.MessageBox]::Show(
            'Also remove the USB/IP driver installed by Parity? Keep it if another app uses USB/IP.',
            'USB/IP driver', 'YesNo', 'Question', 'Button2') -eq 'Yes') {
        $driverFolder = Join-Path $programs 'USBip'
        $uninstaller = Join-Path $driverFolder 'unins000.exe'
        Assert-Regular $driverFolder
        Assert-Regular $uninstaller
        $signature = Get-AuthenticodeSignature -LiteralPath $uninstaller
        if ($signature.Status -ne 'Valid' -or ($signature.SignerCertificate.SubjectName.Format($true) -split '\r?\n') -cnotcontains 'O=Cloudyne Systems (Scheibling Consulting AB)') {
            throw 'The USB/IP uninstaller could not be verified. Remove it separately through Windows Installed apps.'
        }
        $driver = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010' -WindowStyle Hidden -Wait -PassThru
        if ($driver.ExitCode -notin 0, 3010) { throw 'The USB/IP driver could not be removed.' }
        $restart = $driver.ExitCode -eq 3010
        Remove-ItemProperty -LiteralPath $ownership -Name OwnsUsbIp
        Remove-ItemProperty -LiteralPath $ownership -Name UsbIpNeedsRestart -ErrorAction SilentlyContinue
    }
    foreach ($path in @(Get-ComponentTree $folder)) {
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
        elseif ([IO.Directory]::Exists($path)) { [IO.Directory]::Delete($path) }
    }
    Remove-ItemProperty -LiteralPath $ownership -Name OwnsBluetoothFolder
    if ($restart) { exit 3010 }
    exit 0
} catch {
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Parity Bluetooth removal', 'OK', 'Warning')
    exit 1
} finally {
    if ($acquired) { $gate.ReleaseMutex() }
    $gate.Dispose()
}
