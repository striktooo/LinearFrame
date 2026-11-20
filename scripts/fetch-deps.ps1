<#
.SYNOPSIS
  Downloads the playback engine (libmpv), ffmpeg and the text recognition engine into ./deps - needed to build and run LinearFrame.

.DESCRIPTION
  * libmpv: latest "mpv-dev-x86_64" build from https://github.com/shinchiro/mpv-winbuild-cmake (the Windows build linked from mpv.io)
  * ffmpeg: "release essentials" build from https://www.gyan.dev/ffmpeg/builds/ (linked from ffmpeg.org)
  * text recognition: ONNX Runtime (Microsoft's NuGet package, pinned version) and PaddleOCR's PP-OCRv5 models in ONNX
    form (RapidOCR's copies on ModelScope), every file checked against a pinned SHA-256; plus the Visual C++ runtime that
    onnxruntime.dll needs, copied from the local Visual Studio "Redist" folder (app-local deployment, so nothing has to be
    installed on the target PC).
  Use -UseLocalFfmpeg to copy ffmpeg.exe from PATH instead of downloading it.

.EXAMPLE
  pwsh scripts/fetch-deps.ps1
#>
param(
    [switch]$UseLocalFfmpeg,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$deps = Join-Path $root 'deps'
$mpvDir = Join-Path $deps 'mpv'
$ffDir = Join-Path $deps 'ffmpeg'
$ocrDir = Join-Path $deps 'ocr'
$dl = Join-Path $deps 'download'
New-Item -ItemType Directory -Force $deps, $mpvDir, $ffDir, $ocrDir, $dl | Out-Null

function Expand-7z([string]$archive, [string]$dest) {
    $sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
    if ($sevenZip) { & $sevenZip.Source x -y "-o$dest" $archive | Out-Null }
    else { & "$env:SystemRoot\System32\tar.exe" -xf $archive -C $dest }   # Windows' bsdtar reads .7z too
    if ($LASTEXITCODE -ne 0) { throw "Could not extract $archive" }
}

# ---------------------------------------------------------------- libmpv
$libmpv = Join-Path $mpvDir 'libmpv-2.dll'
if ($Force -or -not (Test-Path $libmpv)) {
    Write-Host 'Looking up the latest libmpv build...'
    $headers = @{ 'User-Agent' = 'LinearFrame-build' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
    $rel = Invoke-RestMethod -Headers $headers 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest'
    $asset = $rel.assets | Where-Object { $_.name -match '^mpv-dev-x86_64-\d{8}-git-[0-9a-f]+\.7z$' } | Select-Object -First 1
    if (-not $asset) { throw 'mpv-dev-x86_64 asset not found in the latest release' }
    $file = Join-Path $dl $asset.name
    Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -Headers @{ 'User-Agent' = 'LinearFrame-build' }
    Expand-7z $file $mpvDir
    if (-not (Test-Path $libmpv)) { throw 'libmpv-2.dll missing after extraction' }
    Set-Content (Join-Path $mpvDir 'VERSION.txt') $asset.name
}
Write-Host "libmpv: $libmpv"

# ---------------------------------------------------------------- ffmpeg
$ffmpeg = Join-Path $ffDir 'ffmpeg.exe'
if ($Force -or -not (Test-Path $ffmpeg)) {
    $local = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($UseLocalFfmpeg -and $local) {
        Copy-Item $local.Source $ffmpeg
        Write-Host "ffmpeg copied from $($local.Source)"
    }
    else {
        $zip = Join-Path $dl 'ffmpeg-release-essentials.zip'
        Write-Host 'Downloading ffmpeg-release-essentials.zip from gyan.dev'
        Invoke-WebRequest -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $zip
        $tmp = Join-Path $dl 'ffmpeg-extract'
        if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
        Expand-Archive -Path $zip -DestinationPath $tmp
        $exe = Get-ChildItem -Recurse -Path $tmp -Filter ffmpeg.exe | Select-Object -First 1
        if (-not $exe) { throw 'ffmpeg.exe not found in the archive' }
        Copy-Item $exe.FullName $ffmpeg
        $ver = (Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $exe.FullName)))
        Set-Content (Join-Path $ffDir 'VERSION.txt') $ver
        Remove-Item -Recurse -Force $tmp
    }
}
Write-Host "ffmpeg: $ffmpeg"

# ---------------------------------------------------------------- text recognition (ONNX Runtime + PP-OCRv5)
function Test-Sha256([string]$path, [string]$sha) {
    (Test-Path $path) -and ((Get-FileHash $path -Algorithm SHA256).Hash -eq $sha.ToUpperInvariant())
}

$ortVersion = '1.32.0'
$ortDll = Join-Path $ocrDir 'onnxruntime.dll'
$ortSha = '348C3D29D6995EEE37E3451825EADDB7EA7400BE3AFDA6CA2286B12BC775F042'
if ($Force -or -not (Test-Sha256 $ortDll $ortSha)) {
    $pkg = Join-Path $dl "microsoft.ml.onnxruntime.$ortVersion.nupkg"
    if (-not (Test-Path $pkg)) {
        Write-Host "Downloading ONNX Runtime $ortVersion (NuGet package, ~180 MB, once)"
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/$ortVersion/microsoft.ml.onnxruntime.$ortVersion.nupkg" -OutFile $pkg
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($pkg)
    try {
        foreach ($pair in @(
                @('runtimes/win-x64/native/onnxruntime.dll', 'onnxruntime.dll'),
                @('LICENSE', 'onnxruntime-LICENSE.txt'),
                @('ThirdPartyNotices.txt', 'onnxruntime-ThirdPartyNotices.txt'))) {
            $entry = $zip.GetEntry($pair[0])
            if (-not $entry) { throw "$($pair[0]) missing in the ONNX Runtime package" }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $ocrDir $pair[1]), $true)
        }
    }
    finally { $zip.Dispose() }
    if (-not (Test-Sha256 $ortDll $ortSha)) { throw 'onnxruntime.dll does not match the pinned SHA-256' }
}

$models = @(
    @{ Url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx'; Sha = '4D97C44A20D30A81AAD087D6A396B08F786C4635742AFC391F6621F5C6AE78AE' },
    @{ Url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/rec/latin_PP-OCRv5_rec_mobile.onnx'; Sha = 'B20BD37C168A570F583AFBC8CD7925603890EFBCDC000A59E22C269D160B5F5A' },
    @{ Url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/latin_PP-OCRv5_rec_mobile/ppocrv5_latin_dict.txt'; Sha = '3C0A8A79B612653C25F765271714F71281E4E955962C153E272B7B8C1D2B13FF' }
)
foreach ($m in $models) {
    $file = Join-Path $ocrDir (Split-Path -Leaf $m.Url)
    if (-not $Force -and (Test-Sha256 $file $m.Sha)) { continue }
    Write-Host "Downloading $(Split-Path -Leaf $m.Url)"
    Invoke-WebRequest -Uri $m.Url -OutFile $file
    if (-not (Test-Sha256 $file $m.Sha)) { Remove-Item $file; throw "$(Split-Path -Leaf $m.Url) does not match the pinned SHA-256" }
}

# onnxruntime.dll is built with Visual C++: its runtime goes next to it (Microsoft's app-local deployment), from the
# newest Visual Studio "Redist" folder on this PC - so a PC without the VC++ Redistributable reads text too
$vcFiles = 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll'
if ($Force -or ($vcFiles | Where-Object { -not (Test-Path (Join-Path $ocrDir $_)) })) {
    $crt = Get-ChildItem -Path "$env:ProgramFiles\Microsoft Visual Studio", "${env:ProgramFiles(x86)}\Microsoft Visual Studio" -Recurse -Directory -Filter 'Microsoft.VC14*.CRT' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\Redist\\MSVC\\[\d.]+\\x64\\' } |
        Sort-Object { [version](($_.FullName -replace '.*\\MSVC\\([\d.]+)\\.*', '$1')) } -Descending | Select-Object -First 1
    if ($crt) {
        foreach ($f in $vcFiles) { Copy-Item (Join-Path $crt.FullName $f) $ocrDir }
        Write-Host "Visual C++ runtime: $($crt.FullName)"
    }
    else {
        Write-Warning 'No Visual Studio "Redist" folder found - copying the Visual C++ runtime installed in Windows'
        foreach ($f in $vcFiles) { Copy-Item (Join-Path "$env:SystemRoot\System32" $f) $ocrDir }
    }
}
Write-Host "text recognition: $ocrDir"
