using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinearFrame.Core;
using LinearFrame.Imaging;
using LinearFrame.UI;

namespace LinearFrame.Player.Edit;

/// <summary>
/// The video editor under the player (TikTok/CapCut-like): cut into pieces and remove any of them, join clips, captions,
/// Apple emoji, a brand banner that freezes the video, the output format - with a live preview in the player itself.
/// mpv plays the kept pieces as one EDL timeline; a banner freeze is the player pausing while the banner animation
/// keeps running in its own layered window, exactly like it will be in the finished file.
/// </summary>
public partial class VideoEditor : UserControl
{
    private PlayerWindow? _win;
    private MpvPlayer? _mpv;
    private PlayerOverlay? _ov;
    private EditProject _p = null!;
    private readonly Stack<EditProject> _undo = new(), _redo = new();
    private EditOverlay? _sel;
    private int _selSeg = -1;
    private bool _format;
    private double _f;
    private bool _playing;
    private int _hold = -1;
    private double _holdInto, _afterHold = -1;
    private long _holdStarted;
    private bool _hooked;
    private double[]? _keyframes;
    private bool _reordered = true;
    private CancellationTokenSource _cts = new();
    private string _loaded = "";
    private LayeredSprite? _sprite;
    private BannerStream? _stream;
    private Guid _streamFor;
    private int _streamWant, _bannerFramePending;
    private readonly System.Windows.Threading.DispatcherTimer _streamSettle = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Dictionary<string, string> _bannerWav = new();
    private MediaPlayer? _audio;
    private Guid _audioFor;
    private bool _audioPlaying;
    private bool _saving, _dirty, _dragCommitted, _textCommitted, _suppress, _snapped;
    private string _encoder = "";
    private long _lastText;
    private Rect _canvas;

    private int _frameClip = -1;
    private long _lastBlurCheck, _lastFrameTick;

    internal bool IsActive => _win != null;
    internal bool IsPlaying => _playing;
    /// <summary>A caption is being typed: every key belongs to the text box.</summary>
    internal bool IsTyping => Caption.IsTyping;
    /// <summary>Saved (new or replaced file to open) or left; the player takes over again.</summary>
    internal event Action<string?, bool>? Finished;

