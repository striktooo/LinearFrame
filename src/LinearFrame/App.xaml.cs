using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LinearFrame.Capture;
using LinearFrame.Core;
using LinearFrame.Editor;
using LinearFrame.Player;
using LinearFrame.UI;

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
    private bool _errorShown;

    public IntPtr MessageHandle => _msg?.Handle ?? IntPtr.Zero;

    protected override void OnStartup(StartupEventArgs e)
    {
        Instance = this;
        var args = e.Args;

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

        _mutex = new Mutex(true, @"Local\LinearFrame.SingleInstance", out bool created);
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
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Unhandled", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Unobserved task", ev.Exception); ev.SetObserved(); };

        Settings = AppSettings.Load();
        L.Init(Settings.Language);
        Theme.Init(this);
        Log.Info($"Start {AppPaths.ExePath} args=[{string.Join(" ", args)}]");

        _msg = new MessageWindow();
        _msg.AddHandler(OnMessage);
        Native.TrySetMenuMode(1, _msg.Handle);
        _hotkeys = new HotkeyManager(_msg.Handle);
        CreateTray();
        RegisterHotkeys(showErrors: true);
        StartUpdates(args);

        if (!AppPaths.IsDevBuild && Settings.RunAtStartup) Autostart.Apply(true);
        if (!AppPaths.IsDevBuild) StartMenuShortcut.Ensure(Settings);

        var warm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        warm.Tick += (_, _) => { warm.Stop(); CaptureSession.Prewarm(Settings); _ = RefreshRetinaAsync(); };
        warm.Start();

        bool trayStart = args.Contains("--tray");
        HandleArgs(args.Where(a => a != "--tray").ToArray(), startup: true, trayStart: trayStart);
        Memory.TrimSoon(4000);
    }

    private void HandleArgs(string[] args, bool startup, bool trayStart = false)
    {
        var files = args.Where(a => !a.StartsWith("--") && File.Exists(a)).ToList();
        if (files.Count > 0)
        {
            if (MediaFormats.IsImage(files[0]) && !MediaFormats.IsAnimatedImage(files[0])) OpenImageInEditor(files[0]);
            else OpenMedia(files[0], edit: args.Contains("--trim") || args.Contains("--cut"), cut: args.Contains("--cut"));
            return;
        }
        if (args.Contains("--capture") || args.Contains("--capture-full")) { StartRegionCapture(); return; }
        if (args.Contains("--settings")) { ShowMain("settings"); return; }
        if (args.Contains("--exit")) { Quit(); return; }
        if (startup && trayStart)
        {
            if (!Settings.FirstRunDone) ShowMain("screens");
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
        if (msg == WM_DISPLAYCHANGE) Dispatcher.InvokeAsync(() => _ = RefreshRetinaAsync(), DispatcherPriority.Background);
        return false;
    }

    // ------------------------------------------------------------------ Retina mode

    private const int WM_DISPLAYCHANGE = 0x007E;
    private bool _retinaBusy;
    private RetinaStatus? _retina;
    private Task? _retinaRefresh;

    /// <summary>Monitor modes changed (by us or by anyone else) - or the cached state below was refreshed.</summary>
    public event Action? RetinaChanged;

    public bool RetinaBusy => _retinaBusy;

    /// <summary>Last known monitor state (asking the driver takes ~100 ms, so it's refreshed in the background).</summary>
    public RetinaStatus RetinaState => _retina ??= Retina.Query();
    public bool HasRetinaState => _retina != null;

    public Task RefreshRetinaAsync()
    {
        if (_retinaRefresh is { IsCompleted: false }) { _retinaAgain = true; return _retinaRefresh; }
        return _retinaRefresh = RefreshCore();

        async Task RefreshCore()
        {
            do
            {
                _retinaAgain = false;
                _retina = await Task.Run(Retina.Query);
                RetinaChanged?.Invoke();
            } while (_retinaAgain);
        }
    }

    private bool _retinaAgain;

    /// <summary>
    /// Switches Retina mode on or off for all monitors that support it. When switching on, a 15-second
    /// "Keep?" prompt follows; without an answer everything goes back automatically.
    /// </summary>
    public async Task<bool> SetRetinaAsync(bool on)
    {
        if (_retinaBusy) return false;
        _retinaBusy = true;
        RetinaChanged?.Invoke();
        try
        {
            var r = await Task.Run(() => on ? Retina.Enable(Settings) : Retina.Disable(Settings));
            Settings.Save();
            if (!r.Ok)
            {
                Log.Warn($"Retina {(on ? "on" : "off")} failed: {r.Error}");
                MessageBox.Show(r.Error ?? L.T("Retina.Failed"), "LinearFrame", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (!on) return true;
            await RefreshRetinaAsync();
            if (await RetinaConfirm.AskAsync()) return true;
            await Task.Run(() => Retina.Disable(Settings));
            Settings.Save();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Retina switch failed", ex);
            return false;
        }
        finally
        {
            _retinaBusy = false;
            await RefreshRetinaAsync();
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
        var retina = RetinaState;
        if (retina.CanToggle)
            m.Add(L.T("Tray.Retina"), () => _ = SetRetinaAsync(!retina.IsOn), isChecked: retina.IsOn, enabled: !_retinaBusy);
        m.Separator();
        m.Add(L.T("Tray.OpenMedia"), OpenMediaDialog);
        m.Add(L.T("Tray.Folder"), OpenScreenshotFolder);
        m.Separator();
        m.Add(L.T("Tray.Open"), () => ShowMain(null), isDefault: AvailableUpdate == null);
        m.Add(L.T("Tray.Settings"), () => ShowMain("settings"));
        m.Separator();
        m.Add(L.T("Tray.Quit"), Quit);
        m.Show(_msg.Handle, x, y);
        _ = RefreshRetinaAsync(); // e.g. DSR was just switched on in the driver - the next menu shows it
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
            if (AppPaths.IsDevBuild) Log.Info("Dev build: autostart not touched");
            else Autostart.Apply(Settings.RunAtStartup);
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
        if (dlg.ShowDialog() == true) OpenMedia(dlg.FileName);
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
