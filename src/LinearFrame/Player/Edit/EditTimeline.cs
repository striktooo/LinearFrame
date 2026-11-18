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
    private static readonly Brush TrimDim = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x12)));
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

    public void Select(int segment, EditOverlay? overlay)
    {
        SelectedSegment = segment;
        SelectedOverlay = overlay;
        RenderContent();
    }

    /// <summary>After any change of the edit: lays out pieces and lanes again.</summary>
    public void Rebuild()
    {
        if (Project == null) return;
        _pieces = Project.Pieces();
        _bars.Clear();
        var laneEnds = new List<double>();
        foreach (var o in Project.Overlays.Select(o => (O: o, S: Project.Span(o))).OrderBy(x => x.S.F0))
        {
            int lane = laneEnds.FindIndex(e => e <= o.S.F0 + 1e-6);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(0); }
            laneEnds[lane] = o.S.F1;
            _bars.Add((o.O, lane, o.S.F0, o.S.F1));
        }
        int lanes = Math.Clamp(laneEnds.Count, 1, 4);
        if (lanes != _lanes) { _lanes = lanes; InvalidateMeasure(); }
        if (SelectedSegment >= Project.Segments.Count) SelectedSegment = -1;
        if (SelectedOverlay != null && !Project.Overlays.Contains(SelectedOverlay)) SelectedOverlay = null;
        if (_fit) FitZoom();
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
                _scroll = Math.Max(0, _position - (ActualWidth - 2 * Side) * 0.25 / _pps);
                RenderContent();
            }
            RenderHead();
        }
    }

    // ------------------------------------------------------------------ geometry

    private double Total => Math.Max(0.01, Project?.FinalLength ?? 1);
    private double WaveTop => TrackTop + TrackH + WaveGap;
    private double LanesTop => WaveTop + WaveH + 6;
    private double X(double f) => Side + (f - _scroll) * _pps;
    private double F(double x) => _scroll + (x - Side) / _pps;

    private void FitZoom()
    {
        _pps = Math.Max(0.0001, (ActualWidth - 2 * Side) / Total);
        _scroll = 0;
    }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 600 : available.Width, LanesTop + _lanes * (LaneH + LaneGap) + 2);

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_fit) FitZoom();
        else _pps = Math.Max(_pps, (ActualWidth - 2 * Side) / Total);
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
            if (lane >= 4) continue;
            double x0 = Math.Max(X(f0), -20), x1 = Math.Min(X(f1), w + 20);
            if (x1 < 0 || x0 > w) continue;
            var bar = new Rect(x0, LanesTop + lane * (LaneH + LaneGap), Math.Max(6, x1 - x0), LaneH);
            Brush fill = o switch { TextOverlay => TextBar, EmojiOverlay => EmojiBar, _ => BannerBar };
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
        for (double t = Math.Max(0, t0); X(t) < w; t += step)
        {
            double x = Math.Round(X(t)) + 0.5;
            if (x < 0) continue;
            dc.DrawLine(Tick, new Point(x, RulerH - 5), new Point(x, RulerH));
            var ft = new FormattedText(Clock(t, step < 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Ui, 10.5, RulerText, dpi);
            dc.DrawText(ft, new Point(x + 3, 1));
            for (int k = 1; k < 4; k++)
            {
                double xs = Math.Round(X(t + step * k / 4)) + 0.5;
                if (xs < w) dc.DrawLine(Tick, new Point(xs, RulerH - 2), new Point(xs, RulerH));
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

    private void DrawWave(DrawingContext dc, Piece pc, double l, double r)
    {
        if (Project == null || pc.IsHold || !_waves.TryGetValue(pc.Clip, out var peaks) || peaks.Length == 0) return;
        var clip = Project.Clips[pc.Clip];
        double perSec = peaks.Length / Math.Max(0.01, clip.Duration);
        double mid = WaveTop + WaveH / 2;
        var brush = pc.Segment == SelectedSegment ? WaveSel : WaveBrush;
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
        var shadow = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 4);
        dc.DrawLine(shadow, new Point(x, RulerH - 2), new Point(x, ActualHeight));
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
        var p = e.GetPosition(this);
        double f = Math.Clamp(F(p.X), 0, Total);
        CaptureMouse();
        e.Handled = true;
        _pressF = f;

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
        Seek?.Invoke(f, false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Project == null) return;
        var p = e.GetPosition(this);
        double f = F(p.X);
        switch (_drag)
        {
            case Drag.None:
                Cursor = HoverCursor(p);
                return;
            case Drag.Seek:
                Seek?.Invoke(Math.Clamp(f, 0, Total), false);
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
                OverlayMoved?.Invoke(_dragItem, a, b, false);
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
                RenderContent();
                return;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Project == null) return;
        var p = e.GetPosition(this);
        double f = F(p.X);
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
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
                    if (Math.Abs(f - _pressF) * _pps > 2) OverlayMoved?.Invoke(_dragItem, a, b, true);
                    else Seek?.Invoke(Math.Clamp(f, 0, Total), true); // a click on a bar also moves the playhead there
                }
                _dragItem = null;
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
            double fitPps = (ActualWidth - 2 * Side) / Total;
            _pps = Math.Clamp(_pps * Math.Pow(1.0016, e.Delta), fitPps, 400);
            _fit = _pps <= fitPps * 1.0001;
            _scroll = _fit ? 0 : Math.Clamp(f - (x - Side) / _pps, 0, Math.Max(0, Total - (ActualWidth - 2 * Side) / _pps));
        }
        else
        {
            double visible = (ActualWidth - 2 * Side) / _pps;
            _scroll = Math.Clamp(_scroll - e.Delta / 120.0 * visible * 0.15, 0, Math.Max(0, Total - visible));
        }
        RenderContent();
        RenderHead();
    }
}