    public VideoEditor()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnPanelPress;
        Timeline.Seek += (f, final) => Seek(f, final);
        _scrub = new ScrubSeeker((t, exact) => _mpv?.CommandAsync("seek", N(t), exact ? "absolute+exact" : "absolute+keyframes"));
        _streamSettle.Tick += (_, _) =>
        {
            _streamSettle.Stop();
            UpdateBanner(); // the size stopped changing: decode at the new one
        };
        Timeline.SegmentSelected += i => { _selSeg = i; Select(null, keepSegment: true); };
        Timeline.OverlaySelected += o => Select(o);
        Timeline.OverlayMoved += OnBarMoved;
        Timeline.OverlayReordered += OnBarReordered;
        Timeline.ReorderEnded += () =>
        {
            _dragCommitted = false;
            Refresh(playback: false);
        };
        Timeline.SegmentTrimmed += OnTrimmed;
        Caption.TextEdited += OnCaptionText;
        Caption.StyleChosen += OnCaptionStyle;
        Caption.ColorChosen += OnCaptionColor;
        Caption.FontChosen += OnCaptionFont;
        Caption.DuplicateRequested += DuplicateSelection;
        Caption.LayerTools = true;
        _graphTimer.Tick += (_, _) => GraphTick();
        _stillSettle.Tick += (_, _) =>
        {
            _stillSettle.Stop();
            FetchStill();
        };
        Caption.SizeChosen += (size, final) => { if (_sel is TextOverlay) SetSize(size, final); };
        Caption.DeleteRequested += () => { if (_sel != null) DeleteOverlay(_sel); };
        Caption.Finished += () =>
        {
            Done_Click(this, new RoutedEventArgs());
            if (_win != null) Keyboard.Focus(_win);
        };
        Caption.Redraw += () => { UpdateLayer(force: true); Timeline.Rebuild(); };
        // the item row's slider is the size of a picture / emoji / banner - and the zoom of a video layer
        ItemSize.ValueChanging += v => { if (_sel is VideoLayer vl) SetZoom(vl, ZoomFrom(v), false); else SetSize(v, false); };
        ItemSize.ValueCommitted += v => { if (_sel is VideoLayer vl) SetZoom(vl, ZoomFrom(v), true); else SetSize(v, true); };
        ZoomSlider.ValueChanging += v => SetZoom(_p.Canvas, ZoomFrom(v), false);
        ZoomSlider.ValueCommitted += v => SetZoom(_p.Canvas, ZoomFrom(v), true);
        BuildFormatButtons();
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The panel's empty space works like a title bar: dragging it moves the window.</summary>
    private void OnPanelPress(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ClickCount > 1) return;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != this;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase
                or ThinSlider or EditTimeline or EmojiPicker or System.Windows.Controls.Primitives.Popup) return;
        }
        if (Window.GetWindow(this) is not PlayerWindow w || w.WindowState == WindowState.Maximized) return;
        e.Handled = true;
        w.BeginWindowDrag();
    }

    // =============================================================== start / end

    internal void Begin(PlayerWindow win, MpvPlayer mpv, PlayerOverlay ov, EditClip clip)
    {
        _win = win;
        _mpv = mpv;
        _ov = ov;
        _p = new EditProject(clip);
        _undo.Clear();
        _redo.Clear();
        _sel = null;
        _selSeg = -1;
        _format = false;
        _dirty = _snapped = false;
        _cts = new CancellationTokenSource();
        _loaded = clip.Path;
        _f = Math.Clamp(mpv.TimePos, 0, clip.Duration);
        _playing = false;
        _hold = -1;
        mpv.SetProperty("pause", "yes");
        mpv.WantTimeUpdates = true;
        mpv.TimeIntervalMs = 40;
        mpv.StopReached += OnStopReached;

        var layer = ov.EditLayer;
        layer.Selected += OnLayerSelected;
        layer.Moved += OnLayerMoved;
        layer.Resized += OnLayerResized;
        layer.DeleteRequested += OnLayerDelete;
        layer.EditTextRequested += OnLayerEditText;
        layer.VideoPanned += OnVideoPanned;
        layer.VideoZoomed += OnVideoZoomed;
        layer.FrameChanged += OnFrameChanged;
        layer.VideoSelected += OnVideoSelected;
        _graphWanted = _graphShown = null;
        _graphBusy = false;

        bool audioOnly = !clip.HasVideo;
        foreach (var b in new UIElement[] { TextBtn, ImageBtn, BannerBtn, FormatBtn, ClipBtn, ToolsSep })
            b.Visibility = audioOnly ? Visibility.Collapsed : Visibility.Visible;
        _sprite = new LayeredSprite(win.Handle);
        Timeline.SetProject(_p);
        ShowRow(ToolsRow);
        LoadClipAssets(0);
        LoadKeyframes();
        _ = LoadEncoder();
        Refresh(playback: false);
        UpdatePlayhead(force: true);
    }

    /// <summary>Which encoder a save would use (the GPU one when there is one) - shown under the timeline.</summary>
    private async Task LoadEncoder()
    {
        try
        {
            _encoder = await EditExport.EncoderAsync();
            if (_win != null) UpdateInfo();
        }
        catch (Exception ex) { Log.Error("Encoder check", ex); }
    }

    internal void End(bool reloadOriginal)
    {
        if (_win == null) return;
        _cts.Cancel();
        Hook(false);
        _playing = false;
        Caption.CloseEmoji();
        if (_mpv != null)
        {
            _mpv.StopAt = double.NaN;
            _mpv.StopReached -= OnStopReached;
        }
        if (_ov != null)
        {
            var layer = _ov.EditLayer;
            layer.Selected -= OnLayerSelected;
            layer.Moved -= OnLayerMoved;
            layer.Resized -= OnLayerResized;
            layer.DeleteRequested -= OnLayerDelete;
            layer.EditTextRequested -= OnLayerEditText;
            layer.VideoPanned -= OnVideoPanned;
            layer.VideoZoomed -= OnVideoZoomed;
            layer.FrameChanged -= OnFrameChanged;
            layer.VideoSelected -= OnVideoSelected;
        }
        _graphTimer.Stop();
        _graphWanted = _graphShown = null;
        _graphBusy = false;
        _stillSettle.Stop();
        _stillCts?.Cancel();
        _stillShown = false;
        _still = null;
        _stillAt = (-1, 0);
        _ov?.EditLayer.SetStill(null, null, false);
        EditImages.Clear();
        _sprite?.Dispose();
        _sprite = null;
        DropStream();
        _streamSettle.Stop();
        StopAudio();
        _audio?.Close();
        _audio = null;
        _win.ResetPreviewFrame();
        _win.ShowEditWave(null);
        _scrubbing = false;
        if (reloadOriginal && _mpv != null)
        {
            // the player shows the original file again, at the moment the playhead was on
            var clip = _p.Clips[0];
            var (k, t) = _p.EToClip(_p.FToE(_f).E);
            if (k != 0) t = 0;
            if (_loaded != clip.Path) _mpv.Command("loadfile", clip.Path, "replace", "-1", $"start={N(t)},pause=yes");
            else _mpv.CommandAsync("seek", N(t), "absolute+exact");
        }
        _win = null;
        _mpv = null;
        _ov = null;
    }

    // =============================================================== preview: playback through mpv

    /// <summary>The kept pieces as one mpv timeline (EDL), or the file itself while nothing is cut or joined.</summary>
    private string PlaybackUrl()
    {
        var first = _p.Clips[0];
        if (_p.Clips.Count == 1 && _p.Segments.Count == 1 && _p.Segments[0].In < 0.001 && _p.Segments[0].Out > first.Duration - 0.001) return first.Path;
        var sb = new StringBuilder("edl://");
        foreach (var s in _p.Segments)
        {
            var path = _p.Clips[s.Clip].Path;
            sb.Append('%').Append(Encoding.UTF8.GetByteCount(path)).Append('%').Append(path)
              .Append(",start=").Append(N(s.In)).Append(",length=").Append(N(s.Length)).Append(';');
        }
        return sb.ToString();
    }

    private void ApplyPlayback()
    {
        if (_mpv == null) return;
        _f = Math.Clamp(_f, 0, _p.FinalLength);
        var (e, hold, into) = _p.FToE(_f);
        var url = PlaybackUrl();
        if (url != _loaded)
        {
            _loaded = url;
            _mpv.StopAt = double.NaN;
            _scrub.Jumped(e);
            _mpv.Command("loadfile", url, "replace", "-1", $"start={N(e)},pause={(_playing && hold < 0 && !_scrubbing ? "no" : "yes")}");
        }
        else SeekExact(e);
        _hold = hold;
        _holdInto = into;
        _holdStarted = Stopwatch.GetTimestamp();
        ArmStop();
    }

    internal void OnFileLoaded() => ArmStop();

    private void ArmStop()
    {
        if (_mpv == null) return;
        if (!_playing || _hold >= 0) { _mpv.StopAt = double.NaN; return; }
        double e = _p.FToE(_f).E;
        var next = _p.Holds.Where(h => h.E > e + 0.005 && h.Length > 0.01).Select(h => h.E).DefaultIfEmpty(double.NaN).First();
        _mpv.StopAt = next;
    }

    /// <summary>mpv stopped itself right at a banner freeze: the freeze runs on our clock now.</summary>
    private void OnStopReached()
    {
        if (_mpv == null || !_playing) return;
        double e = _mpv.TimePos;
        int best = -1;
        double d = double.MaxValue;
        for (int i = 0; i < _p.Holds.Count; i++)
            if (Math.Abs(_p.Holds[i].E - e) < d) { d = Math.Abs(_p.Holds[i].E - e); best = i; }
        if (best < 0) return;
        _hold = best;
        _holdInto = 0;
        _holdStarted = Stopwatch.GetTimestamp();
        SeekExact(_p.Holds[best].E); // exactly the frozen frame
        Hook(true);
    }

    internal void TogglePlay()
    {
        if (_mpv == null) return;
        if (_playing)
        {
            _playing = false;
            if (_hold >= 0) _holdInto += Stopwatch.GetElapsedTime(_holdStarted).TotalSeconds;
            else _mpv.SetProperty("pause", "yes");
            _mpv.StopAt = double.NaN;
            StopAudio();
            WantStill();
        }
        else
        {
            if (_f >= _p.FinalLength - 0.04) Seek(0, true);
            _playing = true;
            StillOff();
            _stillSettle.Stop();
            if (_hold >= 0) _holdStarted = Stopwatch.GetTimestamp();
            else if (!_scrubbing) // the hand on the playhead: it plays from where it is let go
            {
                _mpv.SetProperty("pause", "no");
                ArmStop();
            }
        }
        Hook(_playing);
        PlayIcon.Icon = _playing ? "pause" : "play";
        SyncAudio(seek: true);
    }

    private ScrubSeeker _scrub = null!;
    // the hand is on the playhead: the preview (picture and sound) waits, playing goes on from where it is let go
    private bool _scrubbing;

    /// <summary>
    /// The hand on the timeline (<see cref="ScrubSeeker"/>: one seek at a time, each for where the hand is by then) -
    /// also the edge of a piece being trimmed, on the raw clip.
    /// </summary>
    private void MpvSeek(double pos, bool final)
    {
        if (_mpv == null) return;
        _scrub.Span = _mpv.Duration;
        if (final) _scrub.Release(pos);
        else _scrub.Move(pos);
    }

    /// <summary>The player has the picture of a seek on screen (told by the player window).</summary>
    internal void OnSeekLanded()
    {
        _scrub.Landed();
        // a paused player has just shown the layers as they were last sent: the next change may go
        if (_graphBusy && Stopwatch.GetElapsedTime(_graphSent).TotalMilliseconds > 8)
        {
            _graphBusy = false;
            if (_graphWanted != null && _graphWanted != _graphShown) SendGraph();
        }
    }

    // ---- the picture with layers: put together by a filter in the player (EditGraph), one change at a time

    private string? _graphWanted, _graphShown;
    private bool _graphBusy;
    private long _graphSent;
    private readonly System.Windows.Threading.DispatcherTimer _graphTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

    /// <summary>
    /// Asks the player for this picture. While a layer is dragged the asks come with every move of the mouse; a paused
    /// player needs a moment for each (it decodes its frame again), so the next one goes out when the last has been
    /// shown - always the newest, none queue up.
    /// </summary>
    private void ShowComposite(string graph)
    {
        _graphWanted = graph;
        if (_graphBusy)
        {
            // the frame may have moved meanwhile (the window resized): the player's picture follows at once
            _win?.SetPreviewComposite(_canvas, _graphShown ?? graph);
            if (!_graphTimer.IsEnabled) _graphTimer.Start();
            return;
        }
        SendGraph();
    }

    private void SendGraph()
    {
        if (_win == null || _graphWanted == null) { _graphTimer.Stop(); return; }
        bool same = _graphWanted == _graphShown;
        _win.SetPreviewComposite(_canvas, _graphWanted);
        if (same)
        {
            // nothing new for the player (the video was already as far as it goes, say): its picture is right as it is
            StillOffIfLanded();
            return;
        }
        _graphShown = _graphWanted;
        _graphBusy = true;
        _graphSent = Stopwatch.GetTimestamp();
        _graphTimer.Start();
    }

    private void GraphTick()
    {
        // playing: the very next frames have the change; paused: the player tells (OnSeekLanded), this is the fallback
        if (_graphBusy && Stopwatch.GetElapsedTime(_graphSent).TotalMilliseconds < (_playing ? 45 : 400)) return;
        _graphBusy = false;
        if (_graphWanted != null && _graphWanted != _graphShown) SendGraph();
        else
        {
            _graphTimer.Stop();
            StillOffIfLanded();
        }
    }

    private void DropComposite()
    {
        _graphTimer.Stop();
        _graphWanted = _graphShown = null;
        _graphBusy = false;
        StillOff();
    }

    // ---- the clip's frame under the playhead: while a layer is dragged, the layers are drawn from it at once
    //      (EditCanvasLayer.SetStill) and the player - which needs up to a quarter of a second per change - follows

    private System.Windows.Media.Imaging.BitmapSource? _still;
    private (int Clip, double T) _stillAt = (-1, 0);
    private CancellationTokenSource? _stillCts;
    private bool _stillShown;
    private readonly System.Windows.Threading.DispatcherTimer _stillSettle = new() { Interval = TimeSpan.FromMilliseconds(140) };

    private (int Clip, double T) StillSpot() => _p.EToClip(_p.FToE(_f).E);

    private bool StillFits
    {
        get
        {
            if (_still == null) return false;
            var (k, t) = StillSpot();
            return k == _stillAt.Clip && Math.Abs(t - _stillAt.T) < 0.005;
        }
    }

    /// <summary>The playhead rests on a frame while layers share the picture: that frame is fetched, ready for a drag.</summary>
    private void WantStill()
    {
        if (_win == null || !_p.IsComposite || _playing || _scrubbing || StillFits) return;
        _stillSettle.Stop();
        _stillSettle.Start();
    }

    private async void FetchStill()
    {
        if (_win == null || !_p.IsComposite || _playing || _scrubbing || StillFits || !Ffmpeg.Available) return;
        var (k, t) = StillSpot();
        var clip = _p.Clips[k];
        if (!clip.HasVideo) return;
        _stillCts?.Cancel();
        var cts = _stillCts = new CancellationTokenSource();
        try
        {
            var bmp = await Ffmpeg.Frame(clip.Path, t, Math.Clamp(clip.Height, 2, 1080), cts.Token, _win.ColorMatrix);
            if (cts.IsCancellationRequested || _win == null || bmp == null) return;
            var (k2, t2) = StillSpot();
            if (k2 != k || Math.Abs(t2 - t) > 0.005) return; // the playhead has moved on meanwhile
            _still = bmp;
            _stillAt = (k, t);
            if (_stillShown) _ov?.EditLayer.SetStill(_still, clip, true); // a drag is on: it takes over right now
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Still frame", ex); }
    }

    /// <summary>A layer is being changed by hand: the layers are drawn from the still from now on (when this frame's is there).</summary>
    private void StillOn()
    {
        if (_ov == null || _playing || !_p.IsComposite) return;
        _stillShown = true;
        if (StillFits) _ov.EditLayer.SetStill(_still, _p.Clips[_stillAt.Clip], true);
        else WantStill();
    }

    /// <summary>The hand has let go and the player shows the layers as they are now: its own picture again.</summary>
    private void StillOffIfLanded()
    {
        if (!_stillShown || _dragCommitted || _graphBusy || _graphWanted != _graphShown) return;
        StillOff();
    }

    private void StillOff()
    {
        _stillShown = false;
        _ov?.EditLayer.SetStill(_still, _still != null && _stillAt.Clip >= 0 && _stillAt.Clip < _p.Clips.Count ? _p.Clips[_stillAt.Clip] : null, false);
    }

    /// <summary>A jump of its own (keys, a click on a bar, the end of a freeze): exact, and noted.</summary>
    private void SeekExact(double e)
    {
        _scrub.Jumped(e);
        _mpv?.CommandAsync("seek", N(e), "absolute+exact");
    }

    private void Seek(double f, bool final)
    {
        if (_mpv == null) return;
        bool endOfScrub = final && _scrubbing;
        if (!final && !_scrubbing)
        {
            _scrubbing = true;
            StopAudio();
        }
        if (final) _scrubbing = false;
        _f = Math.Clamp(f, 0, _p.FinalLength);
        if (_stillShown) StillOff(); // another frame: the still of the old one must not stay on top
        _afterHold = -1;
        var (e, hold, into) = _p.FToE(_f);
        if (_loaded.StartsWith("%raw%", StringComparison.Ordinal)) _loaded = ""; // back from a trim preview
        if (_loaded != PlaybackUrl()) ApplyPlayback();
        else if (!final || endOfScrub) MpvSeek(e, final);
        else SeekExact(e);
        _hold = hold;
        _holdInto = into;
        _holdStarted = Stopwatch.GetTimestamp();
        if (hold >= 0 || _scrubbing) _mpv.SetProperty("pause", "yes");
        else if (_playing) _mpv.SetProperty("pause", "no");
        ArmStop();
        UpdatePlayhead(force: true);
        UpdateLayer();
        UpdateBanner();
        SyncAudio(seek: true);
        if (final) WantStill();
    }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Every displayed frame while playing: the clock, the playhead, what is on screen, the banner.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (_mpv == null || !_playing) { Hook(false); return; }
        if (_scrubbing) return; // the hand has the playhead (every move already showed where it is)
        // at most ~120 times a second: a 400 Hz screen asked for all of this 400 times a second, for nothing a video
        // (30-60 frames a second) or the eye could tell apart - and the holds still end within 8 ms
        long tick = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastFrameTick, tick).TotalMilliseconds < 7.5) return;
        _lastFrameTick = tick;
        if (_hold >= 0 && _hold < _p.Holds.Count)
        {
            var h = _p.Holds[_hold];
            double into = _holdInto + Stopwatch.GetElapsedTime(_holdStarted).TotalSeconds;
            double hf = _p.EToF(h.E);
            if (into >= h.Length)
            {
                _f = hf + h.Length;
                _afterHold = _f;
                _hold = -1;
                _mpv.SetProperty("pause", "no");
                ArmStop();
            }
            else _f = hf + into;
        }
        else
        {
            _hold = -1;
            double f = _p.EToF(_mpv.TimePos);
            if (_afterHold >= 0) { if (f < _afterHold) f = _afterHold; else _afterHold = -1; }
            _f = f;
            if (_mpv.EofReached || _f >= _p.FinalLength - 0.01 && _mpv.Paused)
            {
                _playing = false;
                PlayIcon.Icon = "play";
                StopAudio();
                Hook(false);
            }
        }
        if (!_p.IsAudioOnly)
        {
            // another clip of a join (its own shape), or the blurred background has a newer picture to show
            long now = Environment.TickCount64;
            int k = _p.Clips.Count > 1 ? _p.EToClip(_p.FToE(_f).E).Clip : 0;
            if (k != _frameClip) UpdateFrame();
            else if (now - _lastBlurCheck > 400 && _p.Canvas.Background == CanvasBackground.Blur && _win != null)
            {
                _lastBlurCheck = now;
                var (ck, ct) = _p.EToClip(_p.FToE(_f).E);
                _win.SetBackdropImage(Timeline.Thumbnail(ck, ct));
            }
        }
        UpdatePlayhead();
        UpdateLayer();
        UpdateBanner();
        SyncAudio(seek: false);
    }

    /// <summary>mpv state changed (seek finished, paused at the end...) while nothing is playing.</summary>
    internal void OnState()
    {
        if (_mpv == null || _hooked) return;
        if (_hold < 0 && !_loaded.StartsWith("%raw%", StringComparison.Ordinal) && !_mpv.Seeking)
        {
            double f = _p.EToF(_mpv.TimePos);
            if (Math.Abs(f - _f) > 0.05 && !_playing) { } // a seek we asked for may still be landing - keep our own position
        }
        UpdatePlayhead();
    }

    private void UpdatePlayhead(bool force = false)
    {
        Timeline.Position = _f;
        long now = Environment.TickCount64;
        if (!force && now - _lastText < 90) return;
        _lastText = now;
        TimeText.Text = $"{EditTimeline.Clock(_f, true)} / {EditTimeline.Clock(_p.FinalLength, true)}";
    }

    // =============================================================== preview: canvas, captions, banner

    internal void OnLayoutChanged()
    {
        if (_win == null) return;
        UpdateFrame();
        UpdateBanner();
    }

    /// <summary>The window moved (or was minimised): the banner preview is a window of its own and follows.</summary>
    internal void OnMoved()
    {
        if (_win == null) return;
        UpdateBanner();
    }

    /// <summary>The frame of the finished video inside the player, and where the video sits in it (GPU crop in mpv).</summary>
    private void UpdateFrame()
    {
        if (_win == null || _mpv == null || _ov == null) return;
        var area = _win.VideoAreaSize;
        if (area.Width <= 0 || area.Height <= 0) return;
        var (cw, ch) = _p.CanvasSize();
        double s = Math.Min(area.Width / cw, area.Height / ch);
        _canvas = new Rect((area.Width - cw * s) / 2, (area.Height - ch * s) / 2, cw * s, ch * s);
        if (_p.IsAudioOnly) { UpdateLayer(force: true); return; }
        var (k, t) = _p.EToClip(_p.FToE(_f).E);
        var clip = _p.Clips[k];
        _frameClip = k;
        double clipAspect = clip.Width / (double)Math.Max(1, clip.Height);
        var c = _p.Canvas;
        if (_p.IsComposite && clip.HasVideo)
        {
            // layers of the video (or the video mirrored): a filter in the player draws the whole frame - the same graph
            // that makes the file, at the size the frame has on screen (in steps of 16 px, so a window being resized
            // doesn't ask for a new one with every pixel)
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double scale = Math.Min(1, _canvas.Width * dpi / cw);
            int pw = Math.Clamp((int)Math.Round(cw * scale / 16) * 16, 32, Math.Max(32, cw));
            int ph = Math.Max(2, (int)Math.Round(pw * (double)ch / cw / 2) * 2);
            ShowComposite(EditGraph.Preview(_p, clip, pw, ph, _win.StoredRotation));
            WantStill();
            UpdateLayer(force: true);
            return;
        }
        DropComposite();
        // the picture fills the frame exactly as the player shows it anyway: nothing to do
        bool plain = c.Format == CanvasFormat.Original && Math.Abs(c.Scale - 1) < 1e-6 && Math.Abs(c.CX - 0.5) < 1e-6 && Math.Abs(c.CY - 0.5) < 1e-6
                     && c.WholeFrame && Math.Abs(clipAspect - cw / (double)ch) < 0.01;
        if (plain || !clip.HasVideo) _win.ResetPreviewFrame();
        else
        {
            var v = _p.VideoRect(clip, _canvas.Width, _canvas.Height);
            v.Offset(_canvas.X, _canvas.Y);
            var frame = EditProject.FrameRect(c, _canvas.Width, _canvas.Height);
            frame.Offset(_canvas.X, _canvas.Y);
            var vis = Rect.Intersect(Rect.Intersect(v, frame), _canvas);
            var blur = c.Background == CanvasBackground.Blur ? Timeline.Thumbnail(k, t) : null;
            _win.SetPreviewFrame(_canvas, vis, v, clipAspect, blur);
        }
        UpdateLayer(force: true);
    }

    /// <summary>The clip under the playhead (video layers are measured against its picture).</summary>
    private EditClip CurrentClip()
    {
        var clip = _p.Clips[_p.EToClip(_p.FToE(_f).E).Clip];
        return clip.HasVideo ? clip : _p.FirstVideo;
    }

    private void UpdateLayer(bool force = false)
    {
        if (_ov == null) return;
        _ov.EditLayer.Update(_p, _canvas, _f, _sel, _format, force);
    }

    private BannerOverlay? ActiveBanner() =>
        _p.Overlays.OfType<BannerOverlay>().FirstOrDefault(b => _p.VisibleAt(b, _f));

    /// <summary>
    /// Shows the banner frame for this moment in its own layered window, at exactly the size it has in the frame. The
    /// picture is decoded on the fly (<see cref="BannerStream"/>); just before a banner comes up while playing, its
    /// decoder starts ahead so the first frame is ready on time.
    /// </summary>
    private void UpdateBanner()
    {
        if (_sprite == null || _ov == null || _win == null) return;
        var b = ActiveBanner();
        if (b == null)
        {
            _sprite.Hide();
            var next = _playing ? _p.Overlays.OfType<BannerOverlay>().FirstOrDefault(x => _p.BannerSpan(x).F0 - _f is > 0 and < 0.8) : null;
            if (next != null && BannerSize(next, out _, out int nw)) StreamFor(next, nw).Frame(0, playing: true); // warm up
            else DropStream();
            return;
        }
        if (_win.WindowState == WindowState.Minimized || !BannerSize(b, out var rect, out int want)) { _sprite.Hide(); return; }
        var s = StreamFor(b, want);
        var (f0, _) = _p.BannerSpan(b);
        var px = s.Frame(_f - f0, _playing);
        if (px == null) return; // on its way (the frame arrives in a moment)
        double dpi = VisualTreeHelper.GetDpi(_ov.EditLayer).DpiScaleX;
        var tl = _ov.EditLayer.PointToScreen(rect.TopLeft);
        int x = (int)Math.Round(tl.X + (rect.Width * dpi - s.Width) / 2.0), y = (int)Math.Round(tl.Y + (rect.Height * dpi - s.Height) / 2.0);
        _sprite.Show(px, s.Width, s.Height, x, y, _ov.Handle);
    }

    /// <summary>Where a banner is on screen and how many pixels wide.</summary>
    private bool BannerSize(BannerOverlay b, out Rect rect, out int width)
    {
        rect = _ov!.EditLayer.ItemRect(b);
        width = 0;
        if (rect.Width < 4 || PresentationSource.FromVisual(_ov.EditLayer) == null) return false;
        double dpi = VisualTreeHelper.GetDpi(_ov.EditLayer).DpiScaleX;
        width = Math.Clamp((int)Math.Round(rect.Width * dpi), 16, 4096) & ~1;
        return true;
    }

    /// <summary>The decoder for a banner at a size; a new size starts a new one once it stops changing (dragging a handle).</summary>
    private BannerStream StreamFor(BannerOverlay b, int width)
    {
        if (_stream != null && _streamFor == b.Id)
        {
            if (Math.Abs(_stream.Width - width) <= Math.Max(2, width / 50)) return _stream;
            if (width != _streamWant)
            {
                _streamWant = width;
                _streamSettle.Stop();
                _streamSettle.Start();
            }
            if (_streamSettle.IsEnabled) return _stream;
        }
        _stream?.Dispose();
        _stream = new BannerStream(b.Asset, width);
        _stream.FrameArrived += OnBannerFrame;
        _streamFor = b.Id;
        _streamWant = width;
        return _stream;
    }

    private void DropStream()
    {
        if (_stream == null) return;
        _stream.Dispose();
        _stream = null;
        _streamFor = Guid.Empty;
    }

    /// <summary>A banner frame was decoded: while nothing plays (e.g. right after a jump) it is shown at once.</summary>
    private void OnBannerFrame()
    {
        if (Interlocked.Exchange(ref _bannerFramePending, 1) == 1) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _bannerFramePending, 0);
            if (!_hooked) UpdateBanner();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>The banner's sound plays along in the preview (the video's own sound is silent in a freeze, like in the file).</summary>
    private void SyncAudio(bool seek)
    {
        var b = _playing && !_scrubbing ? ActiveBanner() : null;
        if (b == null || !b.Asset.HasAudio || !_bannerWav.TryGetValue(b.Asset.Path, out var wav)) { StopAudio(); return; }
        var (f0, _) = _p.BannerSpan(b);
        _audio ??= new MediaPlayer { Volume = (_mpv?.Muted ?? false) ? 0 : Math.Clamp((_mpv?.Volume ?? 100) / 100.0, 0, 1) };
        if (_audioFor != b.Id)
        {
            _audio.Open(new Uri(wav));
            _audioFor = b.Id;
            seek = true;
        }
        if (seek || !_audioPlaying) _audio.Position = TimeSpan.FromSeconds(Math.Max(0, _f - f0));
        if (!_audioPlaying) { _audio.Play(); _audioPlaying = true; }
    }

    private void StopAudio()
    {
        if (_audio == null || !_audioPlaying) return;
        _audio.Pause();
        _audioPlaying = false;
    }

    // =============================================================== assets of a clip

    private async void LoadClipAssets(int k)
    {
        var clip = _p.Clips[k];
        var ct = _cts.Token;
        if (!Ffmpeg.Available) return;
        var tasks = new List<Task>();
        if (clip.HasAudio)
            tasks.Add(Task.Run(async () =>
            {
                int bins = (int)Math.Clamp(clip.Duration * 40, 200, 40000);
                var w = await Ffmpeg.Waveform(clip.Path, bins, clip.Duration, ct);
                if (w != null && !ct.IsCancellationRequested) await Dispatcher.InvokeAsync(() => { Timeline.SetWaveform(k, w); UpdateEditWave(); });
            }, ct));
        if (clip.HasVideo)
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int h = (int)Math.Round(50 * dpi);
            int n = (int)Math.Clamp(Math.Ceiling(clip.Duration / 1.5), 6, 48);
            var gate = new SemaphoreSlim(3);
            for (int i = 0; i < n; i++)
            {
                double t = clip.Duration * (i + 0.5) / n;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var bmp = await Ffmpeg.Frame(clip.Path, t, h, ct);
                        if (bmp != null && !ct.IsCancellationRequested)
                            await Dispatcher.InvokeAsync(() =>
                            {
                                bool first = !Timeline.HasThumbnails(k);
                                Timeline.AddThumbnail(k, t, bmp);
                                if (first && _p.Canvas.Background == CanvasBackground.Blur) UpdateFrame();
                            });
                    }
                    finally { gate.Release(); }
                }, ct));
            }
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Editor assets", ex); }
    }

    private async void LoadKeyframes()
    {
        var clip = _p.Clips[0];
        if (!clip.HasVideo) return;
        var ct = _cts.Token;
        try
        {
            var (kf, reordered) = await Task.Run(() => Ffmpeg.KeyframeScan(clip.Path, ct), ct);
            if (ct.IsCancellationRequested) return;
            _keyframes = kf;
            _reordered = reordered;
            UpdateInfo();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Keyframes", ex); }
    }

    // =============================================================== undo / refresh

    private void Commit()
    {
        _undo.Push(_p.Clone());
        if (_undo.Count > 200) { var keep = _undo.Take(150).Reverse().ToList(); _undo.Clear(); foreach (var x in keep) _undo.Push(x); }
        _redo.Clear();
        _dirty = true;
    }

    private void Restore(EditProject p)
    {
        var selId = _sel?.Id;
        _p = p;
        _p.Changed();
        Timeline.SetProject(_p);
        _sel = selId is Guid id ? _p.Overlays.FirstOrDefault(o => o.Id == id) : null;
        _selSeg = -1;
        Select(_sel);
        Refresh(playback: true);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_p.Clone());
        Restore(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_p.Clone());
        Restore(_redo.Pop());
    }

    private void Refresh(bool playback)
    {
        _p.Changed();
        Timeline.Select(_selSeg, _sel);
        Timeline.Rebuild();
        UpdateEditWave();
        if (playback) ApplyPlayback();
        UpdateFrame();
        UpdateBanner();
        UpdateInfo();
        UpdatePlayhead(force: true);
        UndoBtn.IsEnabled = _undo.Count > 0;
        RedoBtn.IsEnabled = _redo.Count > 0;
        DeleteBtn.IsEnabled = _sel != null || _selSeg >= 0 && _p.Segments.Count > 1;
    }

    /// <summary>A song's big waveform above the editor shows the edit - the kept pieces, the way they play.</summary>
    private void UpdateEditWave()
    {
        if (_win == null || !_p.IsAudioOnly) return;
        // nothing cut or joined yet: the song's own waveform stays as it was
        _win.ShowEditWave(PlaybackUrl() == _p.Clips[0].Path ? null : Timeline.EditedWave());
    }

    private void UpdateInfo()
    {
        var (cw, ch) = _p.CanvasSize();
        string len = EditTimeline.Clock(_p.FinalLength, false);
        InfoText.Text = _p.IsAudioOnly ? L.F("Edit.InfoAudio", len) : L.F("Edit.Info", len, cw, ch);
        if (!_p.IsAudioOnly && (_sel is VideoLayer || _format))
        {
            // a video is picked up: what the mouse does with it
            InfoIcon.Visibility = Visibility.Collapsed;
            InfoNote.Text = L.T("Edit.LayerHint");
            return;
        }
        bool copy = EditExport.CanCopy(_p, _keyframes, _reordered);
        InfoIcon.Visibility = copy ? Visibility.Visible : Visibility.Collapsed;
        InfoNote.Text = copy ? (_snapped ? L.T("Edit.Snapped") : L.T("Player.Lossless"))
            : L.F("Edit.InfoEncode", _encoder.Length == 0 ? "…" : EditExport.EncoderLabel(_encoder));
    }

    // =============================================================== selection & inspector rows

    private void ShowRow(FrameworkElement row)
    {
        foreach (var r in new FrameworkElement[] { ToolsRow, Caption, ItemRow, FormatRow })
            r.Visibility = ReferenceEquals(r, row) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Select(EditOverlay? o, bool keepSegment = false)
    {
        _sel = o;
        if (!keepSegment || o != null) { if (o != null) _selSeg = -1; }
        _textCommitted = false;
        _suppress = true;
        try
        {
            if (o is TextOverlay t)
            {
                _format = false;
                ShowRow(Caption);
                Caption.Show(t);
            }
            else if (o != null)
            {
                _format = false;
                ShowRow(ItemRow);
                ItemTitle.Text = o switch
                {
                    BannerOverlay b => L.T("Edit.BannerItem") + " · " + Path.GetFileNameWithoutExtension(b.Asset.Path),
                    VideoLayer => L.T("Edit.LayerItem"),
                    ImageOverlay im => L.T("Edit.ImageItem") + " · " + Path.GetFileNameWithoutExtension(im.Path),
                    _ => L.T("Edit.EmojiItem"),
                };
                BannerPlace.Visibility = o is BannerOverlay ? Visibility.Visible : Visibility.Collapsed;
                LayerFit.Visibility = o is VideoLayer ? Visibility.Visible : Visibility.Collapsed;
                ItemSizeLabel.Text = L.T(o is VideoLayer ? "Edit.Zoom" : "Edit.Size");
                ItemFlip.Visibility = ItemDuplicate.Visibility = o is BannerOverlay ? Visibility.Collapsed : Visibility.Visible;
                ItemSize.Value = o is VideoLayer layer ? ZoomTo(layer.Scale) : ItemSizeTo(o);
            }
            else if (!_format) ShowRow(ToolsRow);
        }
        finally { _suppress = false; }
        Timeline.Select(_selSeg, _sel);
        UpdateLayer(force: true);
        DeleteBtn.IsEnabled = _sel != null || _selSeg >= 0 && _p.Segments.Count > 1;
        UpdateInfo();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _format = false;
        Select(null);
        ShowRow(ToolsRow);
        UpdateFrame();
        UpdateInfo();
    }

    // ---- layer (the picture) events

    private void OnLayerSelected(EditOverlay? o) => Select(o);

    private void OnLayerMoved(EditOverlay o, double x, double y, bool final)
    {
        if (!_dragCommitted) { Commit(); _dragCommitted = true; }
        o.X = x;
        o.Y = y;
        if (final) _dragCommitted = false;
        UpdateLayer(force: true);
        if (o is BannerOverlay) UpdateBanner();
    }

    private void OnLayerResized(EditOverlay o, double size, bool final)
    {
        if (!_dragCommitted) { Commit(); _dragCommitted = true; }
        o.Size = ClampSize(o, size);
        if (final) _dragCommitted = false;
        _suppress = true;
        if (o is TextOverlay) Caption.ShowSize(o.Size); else ItemSize.Value = ItemSizeTo(o);
        _suppress = false;
        UpdateLayer(force: true);
        if (o is BannerOverlay) UpdateBanner();
    }

    private void OnLayerDelete(EditOverlay o) => DeleteOverlay(o);

    private void OnLayerEditText(TextOverlay t)
    {
        Select(t);
        Caption.FocusText();
    }

    private void OnVideoPanned(IVideoPlacement l, double cx, double cy, bool final)
    {
        if (!_dragCommitted) { Commit(); _dragCommitted = true; }
        l.CX = cx;
        l.CY = cy;
        var (cw, ch) = _p.CanvasSize();
        EditProject.KeepInFrame(CurrentClip(), l, cw, ch);
        if (final) { _dragCommitted = false; UpdateInfo(); }
        StillOn();
        UpdateFrame();
    }

    private void OnVideoZoomed(IVideoPlacement l, double k, Point at) => SetZoom(l, l.Scale * k, true, at);

    /// <summary>A layer's frame was dragged: an edge (the video stays where it is) or the whole layer (the video goes along).</summary>
    private void OnFrameChanged(IVideoPlacement l, Rect frame, bool moved, bool final)
    {
        if (!_dragCommitted) { Commit(); _dragCommitted = true; }
        var (cw, ch) = _p.CanvasSize();
        if (moved)
        {
            l.FX = frame.X;
            l.FY = frame.Y;
        }
        else EditProject.SetFrame(CurrentClip(), l, frame, cw, ch);
        if (final) { _dragCommitted = false; UpdateInfo(); }
        _suppress = true;
        if (ReferenceEquals(l, _p.Canvas)) ZoomSlider.Value = ZoomTo(l.Scale); else ItemSize.Value = ZoomTo(l.Scale);
        _suppress = false;
        StillOn();
        UpdateFrame();
    }

    /// <summary>The video itself was clicked while layers share the picture: the Format tool, with the video picked up.</summary>
    private void OnVideoSelected()
    {
        _format = true;
        Select(null);
        ShowRow(FormatRow);
        SyncFormatControls();
        UpdateFrame();
        UpdateInfo();
    }

    // ---- timeline events

    private void OnBarMoved(EditOverlay o, double f0, double f1, bool final)
    {
        if (!_dragCommitted) { Commit(); _dragCommitted = true; }
        double S(double f) => _p.EToS(_p.FToE(f).E).S;
        if (o is BannerOverlay b)
        {
            // the bar is the banner on screen; its freeze starts Lead seconds after it appears
            var (old0, _) = _p.BannerSpan(b);
            var hold = _p.Holds.FirstOrDefault(h => ReferenceEquals(h.Banner, b));
            double freezeF = f0 + hold.Lead;
            b.At = S(freezeF - (f0 > old0 ? hold.Length * 0 : 0));
        }
        else
        {
            o.Start = S(f0);
            o.End = Math.Max(o.Start + 0.1, S(f1));
        }
        _p.Changed();
        Timeline.Rebuild(nextFrame: !final);
        if (final)
        {
            _dragCommitted = false;
            Refresh(playback: o is BannerOverlay);
        }
        else
        {
            UpdateLayer(force: true);
            UpdateBanner();
        }
    }

    /// <summary>
    /// A bar was dragged to another lane: its item goes right in front of the other one, or right behind it. (What lies
    /// on the video is drawn in the order of the list, within its kind: the layers of the video, then captions and
    /// pictures, the banner on top.) The drag ends with the bar's final move, which closes the undo step.
    /// </summary>
    private void OnBarReordered(EditOverlay o, EditOverlay other, bool inFront)
    {
        int from = _p.Overlays.IndexOf(o);
        if (from < 0 || !_p.Overlays.Contains(other) || ReferenceEquals(o, other)) return;
        if (!_dragCommitted) { Commit(); _dragCommitted = true; }
        _p.Overlays.RemoveAt(from);
        int at = _p.Overlays.IndexOf(other) + (inFront ? 1 : 0);
        _p.Overlays.Insert(Math.Clamp(at, 0, _p.Overlays.Count), o);
        _p.Changed();
        Timeline.Rebuild();
        if (o is VideoLayer) UpdateFrame(); else UpdateLayer(force: true);
    }

    private void OnTrimmed(int seg, bool start, double t, bool final)
    {
        if (_mpv == null || seg < 0 || seg >= _p.Segments.Count) return;
        var clip = _p.Clips[_p.Segments[seg].Clip];
        if (!final)
        {
            // live: the raw clip shows the exact frame the edge is on
            if (_playing) TogglePlay();
            if (!_loaded.StartsWith("%raw%", StringComparison.Ordinal) || !_loaded.EndsWith(clip.Path, StringComparison.Ordinal))
            {
                _loaded = "%raw%" + clip.Path;
                _mpv.Command("loadfile", clip.Path, "replace", "-1", $"start={N(t)},pause=yes");
            }
            else MpvSeek(t, final: false);
            return;
        }
        Commit();
        _p.Trim(seg, start, t);
        if (start && seg > 0) SnapForCopy(seg);
        _p.Changed();
        // the playhead goes to the trimmed edge
        double e = _p.SegmentStartE(seg) + (start ? 0 : _p.Segments[seg].Length - 0.04);
        _f = _p.EToF(Math.Max(0, e));
        _loaded = "";
        Refresh(playback: true);
    }

    // =============================================================== tools

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void Split_Click(object sender, RoutedEventArgs e) => Split();

    private void Split()
    {
        var (e, hold, _) = _p.FToE(_f);
        if (hold >= 0) return;
        var copy = _p.Clone();
        if (!_p.Split(e)) return;
        _undo.Push(copy);
        _redo.Clear();
        _dirty = true;
        _selSeg = _p.EToS(e + 0.001).Segment;
        Select(null, keepSegment: true);
        Refresh(playback: false);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        if (_sel != null) { DeleteOverlay(_sel); return; }
        if (_selSeg < 0 || _p.Segments.Count <= 1) return;
        Commit();
        double start = _p.EToF(_p.SegmentStartE(_selSeg));
        int seg = _selSeg;
        _p.Delete(seg);
        if (seg < _p.Segments.Count && seg > 0) SnapForCopy(seg);
        _selSeg = -1;
        _f = Math.Min(start, _p.FinalLength);
        Select(null);
        Refresh(playback: true);
    }

    /// <summary>
    /// Only cuts so far: the piece after a cut starts on the next keyframe (within 10 s), so the file can still be
    /// saved bit for bit - same as the old "Cut out".
    /// </summary>
    private void SnapForCopy(int seg)
    {
        if (!_p.CutsOnly || _reordered || _keyframes is not { Length: > 0 } kf || seg <= 0 || seg >= _p.Segments.Count) return;
        var s = _p.Segments[seg];
        var prev = _p.Segments[seg - 1];
        if (prev.Clip == s.Clip && Math.Abs(prev.Out - s.In) < 1e-6) return; // nothing was removed in between
        int i = Array.BinarySearch(kf, s.In - 0.0005);
        if (i < 0) i = ~i;
        if (i >= kf.Length || Math.Abs(kf[i] - s.In) < 0.002) return;
        if (kf[i] - s.In > 10 || kf[i] > s.Out - EditProject.MinSegment) return;
        _p.Segments[seg] = s with { In = kf[i] };
        _p.Changed();
        _snapped = true;
    }

    private void DeleteOverlay(EditOverlay o)
    {
        Commit();
        _p.Remove(o);
        bool banner = o is BannerOverlay;
        Select(null);
        Refresh(playback: banner);
    }

    private void ItemDelete_Click(object sender, RoutedEventArgs e) { if (_sel != null) DeleteOverlay(_sel); }

    // ---- video layers, pictures, mirror, duplicate

    /// <summary>
    /// The same video once more as a layer (the video duplicated: the copy button of the Format tool, or Ctrl+D with
    /// nothing else picked up). The first one shares the frame with the video - two halves, each filled - and is picked
    /// up at once: the wheel zooms it (far enough for a small camera picture), dragging moves the video in it.
    /// </summary>
    private void AddLayer()
    {
        if (_p.IsAudioOnly) return;
        Commit();
        var layer = _p.AddLayer();
        Select(layer);
        Refresh(playback: false);
    }

    private void Image_Click(object sender, RoutedEventArgs e) => AddImage(null);

    /// <summary>A picture onto the video: in the middle, for the whole video - then moved, resized, mirrored, trimmed on the time line.</summary>
    private void AddImage(string? path)
    {
        if (_p.IsAudioOnly) return;
        if (path == null)
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("Edit.ImagePick"),
                Filter = $"{L.T("Edit.ImageFilter")}|{string.Join(";", MediaFormats.Images.Select(x => "*" + x))}|{L.T("Player.FilterAny")}|*.*",
                InitialDirectory = File.Exists(_lastImage) ? Path.GetDirectoryName(_lastImage) : Directory.Exists(pictures) ? pictures : null,
            };
            if (dlg.ShowDialog(_win) != true) return;
            path = dlg.FileName;
        }
        var size = EditImages.Size(path);
        if (size == null || EditImages.Preview(path) == null)
        {
            if (_win != null) Hud.Show(L.T("Edit.ImageFailed"), "info", _win);
            return;
        }
        _lastImage = path;
        Commit();
        var (cw, ch) = _p.CanvasSize();
        double aspect = size.Value.Width / (double)Math.Max(1, size.Value.Height);
        var im = new ImageOverlay
        {
            Path = path,
            Width = size.Value.Width,
            Height = size.Value.Height,
            Start = 0,
            End = _p.SourceLength,
            X = 0.5,
            Y = 0.5,
            // at most half the frame's height and 60 % of its width
            Size = Math.Min(0.5, 0.6 * cw / (ch * aspect)),
        };
        _p.Overlays.Add(im);
        Select(im);
        Refresh(playback: false);
    }

    private string _lastImage = "";

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelection();

    /// <summary>
    /// A second one of what is picked up, a little to the side and right above it. With no item picked up it is the
    /// video itself that is duplicated: a new layer of the video.
    /// </summary>
    private void DuplicateSelection()
    {
        if (_p.IsAudioOnly || _sel is BannerOverlay) return;
        if (_sel == null)
        {
            AddLayer();
            return;
        }
        Commit();
        var copy = _sel.Duplicate();
        if (copy is VideoLayer vl)
        {
            vl.FX = Math.Clamp(vl.FX + 0.04, 0, Math.Max(0, 1 - vl.FW));
            vl.FY = Math.Clamp(vl.FY + 0.04, 0, Math.Max(0, 1 - vl.FH));
        }
        else
        {
            copy.X = Math.Min(1.1, copy.X + 0.04);
            copy.Y = Math.Min(1.1, copy.Y + 0.04);
        }
        int at = _p.Overlays.IndexOf(_sel);
        _p.Overlays.Insert(at < 0 ? _p.Overlays.Count : at + 1, copy);
        Select(copy);
        Refresh(playback: false);
    }

    private void Flip_Click(object sender, RoutedEventArgs e) => FlipSelection();

    /// <summary>
    /// Mirrors what is picked up - a picture, an emoji, a video layer, or the video itself with the Format tool open.
    /// Not a caption (mirrored writing is no use) and not a banner.
    /// </summary>
    private void FlipSelection()
    {
        if (_p.IsAudioOnly || _sel is BannerOverlay or TextOverlay || _sel == null && !_format) return;
        Commit();
        if (_sel is IVideoPlacement layer) MirrorVideo(layer);
        else if (_sel != null) _sel.Flip = !_sel.Flip;
        else MirrorVideo(_p.Canvas);
        UpdateFrame();
        UpdateInfo();
    }

    /// <summary>What shows in the frame is mirrored where it is (the video is turned over around the frame's middle).</summary>
    private static void MirrorVideo(IVideoPlacement l)
    {
        l.Flip = !l.Flip;
        l.CX = 1 - l.CX;
    }

    private void LayerFit_Click(object sender, RoutedEventArgs e) => PlaceLayer(fill: false);
    private void LayerFill_Click(object sender, RoutedEventArgs e) => PlaceLayer(fill: true);

    private void PlaceLayer(bool fill)
    {
        if (_sel is not VideoLayer vl) return;
        Commit();
        var (cw, ch) = _p.CanvasSize();
        vl.Scale = fill ? EditProject.FillScale(CurrentClip(), vl, cw, ch) : 1;
        vl.CX = vl.CY = 0.5;
        _suppress = true;
        ItemSize.Value = ZoomTo(vl.Scale);
        _suppress = false;
        Refresh(playback: false);
    }

    // ---- captions

    private void Text_Click(object sender, RoutedEventArgs e) => AddText();

    private void AddText()
    {
        if (_p.IsAudioOnly) return;
        Commit();
        var (cw, ch) = _p.CanvasSize();
        double size = Captions.DefaultSize(cw, ch);
        var t = new TextOverlay
        {
            Text = L.T("Edit.Placeholder"),
            Start = 0,
            End = _p.SourceLength,
            X = 0.5,
            Y = 0.5,
            Size = size,
            WrapEm = Captions.WrapEmFor(size, cw, ch),
            Style = CaptionStyle.Stroke,
            Color = Colors.White,
        };
        _p.Overlays.Add(t);
        Select(t);
        Refresh(playback: false);
        Caption.FocusText();
    }

    // the caption tool (CaptionPanel, shared with the picture editor) reports what was chosen

    private void OnCaptionText(string text)
    {
        if (_sel is not TextOverlay t) return;
        if (!_textCommitted) { Commit(); _textCommitted = true; }
        var (cw, ch) = _p.CanvasSize();
        t.Spans = ColorSpans.Rebase(t.Spans, t.Text, text); // coloured words stay coloured while the text around them changes
        t.Text = text;
        t.WrapEm = Captions.WrapEmFor(t.Size, cw, ch); // typing breaks lines at the frame's width; resizing later doesn't
        UpdateLayer(force: true);
        Timeline.Rebuild();
    }

    private void OnCaptionStyle(CaptionStyle style)
    {
        if (_sel is not TextOverlay t || style == t.Style) return;
        Commit();
        t.Style = style;
        UpdateLayer(force: true);
    }

    private bool _colorCommitted;

    /// <summary>
    /// A colour for the selected part of the text (length &gt; 0), or for the whole caption. While the mouse moves over
    /// the palette the caption follows it (<paramref name="final"/> false); the whole pick is one step to undo.
    /// </summary>
    private void OnCaptionColor(Color color, int start, int length, bool final)
    {
        if (_sel is TextOverlay t)
        {
            var whole = length > 0 ? t.Color : color;
            var spans = length > 0 ? ColorSpans.Paint(t.Spans, start, length, color) : Array.Empty<ColorSpan>();
            // the colour it already has: nothing to draw, and no step to undo that would undo nothing
            if (whole != t.Color || ColorSpans.Key(spans) != ColorSpans.Key(t.Spans))
            {
                if (!_colorCommitted) { Commit(); _colorCommitted = true; }
                t.Color = whole;
                t.Spans = spans;
                UpdateLayer(force: true);
            }
        }
        if (final) _colorCommitted = false;
    }

    private void OnCaptionFont(string? font)
    {
        if (_sel is not TextOverlay t || t.Font == font) return;
        Commit();
        t.Font = font;
        UpdateLayer(force: true);
    }

    // ---- sizes (sliders are 0..1)

    // pictures go from a small sticker to bigger than the frame: an even feel along the slider needs steps in proportion
    private const double ImageMin = 0.03, ImageSpan = 60;

    private double ItemSizeTo(EditOverlay o) => o switch
    {
        BannerOverlay => Math.Clamp((o.Size - 0.25) / (1.4 - 0.25), 0, 1),
        ImageOverlay => Math.Clamp(Math.Log(Math.Max(1e-6, o.Size) / ImageMin) / Math.Log(ImageSpan), 0, 1),
        _ => Math.Clamp((o.Size - 0.04) / (0.6 - 0.04), 0, 1),
    };

    // the zoom slider: from 0.4 (small in its frame) up to EditProject.MaxZoom, in proportion
    private static double ZoomFrom(double v) => 0.4 * Math.Pow(EditProject.MaxZoom / 0.4, v);
    private static double ZoomTo(double s) => Math.Clamp(Math.Log(Math.Max(1e-6, s) / 0.4) / Math.Log(EditProject.MaxZoom / 0.4), 0, 1);

    private static double ClampSize(EditOverlay o, double s) => o switch
    {
        TextOverlay => Math.Clamp(s, 0.012, 0.25),
        EmojiOverlay => Math.Clamp(s, 0.03, 0.9),
        ImageOverlay => Math.Clamp(s, 0.02, 2.5),
        _ => Math.Clamp(s, 0.15, 1.6),
    };

    private void SetSize(double v, bool final)
    {
        if (_suppress || _sel == null || _sel is VideoLayer) return;
        if (!_dragCommitted) { Commit(); _dragCommitted = true; }
        _sel.Size = ClampSize(_sel, _sel switch
        {
            TextOverlay => v,
            BannerOverlay => 0.25 + v * (1.4 - 0.25),
            ImageOverlay => ImageMin * Math.Pow(ImageSpan, v),
            _ => 0.04 + v * (0.6 - 0.04),
        });
        if (final) _dragCommitted = false;
        UpdateLayer(force: true);
        if (_sel is BannerOverlay) UpdateBanner();
    }

    // ---- banner

    private void Banner_Click(object sender, RoutedEventArgs e) => _ = AddBanner(pickFile: false);

    private void Banner_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = AddBanner(pickFile: true);
    }

    private async Task AddBanner(bool pickFile)
    {
        var s = App.Instance!.Settings;
        string? path = !pickFile && File.Exists(s.BannerPath) ? s.BannerPath : PickBanner(s.BannerPath);
        if (path == null) return;
        InfoNote.Text = L.T("Edit.BannerPreparing");
        var info = await Banners.AnalyzeAsync(path);
        if (_win == null) return;
        if (info == null)
        {
            Hud.Show(L.T("Edit.BannerFailed"), "info", _win);
            UpdateInfo();
            return;
        }
        s.BannerPath = path;
        s.Save();
        Commit();
        var (cw, ch) = _p.CanvasSize();
        var (e, _, _) = _p.FToE(_f);
        // the banner starts at the playhead; the video freezes Lead seconds later
        double at = _p.EToS(Math.Min(e + 0.7, _p.EditedLength)).S;
        var b = new BannerOverlay
        {
            Asset = info,
            At = at,
            X = 0.5,
            Y = 0.5,
            Size = cw < ch ? 0.96 : 0.62,
        };
        _p.Overlays.Add(b);
        if (info.HasAudio && !_bannerWav.ContainsKey(info.Path))
            _ = Banners.AudioAsync(info).ContinueWith(t => Dispatcher.InvokeAsync(() => { if (t.IsCompletedSuccessfully && t.Result is string w) _bannerWav[info.Path] = w; }));
        Select(b);
        Refresh(playback: true);
    }

    private string? PickBanner(string? last)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Edit.BannerPick"),
            Filter = L.T("Edit.BannerFilter") + "|*.mov;*.webm;*.mp4;*.mkv;*.gif;*.apng|" + L.T("Player.FilterAny") + "|*.*",
            InitialDirectory = File.Exists(last) ? Path.GetDirectoryName(last) : Directory.Exists(downloads) ? downloads : null,
        };
        return dlg.ShowDialog(_win) == true ? dlg.FileName : null;
    }

    private void Place_Click(object sender, RoutedEventArgs e)
    {
        if (_sel is not BannerOverlay b || sender is not FrameworkElement { Tag: string tag }) return;
        Commit();
        b.Y = double.Parse(tag, CultureInfo.InvariantCulture);
        b.X = 0.5;
        UpdateLayer(force: true);
        UpdateBanner();
    }

    // ---- format

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        _format = !_format;
        Select(null);
        ShowRow(_format ? FormatRow : ToolsRow);
        SyncFormatControls();
        UpdateFrame();
        UpdateInfo();
    }

    private void BuildFormatButtons()
    {
        foreach (var (f, label) in new[] { (CanvasFormat.Original, "Edit.FormatOriginal"), (CanvasFormat.Vertical, "9:16"), (CanvasFormat.Square, "1:1"), (CanvasFormat.Portrait, "4:5"), (CanvasFormat.Wide, "16:9") })
        {
            var rb = new RadioButton { Content = label.Contains('.') ? L.T(label) : label, GroupName = "fmt", Tag = f };
            rb.SetResourceReference(StyleProperty, "HudSegment.Item");
            rb.Checked += (_, _) =>
            {
                if (_suppress || _p.Canvas.Format == f) return;
                Commit();
                _p.Canvas.Format = f;
                var (cw, ch) = _p.CanvasSize();
                if (_p.Canvas.WholeFrame && !_p.Overlays.Any(o => o is VideoLayer))
                {
                    _p.Canvas.Scale = 1;
                    _p.Canvas.CX = _p.Canvas.CY = 0.5;
                }
                else _p.Reframe(); // the picture is shared out: the frames follow the new shape and are filled again
                // a phone-shaped frame from a landscape video: fill it, like TikTok does
                if (f != CanvasFormat.Original && _p.FirstVideo.HasVideo && _p.FirstVideo.Width > _p.FirstVideo.Height != cw > ch) _p.Canvas.Background = CanvasBackground.Blur;
                SyncFormatControls();
                Refresh(playback: false);
            };
            FormatPanel.Children.Add(rb);
        }
        foreach (var (q, label) in new[] { (0, "Edit.QualitySource"), (720, "720p"), (1080, "1080p"), (1440, "1440p"), (2160, "4K") })
        {
            var rb = new RadioButton { Content = label.Contains('.') ? L.T(label) : label, GroupName = "quality", Tag = q };
            rb.SetResourceReference(StyleProperty, "HudSegment.Item");
            rb.Checked += (_, _) =>
            {
                if (_suppress || _p.Canvas.ShortSide == q) return;
                Commit();
                _p.Canvas.ShortSide = q;
                Refresh(playback: false);
            };
            QualityPanel.Children.Add(rb);
        }
    }

    private void SyncFormatControls()
    {
        _suppress = true;
        try
        {
            foreach (RadioButton rb in FormatPanel.Children) rb.IsChecked = (CanvasFormat)rb.Tag == _p.Canvas.Format;
            foreach (RadioButton rb in QualityPanel.Children) rb.IsChecked = (int)rb.Tag == _p.Canvas.ShortSide;
            BgBlur.IsChecked = _p.Canvas.Background == CanvasBackground.Blur;
            BgBlack.IsChecked = _p.Canvas.Background == CanvasBackground.Black;
            ZoomSlider.Value = ZoomTo(_p.Canvas.Scale);
        }
        finally { _suppress = false; }
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        _p.Canvas.Scale = 1;
        _p.Canvas.CX = _p.Canvas.CY = 0.5;
        SyncFormatControls();
        Refresh(playback: false);
    }

    private void Fill_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var (cw, ch) = _p.CanvasSize();
        _p.Canvas.Scale = _p.FillScale(_p.FirstVideo, cw, ch);
        _p.Canvas.CX = _p.Canvas.CY = 0.5;
        SyncFormatControls();
        Refresh(playback: false);
    }

    /// <summary>
    /// The zoom of a video in its frame. <paramref name="at"/> (canvas, 0..1): the point of the video there stays there -
    /// the wheel zooms into what the cursor is on (a camera picture in a corner), not into the middle.
    /// </summary>
    private void SetZoom(IVideoPlacement l, double scale, bool final, Point? at = null)
    {
        if (_suppress) return;
        if (!_dragCommitted) { Commit(); _dragCommitted = true; }
        double old = l.Scale, next = Math.Clamp(scale, EditProject.MinZoom, EditProject.MaxZoom);
        if (at is Point a && old > 0 && l.FW > 0 && l.FH > 0)
        {
            double ax = (a.X - l.FX) / l.FW, ay = (a.Y - l.FY) / l.FH, k = next / old;
            l.CX = ax + (l.CX - ax) * k;
            l.CY = ay + (l.CY - ay) * k;
        }
        l.Scale = next;
        var (cw, ch) = _p.CanvasSize();
        EditProject.KeepInFrame(CurrentClip(), l, cw, ch);
        if (final) { _dragCommitted = false; UpdateInfo(); }
        _suppress = true;
        if (ReferenceEquals(l, _p.Canvas)) ZoomSlider.Value = ZoomTo(next); else ItemSize.Value = ZoomTo(next);
        _suppress = false;
        StillOn();
        UpdateFrame();
    }

    private void Bg_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var bg = BgBlack.IsChecked == true ? CanvasBackground.Black : CanvasBackground.Blur;
        if (bg == _p.Canvas.Background) return;
        Commit();
        _p.Canvas.Background = bg;
        UpdateFrame();
    }

    // ---- join

    private async void AddClip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Edit.ClipPick"),
            Filter = $"{L.T("Player.FilterVideo")}|{string.Join(";", MediaFormats.Video.Select(x => "*" + x))}|{L.T("Player.FilterAny")}|*.*",
            InitialDirectory = Path.GetDirectoryName(_p.Clips[^1].Path),
        };
        if (dlg.ShowDialog(_win) != true) return;
        var clip = await ClipProbe.ProbeAsync(dlg.FileName);
        if (_win == null) return;
        if (clip == null || !clip.HasVideo || clip.Duration <= 0)
        {
            Hud.Show(L.T("Edit.ClipFailed"), "info", _win);
            return;
        }
        Commit();
        _p.AddClip(clip);
        LoadClipAssets(_p.Clips.Count - 1);
        Refresh(playback: true);
    }

    // =============================================================== for the self-test (same code paths as the buttons)

    internal EditProject Project => _p;
    internal double Position => _f;
    internal bool InFreeze => _hold >= 0;
    internal void TestSeek(double f) => Seek(f, true);
    internal void TestSplit() => Split();
    internal void TestDeleteSegment(int i) { _selSeg = i; Select(null, keepSegment: true); DeleteSelection(); }
    internal Task TestAddBanner() => AddBanner(pickFile: false);
    internal void TestAddText(string text)
    {
        AddText();
        Caption.TestType(text);
        Done_Click(this, new RoutedEventArgs());
    }
    internal void TestFormat(CanvasFormat f)
    {
        Format_Click(this, new RoutedEventArgs());
        foreach (RadioButton rb in FormatPanel.Children) if ((CanvasFormat)rb.Tag == f) rb.IsChecked = true;
    }
    internal void TestSelect(EditOverlay? o) => Select(o);
    internal void TestDone() => Done_Click(this, new RoutedEventArgs());
    internal VideoLayer? TestAddLayer() { AddLayer(); return _sel as VideoLayer; }
    internal ImageOverlay? TestAddImage(string path) { AddImage(path); return _sel as ImageOverlay; }
    internal EditOverlay? TestDuplicate() { DuplicateSelection(); return _sel; }
    internal void TestDelete() => DeleteSelection();
    internal void TestUndo() => Undo();
    /// <summary>The banner's picture is on screen, and the two windows over the video: the banner's and the one with captions and pictures.</summary>
    internal bool TestBannerShown => _sprite?.Visible ?? false;
    internal (IntPtr Banner, IntPtr Layer) TestWindows => (_sprite?.Handle ?? IntPtr.Zero, _ov?.Handle ?? IntPtr.Zero);
    internal bool TestFormatOpen => _format;
    internal void TestFlip() => FlipSelection();
    internal void TestColor(Color color, int start, int length) => Caption.TestColor(color, start, length);
    /// <summary>The palette opened for a part of the caption's text (0 long = the whole caption) and a colour under the mouse / chosen.</summary>
    internal void TestPalette(int start, int length, Color color, bool final)
    {
        Caption.TestPaletteFor(start, length);
        Caption.TestPalettePick(color, final);
    }
    internal void TestZoom(IVideoPlacement l, double scale, Point? at = null) => SetZoom(l, scale, true, at);
    internal void TestPan(IVideoPlacement l, double cx, double cy) => OnVideoPanned(l, cx, cy, true);
    internal void TestFrame(IVideoPlacement l, Rect frame, bool moved) => OnFrameChanged(l, frame, moved, true);
    /// <summary>The filter the player was last asked to show (null while the plain video is enough).</summary>
    internal string? TestComposite => _graphWanted;
    /// <summary>A change of the layers is still on its way to the screen.</summary>
    internal bool TestCompositeBusy => _graphBusy || _graphWanted != _graphShown;
    /// <summary>The frame under the playhead is there for drawing the layers during a drag.</summary>
    internal bool TestStillReady => StillFits;
    internal bool TestStillShown => _ov?.EditLayer.ShowsStill ?? false;
    /// <summary>The layer over the picture that takes the mouse (its PressAt / MoveTo / Release / WheelAt are what the mouse does).</summary>
    internal EditCanvasLayer? TestCanvas => _ov?.EditLayer;
    /// <summary>A move of a drag (not let go yet), or its end.</summary>
    internal void TestDrag(IVideoPlacement l, double cx, double cy, bool final) => OnVideoPanned(l, cx, cy, final);
    /// <summary>What the editor itself draws over the player's picture (captions, pictures, frames - and the layers during a drag), the frame only.</summary>
    internal System.Windows.Media.Imaging.BitmapSource? TestLayerPicture()
    {
        if (_ov == null) return null;
        var el = _ov.EditLayer;
        int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
        if (w < 2 || h < 2 || _canvas.Width < 2 || _canvas.Height < 2) return null;
        el.UpdateLayout(); // what was asked to be drawn is drawn now
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        var area = new Int32Rect((int)_canvas.X, (int)_canvas.Y, Math.Min(w - (int)_canvas.X, (int)_canvas.Width), Math.Min(h - (int)_canvas.Y, (int)_canvas.Height));
        var crop = new System.Windows.Media.Imaging.CroppedBitmap(rtb, area);
        crop.Freeze();
        return crop;
    }
    internal void TestOpenEmoji()
    {
        if (_sel is not TextOverlay) AddText();
        Caption.TestOpenEmoji();
    }
    internal bool TestEmojiReady => Caption.EmojiReady;
    /// <summary>An emoji picked in the gallery; on a fresh caption it replaces the placeholder - a sticker.</summary>
    internal void TestPickEmoji(string unified)
    {
        var e = AppleEmoji.Catalog?.FirstOrDefault(x => x.Unified == unified);
        if (e == null) return;
        if (_sel is not TextOverlay) AddText();
        Caption.TestPick(e);
        if (_sel is TextOverlay t && t.Text == e.Text) t.Style = CaptionStyle.None;
    }
    /// <summary>The panel for a picture of it (render-ui): an edit drawn in, no player behind it.</summary>
    internal void TestStatic(EditProject p, EditOverlay? sel, bool format, double position)
    {
        _p = p;
        _f = position;
        _p.Changed();
        Timeline.SetProject(p);
        _selSeg = -1;
        _format = format;
        if (format)
        {
            ShowRow(FormatRow);
            SyncFormatControls();
        }
        else Select(sel);
        _encoder = "h264_nvenc";
        UpdateInfo();
        UpdatePlayhead(force: true);
        UndoBtn.IsEnabled = true;
        RedoBtn.IsEnabled = false;
    }

    internal void TestDiscard()
    {
        _dirty = false;
        Cancel_Click(this, new RoutedEventArgs());
    }

    // =============================================================== keyboard

    internal bool HandleKey(KeyEventArgs e)
    {
        if (Caption.IsTyping) return false;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        double frame = 1 / EditExport.OutputFps(_p);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); return true;
            case Key.Left: Seek(_f - (shift ? 1 : frame), true); return true;
            case Key.Right: Seek(_f + (shift ? 1 : frame), true); return true;
            case Key.Home: Seek(0, true); return true;
            case Key.End: Seek(_p.FinalLength, true); return true;
            case Key.S when !ctrl: Split(); return true;
            case Key.Delete:
            case Key.Back: DeleteSelection(); return true;
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl: Redo(); return true;
            case Key.Z when ctrl: Undo(); return true;
            case Key.T when !ctrl && !_p.IsAudioOnly: AddText(); return true;
            case Key.I when !ctrl && !_p.IsAudioOnly: AddImage(null); return true;
            case Key.D when ctrl: DuplicateSelection(); return true;
            case Key.E when !ctrl: Cancel_Click(this, new RoutedEventArgs()); return true;
            case Key.K:
            case Key.MediaPlayPause: TogglePlay(); return true;
            case Key.Escape:
                if (_sel != null || _selSeg >= 0 || _format) { _selSeg = -1; Done_Click(this, new RoutedEventArgs()); return true; }
                Cancel_Click(this, new RoutedEventArgs());
                return true;
        }
        return false;
    }

    // =============================================================== cancel / save

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeave()) return;
        Finished?.Invoke(null, false);
    }

    /// <summary>Leaving with changes asks first (nothing is lost by a slip of the Esc key).</summary>
    internal bool ConfirmLeave()
    {
        if (_saving) return false;
        if (!_dirty || _undo.Count == 0) return true;
        if (_playing) TogglePlay();
        return AlertWindow.Ask(Window.GetWindow(this), L.T("Edit.Discard"), L.T("Edit.DiscardText"), L.T("Common.Discard"), dark: true);
    }

    private void SaveNew_Click(object sender, RoutedEventArgs e) => _ = Save(replace: false);
    private void Replace_Click(object sender, RoutedEventArgs e) => _ = Save(replace: true);

    private async Task Save(bool replace)
    {
        if (_saving || _win == null || _mpv == null) return;
        if (_playing) TogglePlay();
        var p = _p.Clone();
        bool copy = EditExport.CanCopy(p, _keyframes, _reordered);
        string input = p.Clips[0].Path;
        string dir = Path.GetDirectoryName(input)!, name = Path.GetFileNameWithoutExtension(input);
        string ext = copy ? Path.GetExtension(input) : p.IsAudioOnly ? Path.GetExtension(input) : ".mp4";
        string target = replace ? Path.Combine(dir, name + ext) : ImageIO.UniquePath(dir, $"{name} ({L.T(copy ? "Player.TrimmedSuffix" : "Edit.EditedSuffix")})", ext);
        string output = replace ? Path.Combine(dir, $"{name}.lf-tmp{ext}") : target;
        _saving = true;
        bool keepOutput = false; // once the original is gone, the new file is the only copy - it is never cleaned away
        SaveButtons.Visibility = Visibility.Collapsed;
        SaveProgress.Visibility = Visibility.Visible;
        SaveFill.Width = 0;
        try
        {
            var (ok, err) = await EditExport.SaveAsync(p, output, _keyframes, _reordered, new Progress<double>(v => SaveFill.Width = 150 * v), CancellationToken.None);
            if (!ok)
            {
                AlertWindow.Tell(_win, L.T("Edit.SaveFailed"), err ?? "", dark: true);
                return;
            }
            if (!replace)
            {
                Finished?.Invoke(target, false);
                return;
            }
            // Replace: let go of the files, the original goes to the Recycle Bin, the new file takes its name
            var info = new FileInfo(input);
            DateTime created = info.CreationTime;
            await _win.StopAndRelease();
            bool recycled = await Task.Run(() => ShellUtil.MoveToRecycleBin(input));
            if (!recycled)
            {
                var keep = ImageIO.UniquePath(dir, $"{name} ({L.T("Edit.EditedSuffix")})", ext);
                File.Move(output, keep);
                Finished?.Invoke(keep, false);
                return;
            }
            keepOutput = true;
            // A .mov rendered into an .mp4, next to another file that already has that name: that one is somebody
            // else's video - it stays where it is and the new file gets a name of its own.
            if (File.Exists(target)) target = ImageIO.UniquePath(dir, name, ext);
            File.Move(output, target);
            try { File.SetCreationTime(target, created); } catch { /* not important */ }
            Finished?.Invoke(target, true);
        }
        catch (Exception ex)
        {
            Log.Error("Edit save failed", ex);
            AlertWindow.Tell(_win, L.T("Edit.SaveFailed"), ex.Message, dark: true);
        }
        finally
        {
            _saving = false;
            SaveButtons.Visibility = Visibility.Visible;
            SaveProgress.Visibility = Visibility.Collapsed;
            if (replace && !keepOutput && File.Exists(output)) { try { File.Delete(output); } catch { /* ignore */ } }
        }
    }
}
