using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using LinearFrame.Capture;
using LinearFrame.Core;
using LinearFrame.Imaging;
using LinearFrame.UI;

namespace LinearFrame.Player;

/// <summary>QuickTime-like player window: native mpv video surface + transparent control overlay + iPhone-style trimming.</summary>
public partial class PlayerWindow : Window
{
    private readonly MpvHost _host = new();
    private readonly AppSettings _s;
    private MpvPlayer? _mpv;
    private PlayerOverlay? _overlay;
    private IntPtr _hwnd;
    private string? _path;
    private string? _pendingOpen;
    private bool _editWhenLoaded;
    private bool _isAudio;
    private bool _hasVideo;
    private double _aspect = 16.0 / 9.0;
    private bool _layoutForNewFile;

    private bool _fullscreen;
    private Native.RECT _restoreRect;
    private WindowState _restoreState;

    private bool _editMode;
    private bool _saving;
    private double _trimStart, _trimEnd;
    private CancellationTokenSource? _thumbCts;
    private double _panelGrow;
    private bool _cutMode;
    private double[]? _keyframes;
    private CancellationTokenSource? _kfCts;
    private long _lastSkipTick;

    public IntPtr Handle => _hwnd;
    public bool IsPaused => _mpv?.Paused ?? true;

    public PlayerWindow()
    {
        InitializeComponent();
        _s = App.Instance!.Settings;
        WindowHelper.Attach(this, forceDark: true);
        VideoArea.Children.Add(_host);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
        Loaded += OnLoaded;
        Closed += OnClosed;
        LocationChanged += (_, _) => UpdateOverlayBounds();
        SizeChanged += (_, _) => UpdateOverlayBounds();
        StateChanged += (_, _) => { UpdateOverlayBounds(); UpdateOverlayModes(); };
        Activated += (_, _) => _overlay?.ShowControls();
        VideoArea.SizeChanged += (_, _) => UpdateOverlayBounds();

        Trim.RangeChanged += OnTrimRangeChanged;
        Trim.Scrubbed += (t, final) => _mpv?.CommandAsync("seek", F(t), final ? "absolute+exact" : "absolute+keyframes");
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // =============================================================== lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _overlay = new PlayerOverlay(this) { Owner = this };
        _overlay.ControlsVisibilityChanged += UpdateTimeInterest;
        _overlay.Show();
        UpdateOverlayBounds();
        UpdateOverlayModes();

        if (!MpvNative.TryLoad(out var err))
        {
            ShowMessage(L.T("Player.NoEngine") + (err != null ? $"\n({err})" : ""));
            return;
        }
        try
        {
            _mpv = new MpvPlayer(_host.VideoHandle, Dispatcher, _s.PlayerVolume, _s.PlayerMuted);
            _mpv.StateChanged += OnMpvState;
            _mpv.FileLoaded += OnFileLoaded;
            _mpv.EndFile += OnEndFile;
            _mpv.VideoReconfig += OnVideoReconfig;
            UpdateTimeInterest();
        }
        catch (Exception ex)
        {
            Log.Error("mpv init failed", ex);
            ShowMessage(L.T("Player.NoEngine") + "\n" + ex.Message);
            return;
        }
        if (_pendingOpen != null)
        {
            var p = _pendingOpen;
            _pendingOpen = null;
            Load(p);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _thumbCts?.Cancel();
        if (_mpv != null)
        {
            _s.PlayerVolume = _mpv.Volume;
            _s.PlayerMuted = _mpv.Muted;
            _s.Save();
            var mpv = _mpv;
            _mpv = null;
            mpv.Dispose();
        }
        try { _overlay?.Close(); } catch (InvalidOperationException) { /* already closed together with its owner */ }
        _overlay = null;
    }

    public void OpenFile(string path, bool edit = false, bool cut = false)
    {
        _editWhenLoaded = edit;
        if (cut) _cutMode = true;
        if (!IsLoaded || _mpv == null)
        {
            _pendingOpen = path;
            if (!IsVisible) Show();
            WindowHelper.BringToFront(this);
            return;
        }
        Load(path);
        WindowHelper.BringToFront(this);
    }

    private void Load(string path)
    {
        if (_editMode) ExitEditMode();
        _path = path;
        _isAudio = MediaFormats.IsAudio(path);
        _hasVideo = !_isAudio;
        _layoutForNewFile = true;
        Title = $"{Path.GetFileName(path)} — LinearFrame";
        _overlay?.SetTitle(Path.GetFileName(path));
        MessageView.Visibility = Visibility.Collapsed;
        ApplyAudioMode(_isAudio);
        _mpv?.SetProperty("ab-loop-a", "no");
        _mpv?.SetProperty("ab-loop-b", "no");
        _mpv?.Command("loadfile", path, "replace");
        _mpv?.SetProperty("pause", "no");
        UpdateOverlayModes();
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageView.Visibility = Visibility.Visible;
        _host.Visibility = Visibility.Collapsed;
        AudioView.Visibility = Visibility.Collapsed;
    }

    // =============================================================== mpv events

    private void OnFileLoaded()
    {
        if (_mpv == null) return;
        var tracks = _mpv.Tracks();
        _hasVideo = tracks.Any(t => t.Type == "video" && !t.AlbumArt);
        _isAudio = !_hasVideo;
        ApplyAudioMode(_isAudio);
        if (_isAudio)
        {
            var title = _mpv.GetString("metadata/by-key/title") ?? _mpv.GetString("metadata/by-key/Title");
            var artist = _mpv.GetString("metadata/by-key/artist") ?? _mpv.GetString("metadata/by-key/Artist") ?? _mpv.GetString("metadata/by-key/album_artist");
            AudioTitle.Text = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(_path) : title;
            AudioArtist.Text = artist ?? "";
            _ = LoadCoverArt(_path!);
            if (_layoutForNewFile && !_fullscreen && WindowState == WindowState.Normal) ResizeForContent(460, 480);
            _layoutForNewFile = false;
        }
        UpdateOverlayModes();
        if (_editWhenLoaded)
        {
            _editWhenLoaded = false;
            Dispatcher.BeginInvoke(new Action(EnterEditMode), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private async Task LoadCoverArt(string path)
    {
        ArtImageHost.Visibility = Visibility.Collapsed;
        ArtIcon.Visibility = Visibility.Visible;
        if (!Ffmpeg.Available) return;
        var art = await Ffmpeg.CoverArt(path, CancellationToken.None);
        if (art == null || path != _path) return;
        ArtImageHost.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        ArtImageHost.Visibility = Visibility.Visible;
        ArtIcon.Visibility = Visibility.Collapsed;
    }

    private void OnVideoReconfig()
    {
        if (_mpv == null || !_hasVideo) return;
        long w = _mpv.GetLong("dwidth") ?? 0, h = _mpv.GetLong("dheight") ?? 0;
        Log.Info($"Video {w}x{h} codec={_mpv.GetString("video-codec")} hwdec={_mpv.GetString("hwdec-current")} vo={_mpv.GetString("current-vo")}");
        if (w <= 0 || h <= 0) return;
        _aspect = w / (double)h;
        if (_layoutForNewFile && !_fullscreen && WindowState == WindowState.Normal)
        {
            var work = WindowHelper.WorkAreaAtCursor(out double scale);
            double maxW = work.Width / scale * 0.72, maxH = work.Height / scale * 0.72;
            double vw = w / scale, vh = h / scale;
            double k = Math.Min(1, Math.Min(maxW / vw, maxH / vh));
            vw = Math.Max(MinWidth, vw * k);
            vh = Math.Max(MinHeight, vw / _aspect);
            ResizeForContent(vw, vh);
        }
        _layoutForNewFile = false;
    }

    private void OnEndFile(int reason, int error)
    {
        // reason 4 = error
        if (reason == 4) ShowMessage(L.T("Player.OpenFailed"));
    }

    private long _lastOverlayTick;
    private bool _lastPaused = true;

    /// <summary>Position updates are only needed while controls or the trim bar are on screen.</summary>
    private void UpdateTimeInterest()
    {
        if (_mpv == null) return;
        _mpv.WantTimeUpdates = _editMode || _isAudio || (_overlay?.ControlsVisible ?? true);
        _mpv.TimeIntervalMs = _editMode ? 40 : 120;
    }

    private void OnMpvState()
    {
        if (_mpv == null) return;
        long now = Environment.TickCount64;
        bool pausedChanged = _mpv.Paused != _lastPaused;
        _lastPaused = _mpv.Paused;
        if (_overlay != null && (_overlay.ControlsVisible || _mpv.Paused) && (pausedChanged || _mpv.Paused || now - _lastOverlayTick >= 250))
        {
            _lastOverlayTick = now;
            RefreshOverlay();
        }
        if (_editMode)
        {
            Trim.SetPosition(_mpv.TimePos);
            TrimPlayIcon.Icon = _mpv.Paused ? "play" : "pause";
            if (_mpv.Duration > 0 && Trim.Duration <= 0.02) EnterEditModeCore();
            // preview of a cut: jump over the part that will be removed
            if (_cutMode && !_mpv.Paused && !_mpv.Seeking && _mpv.TimePos >= _trimStart - 0.03 && _mpv.TimePos < _trimEnd - 0.05
                && now - _lastSkipTick > 300)
            {
                _lastSkipTick = now;
                _mpv.CommandAsync("seek", F(_trimEnd), "absolute+exact");
            }
        }
    }

    public void RefreshOverlay()
    {
        if (_mpv == null || _overlay == null) return;
        _overlay.UpdatePlayback(_mpv.TimePos, _mpv.Duration, _mpv.Paused, _mpv.Volume, _mpv.Muted);
    }

    // =============================================================== layout / overlay

    private void ApplyAudioMode(bool audio)
    {
        AudioView.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        _host.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResizeForContent(double widthDip, double heightDip)
    {
        if (_hwnd == IntPtr.Zero) return;
        double scale = WindowHelper.DpiScale(this);
        double extra = _editMode ? TrimPanel.ActualHeight : 0;
        int wpx = (int)Math.Round(widthDip * scale), hpx = (int)Math.Round((heightDip + extra) * scale);
        var mon = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(mon, ref info);
        var wa = info.rcWork;
        int x = wa.Left + (wa.Width - wpx) / 2, y = wa.Top + (wa.Height - hpx) / 2;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, wpx, hpx, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void UpdateOverlayBounds()
    {
        if (_overlay == null || _hwnd == IntPtr.Zero || !VideoArea.IsLoaded) return;
        if (WindowState == WindowState.Minimized) return;
        try
        {
            var tl = VideoArea.PointToScreen(new Point(0, 0));
            var br = VideoArea.PointToScreen(new Point(VideoArea.ActualWidth, VideoArea.ActualHeight));
            _overlay.PlaceOver((int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Round(br.X - tl.X), (int)Math.Round(br.Y - tl.Y));
        }
        catch (InvalidOperationException) { /* not connected to a source yet */ }
    }

    private void UpdateOverlayModes()
    {
        UpdateTimeInterest();
        _overlay?.SetModes(_isAudio, _editMode, _fullscreen, WindowState == WindowState.Maximized,
            canEdit: _path != null && MediaFormats.CanTrim(_path));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_WINDOWPOSCHANGED:
            case Native.WM_MOVING:
                UpdateOverlayBounds();
                break;
            case Native.WM_SIZING:
                if (_hasVideo && !_editMode && !_fullscreen) KeepAspect(wParam.ToInt32(), lParam);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Like QuickTime: resizing keeps the video's aspect ratio, so there are no black bars.</summary>
    private void KeepAspect(int edge, IntPtr lParam)
    {
        var r = Marshal.PtrToStructure<Native.RECT>(lParam);
        int w = r.Width, h = r.Height;
        if (edge is Native.WMSZ_LEFT or Native.WMSZ_RIGHT) h = (int)Math.Round(w / _aspect);
        else if (edge is Native.WMSZ_TOP or Native.WMSZ_BOTTOM) w = (int)Math.Round(h * _aspect);
        else
        {
            if (w / (double)h > _aspect) h = (int)Math.Round(w / _aspect);
            else w = (int)Math.Round(h * _aspect);
        }
        if (edge is Native.WMSZ_LEFT or Native.WMSZ_TOPLEFT or Native.WMSZ_BOTTOMLEFT) r.Left = r.Right - w; else r.Right = r.Left + w;
        if (edge is Native.WMSZ_TOP or Native.WMSZ_TOPLEFT or Native.WMSZ_TOPRIGHT) r.Top = r.Bottom - h; else r.Bottom = r.Top + h;
        Marshal.StructureToPtr(r, lParam, false);
    }

    public void BeginWindowDrag()
    {
        Native.ReleaseCapture();
        Mouse.Capture(null);
        Native.SendMessage(_hwnd, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
    }

    public void ActivatePlayer()
    {
        if (!IsActive) Activate();
        Keyboard.Focus(this);
    }

    // =============================================================== playback controls

    public void TogglePause(bool flash)
    {
        if (_mpv == null) return;
        bool wasPaused = _mpv.Paused;
        if (wasPaused && _mpv.EofReached && !_editMode) _mpv.Command("seek", "0", "absolute");
        if (_editMode && wasPaused)
        {
            double t = _mpv.TimePos;
            if (t < _trimStart - 0.05 || t >= _trimEnd - 0.05) _mpv.Command("seek", F(_trimStart), "absolute+exact");
        }
        _mpv.Command("cycle", "pause");
        if (flash) _overlay?.Flash(playing: wasPaused);
    }

    public void SeekRelative(double sec)
    {
        _mpv?.CommandAsync("seek", F(sec), "relative+exact");
        _overlay?.ShowControls();
    }

    public void SeekFraction(double f, bool exact)
    {
        if (_mpv == null || _mpv.Duration <= 0) return;
        _mpv.CommandAsync("seek", F(f * _mpv.Duration), exact ? "absolute+exact" : "absolute+keyframes");
    }

    public void SetVolume(double v, bool save = false)
    {
        if (_mpv == null) return;
        v = Math.Clamp(v, 0, 100);
        _mpv.SetProperty("volume", F(v));
        if (v > 0 && _mpv.Muted) _mpv.SetProperty("mute", "no");
        if (save) { _s.PlayerVolume = v; _s.Save(); }
    }

    public void ChangeVolume(double delta) => SetVolume((_mpv?.Volume ?? 100) + delta);

    public void ToggleMute() => _mpv?.Command("cycle", "mute");

    public void ToggleFullscreen()
    {
        if (_isAudio || _hwnd == IntPtr.Zero) return;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (!_fullscreen)
        {
            _restoreState = WindowState;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            Native.GetWindowRect(_hwnd, out _restoreRect);
            var mon = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            Native.GetMonitorInfo(mon, ref info);
            var m = info.rcMonitor;
            _fullscreen = true;
            if (chrome != null) chrome.ResizeBorderThickness = new Thickness(0);
            Native.SetWindowPos(_hwnd, Native.HWND_TOP, m.Left, m.Top, m.Width, m.Height, Native.SWP_FRAMECHANGED);
        }
        else
        {
            _fullscreen = false;
            if (chrome != null) chrome.ResizeBorderThickness = new Thickness(6);
            var r = _restoreRect;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, Native.SWP_FRAMECHANGED | Native.SWP_NOZORDER);
            if (_restoreState == WindowState.Maximized) WindowState = WindowState.Maximized;
        }
        UpdateOverlayModes();
        Dispatcher.BeginInvoke(new Action(UpdateOverlayBounds), System.Windows.Threading.DispatcherPriority.Render);
    }

    public void ShowMoreMenu(FrameworkElement? anchor)
    {
        if (_mpv == null) return;
        var m = new NativeMenu();
        bool canEdit = _path != null && MediaFormats.CanTrim(_path) && !_editMode;
        m.Add(L.T("Player.Edit"), EnterEditMode, enabled: canEdit);
        m.Separator();
        m.Sub(L.T("Player.Speed"), sub =>
        {
            foreach (var sp in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            {
                var v = sp;
                sub.Add(v.ToString("0.##", CultureInfo.CurrentCulture) + "×", () => _mpv?.SetProperty("speed", F(v)), isChecked: Math.Abs(_mpv.Speed - v) < 0.01);
            }
        });
        var tracks = _mpv.Tracks();
        var audio = tracks.Where(t => t.Type == "audio").ToList();
        var subs = tracks.Where(t => t.Type == "sub").ToList();
        if (audio.Count > 1)
            m.Sub(L.T("Player.Audio"), sub =>
            {
                foreach (var t in audio) { var id = t.Id; sub.Add(TrackLabel(t), () => _mpv?.SetProperty("aid", id.ToString(CultureInfo.InvariantCulture)), isChecked: t.Selected); }
            });
        if (subs.Count > 0)
            m.Sub(L.T("Player.Subtitles"), sub =>
            {
                sub.Add(L.T("Player.SubsOff"), () => _mpv?.SetProperty("sid", "no"), isChecked: !subs.Any(s => s.Selected));
                foreach (var t in subs) { var id = t.Id; sub.Add(TrackLabel(t), () => _mpv?.SetProperty("sid", id.ToString(CultureInfo.InvariantCulture)), isChecked: t.Selected); }
            });
        m.Separator();
        if (_hasVideo) m.Add(L.T("Player.FrameShot"), SaveFrame);
        m.Add(L.T("Player.OnTop"), () => Topmost = !Topmost, isChecked: Topmost);
        if (_path != null) m.Add(L.T("Player.Reveal"), () => ShellUtil.Reveal(_path));
        m.Add(L.T("Player.OpenOther"), () => App.Instance?.OpenMediaDialog());
        if (anchor != null) m.ShowFor(anchor, forceDark: true, above: true);
        else
        {
            Native.GetCursorPos(out var p);
            m.Show(_hwnd, p.X, p.Y, forceDark: true);
        }
    }

    private static string TrackLabel(MpvTrack t)
    {
        var parts = new List<string> { L.F("Player.Track", t.Id) };
        if (!string.IsNullOrWhiteSpace(t.Lang)) parts.Add(t.Lang!.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(t.Title)) parts.Add(t.Title!);
        else if (!string.IsNullOrWhiteSpace(t.Codec)) parts.Add(t.Codec!);
        return string.Join(" · ", parts);
    }

    private async void SaveFrame()
    {
        if (_mpv == null) return;
        try
        {
            var dir = _s.ScreenshotFolder;
            Directory.CreateDirectory(dir);
            var path = ImageIO.UniquePath(dir, L.F("Capture.FrameName", DateTime.Now), ".png");
            var mpv = _mpv;
            int r = await Task.Run(() => mpv.Command("screenshot-to-file", path, "video"));
            if (r < 0 || !File.Exists(path)) return;
            if (_s.PlaySound) Sound.Shutter();
            var bmp = ImageIO.LoadFull(path);
            if (bmp != null)
            {
                if (_s.CopyToClipboard) ImageIO.CopyToClipboard(bmp);
                App.Instance?.NotifyScreenshotsChanged();
                var mons = MonitorInfo.All();
                Native.GetWindowRect(_hwnd, out var wr);
                var mon = mons.FirstOrDefault(x => x.Contains(wr.Left + wr.Width / 2, wr.Top + wr.Height / 2)) ?? mons[0];
                CaptureToast.Show(bmp, path, mon);
            }
        }
        catch (Exception ex) { Log.Error("Frame screenshot failed", ex); }
    }

    // =============================================================== edit (trim)

    public void EnterEditMode()
    {
        if (_mpv == null || _path == null || _editMode) return;
        if (!MediaFormats.CanTrim(_path)) { Hud.Show(L.T("Player.CannotEdit"), "info", this); return; }
        if (!Ffmpeg.Available) { Hud.Show(L.T("Player.NoFfmpeg"), "info", this); return; }
        _editMode = true;
        _mpv.SetProperty("pause", "yes");
        TrimPanel.Visibility = Visibility.Visible;
        TrimButtons.Visibility = Visibility.Visible;
        TrimProgress.Visibility = Visibility.Collapsed;
        Trim.Reset(0.01);
        UpdateOverlayModes();

        // grow the window downwards so the video keeps its size (when there is room)
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _panelGrow = 0;
            if (!_fullscreen && WindowState == WindowState.Normal)
            {
                double scale = WindowHelper.DpiScale(this);
                Native.GetWindowRect(_hwnd, out var r);
                var mon = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
                var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
                Native.GetMonitorInfo(mon, ref info);
                int grow = (int)Math.Round(TrimPanel.ActualHeight * scale);
                int room = info.rcWork.Bottom - r.Bottom;
                int up = Math.Max(0, grow - room);
                int newTop = Math.Max(info.rcWork.Top, r.Top - up);
                int newH = Math.Min(info.rcWork.Height, r.Height + grow);
                Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, newTop, r.Width, newH, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                _panelGrow = newH - r.Height;
            }
            EnterEditModeCore();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void EnterEditModeCore()
    {
        if (_mpv == null || !_editMode) return;
        double dur = _mpv.Duration;
        if (dur <= 0) return; // will retry from the next state update
        Trim.Reset(dur);
        _trimStart = 0;
        _trimEnd = dur;
        Trim.SetPosition(_mpv.TimePos);
        if (_cutMode) SetCutMode(true);
        UpdateTrimLabels();
        GenerateFilmstrip();
        LoadKeyframes();
    }

    private async void LoadKeyframes()
    {
        _kfCts?.Cancel();
        _keyframes = null;
        if (_isAudio || _path == null) return;
        _kfCts = new CancellationTokenSource();
        var ct = _kfCts.Token;
        try
        {
            var path = _path;
            var kf = await Task.Run(() => Ffmpeg.Keyframes(path, ct), ct);
            if (!ct.IsCancellationRequested && path == _path) _keyframes = kf;
            Log.Info($"Keyframes: {kf?.Length ?? 0}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Keyframe scan failed", ex); }
    }

    /// <summary>First keyframe at or after t (a lossless cut must resume on a keyframe).</summary>
    private double SnapToNextKeyframe(double t)
    {
        if (_keyframes == null || _keyframes.Length == 0) return t;
        int i = Array.BinarySearch(_keyframes, t - 0.001);
        if (i < 0) i = ~i;
        if (i >= _keyframes.Length) return Trim.Duration;
        return _keyframes[i] - t > 10 ? t : _keyframes[i];
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (Trim == null || TrimInfo == null || TrimTimes == null) return; // still loading XAML
        SetCutMode(ModeCut.IsChecked == true);
    }

    private void SetCutMode(bool cut)
    {
        _cutMode = cut;
        Trim.CutMode = cut;
        if (ModeCut.IsChecked != cut) { ModeCut.IsChecked = cut; ModeTrim.IsChecked = !cut; }
        double dur = Trim.Duration;
        if (cut && dur > 1 && _trimStart <= 0.001 && _trimEnd >= dur - 0.001)
        {
            // start with the middle third selected so it's obvious what disappears
            _trimStart = Math.Round(dur / 3, 1);
            _trimEnd = Math.Round(dur * 2 / 3, 1);
            Trim.SetRange(_trimStart, _trimEnd);
        }
        if (_mpv != null)
        {
            _mpv.SetProperty("ab-loop-a", cut ? "no" : F(_trimStart));
            _mpv.SetProperty("ab-loop-b", cut ? "no" : F(_trimEnd));
        }
        TrimInfo.Text = L.T("Player.Lossless");
        TrimInfo.ToolTip = cut ? L.T("Player.CutSnap") : null;
        UpdateTrimLabels();
    }

    private async void GenerateFilmstrip()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;
        var path = _path!;
        double dur = Trim.Duration;
        try
        {
            if (_isAudio)
            {
                var wave = await Ffmpeg.Waveform(path, 140, dur, ct);
                if (!ct.IsCancellationRequested) Trim.SetWaveform(wave);
                return;
            }
            double cellH = Trim.StripHeight, cellW = cellH * _aspect;
            int n = (int)Math.Clamp(Math.Ceiling(Trim.StripWidth / Math.Max(8, cellW)), 3, 24);
            int hpx = (int)Math.Round(cellH * WindowHelper.DpiScale(this) * 1.25);
            Trim.SetThumbnailCount(n);
            using var gate = new SemaphoreSlim(3);
            var tasks = Enumerable.Range(0, n).Select(async i =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    double t = dur * (i + 0.5) / n;
                    var bmp = await Ffmpeg.Frame(path, t, Math.Max(32, hpx), ct);
                    if (bmp != null && !ct.IsCancellationRequested) Dispatcher.Invoke(() => Trim.SetThumbnail(i, bmp));
                }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Filmstrip failed", ex); }
    }

    private void OnTrimRangeChanged(double start, double end, int which, bool final)
    {
        if (_cutMode && final && which == 2)
        {
            // resume on a keyframe so the join is clean and still lossless
            double snapped = SnapToNextKeyframe(end);
            if (Math.Abs(snapped - end) > 0.0005)
            {
                end = snapped;
                Trim.SetRange(start, end);
            }
        }
        _trimStart = start;
        _trimEnd = end;
        UpdateTrimLabels();
        if (_mpv == null) return;
        double t = which == 1 ? start : Math.Max(start, end - 0.04);
        if (_cutMode && which == 2) t = end;
        _mpv.CommandAsync("seek", F(t), final ? "absolute+exact" : "absolute+keyframes");
        if (final && !_cutMode)
        {
            _mpv.SetProperty("ab-loop-a", F(start));
            _mpv.SetProperty("ab-loop-b", F(end));
        }
    }

    private void UpdateTrimLabels()
    {
        if (_cutMode)
            TrimTimes.Text = L.F("Player.CutInfo", TrimBar.FormatTime(_trimStart, true), TrimBar.FormatTime(_trimEnd, true),
                TrimBar.FormatTime(Math.Max(0, Trim.Duration - (_trimEnd - _trimStart)), true));
        else
            TrimTimes.Text = $"{TrimBar.FormatTime(_trimStart, true)}  –  {TrimBar.FormatTime(_trimEnd, true)}     ·     {TrimBar.FormatTime(_trimEnd - _trimStart, true)}";
    }

    private void TrimPlay_Click(object sender, RoutedEventArgs e) => TogglePause(flash: false);

    private void TrimCancel_Click(object sender, RoutedEventArgs e) => ExitEditMode();

    private void ExitEditMode()
    {
        if (!_editMode) return;
        _thumbCts?.Cancel();
        _kfCts?.Cancel();
        _editMode = false;
        TrimPanel.Visibility = Visibility.Collapsed;
        _mpv?.SetProperty("ab-loop-a", "no");
        _mpv?.SetProperty("ab-loop-b", "no");
        if (_panelGrow > 0 && !_fullscreen && WindowState == WindowState.Normal)
        {
            Native.GetWindowRect(_hwnd, out var r);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, (int)(r.Height - _panelGrow), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        _panelGrow = 0;
        UpdateOverlayModes();
        Dispatcher.BeginInvoke(new Action(UpdateOverlayBounds), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void SaveNew_Click(object sender, RoutedEventArgs e) => _ = SaveTrim(replace: false);
    private void Replace_Click(object sender, RoutedEventArgs e) => _ = SaveTrim(replace: true);

    private async Task SaveTrim(bool replace)
    {
        if (_saving || _path == null || _mpv == null) return;
        string input = _path;
        double start = _trimStart, end = _trimEnd;
        bool cut = _cutMode;
        double duration = Trim.Duration;
        if (end - start <= 0.05) return;
        if (cut && end >= duration - 0.05 && start <= 0.05) return; // would remove everything
        _saving = true;
        TrimButtons.Visibility = Visibility.Collapsed;
        TrimProgress.Visibility = Visibility.Visible;
        TrimProgressFill.Width = 0;
        _mpv.SetProperty("pause", "yes");
        var progress = new Progress<double>(p => TrimProgressFill.Width = 150 * p);

        string dir = Path.GetDirectoryName(input)!;
        string name = Path.GetFileNameWithoutExtension(input);
        string ext = Path.GetExtension(input);
        string output = replace
            ? Path.Combine(dir, $"{name}.lf-tmp{ext}")
            : ImageIO.UniquePath(dir, $"{name} ({L.T("Player.TrimmedSuffix")})", ext);

        try
        {
            var (ok, err) = cut
                ? await Ffmpeg.CutOut(input, output, start, SnapToNextKeyframe(end), duration, progress, CancellationToken.None, _keyframes)
                : await Ffmpeg.Trim(input, output, start, end, progress, CancellationToken.None);
            if (!ok)
            {
                MessageBox.Show(this, L.F("Player.TrimFailed", err), "LinearFrame", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!replace)
            {
                ExitEditMode();
                Load(output);
                Hud.Show(L.T("Player.Saved"), "check", this);
                return;
            }

            // Replace: release the file, move the original to the Recycle Bin, put the trimmed file in its place.
            var info = new FileInfo(input);
            DateTime created = info.CreationTime, modified = info.LastWriteTime;
            await StopAndRelease();
            bool recycled = await Task.Run(() => ShellUtil.MoveToRecycleBin(input));
            if (!recycled)
            {
                // could not recycle (locked or no Recycle Bin on this drive) - keep both files, never lose data
                var keep = ImageIO.UniquePath(dir, $"{name} ({L.T("Player.TrimmedSuffix")})", ext);
                File.Move(output, keep);
                ExitEditMode();
                Load(File.Exists(input) ? input : keep);
                Hud.Show(L.T("Player.Saved"), "check", this);
                return;
            }
            File.Move(output, input);
            try { File.SetCreationTime(input, created); File.SetLastWriteTime(input, modified); } catch { /* not important */ }
            ExitEditMode();
            Load(input);
            Hud.Show(L.T("Player.Replaced"), "check", this);
        }
        catch (Exception ex)
        {
            Log.Error("Save trim failed", ex);
            MessageBox.Show(this, L.F("Player.TrimFailed", ex.Message), "LinearFrame", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _saving = false;
            TrimButtons.Visibility = Visibility.Visible;
            TrimProgress.Visibility = Visibility.Collapsed;
            if (replace && File.Exists(output)) { try { File.Delete(output); } catch { /* ignore */ } }
        }
    }

    /// <summary>Stops playback so mpv closes the file handle (Windows won't move an open file).</summary>
    private async Task StopAndRelease()
    {
        if (_mpv == null) return;
        _mpv.Command("stop");
        for (int i = 0; i < 40 && !_mpv.IdleActive; i++) await Task.Delay(25);
        await Task.Delay(60);
    }

    // =============================================================== keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_mpv == null) { base.OnPreviewKeyDown(e); return; }
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        bool handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Space:
            case Key.K:
            case Key.MediaPlayPause:
                TogglePause(flash: true); break;
            case Key.Left: SeekRelative(shift ? -1 : -5); break;
            case Key.Right: SeekRelative(shift ? 1 : 5); break;
            case Key.J: SeekRelative(-10); break;
            case Key.L: SeekRelative(10); break;
            case Key.OemComma: _mpv.Command("frame-back-step"); break;
            case Key.OemPeriod: _mpv.Command("frame-step"); break;
            case Key.Up: ChangeVolume(5); _overlay?.ShowControls(); break;
            case Key.Down: ChangeVolume(-5); _overlay?.ShowControls(); break;
            case Key.M: ToggleMute(); break;
            case Key.F:
            case Key.Enter when !ctrl:
                ToggleFullscreen(); break;
            case Key.Escape:
                if (_fullscreen) ToggleFullscreen();
                else if (_editMode) ExitEditMode();
                else handled = false;
                break;
            case Key.E when !ctrl: if (_editMode) ExitEditMode(); else EnterEditMode(); break;
            case Key.X when _editMode: SetCutMode(!_cutMode); break;
            case Key.I when _editMode:
                _trimStart = Math.Min(_mpv.TimePos, _trimEnd - 0.1);
                Trim.SetRange(_trimStart, _trimEnd);
                OnTrimRangeChanged(_trimStart, _trimEnd, 1, true);
                break;
            case Key.O when _editMode && !ctrl:
                _trimEnd = Math.Max(_mpv.TimePos, _trimStart + 0.1);
                Trim.SetRange(_trimStart, _trimEnd);
                OnTrimRangeChanged(_trimStart, _trimEnd, 2, true);
                break;
            case Key.O when ctrl: App.Instance?.OpenMediaDialog(); break;
            case Key.W when ctrl: Close(); break;
            case Key.Home: _mpv.CommandAsync("seek", "0", "absolute"); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
        else base.OnPreviewKeyDown(e);
    }
}
