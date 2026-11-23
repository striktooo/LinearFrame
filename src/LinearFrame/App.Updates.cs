using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using LinearFrame.Capture;
using LinearFrame.Core;

namespace LinearFrame;

/// <summary>Automatic updates: check twice a day, download + verify in the background, install while nobody uses the app.</summary>
public partial class App
{
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _installTimer;
    private string? _updateRoot;   // extracted, verified and ready to install
    private bool _updateBusy;

    public UpdateInfo? AvailableUpdate { get; private set; }
    public string UpdateStatus { get; private set; } = "";
    public bool UpdateBusy => _updateBusy;
    public event Action? UpdateStateChanged;

    private void StartUpdates(string[] args)
    {
        var updated = args.FirstOrDefault(a => a.StartsWith("--updated="))?["--updated=".Length..];
        if (updated != null)
        {
            if (Version.TryParse(updated, out var v) && v > Updater.Current)
            {
                // the release said it was newer but the new build reports an older version - never loop on it
                Settings.SkippedUpdate = updated;
                Settings.Save();
                Log.Warn($"Update to {updated} produced version {Updater.Current} - skipping that release");
            }
            else
            {
                Log.Info($"Running updated version {Updater.Current}");
                _tray?.ShowInfo("LinearFrame", L.F("Update.Done", Updater.Current));
            }
            // The copy that started this one should be gone by now. One that does not manage to leave would stay behind
            // for good, keeping its old files in use - seen with 1.2.0: after an automatic update its process was still
            // there a minute later (with a warning in its log right after "Restarting"), holding the old 146 MB exe.
            Task.Run(EndReplacedCopy);
        }

        // old binaries may stay locked for a moment by the previous process
        var cleanup = new DispatcherTimer { Interval = TimeSpan.FromSeconds(updated != null ? 8 : 25) };
        cleanup.Tick += (_, _) =>
        {
            cleanup.Stop();
            if (!_updateBusy && _updateRoot == null) Task.Run(Updater.Cleanup);
        };
        cleanup.Start();

        int firstDelay = int.TryParse(Environment.GetEnvironmentVariable("LINEARFRAME_UPDATE_DELAY"), out var d) ? Math.Max(1, d) : 90;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(firstDelay) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(12);
            if (Settings.AutoUpdate) await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    /// <summary>Ends a copy of the previous version that is still running although it has handed over to this one.</summary>
    private static void EndReplacedCopy()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            string exe = AppPaths.ExePath;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == self.Id || p.SessionId != self.SessionId || p.StartTime >= self.StartTime) continue;
                        // (a running exe that has been renamed still reports the name it was started under)
                        string? path = p.MainModule?.FileName;
                        if (path == null || !(path.Equals(exe, StringComparison.OrdinalIgnoreCase) || path.Equals(exe + ".old", StringComparison.OrdinalIgnoreCase))) continue;
                        if (p.WaitForExit(4000)) continue;
                        p.Kill();
                        Log.Info($"The replaced copy (process {p.Id}) had not left by itself - ended");
                    }
                    catch { /* gone meanwhile, or not ours to look at */ }
                }
            }
        }
        catch (Exception ex) { Log.Warn("Looking for the replaced copy: " + ex.Message); }
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync(bool manual)
    {
        if (_updateBusy) return AvailableUpdate;
        _updateBusy = true;
        SetUpdateStatus(L.T("Update.Checking"));
        try
        {
            var u = await Updater.CheckAsync(CancellationToken.None);
            Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            if (u != null && !manual && Settings.SkippedUpdate == u.Version.ToString()) u = null;
            Settings.Save();
            AvailableUpdate = u;
            if (u == null)
            {
                SetUpdateStatus(L.F("Update.UpToDate", Updater.Current));
                return null;
            }
            Log.Info($"Update available: {u.Version}");
            SetUpdateStatus(L.F("Update.Available", u.Version));
            if (!manual)
            {
                if (Updater.CanSelfUpdate())
                {
                    await DownloadUpdateAsync(null);
                    ScheduleInstall();
                }
                else _tray?.ShowInfo("LinearFrame", L.F("Update.Available", u.Version));
            }
            return u;
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            SetUpdateStatus(L.T("Update.CheckFailed"));
            return null;
        }
        finally
        {
            _updateBusy = false;
            UpdateStateChanged?.Invoke();
        }
    }

    private async Task DownloadUpdateAsync(IProgress<double>? progress)
    {
        if (AvailableUpdate == null || _updateRoot != null) return;
        var u = AvailableUpdate;
        // (off this thread: choosing the package means checksumming the installed engine and .NET - some 400 MB)
        var zip = await Task.Run(() => Updater.DownloadAsync(u, progress, CancellationToken.None));
        _updateRoot = await Task.Run(() => Updater.Extract(zip, u.Version));
        Log.Info($"Update {u.Version} downloaded and verified");
    }

    private void ScheduleInstall()
    {
        _installTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _installTimer.Tick -= InstallTick;
        _installTimer.Tick += InstallTick;
        _installTimer.Start();
        InstallTick(null, EventArgs.Empty);
    }

    private void InstallTick(object? sender, EventArgs e)
    {
        if (_updateRoot == null) { _installTimer?.Stop(); return; }
        // never restart under the user's hands: wait until nothing is on screen
        bool busy = CaptureSession.Current != null || Windows.Cast<Window>().Any(w => w.IsVisible);
        if (busy) return;
        _installTimer?.Stop();
        InstallAndRestart(showSettings: false);
    }

    /// <summary>"Install x.y.z" button in Settings or the tray menu.</summary>
    public async Task InstallUpdateNowAsync()
    {
        if (AvailableUpdate == null || _updateBusy) return;
        if (!Updater.CanSelfUpdate())
        {
            ShellUtil.Open(AvailableUpdate.PageUrl);
            return;
        }
        _updateBusy = true;
        try
        {
            SetUpdateStatus(L.F("Update.Downloading", 0));
            await DownloadUpdateAsync(new Progress<double>(p => SetUpdateStatus(L.F("Update.Downloading", (int)(p * 100)))));
            InstallAndRestart(showSettings: true);
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            SetUpdateStatus(L.T("Update.Failed"));
        }
        finally
        {
            _updateBusy = false;
            UpdateStateChanged?.Invoke();
        }
    }

    private void InstallAndRestart(bool showSettings)
    {
        if (_updateRoot == null || AvailableUpdate == null) return;
        var v = AvailableUpdate.Version;
        if (!Updater.Apply(_updateRoot))
        {
            _updateRoot = null;
            SetUpdateStatus(L.T("Update.Failed"));
            _tray?.ShowInfo("LinearFrame", L.T("Update.Failed"));
            return;
        }
        Log.Info($"Restarting into {v}");

        // hand the single-instance lock, tray icon and hotkeys over to the new process
        _hotkeys?.UnregisterAll();
        VirtualScreen.Park();
        _tray?.Dispose();
        _tray = null;
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        _mutex?.Dispose();
        _mutex = null;
        _msg?.Dispose();
        _msg = null;

        var psi = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = false, WorkingDirectory = AppPaths.BaseDir };
        psi.ArgumentList.Add("--tray");
        psi.ArgumentList.Add($"--updated={v}");
        if (showSettings) psi.ArgumentList.Add("--settings");
        Process.Start(psi);
        // the new copy is running: whatever happens on the way out, this one does not stay behind
        new Thread(() => { Thread.Sleep(5000); Environment.Exit(0); }) { IsBackground = true, Name = "leave" }.Start();

        foreach (Window w in Windows.Cast<Window>().ToList())
        {
            try { w.Close(); } catch { /* closing anyway */ }
        }
        Shutdown(0);
    }

    private void SetUpdateStatus(string s)
    {
        UpdateStatus = s;
        UpdateStateChanged?.Invoke();
    }
}
