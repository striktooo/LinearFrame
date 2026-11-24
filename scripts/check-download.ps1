# Release check: what happens to the package on a PC that DOWNLOADS it.
#
# A file built here is trusted by this PC; the same file fetched with a browser carries the "came from the Internet"
# mark, and that is when Microsoft Defender looks hardest.  So before a release goes out:
#   1. the ZIP is marked as a browser download with the Windows call browsers make (it also has the antivirus scan it),
#   2. it is unpacked and every file gets the mark Explorer's "Extract all" would give it,
#   3. Defender scans the folder (report only),
#   4. the marked LinearFrame.exe is started with a windowless command, and
#   5. once as an invisible test copy that goes through its start-up rehearsal (player library, FFmpeg, a clip, a picture).
# Exit code 0 = Defender found nothing and the app ran.  Nothing is shown on screen; the test folder is removed.
#
# What this cannot see: the blue "Windows protected your PC" window (SmartScreen).  That one is shown for every program
# without a digital signature and is not an antivirus detection - see "Download" in the README.
#
#   pwsh scripts/check-download.ps1                      # newest dist\LinearFrame-*-win-x64.zip
#   pwsh scripts/check-download.ps1 -Zip path\to\package.zip
param([string]$Zip = '', [int]$TraySeconds = 45, [switch]$Keep)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($Zip -eq '') {
    $newest = Get-ChildItem (Join-Path $repo 'dist') -File -Filter 'LinearFrame-*-win-x64.zip' | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $newest) { throw 'No dist\LinearFrame-*-win-x64.zip - run build.ps1 first or pass -Zip.' }
    $Zip = $newest.FullName
}
$zipName = Split-Path $Zip -Leaf
$version = if ($zipName -match '^LinearFrame-(.+)-win-x64\.zip$') { $Matches[1] } else { '0.0.0' }
$root = Join-Path ([IO.Path]::GetTempPath()) "lf-download-check-$PID"
$hide = { param($t) "$t".Replace($root, '<test folder>') }
New-Item -ItemType Directory -Force (Join-Path $root 'data') | Out-Null
$zipCopy = Join-Path $root $zipName
Copy-Item $Zip $zipCopy
$t0 = Get-Date
$failed = @()

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAttachmentExecute {
    [PreserveSig] int SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
    [PreserveSig] int SetClientGuid(ref Guid guid);
    [PreserveSig] int SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
    [PreserveSig] int SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
    [PreserveSig] int CheckPolicy();
    [PreserveSig] int Prompt(IntPtr hwnd, int prompt, out int action);
    [PreserveSig] int Save();
    [PreserveSig] int Execute(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string verb, out IntPtr process);
    [PreserveSig] int SaveWithUI(IntPtr hwnd);
    [PreserveSig] int ClearClientState();
}
public static class DownloadMark {
    // What a browser does when a download has finished: the mark and the antivirus scan.  No window.
    public static int Save(string path, string url, string referrer) {
        var a = (IAttachmentExecute)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4125dd96-e03a-4103-8f70-e0597d803b9c")));
        var client = new Guid("7f0c1c2e-52a1-4b0e-9a55-1d2f6e0f4a11");
        a.SetClientGuid(ref client);
        a.SetLocalPath(path); a.SetSource(url); a.SetReferrer(referrer);
        return a.Save();
    }
}
'@

function Get-Detections([datetime]$since) {
    $ids = 1006, 1007, 1008, 1015, 1116, 1117, 1118, 1119, 1121, 1122, 1123, 1124, 1125, 1126, 1127, 1128, 1129
    try {
        foreach ($e in @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = $ids; StartTime = $since } -ErrorAction Stop)) {
            $d = @{}; foreach ($n in ([xml]$e.ToXml()).Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
            if ("$($d['Path'])" -like "*$root*") { "{0} id {1}: {2} [{3}]" -f $e.TimeCreated.ToString('HH:mm:ss'), $e.Id, $d['Threat Name'], (& $hide $d['Path']) }
        }
    } catch { }
}

