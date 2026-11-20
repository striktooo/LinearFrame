using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinearFrame.Core;
using LinearFrame.Imaging;
using LinearFrame.UI;

namespace LinearFrame.Capture;

/// <summary>
/// Full-screen overlay for one monitor: shows the frozen screen, dims it, lets the user drag a selection or click a window.
/// All selection maths happens in physical pixels of this monitor.
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    private enum Mode { Idle, Pressing, Dragging, Selected, Moving, Resizing }

    private readonly CaptureSession _session;
    private readonly BitmapSource _shot;
    private readonly List<Int32Rect> _windows;
    private readonly bool _activateOnShow;
    private readonly bool _warmup;
    private readonly SelectionSurface _surface;
    private readonly Canvas _layer;
    private readonly Border _toolbar;
    private readonly TextBlock _sizeText;
    private TextBlock? _hdText;
    private string? _busy;   // set while an HD render is under way: the overlay only shows the frozen picture
    private IntPtr _hwnd;

    private Mode _mode = Mode.Idle;
    private Int32Rect? _sel;
    private Int32Rect? _hover;
    private Point _pressPx;
    private Point _mousePx;
    private bool _mouseInside;
    private int _handle = -1;
    private Int32Rect _selAtPress;

    public MonitorInfo Monitor { get; }

    /// <summary>Overlay windows that have not been garbage-collected yet (each holds a screen-sized picture) - for the log.</summary>
    internal static int Alive => _alive;
    private static int _alive;
    ~CaptureOverlayWindow() => Interlocked.Decrement(ref _alive);

    /// <summary>Fired once the window has drawn its first frame (it stays invisible - see <see cref="CaptureSession.Prewarm"/>).</summary>
    public event Action? WarmedUp;

    public CaptureOverlayWindow(CaptureSession session, MonitorInfo monitor, BitmapSource shot, List<Int32Rect> windows, bool activate, bool warmup = false)
    {
        Interlocked.Increment(ref _alive);
        _session = session;
        Monitor = monitor;
        _shot = shot;
        _windows = windows;
        _activateOnShow = activate && !warmup;
        _warmup = warmup;

        Title = "LinearFrame.Capture";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = !warmup;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        Focusable = true;
        ShowActivated = activate;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Bounds.X / monitor.Scale;
        Top = monitor.Bounds.Y / monitor.Scale;
        Width = monitor.Bounds.Width / monitor.Scale;
        Height = monitor.Bounds.Height / monitor.Scale;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _surface = new SelectionSurface(this);
        _layer = new Canvas { IsHitTestVisible = true };
        var root = new Grid();
        root.Children.Add(_surface);
        root.Children.Add(_layer);
        Content = root;

        (_toolbar, _sizeText) = BuildToolbar();
        _toolbar.Visibility = Visibility.Collapsed;
        _layer.Children.Add(_toolbar);
        _session.SizeChanged += OnSizeChanged;

        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) =>
        {
            if (_warmup) WarmedUp?.Invoke(); // never uncloaked
            else Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reveal));
        };
        PreviewKeyDown += (_, e) => _session.HandleKey(this, e);
        Closed += (_, _) =>
        {
            _session.SizeChanged -= OnSizeChanged;
            // a QR look that was still waiting would read a capture that is over (and hold its screen picture meanwhile)
            _closed = true;
            _qrTimer?.Stop();
        };

        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonUp += OnRightUp;
        MouseLeave += (_, _) => { _mouseInside = false; if (_mode == Mode.Idle) { _hover = null; _surface.Redraw(); } };
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);

    private sealed class QuietPeer : System.Windows.Automation.Peers.WindowAutomationPeer
    {
        public QuietPeer(Window w) : base(w) { }
        protected override List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null;
    }

    internal BitmapSource Shot => _shot;
    internal Int32Rect? Selection => _sel;

    /// <summary>What an action works on: the selection, or the window under the mouse when nothing is selected yet.</summary>
    internal Int32Rect? Target
    {
        get
        {
            Int32Rect? t = _sel;
            if (t == null && _mode == Mode.Idle && _mouseInside) { UpdateHover(); t = _hover; }
            return t;
        }
    }
    internal Int32Rect? Hover => _mode == Mode.Idle ? _hover : null;
    internal bool ShowHandles => _mode == Mode.Selected;
    internal bool IsDragging => _mode is Mode.Dragging or Mode.Moving or Mode.Resizing;
    internal Point MousePx => _mousePx;
    internal bool MouseInside => _mouseInside;
    internal ExportSize OutputSize => _session.Size;
    /// <summary>How many times more pixels per side the current selection will get (1 = HD off).</summary>
    internal int HdFactor => !_session.Hd ? 1 : _sel is Int32Rect s ? HdCapture.ScaleFor(s.Width, s.Height, _session.HdScale) : _session.HdScale;
    internal string? BusyText => _busy;
    internal double Scale { get; private set; } = 1.0;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // a warm-up window never takes a click or the focus (it is invisible anyway)
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | (_warmup ? Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE : 0));
        Native.SetCloak(_hwnd, true);
        int disable = 1;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_TRANSITIONS_FORCEDISABLED, ref disable, sizeof(int));
        Place();
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == Native.WM_DPICHANGED) Dispatcher.BeginInvoke(new Action(Place));
            return IntPtr.Zero;
        });
    }

    private void Place()
    {
        var b = Monitor.Bounds;
        Native.SetWindowPos(_hwnd, _warmup ? Native.HWND_BOTTOM : Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE);
        Scale = WindowHelper.DpiScale(this);
        _surface.Redraw();
    }

    private void Reveal()
    {
        Scale = WindowHelper.DpiScale(this);
        Native.SetCloak(_hwnd, false);
        if (_activateOnShow)
        {
            Activate();
            Native.SetForegroundWindow(_hwnd);
            Focus();
        }
        Native.GetCursorPos(out var p);
        if (Monitor.Contains(p.X, p.Y))
        {
            _mouseInside = true;
            _mousePx = new Point(p.X - Monitor.Bounds.X, p.Y - Monitor.Bounds.Y);
            UpdateHover();
            _surface.Redraw();
        }
    }

    // ------------------------------------------------------------------ toolbar

    private (Border, TextBlock) BuildToolbar()
    {
        Button Icon(string icon, string tip, Action click)
        {
            var b = new Button { Content = new IconView { Icon = icon, Width = 18, Height = 18 }, ToolTip = tip };
            b.SetResourceReference(StyleProperty, "Hud.IconButton");
            b.Click += (_, _) => click();
            return b;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Icon("pen", L.T("Capture.Edit"), () => Commit(CaptureAction.Edit)));

        var sizeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var sizeContent = new StackPanel { Orientation = Orientation.Horizontal };
        sizeContent.Children.Add(new IconView { Icon = "size", Width = 15, Height = 15, Margin = new Thickness(0, 0, 6, 0) });
        sizeContent.Children.Add(sizeText);
        sizeContent.Children.Add(new IconView { Icon = "chevron-down", Width = 13, Height = 13, Opacity = 0.7 });
        var sizeBtn = new Button { Content = sizeContent, ToolTip = L.T("Capture.SizeTip"), Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(10, 6, 8, 6) };
        sizeBtn.SetResourceReference(StyleProperty, "Hud.Button");
        sizeBtn.Background = Brushes.Transparent;
        sizeBtn.Click += (_, _) => _session.ShowSizeMenu(sizeBtn);
        panel.Children.Add(sizeBtn);

        if (_session.HdAvailable)
        {
            // "HD": render the selection for real at 2-4x instead of taking the pixels off the screen
            _hdText = new TextBlock { Text = "HD", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            var hdBtn = new Button { Content = _hdText, ToolTip = L.F("Capture.HdTip", _session.HdScale), Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(10, 6, 10, 6) };
            hdBtn.SetResourceReference(StyleProperty, "Hud.Button");
            hdBtn.Background = Brushes.Transparent;
            hdBtn.Click += (_, _) => _session.ToggleHd();
            panel.Children.Add(hdBtn);
            UpdateHdLook();
        }

        panel.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 7, 4, 7), Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) });
        panel.Children.Add(Icon("textscan", L.T("Capture.CopyTextTip"), () => _session.CopyText(this)));
        panel.Children.Add(Icon("save", L.T("Capture.SaveAs"), () => Commit(CaptureAction.SaveAs)));
        panel.Children.Add(Icon("close", L.T("Capture.Cancel"), () => _session.Cancel()));

        var copyContent = new StackPanel { Orientation = Orientation.Horizontal };
        copyContent.Children.Add(new IconView { Icon = "copy", Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) });
        copyContent.Children.Add(new TextBlock { Text = L.T("Capture.Copy"), VerticalAlignment = VerticalAlignment.Center });
        copyContent.Children.Add(new TextBlock { Text = "  Ctrl+C", Opacity = 0.65, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
        var copy = new Button { Content = copyContent, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        copy.SetResourceReference(StyleProperty, "Hud.Primary");
        copy.Click += (_, _) => Commit(CaptureAction.Copy);
        panel.Children.Add(copy);

        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1F, 0x1F, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Child = panel,
            Cursor = Cursors.Arrow,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.45, Direction = 270 },
        };
        TextElement.SetForeground(bar, Brushes.White);
        TextElement.SetFontSize(bar, 13);
        TextElement.SetFontFamily(bar, (FontFamily)FindResource("Font.UI"));
        bar.MouseLeftButtonDown += (_, e) => e.Handled = true;
        bar.MouseLeftButtonUp += (_, e) => e.Handled = true;
        sizeText.Text = ExportSizes.Label(_session.Size);
        return (bar, sizeText);
    }

    private static readonly Brush HdOnBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private void UpdateHdLook()
    {
        if (_hdText == null) return;
        _hdText.Foreground = _session.Hd ? HdOnBrush : Brushes.White;
        _hdText.Opacity = _session.Hd ? 1 : 0.45;
    }

    /// <summary>An HD render has started: keep showing the frozen screen, take no more input, say what is going on.</summary>
    public void ShowBusy(string? text)
    {
        _busy = text ?? "";
        _toolbar.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Wait;
        ReleaseMouseCapture();
        _surface.Redraw();
    }

    private void OnSizeChanged()
    {
        UpdateHdLook();
        _sizeText.Text = ExportSizes.Label(_session.Size);
        _surface.Redraw();
        Dispatcher.BeginInvoke(new Action(PositionToolbar), DispatcherPriority.Render);
    }

    private void PositionToolbar()
    {
        if (_sel is not Int32Rect s || _mode != Mode.Selected)
        {
            _toolbar.Visibility = Visibility.Collapsed;
            return;
        }
        _toolbar.Visibility = Visibility.Visible;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toolbar.DesiredSize;
        double k = Scale;
        double left = s.X / k, top = s.Y / k, right = (s.X + s.Width) / k, bottom = (s.Y + s.Height) / k;
        var root = (FrameworkElement)Content;
        double w = root.ActualWidth, h = root.ActualHeight;
        double y = bottom + 12;
        if (y + size.Height > h - 8)
        {
            y = top - 12 - size.Height;
            if (y < 8) y = Math.Max(8, bottom - size.Height - 12);
        }
        double x = (left + right) / 2 - size.Width / 2;
        x = Math.Clamp(x, 8, Math.Max(8, w - size.Width - 8));
        Canvas.SetLeft(_toolbar, Math.Round(x));
        Canvas.SetTop(_toolbar, Math.Round(y));
        ScheduleQr();
    }

    // ------------------------------------------------------------------ a QR code in the selection

    private DispatcherTimer? _qrTimer;
    private Int32Rect? _qrFor;
    private Button? _qrPill;
    private bool _closed;

    /// <summary>The selection settled: look for a QR code in it (a moment later, off the UI thread).</summary>
    private void ScheduleQr()
    {
        if (_closed || _sel is not Int32Rect || _mode != Mode.Selected || _sel == _qrFor) return;
        RemoveQrPill();
        _qrTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, (_, _) => ScanQr(), Dispatcher);
        _qrTimer.Stop();
        _qrTimer.Start();
    }

    private async void ScanQr()
    {
        _qrTimer?.Stop();
        if (_closed || _sel is not Int32Rect s || _mode != Mode.Selected || _busy != null) return;
        _qrFor = s;
        var r = ImageOps.Intersect(s, new Int32Rect(0, 0, _shot.PixelWidth, _shot.PixelHeight));
        if (r.Width < 21 || r.Height < 21) return;
        var crop = new CroppedBitmap(_shot, r);
        crop.Freeze();
        List<QrReader.Code> codes;
        try { codes = await Task.Run(() => QrReader.Read(PixelBuffer.FromBitmap(crop))); }
        catch { return; }
        if (_closed || _sel != s || _mode != Mode.Selected || _busy != null || codes.Count == 0) return;
        ShowQrPill(codes[0], r);
    }

    private void ShowQrPill(QrReader.Code code, Int32Rect r)
    {
        RemoveQrPill();
        string? link = code.Link;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new IconView { Icon = link != null ? "link" : "copy", Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0) });
        content.Children.Add(new TextBlock
        {
            Text = link != null ? L.F("Capture.QrOpen", Viewer.LiveText.Short(link)) : L.T("Live.QrCopy"),
            MaxWidth = 340,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var pill = new Button { Content = content, ToolTip = code.Text, Padding = new Thickness(13, 7, 14, 7), Cursor = Cursors.Hand };
        pill.SetResourceReference(StyleProperty, "Hud.Primary");
        TextElement.SetFontFamily(pill, (FontFamily)FindResource("Font.UI"));
        pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pill.MouseLeftButtonUp += (_, e) => e.Handled = true;
        pill.Click += (_, _) =>
        {
            _session.Cancel();
            if (link != null) ShellUtil.Open(link);
            else if (ImageIO.CopyText(code.Text)) Hud.Show(L.T("Live.Copied"), "copy");
        };
        // right under the code, inside the selection
        var b = code.Bounds;
        double k = Scale;
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = pill.DesiredSize;
        var rootEl = (FrameworkElement)Content;
        double x = (r.X + b.X + b.Width / 2) / k - size.Width / 2;
        double y = (r.Y + b.Bottom) / k + 10;
        if (y + size.Height > (r.Y + r.Height) / k - 6) y = (r.Y + b.Y) / k - size.Height - 10;
        Canvas.SetLeft(pill, Math.Round(Math.Clamp(x, 8, Math.Max(8, rootEl.ActualWidth - size.Width - 8))));
        Canvas.SetTop(pill, Math.Round(Math.Clamp(y, 8, Math.Max(8, rootEl.ActualHeight - size.Height - 8))));
        _layer.Children.Add(pill);
        _qrPill = pill;
    }

    private void RemoveQrPill()
    {
        if (_qrPill == null) return;
        _layer.Children.Remove(_qrPill);
        _qrPill = null;
        _qrFor = null;
    }

    /// <summary>Reading text failed or found nothing: the selection is back in use.</summary>
    public void EndBusy()
    {
        _busy = null;
        Cursor = Cursors.Arrow;
        _surface.Redraw();
        PositionToolbar();
    }

    // ------------------------------------------------------------------ mouse

    private Point ToPx(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return new Point(Math.Clamp(p.X * Scale, 0, Monitor.Bounds.Width), Math.Clamp(p.Y * Scale, 0, Monitor.Bounds.Height));
    }

    private void UpdateHover()
    {
        _hover = null;
        int x = (int)_mousePx.X, y = (int)_mousePx.Y;
        foreach (var r in _windows)
        {
            if (x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height) { _hover = r; break; }
        }
        // Desktop (no window under the cursor) -> whole monitor, only when window detection is on
        if (_hover == null && _windows.Count > 0) _hover = new Int32Rect(0, 0, Monitor.Bounds.Width, Monitor.Bounds.Height);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_busy != null) return;
        _mouseInside = true;
        _mousePx = ToPx(e);
        switch (_mode)
        {
            case Mode.Idle:
                UpdateHover();
                break;
            case Mode.Selected:
                UpdateCursor();
                break;
            case Mode.Pressing:
                if (Math.Abs(_mousePx.X - _pressPx.X) > 3 * Scale || Math.Abs(_mousePx.Y - _pressPx.Y) > 3 * Scale)
                {
                    _mode = Mode.Dragging;
                    _session.OnSelectionStarted(this);
                    _toolbar.Visibility = Visibility.Collapsed;
                }
                break;
        }

        if (_mode == Mode.Dragging) _sel = Normalize(_pressPx, _mousePx);
        else if (_mode == Mode.Moving)
        {
            int dx = (int)Math.Round(_mousePx.X - _pressPx.X), dy = (int)Math.Round(_mousePx.Y - _pressPx.Y);
            var s = _selAtPress;
            int nx = Math.Clamp(s.X + dx, 0, Monitor.Bounds.Width - s.Width);
            int ny = Math.Clamp(s.Y + dy, 0, Monitor.Bounds.Height - s.Height);
            _sel = new Int32Rect(nx, ny, s.Width, s.Height);
        }
        else if (_mode == Mode.Resizing) _sel = ResizeBy(_selAtPress, _handle, _mousePx);

        _surface.Redraw();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy != null) return;
        Focus();
        RemoveQrPill();
        var p = ToPx(e);
        _mousePx = p;
        if (e.ClickCount == 2 && _sel is Int32Rect sd && Contains(sd, p))
        {
            Commit(CaptureAction.Copy);
            return;
        }

        if (_mode == Mode.Selected && _sel is Int32Rect s)
        {
            int h = HitHandle(s, p);
            if (h >= 0)
            {
                _mode = Mode.Resizing;
                _handle = h;
                _selAtPress = s;
                _pressPx = p;
                _toolbar.Visibility = Visibility.Collapsed;
                CaptureMouse();
                return;
            }
            if (Contains(s, p))
            {
                _mode = Mode.Moving;
                _selAtPress = s;
                _pressPx = p;
                _toolbar.Visibility = Visibility.Collapsed;
                CaptureMouse();
                return;
            }
        }

        _mode = Mode.Pressing;
        _pressPx = p;
        _sel = null;
        _toolbar.Visibility = Visibility.Collapsed;
        CaptureMouse();
        _surface.Redraw();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_busy != null) return;
        ReleaseMouseCapture();
        switch (_mode)
        {
            case Mode.Pressing:
                // simple click -> take the window (or whole monitor) under the cursor
                UpdateHover();
                if (_hover is Int32Rect hv)
                {
                    _session.OnSelectionStarted(this);
                    _sel = hv;
                    _mode = Mode.Selected;
                }
                else _mode = Mode.Idle;
                break;
            case Mode.Dragging:
                if (_sel is Int32Rect s && s.Width >= 3 && s.Height >= 3) _mode = Mode.Selected;
                else { _sel = null; _mode = Mode.Idle; UpdateHover(); }
                break;
            case Mode.Moving:
            case Mode.Resizing:
                _mode = Mode.Selected;
                break;
        }
        UpdateCursor();
        _surface.Redraw();
        PositionToolbar();
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_busy != null) return;
        if (_mode == Mode.Selected) ClearSelection();
        else _session.Cancel();
    }

    internal void SelectForTest(Int32Rect r)
    {
        _sel = r;
        _mode = Mode.Selected;
        _surface.Redraw();
        PositionToolbar();
    }

    public void ClearSelection()
    {
        if (_busy != null) return;
        _sel = null;
        _mode = Mode.Idle;
        _toolbar.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Cross;
        if (_mouseInside) UpdateHover(); else _hover = null;
        _surface.Redraw();
    }

    public void Commit(CaptureAction action)
    {
        Int32Rect? target = _sel;
        if (target == null && _mode == Mode.Idle && _mouseInside) { UpdateHover(); target = _hover; }
        if (target is Int32Rect r && r.Width > 0 && r.Height > 0) _session.Finish(this, r, action);
    }

    public void Nudge(int dx, int dy, bool big)
    {
        if (_sel is not Int32Rect s || _mode != Mode.Selected) return;
        int k = big ? 10 : 1;
        int nx = Math.Clamp(s.X + dx * k, 0, Monitor.Bounds.Width - s.Width);
        int ny = Math.Clamp(s.Y + dy * k, 0, Monitor.Bounds.Height - s.Height);
        _sel = new Int32Rect(nx, ny, s.Width, s.Height);
        _surface.Redraw();
        PositionToolbar();
    }

    private void UpdateCursor()
    {
        if (_mode != Mode.Selected || _sel is not Int32Rect s) { Cursor = Cursors.Cross; return; }
        int h = HitHandle(s, _mousePx);
        Cursor = h switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            3 or 7 => Cursors.SizeWE,
            _ => Contains(s, _mousePx) ? Cursors.SizeAll : Cursors.Cross,
        };
    }

    private static bool Contains(Int32Rect r, Point p) => p.X >= r.X && p.Y >= r.Y && p.X <= r.X + r.Width && p.Y <= r.Y + r.Height;

    private static Int32Rect Normalize(Point a, Point b)
    {
        int x0 = (int)Math.Round(Math.Min(a.X, b.X)), y0 = (int)Math.Round(Math.Min(a.Y, b.Y));
        int x1 = (int)Math.Round(Math.Max(a.X, b.X)), y1 = (int)Math.Round(Math.Max(a.Y, b.Y));
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Handle index: 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L.</summary>
    internal static Point[] HandlePoints(Int32Rect r)
    {
        double l = r.X, t = r.Y, rr = r.X + r.Width, b = r.Y + r.Height, cx = l + r.Width / 2.0, cy = t + r.Height / 2.0;
        return new[] { new Point(l, t), new Point(cx, t), new Point(rr, t), new Point(rr, cy), new Point(rr, b), new Point(cx, b), new Point(l, b), new Point(l, cy) };
    }

    private int HitHandle(Int32Rect r, Point p)
    {
        double tol = 9 * Scale;
        var pts = HandlePoints(r);
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs(pts[i].X - p.X) <= tol && Math.Abs(pts[i].Y - p.Y) <= tol) return i;
        return -1;
    }

    private Int32Rect ResizeBy(Int32Rect s, int handle, Point p)
    {
        double l = s.X, t = s.Y, r = s.X + s.Width, b = s.Y + s.Height;
        switch (handle)
        {
            case 0: l = p.X; t = p.Y; break;
            case 1: t = p.Y; break;
            case 2: r = p.X; t = p.Y; break;
            case 3: r = p.X; break;
            case 4: r = p.X; b = p.Y; break;
            case 5: b = p.Y; break;
            case 6: l = p.X; b = p.Y; break;
            case 7: l = p.X; break;
        }
        return Normalize(new Point(l, t), new Point(r, b));
    }
}

