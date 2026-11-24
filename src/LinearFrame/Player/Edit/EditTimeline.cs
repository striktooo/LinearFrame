using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinearFrame.Player.Edit;

/// <summary>
/// The edit's time line, CapCut-style: ruler, the clips as a film strip (cut pieces with gaps, banner freezes as frozen
/// frames), the sound as a waveform, and lanes with captions, emoji and banners. Two layers: everything static is drawn
/// only after a change; the playhead moves on its own layer, so playback costs one line per frame.
/// </summary>
public sealed class EditTimeline : FrameworkElement
{
    private const double RulerH = 18, TrackTop = 20, TrackH = 50, WaveGap = 3, WaveH = 20, LaneGap = 4, LaneH = 24, Side = 14, Handle = 12;

    private static readonly Brush TrackBg = Frozen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));
    private static readonly Brush HoldDim = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Brush WaveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
    private static readonly Brush WaveSel = Frozen(new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC)));
    private static readonly Brush RulerText = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
    private static readonly Pen Tick = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)), 1));
    private static readonly Pen SelPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Brush TextBar = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)));
    private static readonly Brush EmojiBar = Frozen(new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)));
    private static readonly Brush BannerBar = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5E, 0x57)));
    private static readonly Brush LayerBar = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush ImageBar = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    /// <summary>How many lanes of items are shown under the film strip (more than that many things at one moment is rare).</summary>
    private const int MaxLanes = 6;
    private static readonly Brush TrimDim = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x12)));
    private static readonly Brush GhostWave = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x8E, 0x8E, 0x93)));
    private static readonly Pen HeadShadow = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 4));
    private static readonly Typeface Ui = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface EmojiFace = new("Segoe UI Emoji");

    private readonly DrawingVisual _content = new(), _head = new();
    private readonly Dictionary<int, List<(double T, BitmapSource Bmp)>> _thumbs = new();
    private readonly Dictionary<int, float[]> _waves = new();
    private List<Piece> _pieces = new();
    private readonly List<(EditOverlay O, int Lane, double F0, double F1)> _bars = new();
    private int _lanes = 1;

    private double _pps = 1, _scroll;
    private bool _fit = true;
    // how long a stretch of time the fitted time line shows: it grows with the edit, never shrinks with it - a cut or a
    // trim moves the pieces, the time line doesn't stretch them back out to the full width
    private double _viewLength;
    // what was trimmed off the edit's start and end: still on the strip, dimmed - like trimming on an iPhone - it doesn't
    // play, but it is there to pull the edge back out over. _lead = how much time is shown before the edit's start.
    private Piece? _ghostHead, _ghostTail;
    private double _lead;
    // where a drag of the playhead last asked to go
    private double _seekF;
    private double _position;

    // dragging
    private enum Drag { None, Seek, Bar, BarStart, BarEnd, TrimStart, TrimEnd }
    private Drag _drag;
    private EditOverlay? _dragItem;
    private double _pressF, _barF0, _barF1;
    private int _trimSeg = -1;
    private double _trimF;              // where the trimmed edge is while dragging

    public EditProject? Project { get; private set; }
    public int SelectedSegment { get; private set; } = -1;
    public EditOverlay? SelectedOverlay { get; private set; }

    /// <summary>The playhead was moved (F seconds; final = mouse released).</summary>
    public event Action<double, bool>? Seek;
    public event Action<int>? SegmentSelected;
    public event Action<EditOverlay?>? OverlaySelected;
    /// <summary>A bar was moved/resized: new start and end on the finished video (F); final = mouse released.</summary>
    public event Action<EditOverlay, double, double, bool>? OverlayMoved;
    /// <summary>A piece's start or end edge was dragged: (segment, start?, the clip second shown at the edge, final).</summary>
    public event Action<int, bool, double, bool>? SegmentTrimmed;
    /// <summary>
    /// A bar was dragged to another lane: the item is to go right in front of the other one (true) or right behind it
    /// (false). The drag ends with <see cref="OverlayMoved"/> (final) when the bar was also moved in time, else with
    /// <see cref="ReorderEnded"/>.
    /// </summary>
    public event Action<EditOverlay, EditOverlay, bool>? OverlayReordered;
    public event Action? ReorderEnded;

    public EditTimeline()
    {
        AddVisualChild(_content);
        AddVisualChild(_head);
        ClipToBounds = true;
        Focusable = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(_content, BitmapScalingMode.LowQuality);
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _content : _head;

    // ------------------------------------------------------------------ data

    public void SetProject(EditProject p)
    {
        Project = p;
        _viewLength = 0;
        Rebuild();
    }

    public void AddThumbnail(int clip, double t, BitmapSource bmp)
    {
        if (!_thumbs.TryGetValue(clip, out var list)) _thumbs[clip] = list = new();
        int i = list.FindIndex(x => x.T > t);
        if (i < 0) list.Add((t, bmp)); else list.Insert(i, (t, bmp));
        RenderContent();
    }

    public bool HasThumbnails(int clip) => _thumbs.ContainsKey(clip);

    /// <summary>Nearest film-strip picture of a clip (the editor uses it for the blurred background, too).</summary>
    public BitmapSource? Thumbnail(int clip, double t)
    {
        if (!_thumbs.TryGetValue(clip, out var list) || list.Count == 0) return null;
        int lo = 0, hi = list.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid].T < t) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(list[lo - 1].T - t) < Math.Abs(list[lo].T - t)) lo--;
        return list[lo].Bmp;
    }

    public void SetWaveform(int clip, float[] peaks)
    {
        _waves[clip] = peaks;
        RenderContent();
    }

    /// <summary>
    /// The edit's own waveform: the kept pieces one after another, the way they play (for a song's big waveform above
    /// the editor). Null until every piece's sound has been read.
    /// </summary>
    public float[]? EditedWave()
    {
        if (Project == null || Project.Segments.Count == 0) return null;
        var list = new List<float>();
        foreach (var s in Project.Segments)
        {
            var clip = Project.Clips[s.Clip];
            if (!_waves.TryGetValue(s.Clip, out var peaks) || peaks.Length == 0 || clip.Duration <= 0) return null;
            double perSec = peaks.Length / clip.Duration;
            int a = Math.Max(0, (int)Math.Floor(s.In * perSec)), b = Math.Min(peaks.Length, (int)Math.Ceiling(s.Out * perSec));
            for (int i = a; i < b; i++) list.Add(peaks[i]);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    public void Select(int segment, EditOverlay? overlay)
    {
        SelectedSegment = segment;
        SelectedOverlay = overlay;
        RenderContent();
    }

    /// <summary>After any change of the edit: lays out pieces and lanes again.</summary>
    /// <summary>
    /// Lays the pieces and bars out again and draws them - at once, or (<paramref name="nextFrame"/>, while a bar or
    /// an edge is being dragged) on the next frame, once however many mouse moves come before it.
    /// </summary>
    public void Rebuild(bool nextFrame = false)
    {
        if (Project == null) return;
        _pieces = Project.Pieces();
        _bars.Clear();
        // The lanes are the stack of what lies on the video, the top lane in front: every bar sits right under whatever
        // is in front of it at the same time (things that are never on screen together can share a lane).
        int used = 0;
        foreach (var (o, f0, f1) in Stack(Project))
        {
            int lane = 0;
            foreach (var b in _bars)
                if (f0 < b.F1 - 1e-6 && b.F0 < f1 - 1e-6) lane = Math.Max(lane, b.Lane + 1);
            _bars.Add((o, lane, f0, f1));
            used = Math.Max(used, lane + 1);
        }
        int lanes = Math.Clamp(used, 1, MaxLanes);
        if (lanes != _lanes) { _lanes = lanes; InvalidateMeasure(); }
        if (SelectedSegment >= Project.Segments.Count) SelectedSegment = -1;
        if (SelectedOverlay != null && !Project.Overlays.Contains(SelectedOverlay)) SelectedOverlay = null;
        _ghostHead = _ghostTail = null;
        _lead = 0;
        if (Project.Segments.Count > 0)
        {
            var first = Project.Segments[0];
            if (first.In > 0.05)
            {
                _lead = first.In;
                _ghostHead = new Piece(-1, first.Clip, -1, -first.In, 0, 0, first.In);
            }
            var last = Project.Segments[^1];
            double rest = Project.Clips[last.Clip].Duration - last.Out;
            if (rest > 0.05) _ghostTail = new Piece(-1, last.Clip, -1, Total, Total + rest, last.Out, last.Out + rest);
        }
        if (Span > _viewLength) _viewLength = Span;
        if (_fit) FitZoom();
        if (nextFrame) { RequestContent(); return; }
        RenderContent();
        RenderHead();
    }

    // a drawing asked for on the next frame (a 1000 Hz mouse moves far more often than the screen shows anything)
    private bool _contentPending;

    /// <summary>
    /// What kind of thing an item is in the stack on the video: a banner lies on top of everything (0), captions,
    /// pictures and emoji under it (1), layers of the video at the bottom (2). Within a kind the order can be changed.
    /// </summary>
    internal static int Kind(EditOverlay o) => o is BannerOverlay ? 0 : o is VideoLayer ? 2 : 1;

    /// <summary>The items front first (what the top lane shows first), each with when it is on screen in the finished video.</summary>
    internal static List<(EditOverlay O, double F0, double F1)> Stack(EditProject p) =>
        p.Overlays.Select((o, i) => (O: o, I: i, S: p.Span(o)))
            .OrderBy(x => Kind(x.O)).ThenByDescending(x => x.I)
            .Select(x => (x.O, x.S.F0, x.S.F1)).ToList();

    /// <summary>Which lane an item's bar is in (0 = the top one, in front); -1 when it has no bar.</summary>
    internal int LaneOf(EditOverlay o)
    {
        foreach (var b in _bars)
            if (ReferenceEquals(b.O, o)) return b.Lane;
        return -1;
    }

    /// <summary>Where an item's bar is (for tests: where the mouse has to go).</summary>
    internal Rect BarOf(EditOverlay o)
    {
        foreach (var (item, lane, f0, f1) in _bars)
            if (ReferenceEquals(item, o)) return new Rect(X(f0), LanesTop + lane * (LaneH + LaneGap), Math.Max(6, X(f1) - X(f0)), LaneH);
        return Rect.Empty;
    }

    // ---- a bar dragged up or down: the item goes in front of / behind the others of its kind that share its time

    private int _askedLane = int.MinValue;
    private bool _reordered, _movedInTime;

    /// <summary>
    /// The dragged bar's item is to go to another lane. Up: in front of everything of its kind, on screen at the same
    /// time, from that lane down. Down: behind everything of its kind from that lane up. Asked once per lane the mouse
    /// enters - where the bar then lands follows from the new order.
    /// </summary>
    private void DragToLane(EditOverlay item, int lane)
    {
        if (Project == null || item is BannerOverlay) return;
        int cur = LaneOf(item);
        lane = Math.Clamp(lane, 0, _lanes);
        if (cur < 0 || lane == cur || lane == _askedLane) return;
        _askedLane = lane;
        var mine = _bars.First(b => ReferenceEquals(b.O, item));
        bool up = lane < cur;
        EditOverlay? pivot = null;
        int pivotIndex = up ? -1 : int.MaxValue;
        foreach (var b in _bars)
        {
            if (ReferenceEquals(b.O, item) || Kind(b.O) != Kind(item)) continue;
            if (!(mine.F0 < b.F1 - 1e-6 && b.F0 < mine.F1 - 1e-6)) continue;   // never on screen together: no order between them
            // only what the bar passes on its way: the lanes between where it is and where it goes
            if (up ? b.Lane < lane || b.Lane >= cur : b.Lane > lane || b.Lane <= cur) continue;
            int i = Project.Overlays.IndexOf(b.O);
            // up: the front-most of those passed; down: the back-most of those passed
            if (up ? i > pivotIndex : i < pivotIndex) { pivotIndex = i; pivot = b.O; }
        }
        if (pivot == null) return;
        _reordered = true;
        OverlayReordered?.Invoke(item, pivot, up);
    }

    private void RequestContent()
    {
        if (_contentPending) return;
        _contentPending = true;
        CompositionTarget.Rendering += DrawPending;
    }

    private void DrawPending(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= DrawPending;
        if (!_contentPending) return;
        _contentPending = false;
        RenderContent();
        RenderHead();
    }

    public double Position
    {
        get => _position;
        set
        {
            _position = Math.Max(0, value);
            // keep the playhead in view while playing
            double x = X(_position);
            if (!_fit && _drag == Drag.None && (x < Side || x > ActualWidth - Side))
            {
                _scroll = Math.Max(0, _position + _lead - (ActualWidth - 2 * Side) * 0.25 / _pps);
                RenderContent();
            }
            RenderHead();
        }
    }

    // ------------------------------------------------------------------ geometry

    private double Total => Math.Max(0.01, Project?.FinalLength ?? 1);
    /// <summary>The edit with its trimmed-off start and end around it.</summary>
    private double Span => _lead + Total + (_ghostTail is { } g ? g.F1 - g.F0 : 0);
    private double ViewLength => Math.Max(Span, _viewLength);
    private double WaveTop => TrackTop + TrackH + WaveGap;
    private double LanesTop => WaveTop + WaveH + 6;
    private double X(double f) => Side + (f + _lead - _scroll) * _pps;
    private double F(double x) => _scroll + (x - Side) / _pps - _lead;

    private void FitZoom()
    {
        _pps = Math.Max(0.0001, (ActualWidth - 2 * Side) / ViewLength);
        _scroll = 0;
    }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 600 : available.Width, LanesTop + _lanes * (LaneH + LaneGap) + 2);

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_fit) FitZoom();
        else _pps = Math.Max(_pps, (ActualWidth - 2 * Side) / ViewLength);
        RenderContent();
        RenderHead();
    }

    // ------------------------------------------------------------------ drawing

    private void RenderContent()
    {
        using var dc = _content.RenderOpen();
        double w = ActualWidth, h = ActualHeight;
        if (Project == null || w <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        DrawRuler(dc, w);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var track = new Rect(0, TrackTop, w, TrackH);

        // the trimmed-off start and end, dimmed
        foreach (var (ghost, head) in new[] { (_ghostHead, true), (_ghostTail, false) })
        {
            if (ghost is not { } gp) continue;
            double gl = X(gp.F0) + (head ? 0 : 1.5), gr = X(gp.F1) - (head ? 1.5 : 0);
            if (gr < 0 || gl > w || gr - gl < 0.5) continue;
            var rect = new Rect(gl, TrackTop, gr - gl, TrackH);
            dc.PushClip(RoundedRect(rect, head ? 7 : 0, head ? 0 : 7));
            dc.DrawRectangle(TrackBg, null, rect);
            DrawStrip(dc, gp, rect);
            dc.DrawRectangle(TrimDim, null, rect);
            dc.Pop();
            DrawWave(dc, gp, gl, gr, GhostWave);
        }

        int segStartPiece = 0;
        for (int i = 0; i < _pieces.Count; i++)
        {
            var pc = _pieces[i];
            double x0 = X(pc.F0), x1 = X(pc.F1);
            bool segFirst = i == 0 || _pieces[i - 1].Segment != pc.Segment;
            bool segLast = i == _pieces.Count - 1 || _pieces[i + 1].Segment != pc.Segment;
            if (segFirst) segStartPiece = i;
            // a hairline gap between cut pieces shows where the cuts are
            double l = x0 + (segFirst && i > 0 ? 1.5 : 0), r = x1 - (segLast && i < _pieces.Count - 1 ? 1.5 : 0);
            if (r < 0 || l > w || r - l < 0.5) continue;
            var rect = new Rect(l, TrackTop, r - l, TrackH);
            double rl = segFirst ? 7 : 0, rr = segLast ? 7 : 0;
            var clipGeo = RoundedRect(rect, rl, rr);
            dc.PushClip(clipGeo);
            dc.DrawRectangle(TrackBg, null, rect);
            DrawStrip(dc, pc, rect);
            if (pc.IsHold)
            {
                dc.DrawRectangle(HoldDim, null, rect);
                var pause = new FormattedText("❚❚", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Ui, 12, Brushes.White, dpi);
                if (rect.Width > pause.Width + 8) dc.DrawText(pause, new Point(rect.X + (rect.Width - pause.Width) / 2, rect.Y + (rect.Height - pause.Height) / 2));
            }
            dc.Pop();
            DrawWave(dc, pc, l, r);
            if (SelectedSegment == pc.Segment && segLast)
            {
                // the whole selected piece, with grab handles at both ends
                double sl = X(_pieces[segStartPiece].F0) + (segStartPiece > 0 ? 1.5 : 0);
                var sel = new Rect(sl, TrackTop, Math.Max(2, r - sl), TrackH);
                dc.DrawGeometry(null, SelPen, RoundedRect(sel, 7, 7));
                DrawHandle(dc, new Rect(sel.Left - 1, TrackTop, Handle, TrackH), left: true);
                DrawHandle(dc, new Rect(sel.Right - Handle + 1, TrackTop, Handle, TrackH), left: false);
            }
        }

        // the edge being trimmed: what will go is dimmed
        if (_drag is Drag.TrimStart or Drag.TrimEnd && _trimSeg >= 0)
        {
            var (f0, f1) = SegmentSpan(_trimSeg);
            double a = _drag == Drag.TrimStart ? X(f0) : X(_trimF), b = _drag == Drag.TrimStart ? X(_trimF) : X(f1);
            if (b > a) dc.DrawRectangle(TrimDim, null, new Rect(a, TrackTop, b - a, TrackH + WaveGap + WaveH));
            double hx = X(_trimF);
            DrawHandle(dc, new Rect(_drag == Drag.TrimStart ? hx - 1 : hx - Handle + 1, TrackTop, Handle, TrackH), _drag == Drag.TrimStart);
        }

        foreach (var (o, lane, f0, f1) in _bars)
        {
            if (lane >= MaxLanes) continue;
            double x0 = Math.Max(X(f0), -20), x1 = Math.Min(X(f1), w + 20);
            if (x1 < 0 || x0 > w) continue;
            var bar = new Rect(x0, LanesTop + lane * (LaneH + LaneGap), Math.Max(6, x1 - x0), LaneH);
            Brush fill = o switch { TextOverlay => TextBar, EmojiOverlay => EmojiBar, VideoLayer => LayerBar, ImageOverlay => ImageBar, _ => BannerBar };
            dc.DrawRoundedRectangle(fill, null, bar, 6, 6);
            if (o is BannerOverlay b)
            {
                // the frozen stretch inside the banner, darker
                foreach (var hd in Project.Holds.Where(x => ReferenceEquals(x.Banner, b)))
                {
                    double hx0 = X(Project.EToF(hd.E)), hx1 = X(Project.EToF(hd.E) + hd.Length);
                    dc.DrawRectangle(HoldDim, null, new Rect(Math.Max(bar.Left, hx0), bar.Top + 3, Math.Max(0, Math.Min(bar.Right, hx1) - Math.Max(bar.Left, hx0)), bar.Height - 6));
                }
            }
            dc.PushClip(new RectangleGeometry(bar));
            DrawBarLabel(dc, o, bar, dpi);
            dc.Pop();
            if (ReferenceEquals(o, SelectedOverlay))
            {
                dc.DrawRoundedRectangle(null, SelPen, bar, 6, 6);
                if (o is not BannerOverlay)
                {
                    dc.DrawRoundedRectangle(Brushes.White, null, new Rect(bar.Left + 2, bar.Top + 5, 3, bar.Height - 10), 1.5, 1.5);
                    dc.DrawRoundedRectangle(Brushes.White, null, new Rect(bar.Right - 5, bar.Top + 5, 3, bar.Height - 10), 1.5, 1.5);
                }
            }
        }
    }

    private static Geometry RoundedRect(Rect r, double left, double right)
    {
        if (left <= 0 && right <= 0) return new RectangleGeometry(r);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            left = Math.Min(left, r.Width / 2);
            right = Math.Min(right, r.Width / 2);
            c.BeginFigure(new Point(r.Left + left, r.Top), true, true);
            c.LineTo(new Point(r.Right - right, r.Top), false, false);
            if (right > 0) c.ArcTo(new Point(r.Right, r.Top + right), new Size(right, right), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Right, r.Bottom - right), false, false);
            if (right > 0) c.ArcTo(new Point(r.Right - right, r.Bottom), new Size(right, right), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Left + left, r.Bottom), false, false);
            if (left > 0) c.ArcTo(new Point(r.Left, r.Bottom - left), new Size(left, left), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Left, r.Top + left), false, false);
            if (left > 0) c.ArcTo(new Point(r.Left + left, r.Top), new Size(left, left), 0, false, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        return g;
    }

    private static void DrawHandle(DrawingContext dc, Rect r, bool left)
    {
        dc.DrawRoundedRectangle(Brushes.White, null, r, 4, 4);
        var p = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        double d = left ? 1.5 : -1.5;
        dc.DrawLine(p, new Point(cx + d, cy - 5), new Point(cx - d, cy));
        dc.DrawLine(p, new Point(cx - d, cy), new Point(cx + d, cy + 5));
    }

    private void DrawRuler(DrawingContext dc, double w)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800 };
        double step = steps.FirstOrDefault(s => s * _pps >= 70, 3600);
        double t0 = Math.Floor(F(0) / step) * step;
        // times only over the edit itself: the dimmed, trimmed-off parts around it don't play
        for (double t = Math.Max(0, t0); X(t) < w && t <= Total + 1e-6; t += step)
        {
            double x = Math.Round(X(t)) + 0.5;
            if (x < 0) continue;
            dc.DrawLine(Tick, new Point(x, RulerH - 5), new Point(x, RulerH));
            var ft = new FormattedText(Clock(t, step < 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Ui, 10.5, RulerText, dpi);
            dc.DrawText(ft, new Point(x + 3, 1));
            for (int k = 1; k < 4; k++)
            {
                double xs = Math.Round(X(t + step * k / 4)) + 0.5;
                if (xs < w && t + step * k / 4 <= Total + 1e-6) dc.DrawLine(Tick, new Point(xs, RulerH - 2), new Point(xs, RulerH));
            }
        }
    }

    public static string Clock(double t, bool tenths)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, t));
        string s = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
        return tenths ? s + "." + (ts.Milliseconds / 100).ToString(CultureInfo.InvariantCulture) : s;
    }

    private void DrawStrip(DrawingContext dc, Piece pc, Rect rect)
    {
        if (Project == null) return;
        var clip = Project.Clips[pc.Clip];
        if (!clip.HasVideo || !_thumbs.TryGetValue(pc.Clip, out var list) || list.Count == 0) return;
        double aspect = clip.Width / (double)Math.Max(1, clip.Height);
        double cw = Math.Clamp(TrackH * aspect, 24, 140);
        double x0 = X(pc.F0);
        // cells are aligned to the piece start so the strip doesn't "swim" when the view scrolls
        double first = x0 + Math.Floor((Math.Max(rect.Left, 0) - x0) / cw) * cw;
        for (double x = first; x < rect.Right && x < ActualWidth; x += cw)
        {
            double t = pc.IsHold ? pc.SrcIn : pc.SrcIn + (x + cw / 2 - x0) / _pps;
            var bmp = Thumbnail(pc.Clip, t);
            if (bmp == null) continue;
            double bw = TrackH * bmp.PixelWidth / (double)Math.Max(1, bmp.PixelHeight);
            dc.DrawImage(bmp, new Rect(x + (cw - bw) / 2, rect.Top, bw, TrackH));
        }
    }

    private void DrawWave(DrawingContext dc, Piece pc, double l, double r, Brush? brush = null)
    {
        if (Project == null || pc.IsHold || !_waves.TryGetValue(pc.Clip, out var peaks) || peaks.Length == 0) return;
        var clip = Project.Clips[pc.Clip];
        double perSec = peaks.Length / Math.Max(0.01, clip.Duration);
        double mid = WaveTop + WaveH / 2;
        brush ??= pc.Segment == SelectedSegment ? WaveSel : WaveBrush;
        double x0 = X(pc.F0);
        for (double x = Math.Max(l, 0); x < Math.Min(r, ActualWidth); x += 3)
        {
            double t0 = pc.SrcIn + (x - x0) / _pps, t1 = t0 + 3 / _pps;
            int a = Math.Clamp((int)(t0 * perSec), 0, peaks.Length - 1), b = Math.Clamp((int)Math.Ceiling(t1 * perSec), a + 1, peaks.Length);
            float v = 0;
            for (int i = a; i < b; i++) v = Math.Max(v, peaks[i]);
            double hgt = Math.Max(1.5, v * (WaveH - 2));
            dc.DrawRectangle(brush, null, new Rect(x, mid - hgt / 2, 2, hgt));
        }
    }

    private static void DrawBarLabel(DrawingContext dc, EditOverlay o, Rect bar, double dpi)
    {
        string label;
        Typeface face = Ui;
        Brush ink = Brushes.Black;
        switch (o)
        {
            case TextOverlay t:
                label = "T   " + (string.IsNullOrWhiteSpace(t.Text) ? "…" : t.Text.Replace('\n', ' '));
                break;
            case EmojiOverlay e:
                var img = AppleEmoji.Image(e.Unified);
                if (img != null)
                {
                    double s = bar.Height - 6;
                    for (double x = bar.Left + 6; x < bar.Right - s; x += s + 40) dc.DrawImage(img, new Rect(x, bar.Top + 3, s, s));
                    return;
                }
                label = string.Concat(e.Unified.Split('-').Select(h => char.ConvertFromUtf32(Convert.ToInt32(h, 16))));
                face = EmojiFace;
                break;
            case BannerOverlay b:
                label = Core.L.T("Edit.BannerBar") + "  " + Path.GetFileNameWithoutExtension(b.Asset.Path);
                ink = Brushes.White;
                break;
            case VideoLayer:
                label = "▶   " + Core.L.T("Edit.LayerBar");
                ink = Brushes.White;
                break;
            case ImageOverlay im:
            {
                // the picture itself, small, in front of its name
                double left = bar.Left + 6;
                var thumb = EditImages.Preview(im.Path);
                if (thumb != null && bar.Width > 40)
                {
                    double th = bar.Height - 6, tw = Math.Min(th * im.Aspect, 46);
                    dc.PushClip(new RectangleGeometry(new Rect(left, bar.Top + 3, tw, th), 3, 3));
                    dc.DrawImage(thumb, new Rect(left + (tw - th * im.Aspect) / 2, bar.Top + 3, th * im.Aspect, th));
                    dc.Pop();
                    left += tw + 6;
                }
                var name = new FormattedText(Path.GetFileNameWithoutExtension(im.Path), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Ui, 11.5, Brushes.Black, dpi)
                    { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(10, bar.Right - left - 6) };
                if (bar.Right - left > 24) dc.DrawText(name, new Point(left, bar.Top + (bar.Height - name.Height) / 2));
                return;
            }
            default:
                return;
        }
        var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 11.5, ink, dpi) { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(10, bar.Width - 14) };
        dc.DrawText(ft, new Point(bar.Left + 8, bar.Top + (bar.Height - ft.Height) / 2));
    }

    private void RenderHead()
    {
        using var dc = _head.RenderOpen();
        if (Project == null || ActualWidth <= 0) return;
        double x = Math.Round(X(_position) * 2) / 2;
        if (x < -2 || x > ActualWidth + 2) return;
        dc.DrawLine(HeadShadow, new Point(x, RulerH - 2), new Point(x, ActualHeight));
        dc.DrawRectangle(Brushes.White, null, new Rect(x - 1, RulerH - 2, 2, ActualHeight - RulerH + 2));
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(x - 5, RulerH - 8, 10, 8), 3, 3);
    }

    // ------------------------------------------------------------------ mouse

    private (double F0, double F1) SegmentSpan(int seg)
    {
        double a = double.MaxValue, b = 0;
        foreach (var p in _pieces)
            if (p.Segment == seg) { a = Math.Min(a, p.F0); b = Math.Max(b, p.F1); }
        return (a == double.MaxValue ? 0 : a, b);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Project == null) return;
        CaptureMouse();
        e.Handled = true;
        PressAt(e.GetPosition(this));
    }

    /// <summary>The left button went down at a point of the time line.</summary>
    internal void PressAt(Point p)
    {
        if (Project == null) return;
        double f = Math.Clamp(F(p.X), 0, Total);
        _pressF = f;
        _askedLane = int.MinValue;
        _reordered = _movedInTime = false;

        // lanes: captions, emoji, banners
        if (p.Y >= LanesTop)
        {
            foreach (var (o, lane, f0, f1) in _bars.AsEnumerable().Reverse())
            {
                var bar = new Rect(X(f0), LanesTop + lane * (LaneH + LaneGap), Math.Max(6, X(f1) - X(f0)), LaneH);
                if (!bar.Contains(p)) continue;
                SelectedOverlay = o;
                SelectedSegment = -1;
                OverlaySelected?.Invoke(o);
                _dragItem = o;
                _barF0 = f0;
                _barF1 = f1;
                bool edges = o is not BannerOverlay && bar.Width > 24;
                _drag = edges && p.X < bar.Left + 9 ? Drag.BarStart : edges && p.X > bar.Right - 9 ? Drag.BarEnd : Drag.Bar;
                RenderContent();
                return;
            }
        }

        // film strip: select a piece, or grab its edge
        if (p.Y >= TrackTop && p.Y <= TrackTop + TrackH)
        {
            if (SelectedSegment >= 0)
            {
                var (s0, s1) = SegmentSpan(SelectedSegment);
                if (Math.Abs(p.X - X(s0)) <= Handle) { _drag = Drag.TrimStart; _trimSeg = SelectedSegment; _trimF = s0; return; }
                if (Math.Abs(p.X - X(s1)) <= Handle) { _drag = Drag.TrimEnd; _trimSeg = SelectedSegment; _trimF = s1; return; }
            }
            var pc = _pieces.FirstOrDefault(x => f >= x.F0 && f < x.F1);
            if (pc.F1 > pc.F0)
            {
                if (pc.IsHold && pc.Hold < Project.Holds.Count)
                {
                    SelectedOverlay = Project.Holds[pc.Hold].Banner;
                    SelectedSegment = -1;
                    OverlaySelected?.Invoke(SelectedOverlay);
                }
                else
                {
                    SelectedSegment = pc.Segment;
                    SelectedOverlay = null;
                    SegmentSelected?.Invoke(pc.Segment);
                }
                RenderContent();
            }
        }
        else if (SelectedSegment >= 0 || SelectedOverlay != null)
        {
            // a click on the ruler/waveform/empty lane drops the selection
            SelectedSegment = -1;
            SelectedOverlay = null;
            SegmentSelected?.Invoke(-1);
            OverlaySelected?.Invoke(null);
            RenderContent();
        }
        _drag = Drag.Seek;
        _seekF = f;
        Seek?.Invoke(f, false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MoveTo(e.GetPosition(this));
    }

    /// <summary>The mouse is at a point of the time line: what is held follows it.</summary>
    internal void MoveTo(Point p)
    {
        if (Project == null) return;
        double f = F(p.X);
        switch (_drag)
        {
            case Drag.None:
                Cursor = HoverCursor(p);
                return;
            case Drag.Seek:
                f = Math.Clamp(f, 0, Total);
                if (f == _seekF) return; // only across: up and down asks for nothing new
                _seekF = f;
                Seek?.Invoke(f, false);
                return;
            case Drag.Bar:
            case Drag.BarStart:
            case Drag.BarEnd:
                if (_dragItem == null) return;
                double d = f - _pressF;
                double a = _barF0, b = _barF1;
                if (_drag == Drag.Bar) { d = Math.Clamp(d, -a, Total - b); a += d; b += d; }
                else if (_drag == Drag.BarStart) a = Math.Clamp(a + d, 0, b - 0.2);
                else b = Math.Clamp(b + d, a + 0.2, Total);
                // across: its time (only once the hand has really moved across - a bar pulled straight up or down keeps its place)
                if (Math.Abs(d) * _pps > 2 || _movedInTime)
                {
                    _movedInTime = true;
                    OverlayMoved?.Invoke(_dragItem, a, b, false);
                }
                // up or down, into another lane: its place in the stack
                if (_drag == Drag.Bar) DragToLane(_dragItem, (int)Math.Floor((p.Y - LanesTop) / (LaneH + LaneGap)));
                return;
            case Drag.TrimStart:
            case Drag.TrimEnd:
                var (s0, s1) = SegmentSpan(_trimSeg);
                var seg = Project.Segments[_trimSeg];
                var clip = Project.Clips[seg.Clip];
                // the edge may go out over what was cut away, up to the clip's own ends
                double lo = _drag == Drag.TrimStart ? s0 - seg.In : s0 + EditProject.MinSegment;
                double hi = _drag == Drag.TrimStart ? s1 - EditProject.MinSegment : s1 + (clip.Duration - seg.Out);
                _trimF = Math.Clamp(f, lo, hi);
                double t = _drag == Drag.TrimStart ? seg.In + (_trimF - s0) : seg.Out + (_trimF - s1);
                SegmentTrimmed?.Invoke(_trimSeg, _drag == Drag.TrimStart, t, false);
                RequestContent();
                return;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Project == null) return;
        ReleaseAt(e.GetPosition(this));
        ReleaseMouseCapture();
    }

    /// <summary>The left button came up at a point of the time line.</summary>
    internal void ReleaseAt(Point p)
    {
        if (Project == null) return;
        double f = F(p.X);
        var drag = _drag;
        _drag = Drag.None;
        switch (drag)
        {
            case Drag.Seek: Seek?.Invoke(Math.Clamp(f, 0, Total), true); break;
            case Drag.Bar:
            case Drag.BarStart:
            case Drag.BarEnd:
                if (_dragItem != null)
                {
                    double d = f - _pressF, a = _barF0, b = _barF1;
                    if (drag == Drag.Bar) { d = Math.Clamp(d, -a, Total - b); a += d; b += d; }
                    else if (drag == Drag.BarStart) a = Math.Clamp(a + d, 0, b - 0.2);
                    else b = Math.Clamp(b + d, a + 0.2, Total);
                    if (Math.Abs(f - _pressF) * _pps > 2 || _movedInTime) OverlayMoved?.Invoke(_dragItem, a, b, true);
                    else if (_reordered) ReorderEnded?.Invoke(); // only moved to another lane: its time is not touched
                    else Seek?.Invoke(Math.Clamp(f, 0, Total), true); // a click on a bar also moves the playhead there
                }
                _dragItem = null;
                _movedInTime = _reordered = false;
                break;
            case Drag.TrimStart:
            case Drag.TrimEnd:
                if (_trimSeg >= 0 && _trimSeg < Project.Segments.Count)
                {
                    var (s0, s1) = SegmentSpan(_trimSeg);
                    var seg = Project.Segments[_trimSeg];
                    double t = drag == Drag.TrimStart ? seg.In + (_trimF - s0) : seg.Out + (_trimF - s1);
                    SegmentTrimmed?.Invoke(_trimSeg, drag == Drag.TrimStart, t, true);
                }
                _trimSeg = -1;
                break;
        }
        RenderContent();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is Drag.TrimStart or Drag.TrimEnd) { _drag = Drag.None; _trimSeg = -1; RenderContent(); }
        else if (_drag == Drag.Seek)
        {
            // e.g. another window came up mid-drag: the playhead stays where it is and the preview goes on from there
            _drag = Drag.None;
            Seek?.Invoke(Math.Clamp(_seekF, 0, Total), true);
        }
        else _drag = Drag.None;
    }

    private Cursor HoverCursor(Point p)
    {
        if (p.Y >= TrackTop && p.Y <= TrackTop + TrackH && SelectedSegment >= 0)
        {
            var (s0, s1) = SegmentSpan(SelectedSegment);
            if (Math.Abs(p.X - X(s0)) <= Handle || Math.Abs(p.X - X(s1)) <= Handle) return Cursors.SizeWE;
        }
        if (p.Y >= LanesTop)
            foreach (var (o, lane, f0, f1) in _bars)
            {
                var bar = new Rect(X(f0), LanesTop + lane * (LaneH + LaneGap), Math.Max(6, X(f1) - X(f0)), LaneH);
                if (!bar.Contains(p)) continue;
                if (o is not BannerOverlay && bar.Width > 24 && (p.X < bar.Left + 9 || p.X > bar.Right - 9)) return Cursors.SizeWE;
                return Cursors.SizeAll;
            }
        return Cursors.Hand;
    }

    /// <summary>Ctrl + wheel zooms the time line around the cursor; the wheel alone scrolls a zoomed-in one.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Project == null) return;
        e.Handled = true;
        double x = e.GetPosition(this).X;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _fit && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            double f = F(x);
            double fitPps = (ActualWidth - 2 * Side) / ViewLength;
            _pps = Math.Clamp(_pps * Math.Pow(1.0016, e.Delta), fitPps, 400);
            _fit = _pps <= fitPps * 1.0001;
            _scroll = _fit ? 0 : Math.Clamp(f + _lead - (x - Side) / _pps, 0, Math.Max(0, ViewLength - (ActualWidth - 2 * Side) / _pps));
        }
        else
        {
            double visible = (ActualWidth - 2 * Side) / _pps;
            _scroll = Math.Clamp(_scroll - e.Delta / 120.0 * visible * 0.15, 0, Math.Max(0, ViewLength - visible));
        }
        RenderContent();
        RenderHead();
    }
}
