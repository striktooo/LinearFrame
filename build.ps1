<#
.SYNOPSIS
  Builds the ready-to-share LinearFrame package: dist/LinearFrame-<version>-win-x64.zip

.DESCRIPTION
  1. makes sure ./deps has libmpv, ffmpeg and the text recognition engine (scripts/fetch-deps.ps1)
  2. publishes LinearFrame.exe + LinearFrame.dll and puts the .NET they run on into the "runtime" folder next to them
     (no .NET install needed on the target PC)
  3. assembles dist/LinearFrame with the app, the runtime folder, the engine folder and short instructions, and zips it
  4. makes the two smaller packages installed copies update themselves from

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

$pub = Join-Path $root 'src/LinearFrame/bin/Release/net10.0-windows/win-x64/publish'
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub } # nothing left over from an earlier build ends up in the package
dotnet publish $proj -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'LinearFrame'
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
New-Item -ItemType Directory -Force (Join-Path $app 'engine') | Out-Null

# --- the app itself: a small exe that starts LinearFrame.dll
foreach ($f in 'LinearFrame.exe', 'LinearFrame.dll', 'LinearFrame.deps.json', 'LinearFrame.runtimeconfig.json') {
    if (-not (Test-Path (Join-Path $pub $f))) { throw "$f missing in the publish folder" }
    Copy-Item (Join-Path $pub $f) $app
}
if ((Get-Item (Join-Path $app 'LinearFrame.exe')).Length -gt 5MB) { throw 'LinearFrame.exe is not the small launcher - is PublishSingleFile switched on again? (see the csproj for why it must not be)' }

# --- .NET: LinearFrame.exe runs on the copy in the "runtime" folder next to it and looks nowhere else
# (AppHostRelativeDotNet in the csproj). It is the newest patch of the .NET release the app was built for, taken from
# the SDK that has just built it.
$dotnetRoot = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'shared'))) { $env:DOTNET_ROOT } else { Split-Path (Get-Command dotnet).Source }
$config = Get-Content (Join-Path $pub 'LinearFrame.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworks = @($config.runtimeOptions.frameworks)
if ($frameworks.Count -eq 0 -and $config.runtimeOptions.framework) { $frameworks = @($config.runtimeOptions.framework) }
if ($frameworks.Count -eq 0) { throw 'LinearFrame.runtimeconfig.json names no .NET framework' }
$want = [version]($frameworks[0].version -replace '-.*$', '')
$runtimeVersion = $null
foreach ($dir in Get-ChildItem (Join-Path $dotnetRoot "shared/$($frameworks[0].name)") -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name }) {
    $v = [version]$dir.Name
    if ($v.Major -ne $want.Major -or $v.Minor -ne $want.Minor -or $v -lt $want) { continue }
    if (-not (Test-Path (Join-Path $dotnetRoot "host/fxr/$($dir.Name)/hostfxr.dll"))) { continue }
    $complete = $true
    foreach ($fw in $frameworks) { if (-not (Test-Path (Join-Path $dotnetRoot "shared/$($fw.name)/$($dir.Name)"))) { $complete = $false } }
    if ($complete) { $runtimeVersion = $dir.Name }
}
if (-not $runtimeVersion) { throw "No .NET $($want.Major).$($want.Minor) runtime with $($frameworks.name -join ' + ') found under $dotnetRoot" }
Write-Host "Runtime: .NET $runtimeVersion from $dotnetRoot"

$runtime = Join-Path $app 'runtime'
New-Item -ItemType Directory -Force (Join-Path $runtime "host/fxr/$runtimeVersion") | Out-Null
Copy-Item (Join-Path $dotnetRoot "host/fxr/$runtimeVersion/hostfxr.dll") (Join-Path $runtime "host/fxr/$runtimeVersion")
foreach ($fw in $frameworks) {
    $to = Join-Path $runtime "shared/$($fw.name)/$runtimeVersion"
    New-Item -ItemType Directory -Force $to | Out-Null
    # files only: the sub-folders hold translations of .NET's own texts, which LinearFrame never shows
    Get-ChildItem (Join-Path $dotnetRoot "shared/$($fw.name)/$runtimeVersion") -File | Copy-Item -Destination $to
}
foreach ($f in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
    if (Test-Path (Join-Path $dotnetRoot $f)) { Copy-Item (Join-Path $dotnetRoot $f) (Join-Path $runtime "dotnet-$f") }
}

