param([string]$StageDirectory)

$ErrorActionPreference = 'Stop'
$live = !$StageDirectory
$directory = if ($live) {
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/Pairside'
} else {
    [IO.Path]::GetFullPath($StageDirectory)
}
$directory = (Get-Item -LiteralPath $directory -Force).FullName
$program = Join-Path $directory 'Pairside.exe'
$manifestPath = Join-Path $directory 'parity-installed.json'
$scriptPath = Join-Path $directory 'Uninstall-Parity.ps1'

function Assert-PhysicalPath([string]$Path) {
    if ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "A link blocks uninstallation at $Path."
    }
}

function Get-ManagedPath([string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative -match '[:*?]' -or [IO.Path]::IsPathRooted($Relative)) {
        throw 'Invalid Parity installation manifest.'
    }
    $parts = $Relative -split '[/\\]'
    if ($parts -contains '..' -or $parts -contains '.' -or $parts -contains '') {
        throw 'Invalid Parity installation path.'
    }
    $path = $directory
    foreach ($part in $parts) {
        $path = Join-Path $path $part
        if (Test-Path -LiteralPath $path) { Assert-PhysicalPath $path }
    }
    return $path
}

try {
    Assert-PhysicalPath $directory
    Assert-PhysicalPath $manifestPath
    Assert-PhysicalPath $scriptPath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.product -ne 'Parity' -or $manifest.schema -ne 1 -or $manifest.files.path -notcontains 'Pairside.exe') {
        throw 'This folder does not contain a verified Parity installation.'
    }

    if ($live) {
        Add-Type -AssemblyName System.Windows.Forms
        if ([Windows.Forms.MessageBox]::Show(
            'Remove Parity from this PC? Parity will keep your preferences. If you added AirPods controls, Windows will ask for approval to restore the Bluetooth adapter.',
            'Uninstall Parity', 'YesNo', 'Question') -ne 'Yes') { exit 0 }
    }

    $lockPath = Join-Path (Split-Path -Parent $directory) ('.' + (Split-Path -Leaf $directory) + '.install.lock')
    $updateLock = [IO.FileStream]::new($lockPath, 'OpenOrCreate', 'ReadWrite', 'None', 1, 'DeleteOnClose')
    try {
        if (Get-Process -Name Pairside -ErrorAction SilentlyContinue | Where-Object Path -eq $program) {
            throw 'Quit Parity from its tray menu before uninstalling.'
        }
        $paths = @(foreach ($entry in $manifest.files) { Get-ManagedPath $entry.path })
        if ($live -and $manifest.bluetooth -eq 'parity') {
            $folder = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Parity Bluetooth'
            $remover = Join-Path $folder 'Remove-ParityBluetooth.ps1'
            $component = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Parity\Installer' -ErrorAction SilentlyContinue).OwnsBluetoothFolder
            if ($component -eq 1 -or (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\ParityBluetoothService')) {
                Assert-PhysicalPath $folder
                Assert-PhysicalPath $remover
                $powershell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell/v1.0/powershell.exe'
                $result = Start-Process -FilePath $powershell -Verb RunAs -ArgumentList (
                    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}"' -f $remover
                ) -WindowStyle Hidden -Wait -PassThru
                if ($result.ExitCode -notin 0, 3010) {
                    throw 'Parity kept the desktop app installed so you can retry Bluetooth removal.'
                }
                if ($result.ExitCode -eq 3010) {
                    [void][Windows.Forms.MessageBox]::Show('Restart Windows when convenient to finish removing the USB/IP driver.', 'Parity', 'OK', 'Information')
                }
            }
        }
        $folders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($path in $paths) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
            $parent = Split-Path -Parent $path
            while ($parent -and $parent -ne $directory) {
                [void]$folders.Add($parent)
                $parent = Split-Path -Parent $parent
            }
        }
        foreach ($folder in ($folders | Sort-Object Length -Descending)) {
            if ((Test-Path -LiteralPath $folder -PathType Container) -and
                !(Get-ChildItem -LiteralPath $folder -Force | Select-Object -First 1)) {
                Remove-Item -LiteralPath $folder
            }
        }
        if ($live) {
            $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Parity.lnk'
            if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
                $shell = New-Object -ComObject WScript.Shell
                if ($shell.CreateShortcut($shortcut).TargetPath -eq $program) { Remove-Item -LiteralPath $shortcut }
            }
            $startup = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            $command = (Get-ItemProperty -LiteralPath $startup -Name Pairside -ErrorAction SilentlyContinue).Pairside
            if ($command -and $command.StartsWith('"' + $program + '" ', [StringComparison]::OrdinalIgnoreCase)) {
                Remove-ItemProperty -LiteralPath $startup -Name Pairside
            }
            $entry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ParityDesktop'
            if (Test-Path -LiteralPath $entry) { Remove-Item -LiteralPath $entry }
        }
        Remove-Item -LiteralPath $manifestPath, $scriptPath
    } finally {
        $updateLock.Dispose()
    }
    if (!(Get-ChildItem -LiteralPath $directory -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $directory }
    if ($live) {
        [void][Windows.Forms.MessageBox]::Show('Parity was removed.', 'Uninstall Parity', 'OK', 'Information')
    }
} catch {
    if ($live) {
        Add-Type -AssemblyName System.Windows.Forms
        [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Uninstall Parity', 'OK', 'Warning')
    }
    throw
}
