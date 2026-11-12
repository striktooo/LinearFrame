<#
.SYNOPSIS
  Builds the ready-to-share LinearFrame package: dist/LinearFrame-<version>-win-x64.zip

.DESCRIPTION
  1. makes sure ./deps has libmpv and ffmpeg (scripts/fetch-deps.ps1)
  2. publishes a self-contained single-file LinearFrame.exe (no .NET install needed on the target PC)
  3. assembles dist/LinearFrame with the exe, the engine folder and short instructions, and zips it

.EXAMPLE
  pwsh ./build.ps1
  pwsh ./build.ps1 -UseLocalFfmpeg
#>
param(
    [switch]$UseLocalFfmpeg
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src/LinearFrame/LinearFrame.csproj'

& (Join-Path $root 'scripts/fetch-deps.ps1') -UseLocalFfmpeg:$UseLocalFfmpeg

[xml]$csproj = Get-Content $proj
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { $version = '1.0.0' }
Write-Host "Building LinearFrame $version"

dotnet publish $proj -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$pub = Join-Path $root 'src/LinearFrame/bin/Release/net10.0-windows/win-x64/publish'
$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'LinearFrame'
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
New-Item -ItemType Directory -Force (Join-Path $app 'engine') | Out-Null

Copy-Item (Join-Path $pub 'LinearFrame.exe') $app
Copy-Item (Join-Path $pub 'engine/libmpv-2.dll') (Join-Path $app 'engine')
Copy-Item (Join-Path $pub 'engine/ffmpeg.exe') (Join-Path $app 'engine')
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $app 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $app 'THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $root 'docs/CZYTAJ.txt') (Join-Path $app 'CZYTAJ - README.txt')

$zip = Join-Path $dist "LinearFrame-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content "$zip.sha256" "$hash  $(Split-Path -Leaf $zip)"
Write-Host ("Done: {0} ({1:N1} MB)  SHA256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash)
