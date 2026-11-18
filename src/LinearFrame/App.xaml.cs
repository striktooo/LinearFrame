using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LinearFrame.Capture;
using LinearFrame.Core;
using LinearFrame.Editor;
using LinearFrame.Imaging;
using LinearFrame.Player;
using LinearFrame.UI;
using LinearFrame.Viewer;

namespace LinearFrame;

public partial class App : Application
{
    public static App? Instance { get; private set; }

    public AppSettings Settings { get; private set; } = new();

    private Mutex? _mutex;
    private MessageWindow? _msg;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private MainWindow? _main;
    private PlayerWindow? _player;
    private ImageViewerWindow? _viewer;
    private bool _errorShown;

    public IntPtr MessageHandle => _msg?.Handle ?? IntPtr.Zero;

    protected override void OnStartup(StartupEventArgs e)
    {
        Instance = this;
        var args = e.Args;

        // Elevated helper that installs / removes the virtual screen for HD captures (no UI, exit code = result)
        if (args.Length > 0 && args[0] == "--hd-setup")
        {
            Shutdown(HdSetup.RunElevated(args));
            return;
        }

        // Developer commands (no single-instance lock, no tray)
        if (args.Length > 0 && args[0] is "--selftest" or "--make-icon" or "--render-ui" or "--marketing" or "--compose")
        {
            Settings = AppSettings.Load();
            L.Init(args.Contains("--en") ? "en" : Settings.Language);
            Theme.Init(this, args.Contains("--light") ? false : args.Contains("--dark") ? true : null);
            int code = Dev.SelfTest.Run(args);
            Shutdown(code);
            return;
        }

        _mutex = new Mutex(true, @"Local\LinearFrame.SingleInstance" + AppPaths.Instance, out bool created);
        if (!created)
        {
            bool sent = false;
            for (int i = 0; i < 10 && !sent; i++)
            {
                sent = Ipc.TrySendToRunningInstance(args);
                if (!sent) Thread.Sleep(200);
            }
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        Deactivated += (_, _) => Memory.TrimSoon(8000); // left in the background -> hand the memory back
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Unhandled", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Unobserved task", ev.Exception); ev.SetObserved(); };

        Settings = AppSettings.Load();
        L.Init(Settings.Language);
        Theme.Init(this);
        RaisePriorityIfLowered();
        long uptimeS = Environment.TickCount64 / 1000;
        Log.Info($"Start {AppPaths.ExePath} args=[{string.Join(" ", args)}], Windows up {uptimeS / 60}:{uptimeS % 60:00}");

        _msg = new MessageWindow();
        _msg.AddHandler(OnMessage);
        Native.TrySetMenuMode(1, _msg.Handle);
        _hotkeys = new HotkeyManager(_msg.Handle);
        CreateTray();
        RegisterHotkeys(showErrors: true);
        StartUpdates(args);

        if (AppPaths.RegistersWithWindows && Settings.RunAtStartup) After(TimeSpan.FromSeconds(6), CheckAutostart);
        if (AppPaths.RegistersWithWindows) StartMenuShortcut.Ensure(Settings);

        // The virtual HD screen is tidied away first (that changes the display set-up), then the capture overlays are
        // warmed up on every monitor. Right after Windows has started, graphics drivers and screens keep settling for a
        // few minutes - the warm-up is repeated then, so the first screenshot never waits for that.
        After(TimeSpan.FromMilliseconds(1200), () => RefreshHd(tidy: true));
        After(TimeSpan.FromMilliseconds(2500), () => Warmup("start"));
        if (uptimeS < 10 * 60)
            foreach (int s in new[] { 30, 90, 240 }) After(TimeSpan.FromSeconds(s), () => Warmup("after Windows start"));

