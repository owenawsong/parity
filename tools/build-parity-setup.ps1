param(
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$DesktopArchive
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($destination) -ne '.exe' -or (Test-Path -LiteralPath $destination)) {
    throw 'Choose an unused .exe path for Parity Setup.'
}
$build = Join-Path $root ('.build/parity-setup-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($build) | Out-Null
if (!$DesktopArchive) {
    $DesktopArchive = Join-Path $build 'ParityDesktop.zip'
    & (Join-Path $PSScriptRoot 'build-pairside-live.ps1') -PackagePath $DesktopArchive
}
$zip = (Get-Item -LiteralPath $DesktopArchive).FullName
[xml]$project = Get-Content -Raw -LiteralPath (Join-Path $root 'src/WPods/WPods.csproj')
$version = ($project.Project.PropertyGroup | Where-Object { $_.AssemblyName -eq 'Pairside' }).Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Could not determine the Parity desktop version.' }
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
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$setup" "/resource:$zip,ParityDesktop.zip" `
    "/win32icon:$(Join-Path $root 'src/Pairside/Pairside.ico')" /r:System.IO.Compression.dll `
    /r:System.IO.Compression.FileSystem.dll /r:System.ServiceProcess.dll `
    (Join-Path $root 'installer/ParitySetup.cs') $metadata
if ($LASTEXITCODE -ne 0) { throw "Parity Setup compile failed ($LASTEXITCODE)." }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
[IO.File]::Move($setup,$destination)
Write-Host "Parity Setup: $destination"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $destination).Hash)"
