using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using LinearFrame.Core;

namespace LinearFrame.UI;

/// <summary>
/// Common window plumbing: maximize to the work area (nothing cut off by the screen), the resize edges everywhere - over
/// the window buttons in the corner too - Windows 11 rounded corners, theme-aware frame.
/// </summary>
public static class WindowHelper
{
    public static void Attach(Window w, bool? forceDark = null)
    {
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            Theme.ApplyFrame(hwnd, forceDark ?? Theme.IsDark);
            if (AppPaths.Ghost) Native.SetCloak(hwnd, true);
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == Native.WM_GETMINMAXINFO)
                {
                    FixMaximize(w, h, lp);
                    handled = true;
                }
                else if (msg == Native.WM_NCHITTEST && EdgeAt(w, h, lp) is int edge and > 0)
                {
                    // ahead of WindowChrome: there the window buttons would win over the top-right corner and edge
                    handled = true;
                    return new IntPtr(edge);
                }
                return IntPtr.Zero;
            });
        };
        // maximised, a window may still stick out past the screen (what Windows does depends on the monitor and its
        // taskbar): its content is pulled in by exactly that much, so the window buttons are never cut off
        w.StateChanged += (_, _) => FitMaximized(w);
        w.LocationChanged += (_, _) => { if (w.WindowState == WindowState.Maximized) FitMaximized(w); };
        w.DpiChanged += (_, _) => FitMaximized(w);
        if (forceDark == null)
        {
            void OnTheme() => Theme.ApplyFrame(w);
            Theme.Changed += OnTheme;
            w.Closed += (_, _) => Theme.Changed -= OnTheme;
        }
    }

    /// <summary>The resize edge (HT code) under the point of a WM_NCHITTEST, or 0 - only for a normal, resizable window.</summary>
    private static int EdgeAt(Window w, IntPtr hwnd, IntPtr lParam)
    {
        if (w.WindowState != WindowState.Normal || w.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize) return 0;
        var border = WindowChrome.GetWindowChrome(w)?.ResizeBorderThickness ?? new Thickness(0);
        if (border.Top <= 0 && border.Right <= 0) return 0; // e.g. a player in full screen
        int x = Native.LoWord(lParam), y = Native.HiWord(lParam);
        Native.GetWindowRect(hwnd, out var r);
        double k = Native.GetDpiForWindow(hwnd) / 96.0;
        if (k <= 0) k = 1;
        bool l = x < r.Left + border.Left * k, rt = x >= r.Right - border.Right * k;
        bool t = y < r.Top + border.Top * k, b = y >= r.Bottom - border.Bottom * k;
        return (t, b, l, rt) switch
        {
            (true, _, true, _) => Native.HTTOPLEFT,
            (true, _, _, true) => Native.HTTOPRIGHT,
            (_, true, true, _) => Native.HTBOTTOMLEFT,
            (_, true, _, true) => Native.HTBOTTOMRIGHT,
            (true, _, _, _) => Native.HTTOP,
            (_, true, _, _) => Native.HTBOTTOM,
            (_, _, true, _) => Native.HTLEFT,
            (_, _, _, true) => Native.HTRIGHT,
            _ => 0,
        };
    }

    private static void FitMaximized(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (w.WindowState != WindowState.Maximized)
        {
            if (w.BorderThickness != default) w.BorderThickness = default;
            return;
        }
        Native.GetWindowRect(hwnd, out var r);
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), ref info)) return;
        var work = info.rcWork;
        double k = Native.GetDpiForWindow(hwnd) / 96.0;
        if (k <= 0) k = 1;
        w.BorderThickness = new Thickness(Math.Max(0, work.Left - r.Left) / k, Math.Max(0, work.Top - r.Top) / k,
            Math.Max(0, r.Right - work.Right) / k, Math.Max(0, r.Bottom - work.Bottom) / k);
    }

    private static void FixMaximize(Window w, IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref info))
        {
            var work = info.rcWork;
            var monitor = info.rcMonitor;
            mmi.ptMaxPosition.X = work.Left - monitor.Left;
            mmi.ptMaxPosition.Y = work.Top - monitor.Top;
            mmi.ptMaxSize.X = work.Width;
            mmi.ptMaxSize.Y = work.Height;
        }
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        if (w.MinWidth > 0) mmi.ptMinTrackSize.X = (int)Math.Ceiling(w.MinWidth * scale);
        if (w.MinHeight > 0) mmi.ptMinTrackSize.Y = (int)Math.Ceiling(w.MinHeight * scale);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    public static double DpiScale(Visual v)
    {
        try { return VisualTreeHelper.GetDpi(v).DpiScaleX; } catch { return 1.0; }
    }

    /// <summary>Monitor work area (in WPF units of the given DPI) under the mouse cursor.</summary>
    public static Rect WorkAreaAtCursor(out double scale)
    {
        Native.GetCursorPos(out var p);
        var mon = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(mon, ref info);
        Native.GetDpiForMonitor(mon, 0, out uint dpi, out _);
        scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var r = info.rcWork;
        return new Rect(r.Left, r.Top, r.Width, r.Height); // physical pixels
    }

    /// <summary>Positions a window (physical pixels) once it has a handle, independent of per-monitor DPI quirks.</summary>
    public static void PlaceCentered(Window w, double widthDip, double heightDip)
    {
        var work = WorkAreaAtCursor(out double scale);
        double wpx = Math.Min(widthDip * scale, work.Width * 0.96);
        double hpx = Math.Min(heightDip * scale, work.Height * 0.94);
        int x = (int)(work.X + (work.Width - wpx) / 2);
        int y = (int)(work.Y + (work.Height - hpx) / 2);
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Width = wpx / scale;
        w.Height = hpx / scale;
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, (int)wpx, (int)hpx, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        };
    }

    /// <summary>Another app (a game, a video) fills its whole monitor and has the focus.</summary>
    public static bool FullScreenAppInFront()
    {
        try
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;
            // the desktop, the taskbar, and Windows' own full-screen surfaces (lock screen, Start, search) are no game
            if (Native.ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow" or "LockScreenBackstopFrame") return false;
            if (!Native.GetWindowRect(fg, out var r)) return false;
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST), ref info)) return false;
            var m = info.rcMonitor;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }
        catch { return false; }
    }

    public static void BringToFront(Window w)
    {
        if (AppPaths.Ghost)
        {
            // a test copy that must not be seen: on (out of sight), never in front
            if (!w.IsVisible) { w.ShowActivated = false; w.Show(); }
            return;
        }
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        if (!w.IsVisible) w.Show();
        w.Activate();
        var hwnd = new WindowInteropHelper(w).Handle;
        Native.SetForegroundWindow(hwnd);
    }

    public static WindowChrome StandardChrome(double captionHeight) => new()
    {
        CaptionHeight = captionHeight,
        ResizeBorderThickness = new Thickness(6),
        GlassFrameThickness = new Thickness(0, 0, 0, 1),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,
    };
}