        bool trayStart = args.Contains("--tray");
        HandleArgs(args.Where(a => a != "--tray").ToArray(), startup: true, trayStart: trayStart);
        Memory.TrimSoon(4000);
    }

    private void HandleArgs(string[] args, bool startup, bool trayStart = false)
    {
        var files = args.Where(a => !a.StartsWith("--") && File.Exists(a)).ToList();
        if (files.Count > 0)
        {
            if (args.Contains("--trim") || args.Contains("--cut")) OpenMedia(files[0], edit: true, cut: args.Contains("--cut"));
            else OpenAny(files[0]);
            return;
        }
        if (args.Contains("--capture") || args.Contains("--capture-full")) { StartRegionCapture(); return; }
        if (AppPaths.Instance.Length > 0 && args.FirstOrDefault(a => a.StartsWith("--dev-capture=")) is string dev)
        {
            // test copies only: take a screenshot of x,y,w,h on the main monitor without anybody touching the mouse
            var n = dev["--dev-capture=".Length..].Split(',').Select(v => int.TryParse(v, out int i) ? i : 0).ToArray();
            if (n.Length == 4)
            {
                StartRegionCapture();
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                t.Tick += (_, _) => { t.Stop(); CaptureSession.Current?.CommitForTest(new Int32Rect(n[0], n[1], n[2], n[3])); };
                t.Start();
            }
            return;
        }
        if (args.Contains("--settings")) { ShowMain("settings"); return; }
        if (args.Contains("--exit")) { Quit(); return; }
        if (startup && trayStart)
        {
            // The welcome opens by itself only until the window has been seen once; after that LinearFrame starts
            // silently in the tray (an open window would also hold back quiet updates).
            if (!Settings.FirstRunDone && !Settings.WelcomeAutoShown) ShowMain("screens");
            return;
        }
        ShowMain(null);
    }

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_hotkeys?.Handle(msg, wParam) == true) return true;
        if (_tray?.Handle(msg, wParam, lParam) == true) return true;

        if (msg == Native.WM_COPYDATA)
        {
            var a = Ipc.ReadCopyData(lParam);
            if (a != null) Dispatcher.InvokeAsync(() => HandleArgs(a, false));
            return true;
        }
        if (msg == Native.WM_SETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var area = Marshal.PtrToStringUni(lParam);
            if (area == "ImmersiveColorSet") Dispatcher.InvokeAsync(Theme.Refresh);
        }
        if (msg == WM_DISPLAYCHANGE)
        {
            Dispatcher.InvokeAsync(() => RefreshHd(), DispatcherPriority.Background);
            Dispatcher.InvokeAsync(() => WarmupSoon("display change"), DispatcherPriority.Background);
        }
        if (msg == WM_POWERBROADCAST && wParam.ToInt64() is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND
            && Environment.TickCount64 - _lastResume > 20000) // Windows sends both after waking up
        {
            _lastResume = Environment.TickCount64;
            // back from sleep: drivers and screens come back over a while, like after a restart
            Dispatcher.InvokeAsync(() =>
            {
                After(TimeSpan.FromSeconds(5), () => Warmup("after sleep"));
                After(TimeSpan.FromSeconds(60), () => Warmup("after sleep"));
            });
        }
        return false;
    }

    private long _lastResume = long.MinValue / 2;

    // ------------------------------------------------------------------ start-up helpers

    private const int WM_POWERBROADCAST = 0x0218, PBT_APMRESUMESUSPEND = 0x0007, PBT_APMRESUMEAUTOMATIC = 0x0012;
    private DispatcherTimer? _displayWarm;
    private long _lastWarmup = long.MinValue / 2;

    /// <summary>Runs <paramref name="action"/> once, after <paramref name="delay"/>, on the UI thread.</summary>
    private static void After(TimeSpan delay, Action action)
    {
        var t = new DispatcherTimer { Interval = delay };
        t.Tick += (_, _) => { t.Stop(); action(); };
        t.Start();
    }

    /// <summary>Warms the capture overlays up - unless a capture is on screen or a game/video is full screen (then later).</summary>
    private void Warmup(string why, int attempt = 0)
    {
        if (CaptureSession.Current != null || WindowHelper.FullScreenAppInFront())
        {
            if (attempt < 20) After(TimeSpan.FromSeconds(30), () => Warmup(why, attempt + 1));
            return;
        }
        _lastWarmup = Environment.TickCount64;
        CaptureSession.Prewarm(Settings, why);
    }

    /// <summary>After a display change (several arrive in a row): one warm-up a few seconds after the last one.</summary>
    private void WarmupSoon(string why)
    {
        if (Environment.TickCount64 - _lastWarmup < 15000 || CaptureSession.Current != null) return; // (an HD capture switches the virtual screen on and off)
        if (_displayWarm == null)
        {
            _displayWarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _displayWarm.Tick += (_, _) => { _displayWarm.Stop(); Warmup(_displayWarmWhy); };
        }
        _displayWarmWhy = why;
        _displayWarm.Stop();
        _displayWarm.Start();
    }

    private string _displayWarmWhy = "";

    /// <summary>A scheduled task starts programs "below normal" unless told otherwise - LinearFrame should never lag behind.</summary>
    private static void RaisePriorityIfLowered()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            if (p.PriorityClass is System.Diagnostics.ProcessPriorityClass.BelowNormal or System.Diagnostics.ProcessPriorityClass.Idle)
            {
                p.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                Log.Info("Priority raised to normal");
            }
        }
        catch { /* not allowed - keep going */ }
    }

    /// <summary>Makes sure LinearFrame starts right at sign-in (a few seconds after start-up, off the UI thread).</summary>
    private void CheckAutostart()
    {
        Task.Run(Autostart.EnsureOnStart).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || t.Result) return;
            // switched off outside the app (Task Manager / Task Scheduler): the setting follows
            Dispatcher.InvokeAsync(() =>
            {
                Settings.RunAtStartup = false;
                Settings.Save();
            });
        });
    }

    // ------------------------------------------------------------------ HD captures

    private const int WM_DISPLAYCHANGE = 0x007E;
    private int _hdRefreshing;

    /// <summary>The virtual screen for HD captures is installed (cached; refreshed in the background).</summary>
    public bool HdInstalled { get; private set; }
    public bool HdBusy { get; private set; }
    public string HdStatus { get; private set; } = "";
    public event Action? HdChanged;

    /// <summary>HD captures are switched on and ready to use.</summary>
    public bool HdAvailable => Settings.HdEnabled && HdInstalled;

    /// <summary>Looks whether the virtual screen exists (off the UI thread). With <paramref name="tidy"/>: if our own
    /// virtual screen is on the desktop although nothing uses it (e.g. after a restart), it's taken off again.</summary>
    public void RefreshHd(bool tidy = false)
    {
        if (Interlocked.Exchange(ref _hdRefreshing, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var v = VirtualScreen.Find();
                if (tidy && v is { Attached: true } && CaptureSession.Current == null && HdSetup.OwnsDriver) VirtualScreen.Park(force: true);
                return v != null;
            }
            catch (Exception ex) { Log.Error("HD state", ex); return false; }
            finally { Interlocked.Exchange(ref _hdRefreshing, 0); }
        }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (HdInstalled == t.Result) return;
            HdInstalled = t.Result;
            HdChanged?.Invoke();
        }));
    }

    /// <summary>Switches HD captures on (installing the virtual screen the first time - one Windows confirmation) or off.</summary>
    public async Task<bool> SetHdAsync(bool on)
    {
        if (HdBusy) return false;
        if (!on)
        {
            Settings.HdEnabled = false;
            Settings.Save();
            VirtualScreen.Park();
            HdChanged?.Invoke();
            return true;
        }
        HdBusy = true;
        HdStatus = "";
        HdChanged?.Invoke();
        try
        {
            var r = await HdSetup.InstallAsync(s => Dispatcher.InvokeAsync(() => { HdStatus = s; HdChanged?.Invoke(); }));
            HdInstalled = r.Ok;
            Settings.HdEnabled = r.Ok;
            Settings.Save();
            HdStatus = r.Ok ? "" : r.Error ?? L.T("Hd.Why.Error");
            if (!r.Ok) Log.Warn("HD enable failed: " + r.Error);
            return r.Ok;
        }
        finally
        {
            HdBusy = false;
            HdChanged?.Invoke();
        }
    }

    /// <summary>Removes the virtual screen driver again (only offered when LinearFrame installed it).</summary>
    public async Task<bool> RemoveHdAsync()
    {
        if (HdBusy) return false;
        HdBusy = true;
        HdStatus = "";
        HdChanged?.Invoke();
        try
        {
            var r = await HdSetup.RemoveAsync();
            if (r.Ok)
            {
                HdInstalled = false;
                Settings.HdEnabled = false;
                Settings.Save();
            }
            else HdStatus = r.Error ?? "";
            return r.Ok;
        }
        finally
        {
            HdBusy = false;
            HdChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ tray

    private void CreateTray()
    {
        if (_msg == null) return;
        uint dpi = Native.GetDpiForSystem();
        int px = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi);
        if (px <= 0) px = 16;
        var icon = AppIconArt.CreateHIcon(px);
        _tray = new TrayIcon(_msg.Handle, icon, L.F("Tray.Tip", ShortcutText));
        _tray.Click += () => ShowMain(null);
        _tray.ContextMenu += ShowTrayMenu;
        _tray.BalloonClick += () => ShowMain("settings");
        _tray.Show();
    }

    private void ShowTrayMenu(int x, int y)
    {
        if (_msg == null) return;
        var m = new NativeMenu();
        if (AvailableUpdate != null)
        {
            m.Add(L.F("Tray.Update", AvailableUpdate.Version), () => _ = InstallUpdateNowAsync(), isDefault: true);
            m.Separator();
        }
        m.Add($"{L.T("Tray.Region")}\t{Settings.RegionHotkey}", () => StartRegionCapture(180));
        m.Separator();
        m.Add(L.T("Tray.OpenMedia"), OpenMediaDialog);
        m.Add(L.T("Tray.Folder"), OpenScreenshotFolder);
        m.Separator();
        m.Add(L.T("Tray.Open"), () => ShowMain(null), isDefault: AvailableUpdate == null);
        m.Add(L.T("Tray.Settings"), () => ShowMain("settings"));
        m.Separator();
        m.Add(L.T("Tray.Quit"), Quit);
        m.Show(_msg.Handle, x, y);
    }

    // ------------------------------------------------------------------ hotkeys

    public void RegisterHotkeys(bool showErrors)
    {
        if (_hotkeys == null) return;
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        if (!_hotkeys.Register(Settings.RegionHotkey, () => StartRegionCapture())) failed.Add(Settings.RegionHotkey.ToString());
        if (!Settings.RegionHotkey2.SameAs(Settings.RegionHotkey)
            && !_hotkeys.Register(Settings.RegionHotkey2, () => StartRegionCapture())) failed.Add(Settings.RegionHotkey2.ToString());
        _tray?.SetTip(L.F("Tray.Tip", ShortcutText));
        if (failed.Count > 0 && showErrors)
            _tray?.ShowInfo("LinearFrame", L.F("Hotkey.Failed", string.Join(", ", failed)));
    }

    /// <summary>"End" or "End / Mouse 5" - how the capture shortcut(s) read in texts.</summary>
    public string ShortcutText => Settings.RegionHotkey2.IsEmpty || Settings.RegionHotkey2.SameAs(Settings.RegionHotkey)
        ? Settings.RegionHotkey.ToString()
        : $"{Settings.RegionHotkey} / {Settings.RegionHotkey2}";

    public void SuspendHotkeys(bool suspend)
    {
        if (suspend) _hotkeys?.UnregisterAll();
        else RegisterHotkeys(showErrors: false);
    }

    public void SettingsChanged(bool hotkeysChanged = false, bool startupChanged = false)
    {
        Settings.Save();
        if (hotkeysChanged) RegisterHotkeys(showErrors: true);
        if (startupChanged)
        {
            if (!AppPaths.RegistersWithWindows) Log.Info("Dev build / test copy: autostart not touched");
            else
            {
                bool on = Settings.RunAtStartup;
                Task.Run(() => Autostart.Apply(on));
            }
        }
    }

    // ------------------------------------------------------------------ actions

    public void StartRegionCapture(int delayMs = 0)
    {
        if (delayMs > 0)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            t.Tick += (_, _) => { t.Stop(); CaptureSession.StartRegion(Settings); };
            t.Start();
        }
        else CaptureSession.StartRegion(Settings);
    }

    public void ShowMain(string? page)
    {
        if (!Settings.WelcomeAutoShown)
        {
            Settings.WelcomeAutoShown = true;
            Settings.Save();
        }
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => { _main = null; Memory.TrimSoon(); };
        }
        if (page != null) _main.Navigate(page);
        WindowHelper.BringToFront(_main);
    }

    public void OpenMedia(string path, bool edit = false, bool cut = false)
    {
        if (_player == null)
        {
            _player = new PlayerWindow();
            _player.Closed += (_, _) => { _player = null; Memory.TrimSoon(3000); };
        }
        _player.OpenFile(path, edit, cut);
        Settings.AddRecentMedia(path);
        Settings.Save();
        _main?.RefreshRecent();
    }

    public void OpenMediaDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = MediaFormats.DialogFilter,
            Title = "LinearFrame",
        };
        if (dlg.ShowDialog() == true) OpenAny(dlg.FileName);
    }

    /// <summary>Any file: pictures go to the viewer, everything else (and moving pictures) to the player.</summary>
    public void OpenAny(string path)
    {
        if (MediaFormats.IsImage(path)) OpenImage(path);
        else OpenMedia(path);
    }

    /// <summary>A still picture opens in the viewer; a GIF/WebP that moves (or a format Windows can't read) plays in the player.</summary>
    public void OpenImage(string path)
    {
        var info = ImageIO.Probe(path);
        if (info == null ? MediaFormats.IsAnimatedImage(path) : ImageIO.IsAnimated(path, info))
        {
            OpenMedia(path);
            return;
        }
        if (_viewer == null)
        {
            _viewer = new ImageViewerWindow();
            _viewer.Closed += (_, _) => _viewer = null;
        }
        _viewer.Open(path, info);
    }

    public void OpenImageInEditor(string path)
    {
        try
        {
            var doc = MarkupDocument.FromFile(path);
            new EditorWindow(doc).Show();
        }
        catch (Exception ex)
        {
            Log.Error("Open image failed", ex);
        }
    }

    public void OpenScreenshotFolder()
    {
        var dir = Settings.ScreenshotFolder;
        Directory.CreateDirectory(dir);
        ShellUtil.Open(dir);
    }

    public void NotifyScreenshotsChanged() => _main?.ScheduleShotsReload();

    public void Quit()
    {
        try
        {
            _hotkeys?.UnregisterAll();
            VirtualScreen.Park(); // never leave the off-screen HD canvas on the desktop
            _tray?.Dispose();
            _tray = null;
            foreach (Window w in Windows.Cast<Window>().ToList())
            {
                try { w.Close(); } catch { /* ignore */ }
            }
        }
        finally
        {
            Shutdown(0);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _msg?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
        if (_errorShown) return;
        _errorShown = true;
        MessageBox.Show(e.Exception.Message, "LinearFrame — " + L.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
        _errorShown = false;
    }
}
