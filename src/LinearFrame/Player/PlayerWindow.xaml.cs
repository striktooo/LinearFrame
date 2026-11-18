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
using LinearFrame.Player.Edit;
using LinearFrame.UI;

namespace LinearFrame.Player;

/// <summary>QuickTime-like player window: native mpv video surface + transparent control overlay + the video editor under it.</summary>
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
    private double _panelGrow;
    private CancellationTokenSource? _waveCts;
    private bool _waveDragging;

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
        Closing += OnClosing;
        Closed += OnClosed;
        LocationChanged += (_, _) => UpdateOverlayBounds();
        SizeChanged += (_, _) => UpdateOverlayBounds();
        StateChanged += (_, _) => { UpdateOverlayBounds(); UpdateOverlayModes(); if (_editMode) Editor.OnMoved(); };
        Activated += (_, _) => _overlay?.ShowControls();
        VideoArea.SizeChanged += (_, _) =>
        {
            UpdateOverlayBounds();
            if (_editMode) Editor.OnLayoutChanged();
        };
        AudioView.SizeChanged += (_, _) => UpdateAudioLayout();
        Editor.Finished += OnEditorFinished;
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

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // unsaved edits: ask first, like when leaving the editor
        if (_editMode && !Editor.ConfirmLeave()) e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_editMode) Editor.End(reloadOriginal: false);
        _waveCts?.Cancel();
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

    /// <summary>Opens a file; <paramref name="edit"/> goes straight into the editor (the old "cut" entry lands there too).</summary>
    public void OpenFile(string path, bool edit = false, bool cut = false)
    {
        // another file while editing: unsaved changes are not thrown away without asking
        if (_editMode && !Editor.ConfirmLeave()) { WindowHelper.BringToFront(this); return; }
        _editWhenLoaded = edit || cut;
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
        if (_editMode) ExitEditMode(reloadOriginal: false);
        _path = path;
        _isAudio = MediaFormats.IsAudio(path);
        _hasVideo = !_isAudio;
        _layoutForNewFile = true;
        Title = $"{Path.GetFileName(path)} — LinearFrame";
        _overlay?.SetTitle(Path.GetFileName(path));
        MessageView.Visibility = Visibility.Collapsed;
        ApplyAudioMode(_isAudio);
        _waveCts?.Cancel();
        _waveDragging = false;
        Wave.SetPeaks(null);
        Wave.Hover = null;
        Wave.Position = 0;
        Wave.Duration = 0;
        _mpv?.SetProperty("ab-loop-a", "no");
        _mpv?.SetProperty("ab-loop-b", "no");
        // an animated GIF / WebP plays in a loop, like everywhere else
        _mpv?.SetProperty("loop-file", MediaFormats.IsAnimatedImage(path) ? "inf" : "no");
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
        if (_editMode)
        {
            // the editor plays its own timeline (the kept pieces as one EDL) - nothing about the file changes
            Editor.OnFileLoaded();
            return;
        }
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
            AudioArtist.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;
            _ = LoadCoverArt(_path!);
            double dur = _mpv.GetDouble("duration") ?? _mpv.Duration;
            Wave.Duration = dur;
            LoadWaveform(_path!, dur);
            if (_layoutForNewFile && !_fullscreen && WindowState == WindowState.Normal) ResizeForContent(680, 500);
            _layoutForNewFile = false;
            UpdateAudioLayout();
        }
        UpdateOverlayModes();
        if (_editWhenLoaded)
        {
            _editWhenLoaded = false;
            Dispatcher.BeginInvoke(new Action(EnterEditMode), System.Windows.Threading.DispatcherPriority.Background);
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

    // =============================================================== audio: waveform + layout

    /// <summary>Reads the whole song's loudness envelope with FFmpeg (well under a second for a normal song).</summary>
    private async void LoadWaveform(string path, double duration)
    {
        _waveCts?.Cancel();
        if (!Ffmpeg.Available) return;
        var cts = _waveCts = new CancellationTokenSource();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var peaks = await Ffmpeg.Waveform(path, 1200, duration, cts.Token);
            if (cts.IsCancellationRequested || path != _path) return;
            Wave.SetPeaks(peaks);
            Log.Info($"Waveform {(peaks == null ? "failed" : "ready")} in {sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Waveform failed", ex); }
    }

    /// <summary>
    /// Cover, title and waveform share the room between the window buttons and the control pill: a small window shows
    /// a small cover (or none) and a slim waveform, a big one a big cover and a tall, finely detailed waveform.
    /// </summary>
    internal void UpdateAudioLayout(bool force = false)
    {
        if (!_isAudio && !force) return;
        double w = AudioView.ActualWidth, h = AudioView.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double room = Math.Max(0, h - 42 - 112);
        double texts = AudioArtist.Visibility == Visibility.Visible ? 48 : 28;
        double wave = Math.Clamp(room * 0.34, 48, 230);
        double art = Math.Min(240, room - wave - texts - 18 - 22 - 8);
        if (art < 72) art = 0;                                         // no room for a cover
        bool showTexts = room >= wave + texts + 22;                    // tiny window: only the waveform
        if (!showTexts) wave = Math.Clamp(room - 8, 24, 230);

        ArtBorder.Visibility = art > 0 ? Visibility.Visible : Visibility.Collapsed;
        ArtBorder.Width = ArtBorder.Height = Math.Max(1, art);
        var radius = new CornerRadius(Math.Round(Math.Max(8, art * 0.08)));
        ArtBorder.CornerRadius = radius;
        ArtImageHost.CornerRadius = radius;
        ArtIcon.Width = ArtIcon.Height = Math.Max(24, art * 0.4);
        AudioTexts.Visibility = showTexts ? Visibility.Visible : Visibility.Collapsed;
        AudioTexts.Margin = new Thickness(0, art > 0 ? 18 : 0, 0, 0);
        AudioTitle.FontSize = art >= 170 ? 22 : 19;
        AudioTitle.MaxWidth = AudioArtist.MaxWidth = Math.Max(120, w - 64);
        Wave.Height = wave;
        Wave.Margin = new Thickness(0, showTexts ? 22 : 0, 0, 0);
    }

    // ---- called by the overlay, which owns the mouse (the waveform lies underneath it)

    /// <summary>The point (overlay = video area coordinates) is on the waveform: where in the song (0..1).</summary>
    public bool WaveformHit(Point p, out double fraction)
    {
        fraction = 0;
        if (!_isAudio || _editMode || AudioView.Visibility != Visibility.Visible || Wave.ActualWidth <= 0) return false;
        var q = VideoArea.TranslatePoint(p, Wave);
        if (q.X < -4 || q.X > Wave.ActualWidth + 4 || q.Y < -10 || q.Y > Wave.ActualHeight + 10) return false;
        fraction = Math.Clamp(q.X / Wave.ActualWidth, 0, 1);
        return true;
    }

    /// <summary>Where in the song a point is, also outside the waveform (while dragging).</summary>
    public double WaveformFraction(Point p) => Wave.ActualWidth <= 0 ? 0 : Math.Clamp(VideoArea.TranslatePoint(p, Wave).X / Wave.ActualWidth, 0, 1);

    public void WaveformSeek(double fraction, bool final)
    {
        if (_mpv == null) return;
        _waveDragging = !final;
        double dur = _mpv.Duration > 0 ? _mpv.Duration : Wave.Duration;
        Wave.Position = fraction * dur;
        if (final) Wave.Hover = null;
        SeekFraction(fraction, exact: final);
        _overlay?.ShowControls();
    }

    public void WaveformHover(double? fraction) => Wave.Hover = fraction;

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
        if (_isAudio && !_waveDragging)
        {
            Wave.Duration = _mpv.Duration;
            Wave.Position = _mpv.TimePos;
        }
        if (_editMode) Editor.OnState();
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
        double extra = _editMode ? Editor.ActualHeight : 0;
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
                if (_editMode) Editor.OnMoved(); // the banner preview is a window of its own
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
        if (_editMode)
        {
            Editor.TogglePlay();
            if (flash) _overlay?.Flash(playing: Editor.IsPlaying);
            return;
        }
        bool wasPaused = _mpv.Paused;
        if (wasPaused && _mpv.EofReached) _mpv.Command("seek", "0", "absolute");
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
        bool any = false;
        void Group()
        {
            if (any) m.Separator();
            any = true;
        }

        if (_path != null && MediaFormats.CanTrim(_path))
        {
            Group();
            m.Add(L.T("Player.Edit") + "\tE", EnterEditMode, enabled: !_editMode);
        }

        var tracks = _mpv.Tracks();
        var audio = tracks.Where(t => t.Type == "audio").ToList();
        var subs = tracks.Where(t => t.Type == "sub").ToList();
        if (audio.Count > 1 || subs.Count > 0) Group();
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

        if (_hasVideo)
        {
            Group();
            m.Add(L.T("Player.CopyFrame") + "\tCtrl+C", () => _ = GrabFrame(save: false));
            m.Add(L.T("Player.FrameShot"), () => _ = GrabFrame(save: true));
        }
        if (!any) return;
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

    /// <summary>
    /// The frame on screen, at the video's own resolution: copied to the clipboard, or saved into the screenshot
    /// folder like a capture (then it also lands in the clipboard when that is switched on).
    /// </summary>
    private async Task GrabFrame(bool save)
    {
        if (_mpv == null || !_hasVideo) return;
        string? temp = null;
        try
        {
            string path;
            if (save)
            {
                var dir = _s.ScreenshotFolder;
                Directory.CreateDirectory(dir);
                path = ImageIO.UniquePath(dir, L.F("Capture.FrameName", DateTime.Now), ".png");
            }
            else
            {
                var dir = Path.Combine(Path.GetTempPath(), "LinearFrame");
                Directory.CreateDirectory(dir);
                path = temp = Path.Combine(dir, $"frame-{Environment.ProcessId}-{Environment.TickCount64}.png");
            }
            var mpv = _mpv;
            int r = await Task.Run(() => mpv.Command("screenshot-to-file", path, "video"));
            if (r < 0 || !File.Exists(path)) { Log.Warn($"Frame grab failed ({r})"); return; }
            if (_s.PlaySound) Sound.Shutter();
            var bmp = await Task.Run(() =>
            {
                var b = ImageIO.LoadFull(path);
                b?.Freeze();
                return b;
            });
            if (bmp == null) return;
            if (!save)
            {
                var png = await File.ReadAllBytesAsync(path); // mpv wrote a PNG already - no need to encode it again
                if (ImageIO.CopyToClipboard(bmp, png)) Hud.Show(L.T("Capture.Copied"), "copy", this);
                return;
            }
            if (_s.CopyToClipboard) ImageIO.CopyToClipboard(bmp);
            App.Instance?.NotifyScreenshotsChanged();
            var mons = MonitorInfo.All();
            Native.GetWindowRect(_hwnd, out var wr);
            var mon = mons.FirstOrDefault(x => x.Contains(wr.Left + wr.Width / 2, wr.Top + wr.Height / 2)) ?? mons[0];
            CaptureToast.Show(bmp, path, mon);
        }
        catch (Exception ex) { Log.Error("Frame grab failed", ex); }
        finally
        {
            if (temp != null) { try { File.Delete(temp); } catch { /* temp folder */ } }
            Memory.TrimSoon(4000);
        }
    }

    // =============================================================== edit

    public void EnterEditMode()
    {
        if (_mpv == null || _path == null || _editMode || _overlay == null) return;
        if (!MediaFormats.CanTrim(_path)) { Hud.Show(L.T("Player.CannotEdit"), "info", this); return; }
        if (!Ffmpeg.Available) { Hud.Show(L.T("Player.NoFfmpeg"), "info", this); return; }
        if ((_mpv.GetDouble("duration") ?? _mpv.Duration) <= 0) { _editWhenLoaded = true; return; } // still opening
        var clip = ClipProbe.FromPlayer(_mpv, _path);
        if (_fullscreen) ToggleFullscreen();
        _editMode = true;
        _mpv.SetProperty("pause", "yes");
        _mpv.SetProperty("loop-file", "no");
        Editor.Visibility = Visibility.Visible;
        UpdateOverlayModes();

        // grow the window downwards so the video keeps its size (when there is room)
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_editMode || _mpv == null || _overlay == null) return;
            _panelGrow = 0;
            if (WindowState == WindowState.Normal)
            {
                double scale = WindowHelper.DpiScale(this);
                Native.GetWindowRect(_hwnd, out var r);
                var mon = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
                var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
                Native.GetMonitorInfo(mon, ref info);
                int grow = (int)Math.Round(Editor.ActualHeight * scale);
                int room = info.rcWork.Bottom - r.Bottom;
                int up = Math.Max(0, grow - room);
                int newTop = Math.Max(info.rcWork.Top, r.Top - up);
                int newH = Math.Min(info.rcWork.Height, r.Height + grow);
                // the editor needs some width for its tools
                int minW = (int)Math.Round(880 * scale), newW = Math.Min(info.rcWork.Width, Math.Max(r.Width, minW));
                int newLeft = Math.Clamp(r.Left - (newW - r.Width) / 2, info.rcWork.Left, Math.Max(info.rcWork.Left, info.rcWork.Right - newW));
                Native.SetWindowPos(_hwnd, IntPtr.Zero, newLeft, newTop, newW, newH, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                _panelGrow = newH - r.Height;
            }
            Editor.Begin(this, _mpv, _overlay, clip);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ExitEditMode(bool reloadOriginal)
    {
        if (!_editMode) return;
        Editor.End(reloadOriginal);
        _editMode = false;
        Editor.Visibility = Visibility.Collapsed;
        if (_path != null) _mpv?.SetProperty("loop-file", MediaFormats.IsAnimatedImage(_path) ? "inf" : "no");
        if (_panelGrow > 0 && !_fullscreen && WindowState == WindowState.Normal)
        {
            Native.GetWindowRect(_hwnd, out var r);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, (int)(r.Height - _panelGrow), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        _panelGrow = 0;
        UpdateOverlayModes();
        Dispatcher.BeginInvoke(new Action(UpdateOverlayBounds), System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>The editor saved (a file to open: the new one, or the replaced original) or was left.</summary>
    private void OnEditorFinished(string? saved, bool replaced)
    {
        ExitEditMode(reloadOriginal: saved == null);
        if (saved == null) return;
        Load(saved);
        Hud.Show(L.T(replaced ? "Player.Replaced" : "Player.Saved"), "check", this);
    }

    // ---- the editor's preview frame (another format than the video's own)

    internal Size VideoAreaSize => new(VideoArea.ActualWidth, VideoArea.ActualHeight);

    /// <summary>
    /// The finished video's frame inside the player: <paramref name="canvas"/> is the frame (black or blurred around the
    /// picture), <paramref name="visible"/> the part of it the video covers and <paramref name="video"/> where the whole
    /// picture is (it may stick out of the frame when zoomed in). mpv renders only into the visible part and
    /// video-zoom/pan put the picture where it is - all on the GPU, the video plays as smoothly as without a format.
    /// </summary>
    internal void SetPreviewFrame(Rect canvas, Rect visible, Rect video, double videoAspect, BitmapSource? blur)
    {
        Backdrop.Visibility = Visibility.Visible;
        Canvas.SetLeft(BackdropFrame, canvas.X);
        Canvas.SetTop(BackdropFrame, canvas.Y);
        BackdropFrame.Width = Math.Max(1, canvas.Width);
        BackdropFrame.Height = Math.Max(1, canvas.Height);
        if (!ReferenceEquals(BackdropImage.Source, blur)) BackdropImage.Source = blur;
        BackdropImage.Visibility = BackdropDim.Visibility = blur != null ? Visibility.Visible : Visibility.Collapsed;
        double radius = Math.Clamp(Math.Max(canvas.Width, canvas.Height) * 0.07, 6, 90);
        BackdropBlur.Radius = radius;
        BackdropImage.Margin = new Thickness(-radius);
        if (_isAudio) return;
        if (visible.IsEmpty || visible.Width < 2 || visible.Height < 2 || video.Width < 1 || video.Height < 1)
        {
            _host.Visibility = Visibility.Hidden; // the video is out of the frame
            return;
        }
        _host.Visibility = Visibility.Visible;
        _host.HorizontalAlignment = HorizontalAlignment.Left;
        _host.VerticalAlignment = VerticalAlignment.Top;
        _host.Margin = new Thickness(visible.X, visible.Y, 0, 0);
        _host.Width = visible.Width;
        _host.Height = visible.Height;
        if (_mpv == null) return;
        // mpv fits the picture into its window first (keepaspect), then scales it by 2^zoom and moves it by pan × size
        double fitW = visible.Width / visible.Height > videoAspect ? visible.Height * videoAspect : visible.Width;
        double zoom = Math.Log2(video.Width / Math.Max(1, fitW));
        double panX = (video.X - visible.X - (visible.Width - video.Width) / 2) / video.Width;
        double panY = (video.Y - visible.Y - (visible.Height - video.Height) / 2) / video.Height;
        static string P(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        _mpv.SetProperty("video-zoom", P(zoom));
        _mpv.SetProperty("video-pan-x", P(panX));
        _mpv.SetProperty("video-pan-y", P(panY));
    }

    /// <summary>A newer picture for the blurred background while playing (only when one is shown).</summary>
    internal void SetBackdropImage(BitmapSource? blur)
    {
        if (Backdrop.Visibility != Visibility.Visible || blur == null || BackdropImage.Source == null || ReferenceEquals(BackdropImage.Source, blur)) return;
        BackdropImage.Source = blur;
    }

    internal void ResetPreviewFrame()
    {
        Backdrop.Visibility = Visibility.Collapsed;
        BackdropImage.Source = null;
        _host.HorizontalAlignment = HorizontalAlignment.Stretch;
        _host.VerticalAlignment = VerticalAlignment.Stretch;
        _host.Margin = new Thickness(0);
        _host.Width = double.NaN;
        _host.Height = double.NaN;
        if (!_isAudio && MessageView.Visibility != Visibility.Visible) _host.Visibility = Visibility.Visible;
        if (_mpv == null) return;
        _mpv.SetProperty("video-zoom", "0");
        _mpv.SetProperty("video-pan-x", "0");
        _mpv.SetProperty("video-pan-y", "0");
    }

    /// <summary>Stops playback so mpv closes the file handle (Windows won't move an open file).</summary>
    internal async Task StopAndRelease()
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
        if (_editMode)
        {
            // typing a caption: every key belongs to the text box
            if (Editor.IsTyping) { base.OnPreviewKeyDown(e); return; }
            if (Editor.HandleKey(e)) { e.Handled = true; return; }
            switch (key)
            {
                case Key.Up: ChangeVolume(5); break;
                case Key.Down: ChangeVolume(-5); break;
                case Key.M: ToggleMute(); break;
                case Key.C when ctrl: if (_hasVideo) _ = GrabFrame(save: false); break;
                case Key.W when ctrl: Close(); break;
                default: handled = false; break;
            }
            if (handled) e.Handled = true;
            else base.OnPreviewKeyDown(e);
            return;
        }
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
                else handled = false;
                break;
            case Key.E when !ctrl: EnterEditMode(); break;
            case Key.O when ctrl: App.Instance?.OpenMediaDialog(); break;
            case Key.C when ctrl: if (_hasVideo) _ = GrabFrame(save: false); break;
            case Key.W when ctrl: Close(); break;
            case Key.Home: _mpv.CommandAsync("seek", "0", "absolute"); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
        else base.OnPreviewKeyDown(e);
    }
}
