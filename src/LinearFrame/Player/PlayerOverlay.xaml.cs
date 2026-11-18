using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LinearFrame.Core;

namespace LinearFrame.Player;

/// <summary>
/// Transparent window floating exactly over the video. It owns every mouse interaction (the native video
/// window underneath never sees input), shows the QuickTime-style controls and hides them while you watch.
/// </summary>
public partial class PlayerOverlay : Window
{
    private readonly PlayerWindow _player;
    private readonly DispatcherTimer _hideTimer;
    private IntPtr _hwnd;
    private bool _visible = true;
    private bool _audioMode;
    private bool _editMode;
    private bool _fullscreen;
    private bool _pressed;
    private Point _pressPos;
    private Point _lastMove;
    private bool _waveDrag;
    private System.Windows.Media.Brush? _topShade;

    public bool ControlsVisible => _visible;
    public IntPtr Handle => _hwnd;
    public event Action? ControlsVisibilityChanged;

    // This window is a transient visual layer over the video; keyboard control lives in the player window.
    // A childless automation peer keeps accessibility clients from walking (and re-walking) its tree.
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);

    private sealed class QuietPeer : System.Windows.Automation.Peers.WindowAutomationPeer
    {
        public QuietPeer(Window w) : base(w) { }
        protected override List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null;
    }

    public PlayerOverlay(PlayerWindow player)
    {
        InitializeComponent();
        _player = player;
        _topShade = TopBar.Background;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(2600);
            MaybeHide();
        };

        Scrubber.ValueChanging += v => _player.SeekFraction(v, exact: false);
        Scrubber.ValueCommitted += v => _player.SeekFraction(v, exact: true);
        VolumeSlider.ValueChanging += v => _player.SetVolume(v * 100);
        VolumeSlider.ValueCommitted += v => _player.SetVolume(v * 100, save: true);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };

        // a click anywhere (also on a caption the editor's layer takes) gives the player the keyboard
        PreviewMouseLeftButtonDown += (_, _) => _player.ActivatePlayer();
        MouseMove += OnMouseMoveAny;
        MouseLeave += (_, _) =>
        {
            if (!_waveDrag) _player.WaveformHover(null);
            if (!_pressed) ScheduleHide(700);
        };
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, e) =>
        {
            if (!_waveDrag) return;
            _waveDrag = false; // e.g. another window popped up mid-drag: finish where we are
            _player.WaveformSeek(_player.WaveformFraction(e.GetPosition(this)), final: true);
        };
        MouseRightButtonUp += (_, e) => { e.Handled = true; _player.ShowMoreMenu(null); };
        MouseWheel += (_, e) => { _player.ChangeVolume(e.Delta > 0 ? 5 : -5); ShowControls(); };
        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { DropHint.Visibility = Visibility.Visible; e.Effects = DragDropEffects.Copy; } };
        DragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
        Drop += (_, e) =>
        {
            DropHint.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (MediaFormats.IsImage(files[0])) App.Instance?.OpenImage(files[0]); // pictures have their own viewer
                else _player.OpenFile(files[0]);
            }
        };
    }

    // ------------------------------------------------------------------ native

    private const int ResizeBorder = 6;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            case Native.WM_NCHITTEST:
            {
                if (_fullscreen || _player.WindowState == WindowState.Maximized) break;
                int x = Native.LoWord(lParam), y = Native.HiWord(lParam);
                Native.GetWindowRect(hwnd, out var r);
                double k = Native.GetDpiForWindow(hwnd) / 96.0;
                int b = (int)Math.Ceiling(ResizeBorder * k);
                bool l = x < r.Left + b, rt = x >= r.Right - b, t = y < r.Top + b, bt = y >= r.Bottom - b;
                if (_editMode) bt = false; // the editor panel lies under the video: the window's bottom edge is down there
                int code = (t, bt, l, rt) switch
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
                if (code != 0) { handled = true; return new IntPtr(code); }
                break;
            }
            case Native.WM_NCLBUTTONDOWN:
            {
                int ht = wParam.ToInt32();
                if (ht is >= Native.HTLEFT and <= Native.HTBOTTOMRIGHT)
                {
                    // resize the player window, not this overlay
                    handled = true;
                    Native.ReleaseCapture();
                    Native.SendMessage(_player.Handle, Native.WM_NCLBUTTONDOWN, wParam, lParam);
                    return IntPtr.Zero;
                }
                break;
            }
            case Native.WM_NCLBUTTONDBLCLK:
                handled = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    public void PlaceOver(int x, int y, int w, int h)
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, Math.Max(1, w), Math.Max(1, h), Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER);
    }

    // ------------------------------------------------------------------ state

    public void SetTitle(string title) => TitleText.Text = title;

    public void SetModes(bool audio, bool edit, bool fullscreen, bool maximized, bool canEdit)
    {
        _audioMode = audio;
        _editMode = edit;
        _fullscreen = fullscreen;
        WindowButtons.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Visibility = audio || edit ? Visibility.Collapsed : Visibility.Visible; // a song shows its title under the cover
        EditBtn.Visibility = edit || !canEdit ? Visibility.Collapsed : Visibility.Visible;
        Controls.Visibility = edit ? Visibility.Collapsed : Visibility.Visible;
        // while editing, the picture belongs to the captions: no shade over its top, only the window buttons stay
        EditLayer.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        TopBar.Background = edit ? null : _topShade;
        FullIcon.Icon = fullscreen ? "unfullscreen" : "fullscreen";
        MaxIcon.Icon = maximized ? "restore" : "maximize";
        FullBtn.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
        ShowControls();
    }

    public void UpdatePlayback(double pos, double dur, bool paused, double volume, bool muted)
    {
        PlayIcon.Icon = paused ? "play" : "pause";
        if (!Scrubber.IsDragging) Scrubber.Value = dur > 0 ? pos / dur : 0;
        ElapsedText.Text = TrimBar.FormatTime(pos);
        RemainingText.Text = "-" + TrimBar.FormatTime(Math.Max(0, dur - pos));
        if (!VolumeSlider.IsDragging) VolumeSlider.Value = muted ? 0 : volume / 100.0;
        VolIcon.Icon = muted || volume <= 0.5 ? "mute" : "volume";
        Controls.Width = Math.Clamp(ActualWidth - 32, 300, 600);
        if (paused) ShowControls(autoHide: false);
    }

    public void Flash(bool playing)
    {
        FlashIcon.Icon = playing ? "play" : "pause";
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620))));
        FlashBadge.BeginAnimation(OpacityProperty, a);
    }

    // ------------------------------------------------------------------ show / hide

    public void ShowControls(bool autoHide = true)
    {
        if (!_visible)
        {
            _visible = true;
            Animate(TopBar, 1);
            Animate(Controls, 1);
            Cursor = null;
            ControlsVisibilityChanged?.Invoke();
            _player.RefreshOverlay();
        }
        _hideTimer.Stop();
        if (autoHide) _hideTimer.Start();
    }

    private void ScheduleHide(int ms)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _hideTimer.Start();
    }

    private void MaybeHide()
    {
        if (_audioMode || _editMode || _player.IsPaused || Scrubber.IsDragging || VolumeSlider.IsDragging) return;
        if (Controls.IsMouseOver || TopBar.IsMouseOver && !_fullscreen) { _hideTimer.Start(); return; }
        _visible = false;
        Animate(TopBar, 0);
        Animate(Controls, 0);
        ControlsVisibilityChanged?.Invoke();
        if (_fullscreen && IsMouseOver) Cursor = Cursors.None;
    }

    private static void Animate(UIElement e, double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 140 : 320)) { EasingFunction = new QuadraticEase() };
        e.BeginAnimation(OpacityProperty, a);
        e.IsHitTestVisible = to > 0;
    }

    // ------------------------------------------------------------------ mouse

    private void OnMouseMoveAny(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if ((p - _lastMove).Length > 2)
        {
            _lastMove = p;
            ShowControls();
        }
        if (_waveDrag)
        {
            double f = _player.WaveformFraction(p);
            _player.WaveformHover(f);
            _player.WaveformSeek(f, final: false);
            return;
        }
        if (_audioMode)
        {
            bool onWave = !IsOverControl(e.OriginalSource as DependencyObject) && _player.WaveformHit(p, out _);
            _player.WaveformHover(onWave ? _player.WaveformFraction(p) : null);
            Cursor = onWave ? Cursors.Hand : null;
        }
        if (_pressed && e.LeftButton == MouseButtonState.Pressed && (p - _pressPos).Length > 5 && !_fullscreen && _player.WindowState != WindowState.Maximized)
        {
            _pressed = false;
            _player.BeginWindowDrag();
        }
    }

    private bool IsOnControl(MouseButtonEventArgs e) => IsOverControl(e.OriginalSource as DependencyObject);

    private bool IsOverControl(DependencyObject? d)
    {
        while (d != null && d != this)
        {
            if (d == Controls || d == WindowButtons) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _player.ActivatePlayer();
        if (IsOnControl(e)) return;
        if (_audioMode && _player.WaveformHit(e.GetPosition(this), out double f))
        {
            // the song's waveform: click or drag to jump there
            _pressed = false;
            _waveDrag = true;
            CaptureMouse();
            _player.WaveformHover(f);
            _player.WaveformSeek(f, final: false);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            _pressed = false;
            if (!_audioMode) _player.ToggleFullscreen();
            return;
        }
        _pressed = true;
        _pressPos = e.GetPosition(this);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_waveDrag)
        {
            _waveDrag = false;
            ReleaseMouseCapture();
            var p = e.GetPosition(this);
            _player.WaveformSeek(_player.WaveformFraction(p), final: true);
            _player.WaveformHover(_player.WaveformHit(p, out double f) ? f : null);
            e.Handled = true;
            return;
        }
        if (!_pressed) return;
        _pressed = false;
        if (IsOnControl(e)) return;
        // single click toggles playback (double click is fullscreen and flips it back - like the web players)
        _player.TogglePause(flash: true);
    }

    // ------------------------------------------------------------------ buttons

    private void Play_Click(object sender, RoutedEventArgs e) => _player.TogglePause(flash: false);
    private void Back_Click(object sender, RoutedEventArgs e) => _player.SeekRelative(-10);
    private void Forward_Click(object sender, RoutedEventArgs e) => _player.SeekRelative(10);
    private void Mute_Click(object sender, RoutedEventArgs e) => _player.ToggleMute();
    private void Full_Click(object sender, RoutedEventArgs e) => _player.ToggleFullscreen();
    private void More_Click(object sender, RoutedEventArgs e) => _player.ShowMoreMenu(MoreBtn);
    private void Edit_Click(object sender, RoutedEventArgs e) => _player.EnterEditMode();
    private void Min_Click(object sender, RoutedEventArgs e) => _player.WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        _player.WindowState = _player.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => _player.Close();
}