# --- the engines
Copy-Item (Join-Path $pub 'engine/*') (Join-Path $app 'engine')
foreach ($f in 'libmpv-2.dll', 'ffmpeg.exe', 'onnxruntime.dll', 'ch_PP-OCRv5_det_mobile.onnx', 'latin_PP-OCRv5_rec_mobile.onnx', 'ppocrv5_latin_dict.txt', 'msvcp140.dll') {
    if (-not (Test-Path (Join-Path $app "engine/$f"))) { throw "engine/$f missing - run scripts/fetch-deps.ps1" }
}
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $app 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $app 'THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $root 'src/LinearFrame/Assets/Fonts/OFL.txt') (Join-Path $app 'OFL-TikTokSans.txt')
Copy-Item (Join-Path $root 'docs/CZYTAJ.txt') (Join-Path $app 'CZYTAJ - README.txt')

# --- the package really starts on its own .NET (a tool of the app that needs no window; the exit code is the answer)
$env:DOTNET_DISABLE_GUI_ERRORS = '1' # a missing runtime must fail the build, not wait for somebody to click a message away
$check = Start-Process -FilePath (Join-Path $app 'LinearFrame.exe') -ArgumentList '--runtime-check' -PassThru
if (-not $check.WaitForExit(60000)) { $check.Kill(); throw 'The packaged LinearFrame.exe did not answer the runtime check within a minute' }
if ($check.ExitCode -ne 0) { throw "The packaged LinearFrame.exe did not start on its own runtime folder (exit code $($check.ExitCode))" }
Remove-Item Env:DOTNET_DISABLE_GUI_ERRORS

function Get-Sha([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash }

$zip = Join-Path $dist "LinearFrame-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Optimal
$hash = Get-Sha $zip
Set-Content "$zip.sha256" "$hash  $(Split-Path -Leaf $zip)"
Write-Host ("Done: {0} ({1:N1} MB)  SHA256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash)

# Installed copies update themselves from the smallest package that fits them. Each package's .sha256 starts with the
# package's own checksum and then lists the files this version expects to find already installed - a copy that has
# exactly those takes the package, any other one takes the next bigger one.
$engineLines = foreach ($f in Get-ChildItem (Join-Path $app 'engine') -File | Sort-Object Name) { "$(Get-Sha $f.FullName)  engine/$($f.Name)" }
$runtimeLines = foreach ($f in Get-ChildItem $runtime -File -Recurse | Sort-Object FullName) {
    "$(Get-Sha $f.FullName)  runtime/$([IO.Path]::GetRelativePath($runtime, $f.FullName).Replace('\', '/'))"
}
function New-UpdatePackage([string]$suffix, [bool]$withRuntime, [string[]]$expects) {
    $package = Join-Path $dist "LinearFrame-$version-$suffix.zip"
    if (Test-Path $package) { Remove-Item -Force $package }
    $stage = Join-Path $dist 'update-stage'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force (Join-Path $stage 'LinearFrame') | Out-Null
    Get-ChildItem $app -File | Copy-Item -Destination (Join-Path $stage 'LinearFrame')
    if ($withRuntime) { Copy-Item $runtime (Join-Path $stage 'LinearFrame') -Recurse }
    Compress-Archive -Path (Join-Path $stage 'LinearFrame') -DestinationPath $package -CompressionLevel Optimal
    Remove-Item -Recurse -Force $stage
    Set-Content "$package.sha256" (@("$(Get-Sha $package)  $(Split-Path -Leaf $package)") + $expects)
    Write-Host ("Update package: {0} ({1:N1} MB)" -f $package, ((Get-Item $package).Length / 1MB))
}
# "-update.zip": the app and its .NET, for copies whose engine folder (mpv, FFmpeg, text recognition - most of the
# download, and it hardly ever changes) is still the right one. Every version since 1.0.2 understands this one.
New-UpdatePackage 'update' $true $engineLines
# "-app.zip": only the app's own files (a few MB), for copies that also have the right .NET already (1.2.1 and later).
New-UpdatePackage 'app' $false ($engineLines + $runtimeLines)
