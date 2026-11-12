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

/// <summary>Common window plumbing: maximize to the work area, Windows 11 rounded corners, theme-aware frame.</summary>
public static class WindowHelper
{
    public static void Attach(Window w, bool? forceDark = null)
    {
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            Theme.ApplyFrame(hwnd, forceDark ?? Theme.IsDark);
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == Native.WM_GETMINMAXINFO)
                {
                    FixMaximize(w, h, lp);
                    handled = true;
                }
                return IntPtr.Zero;
            });
        };
        if (forceDark == null)
        {
            void OnTheme() => Theme.ApplyFrame(w);
            Theme.Changed += OnTheme;
            w.Closed += (_, _) => Theme.Changed -= OnTheme;
        }
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

    public static void BringToFront(Window w)
    {
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
        VerticalAlignment = VerticalAlignment.Top;
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
    public static void Show(string text, string icon = "check", Window? over = null)
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
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1150) };
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

/// <summary>Click, then press a key combination to record a global shortcut.</summary>
public sealed class HotkeyBox : Button
{
    private HotkeyGesture _gesture = new();
    private bool _recording;

    public event Action<HotkeyGesture>? GestureChanged;

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
        if (!_recording) Content = _gesture.ToString();
    }

    protected override void OnClick()
    {
        if (_recording) return;
        _recording = true;
        Content = L.T("Hotkey.Press");
        App.Instance?.SuspendHotkeys(true);
        Focus();
        Keyboard.Focus(this);
    }

    private static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recording) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Stop(); return; }
        var mods = Keyboard.Modifiers;
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
        bool standalone = key is >= Key.F1 and <= Key.F24 or Key.Snapshot or Key.Pause or Key.Scroll or Key.Insert;
        if (mods == ModifierKeys.None && !standalone)
        {
            Content = L.T("Hotkey.Press");
            return;
        }
        Commit(new HotkeyGesture(mods, key));
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
        UpdateText();
        App.Instance?.SuspendHotkeys(false);
    }
}