/// <summary>Minimize / maximize / close buttons for custom-chrome windows.</summary>
public sealed class CaptionButtons : StackPanel
{
    private Button? _max;
    private Window? _window;

    public static readonly DependencyProperty ShowMaximizeProperty = DependencyProperty.Register(
        nameof(ShowMaximize), typeof(bool), typeof(CaptionButtons), new PropertyMetadata(true));
    public static readonly DependencyProperty ShowMinimizeProperty = DependencyProperty.Register(
        nameof(ShowMinimize), typeof(bool), typeof(CaptionButtons), new PropertyMetadata(true));

    public bool ShowMaximize { get => (bool)GetValue(ShowMaximizeProperty); set => SetValue(ShowMaximizeProperty, value); }
    public bool ShowMinimize { get => (bool)GetValue(ShowMinimizeProperty); set => SetValue(ShowMinimizeProperty, value); }

    public CaptionButtons()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Right;
        // as tall as the toolbar they sit in: their pictures land on its middle line, with its other buttons
        VerticalAlignment = VerticalAlignment.Stretch;
        WindowChrome.SetIsHitTestVisibleInChrome(this, true);
        Build();
        Loaded += OnLoaded;
    }

    private Window? Host => _window ??= Window.GetWindow(this);

    private void Build()
    {
        Children.Clear();
        _max = null;
        if (ShowMinimize) Children.Add(Make("minimize", "Btn.Caption", () => { if (Host != null) Host.WindowState = WindowState.Minimized; }));
        if (ShowMaximize)
        {
            _max = Make("maximize", "Btn.Caption", () =>
            {
                if (Host != null) Host.WindowState = Host.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            });
            Children.Add(_max);
        }
        Children.Add(Make("close", "Btn.CaptionClose", () => Host?.Close()));
    }

    private bool _hooked;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ShowMinimize || !ShowMaximize) Build(); // properties set in XAML after construction
        if (_hooked || Host == null) return;
        _hooked = true;
        Host.StateChanged += (_, _) => UpdateMax();
        UpdateMax();
    }

    private void UpdateMax()
    {
        if (_max?.Content is IconView iv && _window != null)
            iv.Icon = _window.WindowState == WindowState.Maximized ? "restore" : "maximize";
    }

    private static Button Make(string icon, string style, Action click)
    {
        var b = new Button { Content = new IconView { Icon = icon, Width = 16, Height = 16, StrokeWidth = 1.5 }, Focusable = false };
        b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => click();
        return b;
    }
}