/// <summary>Draws the frozen screen (once) and the dim/selection layer (on every change).</summary>
internal sealed class SelectionSurface : FrameworkElement
{
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)));
    private static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0x0A, 0x84, 0xFF)));
    private static readonly Pen HoverPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 2));
    private static readonly Pen HandlePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 1));
    private static readonly Brush LabelBg = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x1F, 0x1F, 0x22)));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly CaptureOverlayWindow _owner;
    private readonly DrawingVisual _overlay = new();
    private readonly ImageBrush _bright; // the undimmed screen, windowed to the selection
    private (Int32Rect? Sel, Int32Rect? Hover, bool Handles, bool Dragging, Point Mouse, ExportSize Size, int Hd, string? Busy, double Scale, double W, double H) _drawn;
    private bool _hasDrawn;

    public SelectionSurface(CaptureOverlayWindow owner)
    {
        _owner = owner;
        AddVisualChild(_overlay);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_overlay, BitmapScalingMode.NearestNeighbor);
        _bright = new ImageBrush(owner.Shot) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, TileMode = TileMode.None };
        RenderOptions.SetBitmapScalingMode(_bright, BitmapScalingMode.NearestNeighbor);
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _overlay;

    protected override void OnRender(DrawingContext dc)
    {
        // The frozen screen (1 bitmap pixel = 1 device pixel), dimmed - drawn once and never touched again.
        // The selection is a second, small layer on top that shows the same picture undimmed (see Redraw).
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawImage(_owner.Shot, full);
        dc.DrawRectangle(Dim, null, full);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }

    public void Redraw()
    {
        double k = _owner.Scale;
        double W = ActualWidth, H = ActualHeight;
        // Nothing visible changed (e.g. the mouse moved inside the same window) -> nothing to redraw.
        bool dragging = _owner.IsDragging;
        var state = (_owner.Selection, _owner.Hover, _owner.ShowHandles, dragging, dragging ? _owner.MousePx : default,
            _owner.OutputSize, _owner.HdFactor, _owner.BusyText, k, W, H);
        if (_hasDrawn && state.Equals(_drawn)) return;
        _drawn = state;
        _hasDrawn = true;

        using var dc = _overlay.RenderOpen();
        if (W <= 0) return;

        // Only the selected (or hovered) area is drawn here: the screen picture without the dimming. Everything
        // around it is the static layer underneath, so a moving selection redraws just its own rectangle.
        Int32Rect? clearPx = _owner.Selection ?? _owner.Hover;
        if (clearPx is Int32Rect c && c.Width > 0 && c.Height > 0)
        {
            _bright.Viewbox = new Rect(c.X, c.Y, c.Width, c.Height); // picture pixels
            dc.DrawRectangle(_bright, null, new Rect(c.X / k, c.Y / k, c.Width / k, c.Height / k));
        }

        if (_owner.Selection is Int32Rect s && s.Width > 0 && s.Height > 0)
        {
            double half = 0.5 / k;
            var r = new Rect(s.X / k + half, s.Y / k + half, Math.Max(0, s.Width / k - 2 * half), Math.Max(0, s.Height / k - 2 * half));
            dc.DrawRectangle(null, new Pen(Brushes.White, 1 / k), r);

            if (_owner.ShowHandles && _owner.BusyText == null)
            {
                foreach (var hp in CaptureOverlayWindow.HandlePoints(s))
                    dc.DrawEllipse(Brushes.White, HandlePen, new Point(hp.X / k, hp.Y / k), 4.5, 4.5);
            }
            DrawSizeLabel(dc, s, k, W, H);
        }
        else if (_owner.Hover is Int32Rect hv && hv.Width > 0)
        {
            var r = new Rect(hv.X / k + 1, hv.Y / k + 1, Math.Max(0, hv.Width / k - 2), Math.Max(0, hv.Height / k - 2));
            dc.DrawRectangle(HoverFill, HoverPen, r);
        }
    }

    private void DrawSizeLabel(DrawingContext dc, Int32Rect s, double k, double W, double H)
    {
        string text = $"{s.Width} × {s.Height}";
        int hd = _owner.HdFactor;
        if (!string.IsNullOrEmpty(_owner.BusyText)) text = _owner.BusyText!;
        else if (!_owner.IsDragging)
        {
            if (_owner.OutputSize != ExportSize.Original)
            {
                var (tw, th) = ExportSizes.TargetSize(_owner.OutputSize, s.Width * hd, s.Height * hd);
                text += $"  →  {tw} × {th}";
            }
            else if (hd > 1) text += $"  →  {s.Width * hd} × {s.Height * hd}";
            if (hd > 1) text += "  HD";
        }
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 12, Brushes.White, 1.0);
        double pad = 7, w = ft.Width + pad * 2, h = ft.Height + 6;

        double x, y;
        if (_owner.IsDragging)
        {
            // follow the cursor while dragging (like macOS)
            x = _owner.MousePx.X / k + 14;
            y = _owner.MousePx.Y / k + 14;
            if (x + w > W - 4) x = _owner.MousePx.X / k - w - 10;
            if (y + h > H - 4) y = _owner.MousePx.Y / k - h - 10;
        }
        else
        {
            x = s.X / k;
            y = s.Y / k - h - 6;
            if (y < 4) y = s.Y / k + 6;
            if (y < 4) y = 4;
            x = Math.Clamp(x, 4, Math.Max(4, W - w - 4));
            if (y > H - h - 4) y = H - h - 4;
        }
        dc.DrawRoundedRectangle(LabelBg, null, new Rect(x, y, w, h), 6, 6);
        dc.DrawText(ft, new Point(x + pad, y + 3));
    }
}
