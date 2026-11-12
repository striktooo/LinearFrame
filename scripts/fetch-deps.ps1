<#
.SYNOPSIS
  Downloads the playback engine (libmpv) and ffmpeg into ./deps - needed to build and run LinearFrame.

.DESCRIPTION
  * libmpv: latest "mpv-dev-x86_64" build from https://github.com/shinchiro/mpv-winbuild-cmake (the Windows build linked from mpv.io)
  * ffmpeg: "release essentials" build from https://www.gyan.dev/ffmpeg/builds/ (linked from ffmpeg.org)
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
$dl = Join-Path $deps 'download'
New-Item -ItemType Directory -Force $deps, $mpvDir, $ffDir, $dl | Out-Null

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