/// <summary>Apple-style HUD toast ("Copied", "Saved") that fades by itself.</summary>
public static class Hud
{
    /// <summary>A big, short-lived message in the middle (of <paramref name="over"/> or of the screen), with an optional smaller line under it.</summary>
    public static void Show(string text, string icon = "check", Window? over = null, string? hint = null)
    {
        try
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Focusable = false,
                IsHitTestVisible = false,
                Title = "LinearFrame.Hud",
            };
            var panel = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new IconView { Icon = icon, Width = 40, Height = 40, StrokeWidth = 2.2, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 10) });
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
                FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
            });
            if (!string.IsNullOrEmpty(hint))
                panel.Children.Add(new TextBlock
                {
                    Text = hint,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 260,
                    Margin = new Thickness(0, 5, 0, 0),
                    FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
                });
            var box = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x1F, 0x22)),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(24, 20, 24, 18),
                MinWidth = 150,
                Margin = new Thickness(20),
                Child = panel,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Direction = 270 },
            };
            w.Content = box;
            w.Opacity = 0;

            w.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
            };
            w.Loaded += (_, _) =>
            {
                // centre over the given window, else on the monitor under the cursor
                Rect area;
                if (over != null && over.IsVisible)
                    area = new Rect(over.Left, over.Top, over.ActualWidth, over.ActualHeight);
                else
                {
                    var work = WindowHelper.WorkAreaAtCursor(out double scale);
                    area = new Rect(work.X / scale, work.Y / scale, work.Width / scale, work.Height / scale);
                }
                w.Left = area.X + (area.Width - w.ActualWidth) / 2;
                w.Top = area.Y + (area.Height - w.ActualHeight) / 2;

                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130));
                w.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(hint == null ? 1150 : 1900) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
                    fadeOut.Completed += (_, _) => w.Close();
                    w.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                };
                timer.Start();
            };
            w.Show();
        }
        catch (Exception ex) { Log.Error("HUD failed", ex); }
    }
}

/// <summary>
/// Click, then press what should trigger the action: a combination (Ctrl+Shift+4), a single key (End, F13, PrintScreen…)
/// or a side mouse button. Esc cancels; Backspace clears an optional shortcut.
/// </summary>
public sealed class HotkeyBox : Button
{
    private HotkeyGesture _gesture = new();
    private bool _recording;
    private Window? _window;

    public event Action<HotkeyGesture>? GestureChanged;

    /// <summary>Optional shortcut: Backspace removes it.</summary>
    public bool AllowClear { get; set; }

    public HotkeyGesture Gesture
    {
        get => _gesture;
        set { _gesture = value; UpdateText(); }
    }

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, "Btn.Secondary");
        MinWidth = 170;
        Focusable = true;
        FocusVisualStyle = null;
        UpdateText();
    }

    private void UpdateText()
    {
        if (!_recording) Content = _gesture.IsEmpty && AllowClear ? L.T("Hotkey.Add") : _gesture.ToString();
    }

    protected override void OnClick()
    {
        if (_recording) return;
        _recording = true;
        Content = L.T("Hotkey.Press");
        App.Instance?.SuspendHotkeys(true);
        Focus();
        Keyboard.Focus(this);
        _window = Window.GetWindow(this);
        if (_window != null) _window.PreviewMouseDown += OnWindowMouseDown;
    }

    private static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>Keys you type with. On their own they'd stop working everywhere else, so they need Ctrl/Alt/Shift/Win.</summary>
    internal static bool IsTypingKey(Key k) => k is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9
        or >= Key.NumPad0 and <= Key.Divide or >= Key.Oem1 and <= Key.Oem102
        or Key.Space or Key.Enter or Key.Tab or Key.Back;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recording) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Stop(); return; }
        var mods = Keyboard.Modifiers;
        if (key == Key.Back && mods == ModifierKeys.None && AllowClear) { Commit(new HotkeyGesture()); return; }
        if (IsModifier(key))
        {
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add("…");
            Content = string.Join(" + ", parts);
            return;
        }
        if (key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed) return;
        if (mods == ModifierKeys.None && IsTypingKey(key))
        {
            Content = L.F("Hotkey.NeedsModifier", HotkeyGesture.KeyName(key));
            return;
        }
        Commit(new HotkeyGesture(mods, key));
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_recording) return;
        var button = e.ChangedButton switch
        {
            MouseButton.XButton1 => HotMouse.XButton1,
            MouseButton.XButton2 => HotMouse.XButton2,
            MouseButton.Middle => HotMouse.Middle,
            _ => HotMouse.None,
        };
        if (button == HotMouse.None) return; // a normal click elsewhere simply ends recording (focus moves)
        e.Handled = true;
        var mods = Keyboard.Modifiers;
        if (button == HotMouse.Middle && mods == ModifierKeys.None)
        {
            Content = L.T("Hotkey.MiddleNeedsModifier");
            return;
        }
        Commit(new HotkeyGesture(mods, button));
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // PrintScreen only produces a key-up for normal apps.
        if (_recording && (e.Key == Key.Snapshot || e.SystemKey == Key.Snapshot))
        {
            e.Handled = true;
            Commit(new HotkeyGesture(Keyboard.Modifiers, Key.Snapshot));
            return;
        }
        base.OnPreviewKeyUp(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_recording) Stop();
    }

    private void Commit(HotkeyGesture g)
    {
        _gesture = g;
        Stop();
        GestureChanged?.Invoke(g);
    }

    private void Stop()
    {
        _recording = false;
        if (_window != null) { _window.PreviewMouseDown -= OnWindowMouseDown; _window = null; }
        UpdateText();
        App.Instance?.SuspendHotkeys(false);
    }
}
