param([switch]$Live, [string]$StageDirectory, [switch]$RegisterUninstall)

$ErrorActionPreference = 'Stop'

$packaged = [IO.Path]::GetFileName($PSCommandPath) -eq 'Install-Parity.ps1'
if ($RegisterUninstall -and !$packaged) { throw 'Uninstall registration requires the packaged installer.' }
if ($packaged) {
    $Live = $true
    $source = Join-Path $PSScriptRoot 'app'
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.product -ne 'Parity' -or $manifest.schema -ne 1) { throw 'Unrecognized Parity package manifest.' }
    $hashes = @{}
    foreach ($entry in $manifest.files) {
        if ($entry.sha256 -notmatch '^[0-9a-f]{64}$' -or $hashes.ContainsKey($entry.path)) { throw 'Invalid package file manifest.' }
        $hashes.Add($entry.path, $entry.sha256)
    }
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File -Force) {
        $relative = $file.FullName.Substring($source.Length + 1)
        if (!$hashes.ContainsKey($relative) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $hashes[$relative]) {
            throw "Package verification failed for $relative. Extract a fresh copy."
        }
        $hashes.Remove($relative)
    }
    if ($hashes.Count) { throw "Incomplete package. Missing $($hashes.Keys -join ', ')." }
} else {
    $source = Join-Path $PSScriptRoot $(if ($Live) { '../dist/pairside-live' } else { '../dist/pairside-desktop' })
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'Pairside.exe') -PathType Leaf)) {
    throw "Parity hasn't been published to $source."
}

$programs = [Environment]::GetFolderPath('LocalApplicationData')
$appName = if ($Live) { 'Parity' } else { 'Parity Preview' }
# Keep upgrade, taskbar, profile and startup targets stable across the rebrand.
$installedFolder = if ($Live) { 'Pairside' } else { 'Pairside Preview' }
$install = Join-Path (Join-Path $programs 'Programs') $installedFolder
if ($StageDirectory) { $install = [IO.Path]::GetFullPath($StageDirectory) }
$exe = Join-Path $install 'Pairside.exe'
if (Get-Process -Name 'Pairside' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw "Quit $appName from its tray menu before updating the desktop app."
}

function Install-AppFiles([string]$sourceFolder, [string]$installFolder) {
    $sourceRoot = (Get-Item -LiteralPath $sourceFolder).FullName
    $installRoot = [IO.Path]::GetFullPath($installFolder)
    $changes = [Collections.Generic.List[object]]::new()
    $unchanged = 0
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1)
        $destination = Join-Path $installRoot $relative
        $existing = Get-Item -LiteralPath $destination -ErrorAction SilentlyContinue
        if ($existing -and $existing.PSIsContainer) {
            throw "A directory is blocking $destination."
        }
        if ($existing -and $existing.Length -eq $file.Length -and
            (Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $destination).Hash) {
            $unchanged++
            continue
        }
        $changes.Add(@{ Source = $file.FullName; Destination = $destination; Existing = [bool]$existing })
    }

    if ($changes.Count -gt 0) {
        # Same volume as the installed files so Replace can save the old file as a backup.
        $stage = Join-Path (Split-Path -Parent $installRoot) ('.pairside-update-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        $started = 0
        $recovered = $true
        try {
            for ($i = 0; $i -lt $changes.Count; $i++) {
                $change = $changes[$i]
                $change.Staged = Join-Path $stage "$i.new"
                $change.Backup = Join-Path $stage "$i.old"
                [IO.File]::Copy($change.Source, $change.Staged)
            }
            foreach ($change in $changes) {
                [IO.Directory]::CreateDirectory((Split-Path -Parent $change.Destination)) | Out-Null
                $started++
                if ($change.Existing) {
                    [IO.File]::Replace($change.Staged, $change.Destination, $change.Backup)
                } else {
                    [IO.File]::Move($change.Staged, $change.Destination)
                }
            }
        } catch {
            for ($i = $started - 1; $i -ge 0; $i--) {
                $change = $changes[$i]
                try {
                    if ([IO.File]::Exists($change.Backup)) {
                        if ([IO.File]::Exists($change.Destination)) {
                            [IO.File]::Replace($change.Backup, $change.Destination, [NullString]::Value)
                        } else {
                            [IO.File]::Move($change.Backup, $change.Destination)
                        }
                    } elseif (!$change.Existing -and ![IO.File]::Exists($change.Staged)) {
                        [IO.File]::Delete($change.Destination)
                    }
                } catch {
                    $recovered = $false
                    Write-Warning "Could not restore $($change.Destination). Backups remain in $stage."
                }
            }
            throw
        } finally {
            if ($recovered) {
                foreach ($change in $changes) {
                    if ($change.Staged) { [IO.File]::Delete($change.Staged) }
                    if ($change.Backup) { [IO.File]::Delete($change.Backup) }
                }
                [IO.Directory]::Delete($stage)
            }
        }
    }
    Write-Host "App files updated: $($changes.Count), unchanged: $unchanged"
}