try {
    "Package: $zipName ($('{0:N0}' -f (Get-Item $zipCopy).Length) bytes)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $hr = [DownloadMark]::Save($zipCopy, "https://github.com/striktooo/LinearFrame/releases/download/v$version/$zipName", "https://github.com/striktooo/LinearFrame/releases/tag/v$version")
    $marked = (Test-Path $zipCopy) -and ((Get-Content -LiteralPath $zipCopy -Stream Zone.Identifier -ErrorAction SilentlyContinue) -join ' ') -match 'ZoneId=3'
    "1. marked as a download and scanned in $([int]$sw.Elapsed.TotalSeconds) s: result 0x$('{0:X8}' -f $hr), ZIP kept and marked: $marked"
    if ($hr -ne 0 -or -not $marked) { $failed += 'the antivirus did not accept the downloaded ZIP'; throw 'stop' }

    $sw.Restart()
    [IO.Compression.ZipFile]::ExtractToDirectory($zipCopy, (Join-Path $root 'x'))
    $app = Join-Path $root 'x\LinearFrame'; $exe = Join-Path $app 'LinearFrame.exe'
    $files = @(Get-ChildItem $app -Recurse -File)
    foreach ($f in $files) { Set-Content -LiteralPath $f.FullName -Stream Zone.Identifier -Value '[ZoneTransfer]', 'ZoneId=3', "ReferrerUrl=$zipCopy" }
    "2. unpacked $($files.Count) files in $([int]$sw.Elapsed.TotalSeconds) s, each marked; LinearFrame.exe is $('{0:N0}' -f (Get-Item $exe).Length) bytes, signature: $((Get-AuthenticodeSignature $exe).Status)"

    $mp = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    if (Test-Path $mp) {
        $sw.Restart()
        $out = & $mp -Scan -ScanType 3 -File $app -DisableRemediation 2>&1
        "3. Defender scan of the folder: exit code $LASTEXITCODE (0 = clean) after $([int]$sw.Elapsed.TotalSeconds) s"
        if ($LASTEXITCODE -ne 0) { $failed += 'Defender reports something in the unpacked folder'; $out | Where-Object { "$_".Trim() -ne '' } | Select-Object -Last 15 | ForEach-Object { "      $(& $hide $_)" } }
    } else { "3. Defender's command-line scanner is not on this PC - skipped" }

    $sw.Restart()
    try {
        $psi = [Diagnostics.ProcessStartInfo]::new($exe, '--runtime-check'); $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
        $psi.Environment['DOTNET_DISABLE_GUI_ERRORS'] = '1'
        $p = [Diagnostics.Process]::Start($psi)
        if (-not $p.WaitForExit(120000)) { $p.Kill(); $failed += 'the marked exe did not finish its start check' }
        elseif ($p.ExitCode -ne 0) { $failed += "the marked exe's start check ended with $($p.ExitCode)" }
        "4. the marked exe started and checked its runtime: exit code $($p.ExitCode) after $($sw.ElapsedMilliseconds) ms"
    } catch [ComponentModel.Win32Exception] {
        $failed += "Windows refused to start the marked exe (error $($_.Exception.NativeErrorCode))"
        "4. NOT ALLOWED TO START: Windows error $($_.Exception.NativeErrorCode) - $($_.Exception.Message)"
    }

    # the invisible test copy: its own instance and data folder, no shortcuts, no autostart, no update check
    $none = '{ "Modifiers": "None", "Key": "None", "Mouse": "None" }'
    @"
{ "Language": "en", "RegionHotkey": $none, "RegionHotkey2": $none, "ClearClipboardHotkey": $none,
  "CopyToClipboard": false, "RunAtStartup": false, "PlaySound": false, "AutoUpdate": false, "AutoSave": false,
  "FirstRunDone": true, "WelcomeAutoShown": true, "StartMenuShortcutDone": true, "HdEnabled": false, "PlayerMuted": true, "PlayerVolume": 0 }
"@ | Set-Content (Join-Path $root 'data\settings.json') -Encoding utf8
    $log = Join-Path $root 'data\log.txt'
    $sw.Restart()
    try {
        $psi = [Diagnostics.ProcessStartInfo]::new($exe, '--tray'); $psi.UseShellExecute = $false
        foreach ($v in 'LINEARFRAME_UPDATE_FEED', 'LINEARFRAME_UPDATE_DELAY', 'LINEARFRAME_IDLE_SECONDS', 'LF_SELFTEST_ONLY') { $psi.Environment.Remove($v) | Out-Null }
        $psi.Environment['LINEARFRAME_INSTANCE'] = "dlcheck$PID"; $psi.Environment['LINEARFRAME_DATA_DIR'] = (Join-Path $root 'data')
        $psi.Environment['LINEARFRAME_GHOST'] = '1'; $psi.Environment['LF_MPV_OPTS'] = 'mute=yes'
        $tray = [Diagnostics.Process]::Start($psi)
        $over = $false
        while ($sw.Elapsed.TotalSeconds -lt $TraySeconds -and -not $over -and -not $tray.HasExited) {
            Start-Sleep -Milliseconds 500
            $over = (Test-Path $log) -and [bool](Select-String -Path $log -Pattern 'Rehearsal over' -Quiet)
        }
        "5. invisible test copy: still running $(-not $tray.HasExited), start-up rehearsal finished: $over ($([int]$sw.Elapsed.TotalSeconds) s)"
        if (Test-Path $log) { Get-Content $log | Where-Object { $_ -match 'Rehearsal: | ERR ' } | Select-Object -First 8 | ForEach-Object { '      ' + $_.Substring([Math]::Min(11, $_.Length)) } }
        if ($tray.HasExited -or -not $over) { $failed += 'the marked copy did not get through its start-up rehearsal' }
        $quit = [Diagnostics.ProcessStartInfo]::new($exe, '--exit'); $quit.UseShellExecute = $false
        $quit.Environment['LINEARFRAME_INSTANCE'] = "dlcheck$PID"; $quit.Environment['LINEARFRAME_DATA_DIR'] = (Join-Path $root 'data'); $quit.Environment['LINEARFRAME_GHOST'] = '1'
        [Diagnostics.Process]::Start($quit).WaitForExit(15000) | Out-Null
        if (-not $tray.WaitForExit(8000)) { $tray.Kill() }
    } catch [ComponentModel.Win32Exception] {
        $failed += "Windows refused to start the marked copy (error $($_.Exception.NativeErrorCode))"
        "5. NOT ALLOWED TO START: Windows error $($_.Exception.NativeErrorCode) - $($_.Exception.Message)"
    }
    $gone = @($files | Where-Object { -not (Test-Path -LiteralPath $_.FullName) })
    if ($gone.Count -gt 0) { $failed += "$($gone.Count) file(s) were removed while the app ran: $(($gone | Select-Object -First 5 | ForEach-Object Name) -join ', ')" }
}
catch { if ("$($_.Exception.Message)" -ne 'stop') { $failed += "the check itself failed: $($_.Exception.Message)" } }
finally {
    $found = @(Get-Detections $t0)
    foreach ($d in $found) { "   DEFENDER: $d" }
    if ($found.Count -gt 0) { $failed += "$($found.Count) Defender detection(s) in the test folder" }
    if (-not $Keep) {
        Start-Sleep -Milliseconds 800
        try { [IO.Directory]::Delete($root, $true) } catch { "test folder left behind: $(& $hide $_.Exception.Message)" }
    }
}
if ($failed.Count -gt 0) { "FAILED: " + ($failed -join '; '); exit 1 }
"OK: Defender finds nothing in the downloaded package and the app starts from it."
