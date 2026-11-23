# Builds src\WPods\Assets\WPods.ico from the design's app-icon generator.
# Headless Edge renders WPods.appIcon(size) for all nine sizes (tools\icon\render.html), then the PNGs are packed
# into one .ico as PNG-compressed 32-bit entries (Vista+).
# Use -Pairside to regenerate the fork's icon in src\Pairside.
param(
    [string]$Generator,
    [string]$Out,
    [string]$PngDir,
    [switch]$Pairside
)
$ErrorActionPreference = 'Stop'
if (!$Generator) {
    $Generator = Join-Path $PSScriptRoot $(if ($Pairside) { 'icon/pairside.js' } else { 'icon/appicon.js' })
}
if (!$Out) {
    $Out = Join-Path $PSScriptRoot $(if ($Pairside -or (Split-Path $Generator -Leaf) -eq 'pairside.js') {
        '../src/Pairside/Pairside.ico'
    } else {
        '../src/WPods/Assets/WPods.ico'
    })
}

# The files are written through .NET, which resolves relative paths against the process cwd, not the PowerShell
# location: resolve them here.
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
if ($PngDir) {
    $PngDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PngDir)
}

$renderers = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$work = Join-Path $tempRoot ("wpods-icon-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $page = ([Uri](Resolve-Path "$PSScriptRoot\icon\render.html").Path).AbsoluteUri
    $src = ([Uri](Resolve-Path $Generator).Path).AbsoluteUri
    $dom = Join-Path $work 'dom.html'
    $lines = @()
    foreach ($renderer in $renderers) {
        if (!(Test-Path -LiteralPath $renderer -PathType Leaf)) { continue }
        $profile = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($renderer))
        $renderArgs = @('--headless=new', '--disable-gpu', '--no-first-run', "--user-data-dir=`"$profile`"",
            '--virtual-time-budget=10000', '--dump-dom', "`"$page`?src=$([Uri]::EscapeDataString($src))`"")
        $process = Start-Process $renderer -ArgumentList $renderArgs -RedirectStandardOutput $dom -Wait -WindowStyle Hidden -PassThru
        if ($process.ExitCode -ne 0) { continue }
        $match = [regex]::Match([IO.File]::ReadAllText($dom), '<pre id="out">([^<]*)</pre>')
        $lines = $match.Groups[1].Value.Trim() -split "`n"
        if ($lines.Count -eq 9) { break }
    }
    if ($lines.Count -ne 9) {
        throw "Could not render all nine icon sizes. Check that Microsoft Edge or Google Chrome permits headless rendering."
    }

    $images = foreach ($line in $lines) {
        $size, $b64 = $line.Trim() -split ' '
        [pscustomobject]@{ Size = [int]$size; Png = [Convert]::FromBase64String($b64) }
    }
    if ($PngDir) {
        New-Item -ItemType Directory -Force $PngDir | Out-Null
        foreach ($img in $images) {
            [IO.File]::WriteAllBytes((Join-Path $PngDir "appicon-$($img.Size).png"), $img.Png)
        }
    }

    # ICONDIR (6 bytes), one ICONDIRENTRY (16 bytes) per image, then the PNG data. 256 is stored as 0.
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint16]0)
    $w.Write([uint16]1)
    $w.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $dim = [byte]($img.Size % 256)
        $w.Write($dim)
        $w.Write($dim)
        $w.Write([byte]0)
        $w.Write([byte]0)
        $w.Write([uint16]1)
        $w.Write([uint16]32)
        $w.Write([uint32]$img.Png.Length)
        $w.Write([uint32]$offset)
        $offset += $img.Png.Length
    }
    foreach ($img in $images) {
        $w.Write($img.Png)
    }
    $w.Flush()
    [IO.File]::WriteAllBytes($Out, $ms.ToArray())
    Write-Host "Wrote $Out ($($ms.Length) bytes, sizes $(($images.Size) -join ', '))"
}
finally {
    $resolved = (Get-Item -LiteralPath $work -ErrorAction SilentlyContinue).FullName
    if ($resolved -and $resolved -eq $work -and
        [IO.Path]::GetDirectoryName($resolved).TrimEnd('\') -eq $tempRoot.TrimEnd('\')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
