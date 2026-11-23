param(
    [string]$RuntimeRoot,
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$rootPath = Split-Path -Parent $PSScriptRoot
$runtimeFiles = @(
    'wpods_native.dll', 'avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll',
    'deepfilter.dll', 'DeepFilterNet3_onnx.tar.gz', 'onnxruntime.dll', 'rnnoise.dll', 'webrtc_apm.dll'
)
$models = 'dpdfnet2_48khz_hr', 'dpdfnet8_48khz_hr', 'fastenhancer48k_t', 'fastenhancer48k_b',
    'fastenhancer48k_s', 'fastenhancer48k_m', 'fastenhancer48k_l', 'gtcrn_simple', 'fevd_gtcrn_spec', 'nsnet2_48k'
$runtimeFiles += $models | ForEach-Object { "models\$_.onnx" }

if (!$RuntimeRoot) {
    $RuntimeRoot = Join-Path $rootPath '.build/native/bin'
    if ($runtimeFiles | Where-Object { !(Test-Path -LiteralPath (Join-Path $RuntimeRoot $_) -PathType Leaf) } | Select-Object -First 1) {
        $RuntimeRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'WPods'
    }
}
foreach ($name in $runtimeFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $RuntimeRoot $name) -PathType Leaf)) {
        throw "Missing $name in $RuntimeRoot. Run ./build.ps1 -NativeOnly or specify a complete -RuntimeRoot."
    }
}
$localSdk = Join-Path $rootPath '.build/dotnet/dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$publishPath = Join-Path $rootPath 'dist/pairside-live'
$uiArgs = @('--local-pro3-art')
$publishArgs = @('-p:PairsideLive=true')
if ($PackagePath) {
    $archive = [IO.Path]::GetFullPath($PackagePath)
    if ([IO.Path]::GetExtension($archive) -ne '.zip' -or (Test-Path -LiteralPath $archive)) {
        throw 'Choose a new .zip destination for the package.'
    }
    $stage = Join-Path $rootPath ('.build/parity-package-' + [Guid]::NewGuid().ToString('N'))
    $bundle = Join-Path $stage 'bundle'
    $packageWeb = Join-Path $stage 'web'
    [IO.Directory]::CreateDirectory($bundle) | Out-Null
    $publishPath = Join-Path $bundle 'app'
    $uiArgs = @('--bundled-pro3-art', "--fresh-output=$packageWeb")
    $publishArgs += @("-p:ParityPackageWeb=$packageWeb", '--artifacts-path', (Join-Path $stage 'artifacts'), '-p:DebugSymbols=false', '-p:DebugType=None')
}

Push-Location (Join-Path $rootPath 'ui')
try {
    & node build.mjs @uiArgs
    if ($LASTEXITCODE -ne 0) { throw "UI build failed ($LASTEXITCODE)." }
} finally {
    Pop-Location
}
& $dotnetExe publish (Join-Path $rootPath 'src/WPods/WPods.csproj') -c Release -o $publishPath @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Parity publish failed ($LASTEXITCODE)." }
if (!(Test-Path -LiteralPath (Join-Path $publishPath 'WebView2Loader.dll') -PathType Leaf)) {
    throw 'The WebView2 NuGet package did not publish its x64 loader.'
}
[IO.Directory]::CreateDirectory((Join-Path $publishPath 'models')) | Out-Null
foreach ($name in $runtimeFiles) {
    Copy-Item -LiteralPath (Join-Path $RuntimeRoot $name) -Destination (Join-Path $publishPath $name) -Force
}

if ($PackagePath) {
    $files = @(foreach ($file in Get-ChildItem -LiteralPath $publishPath -File -Recurse) {
        [ordered]@{ path = $file.FullName.Substring($publishPath.Length + 1); sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash }
    })
    $manifest = [ordered]@{ product = 'Parity'; schema = 1; files = $files }
    [IO.File]::WriteAllText((Join-Path $bundle 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-pairside-desktop.ps1') -Destination (Join-Path $bundle 'Install-Parity.ps1')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall-parity-desktop.ps1') -Destination (Join-Path $bundle 'Uninstall-Parity.ps1')
    $instructions = @'
Parity for Windows x64

The desktop app requires the Microsoft .NET 10 Desktop Runtime (x64) and the
Microsoft Edge WebView2 Runtime. For AirPods controls, first configure the
WPods Bluetooth service and driver on this PC. Quit WPods from its tray menu.

Extract this entire archive. Open PowerShell in the extracted folder and run:
    .\Install-Parity.ps1

Open Parity from the Start menu. When updating, quit Parity from the tray
before running the installer. Existing Parity preferences are retained.
The app license and bundled dependency notices are in the app folder.
'@
    [IO.File]::WriteAllText((Join-Path $bundle 'README.txt'), $instructions)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archive)) | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $partial = Join-Path ([IO.Path]::GetDirectoryName($archive)) ([IO.Path]::GetRandomFileName())
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($bundle, $partial)
        [IO.File]::Move($partial, $archive)
    } finally {
        if ([IO.File]::Exists($partial)) { [IO.File]::Delete($partial) }
    }
    Write-Host "Parity desktop package: $archive"
    Write-Host "SHA-256: $((Get-FileHash -LiteralPath $archive).Hash)"
    $stageFolder = Get-Item -LiteralPath $stage
    if ($stageFolder.Parent.FullName -eq [IO.Path]::GetFullPath((Join-Path $rootPath '.build')) -and
        $stageFolder.Name -match '^parity-package-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $stageFolder.FullName -Recurse -Force
    }
}
