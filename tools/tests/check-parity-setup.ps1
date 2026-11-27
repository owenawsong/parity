param([Parameter(Mandatory)][string]$Setup, [switch]$CheckUi)

$ErrorActionPreference = 'Stop'
$exe = (Get-Item -LiteralPath $Setup).FullName
$stage = Join-Path ([IO.Path]::GetTempPath()) ('parity-package-' + [Guid]::NewGuid().ToString('N'))
$desktop = Join-Path $stage 'desktop'
$bluetooth = Join-Path $stage 'bluetooth'
$serviceKeys = 'WPodsService', 'ParityBluetoothService', 'usbip2_ude'
function Get-ServicePaths {
    foreach ($name in $serviceKeys) {
        $service = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -ErrorAction SilentlyContinue
        "$name=$($service.ImagePath)"
    }
}
function Expand-Setup([string]$Mode, [string]$Target) {
    $process = Start-Process -FilePath $exe -ArgumentList "$Mode `"$Target`"" -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode) { throw "Setup $Mode failed ($($process.ExitCode))." }
}
$before = @(Get-ServicePaths)
[IO.Directory]::CreateDirectory($stage) | Out-Null
Expand-Setup '--verify-package' $desktop
Expand-Setup '--verify-bluetooth-package' $bluetooth
$manifest = Get-Content -LiteralPath (Join-Path $desktop 'parity-installed.json') -Raw | ConvertFrom-Json
if ($manifest.bluetooth -ne 'parity') { throw 'The packaged desktop does not target Parity Bluetooth.' }
foreach ($entry in $manifest.files) {
    if ((Get-FileHash -LiteralPath (Join-Path $desktop $entry.path)).Hash -ne $entry.sha256) {
        throw "Staged hash mismatch for $($entry.path)."
    }
}
foreach ($name in 'Parity.Bluetooth.exe', 'Parity.Bluetooth.dll', 'Remove-ParityBluetooth.ps1', 'USBip-0.9.8.1-x64.exe') {
    if (!(Test-Path -LiteralPath (Join-Path $bluetooth $name) -PathType Leaf)) { throw "Missing $name in the Bluetooth payload." }
}
if (Get-ChildItem -LiteralPath $stage -Recurse -Filter 'WPods*.exe' | Select-Object -First 1) {
    throw 'A WPods executable was found in the standalone package.'
}
$driver = Join-Path $bluetooth 'USBip-0.9.8.1-x64.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $driver
if ($signature.Status -ne 'Valid' -or ($signature.SignerCertificate.SubjectName.Format($true) -split '\r?\n') -cnotcontains 'O=Cloudyne Systems (Scheibling Consulting AB)' -or
    (Get-FileHash -LiteralPath $driver).Hash -ne '38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeCA1A2752A8518A') {
    throw 'The bundled USB/IP installer failed package verification.'
}
if ($CheckUi) {
    & python (Join-Path $PSScriptRoot 'check-packaged-ui.py') $desktop
    if ($LASTEXITCODE) { throw 'The packaged UI check failed.' }
}
$sentinel = Join-Path $desktop 'unmanaged.txt'
Set-Content -LiteralPath $sentinel -Value 'Unmanaged file'
$ps = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell/v1.0/powershell.exe'
& $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $desktop 'Uninstall-Parity.ps1') -StageDirectory $desktop
$left = @(Get-ChildItem -LiteralPath $desktop -Force)
if ($LASTEXITCODE -or $left.Count -ne 1 -or $left[0].FullName -ne $sentinel) { throw 'Staged desktop removal failed.' }
if (Compare-Object $before @(Get-ServicePaths)) { throw 'Setup verification changed a Windows service path.' }
Write-Output "Verified $($manifest.files.Count) desktop files, the standalone Bluetooth payload, and staged removal."
Write-Output "Staged files: $stage"