function Initialize-LiveProfile {
    $liveProfile = Join-Path $programs 'Pairside/Live'
    $originalProfile = Join-Path $programs 'WPods'
    New-Item -ItemType Directory -Path $liveProfile -Force | Out-Null
    foreach ($profileFile in 'settings.json', 'keys.bin') {
        $existingFile = Join-Path $originalProfile $profileFile
        $newFile = Join-Path $liveProfile $profileFile
        if ((Test-Path -LiteralPath $existingFile -PathType Leaf) -and !(Test-Path -LiteralPath $newFile)) {
            $staged = Join-Path $liveProfile ([IO.Path]::GetRandomFileName())
            try {
                if ($profileFile -eq 'settings.json') {
                    try {
                        $seed = Get-Content -LiteralPath $existingFile -Raw -Encoding UTF8 | ConvertFrom-Json
                    } catch {
                        Write-Warning 'The WPods preferences could not be read. Parity will use its default preferences.'
                        continue
                    }
                    if ($null -ne $seed.ui) {
                        if ($null -ne $seed.ui.PSObject.Properties['theme']) { $seed.ui.theme = 'dark' }
                        $seed.ui.PSObject.Properties.Remove('windowWidth')
                        $seed.ui.PSObject.Properties.Remove('windowHeight')
                    }
                    [IO.File]::WriteAllText($staged, ($seed | ConvertTo-Json -Depth 100))
                } else {
                    [IO.File]::Copy($existingFile, $staged)
                }
                [IO.File]::Move($staged, $newFile)
            } finally {
                if ([IO.File]::Exists($staged)) { [IO.File]::Delete($staged) }
            }
        }
    }
}

function Register-ParityUninstaller {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-Parity.ps1') -Destination $install -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Destination (Join-Path $install 'parity-installed.json') -Force
    if ($StageDirectory) { return }
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ParityDesktop'
    New-Item -Path $key -Force | Out-Null
    $powershell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell/v1.0/powershell.exe'
    $uninstall = Join-Path $install 'Uninstall-Parity.ps1'
    Set-ItemProperty -LiteralPath $key -Name DisplayName -Value 'Parity'
    Set-ItemProperty -LiteralPath $key -Name Publisher -Value 'Parity'
    Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    Set-ItemProperty -LiteralPath $key -Name DisplayIcon -Value $exe
    Set-ItemProperty -LiteralPath $key -Name InstallLocation -Value $install
    Set-ItemProperty -LiteralPath $key -Name UninstallString -Value ('"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}"' -f $powershell,$uninstall)
    New-ItemProperty -LiteralPath $key -Name NoModify -PropertyType DWord -Value 1 -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name NoRepair -PropertyType DWord -Value 1 -Force | Out-Null
}

function New-ParityShortcut {
    $startMenu = [Environment]::GetFolderPath('Programs')
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $startMenu "$appName.lnk"))
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $install
    $shortcut.Description = if ($Live) { 'Parity AirPods controls for Windows' } else { 'Parity desktop preview' }
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Save()
    $previousShortcut = Join-Path $startMenu "$installedFolder.lnk"
    if ((Test-Path -LiteralPath $previousShortcut -PathType Leaf) -and
        $shell.CreateShortcut($previousShortcut).TargetPath -eq $exe) {
        Remove-Item -LiteralPath $previousShortcut
    }
}

$parent = Split-Path -Parent $install
[IO.Directory]::CreateDirectory($parent) | Out-Null
$lockPath = Join-Path $parent ('.' + (Split-Path -Leaf $install) + '.install.lock')
try {
    $updateLock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
} catch [IO.IOException] {
    throw "Cannot lock the installation at $install. If another update is running, let it finish and try again. $($_.Exception.Message)"
}
try {
    Install-AppFiles $source $install
    if ($RegisterUninstall) { Register-ParityUninstaller }
    if (!$StageDirectory) {
        if ($Live -and (!$packaged -or $manifest.bluetooth -ne 'parity')) { Initialize-LiveProfile }
        New-ParityShortcut
    }
} finally {
    $updateLock.Dispose()
}
Write-Output $exe
