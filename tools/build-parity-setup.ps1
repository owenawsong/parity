param(
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($destination) -ne '.exe' -or (Test-Path -LiteralPath $destination)) {
    throw 'Choose an unused .exe path for Parity Setup.'
}
$build = Join-Path $root ('.build/parity-setup-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($build) | Out-Null
$desktopArchive = Join-Path $build 'ParityDesktop.zip'
& (Join-Path $PSScriptRoot 'build-pairside-live.ps1') -PackagePath $desktopArchive -RuntimeRoot $RuntimeRoot
[xml]$project = Get-Content -Raw -LiteralPath (Join-Path $root 'src/WPods/WPods.csproj')
$version = ($project.Project.PropertyGroup | Where-Object { $_.AssemblyName -eq 'Pairside' }).Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Could not determine the Parity desktop version.' }
$service = Join-Path $build 'Bluetooth'
$localSdk = Join-Path $root '.build/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
& $dotnet publish (Join-Path $root 'src/WPods.Service/WPods.Service.csproj') -c Release -o $service `
    --artifacts-path (Join-Path $build 'artifacts') -p:ParityBluetooth=true "-p:Version=$version" -p:DebugSymbols=false -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "Parity Bluetooth publish failed ($LASTEXITCODE)." }
& (Join-Path $root 'native/fetch-usbip.ps1')
Copy-Item -LiteralPath (Join-Path $root '.build/deps/USBip-0.9.8.1-x64.exe') -Destination $service
Copy-Item -LiteralPath (Join-Path $root 'installer/Remove-ParityBluetooth.ps1') -Destination $service
$bluetoothArchive = Join-Path $build 'ParityBluetooth.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($service, $bluetoothArchive)
$metadata = Join-Path $build 'Version.cs'
[IO.File]::WriteAllText($metadata, @"
using System.Reflection;
[assembly: AssemblyTitle("Parity Setup")]
[assembly: AssemblyProduct("Parity")]
[assembly: AssemblyFileVersion("$version")]
[assembly: AssemblyInformationalVersion("$version")]
"@)
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $csc -PathType Leaf)) { throw 'The Windows .NET Framework compiler is unavailable.' }
$setup = Join-Path $build 'Parity-Setup.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$setup" "/resource:$desktopArchive,ParityDesktop.zip" `
    "/resource:$bluetoothArchive,ParityBluetooth.zip" `
    "/win32icon:$(Join-Path $root 'src/Pairside/Pairside.ico')" /r:System.IO.Compression.dll `
    /r:System.IO.Compression.FileSystem.dll /r:System.ServiceProcess.dll `
    (Join-Path $root 'installer/ParitySetup.cs') (Join-Path $root 'installer/BluetoothProvisioning.cs') $metadata
if ($LASTEXITCODE -ne 0) { throw "Parity Setup compile failed ($LASTEXITCODE)." }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
[IO.File]::Move($setup,$destination)
Write-Host "Parity Setup: $destination"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $destination).Hash)"
