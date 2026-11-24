using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Imaging;

namespace LinearFrame.Player.Edit;

/// <summary>Pictures laid on a video, read once: a screen-sized copy for the preview, every pixel for the finished file.</summary>
internal static class EditImages
{
    private const int PreviewSide = 1600;
    private static readonly Dictionary<string, BitmapSource?> Previews = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture's size as it is shown, or null when the file is not a picture that can be read.</summary>
    public static (int Width, int Height)? Size(string path)
    {
        var info = ImageIO.Probe(path);
        return info == null || info.ShownWidth < 1 || info.ShownHeight < 1 ? null : (info.ShownWidth, info.ShownHeight);
    }

    public static BitmapSource? Preview(string path)
    {
        if (Previews.TryGetValue(path, out var bmp)) return bmp;
        return Previews[path] = Read(path, PreviewSide);
    }

    public static BitmapSource? Full(string path) => Read(path, 0);

    /// <summary>The editor was closed: its pictures are let go.</summary>
    public static void Clear() => Previews.Clear();

    private static BitmapSource? Read(string path, int maxSide)
    {
        try
        {
            var info = ImageIO.Probe(path);
            return info == null ? null : ImageIO.Decode(path, info, maxSide);
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Picture {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Lies over the video in the player's overlay while editing: draws captions, pictures and emoji exactly as they will be
/// in the finished video, and lets you pick them up - drag to move (snaps to the middle), the corner handle or the wheel
/// to resize, the × to delete, a double-click on a caption to type.
/// A video layer (and the video itself with the Format tool open) is a frame with the video in it: dragging inside moves
/// the video in its frame, the wheel zooms it at the cursor, the frame's handles change what part of the canvas it takes
/// and the knob under its top edge moves the whole layer.
/// </summary>
public sealed class EditCanvasLayer : FrameworkElement
{
    private static readonly Pen SelPen = Frozen(new Pen(Brushes.White, 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
    private static readonly Pen FramePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255)), 1));
    private static readonly Pen LayerPen = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Pen LayerShade = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 3.5));
    private static readonly Pen OtherPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) });
    private static readonly Pen GuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x8E)), 1));
    private static readonly Brush KnobFill = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)));
    private static readonly Pen KnobEdge = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), 1.2));
    private const double Snap = 7, Knob = 11, Grip = 5, GripHit = 10, MoveKnobY = 24;
    private const int Left = 1, Top = 2, Right = 4, Bottom = 8;

    private EditProject? _p;
    private Rect _canvas;
    private double _f;
    private EditOverlay? _sel;
    private bool _format;
    private readonly Dictionary<Guid, (string Key, Captions.Layout Layout)> _layouts = new();
    private HashSet<Guid> _shown = new();

    private enum Drag { None, Move, Resize, Pan, Frame, FrameMove }
    private Drag _drag;
    private Point _press;
    private double _startX, _startY, _startSize, _startDist;
    private bool _guideX, _guideY, _moved;
    // the video layer being worked on while the mouse is down, its frame at the press and which edges are held
    private IVideoPlacement? _video;
    private Rect _startFrame, _guideBox;
    private int _edges;

    public event Action<EditOverlay?>? Selected;
    public event Action<EditOverlay, double, double, bool>? Moved;
    public event Action<EditOverlay, double, bool>? Resized;
    public event Action<EditOverlay>? DeleteRequested;
    public event Action<TextOverlay>? EditTextRequested;
    /// <summary>The video was dragged inside a layer's frame: its new centre in that frame; true = let go.</summary>
    public event Action<IVideoPlacement, double, double, bool>? VideoPanned;
    /// <summary>The wheel over a layer: zoom by this factor, the point under the cursor (canvas, 0..1) stays where it is.</summary>
    public event Action<IVideoPlacement, double, Point>? VideoZoomed;
    /// <summary>
    /// A layer's frame (canvas, 0..1) was changed: an edge dragged (the video stays where it is on the canvas) or the
    /// whole layer moved (the video goes along); the last flag = let go.
    /// </summary>
    public event Action<IVideoPlacement, Rect, bool, bool>? FrameChanged;
    /// <summary>The video itself was clicked while layers share the picture: it is the one being framed now.</summary>
    public event Action? VideoSelected;

    public EditCanvasLayer()
    {
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => AppleEmoji.ImagesChanged += OnImages;
        Unloaded += (_, _) => AppleEmoji.ImagesChanged -= OnImages;
    }

    private void OnImages()
    {
        _layouts.Clear();
        InvalidateVisual();
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public Rect Canvas => _canvas;

    // ---- the layers drawn here, from a still of the clip's frame, while one of them is being dragged
    private BitmapSource? _still, _stillTiny;
    private EditClip? _stillClip;
    private bool _stillOn;
    private static readonly Brush StillDim = Frozen(new SolidColorBrush(Color.FromArgb(0x10, 0, 0, 0)));

    /// <summary>
    /// A paused player needs a moment for every change of a layer (it decodes its frame again and runs the filter) -
    /// too slow to follow a hand. So while a layer is dragged, zoomed or reframed, the layers are drawn right here from
    /// <paramref name="still"/>, the clip's frame under the playhead, with every move of the mouse; the player catches
    /// up underneath and takes over again when the hand lets go (<paramref name="show"/> = false).
    /// </summary>
    public void SetStill(BitmapSource? still, EditClip? clip, bool show)
    {
        show &= still != null && clip != null;
        if (ReferenceEquals(still, _still) && show == _stillOn) return;
        if (!ReferenceEquals(still, _still))
        {
            _still = still;
            _stillClip = clip;
            _stillTiny = null;
            if (still != null)
            {
                // a few pixels of it: stretched over the canvas they are the blurred background
                double k = 20.0 / Math.Max(1, still.PixelWidth);
                var tiny = new TransformedBitmap(still, new ScaleTransform(k, k));
                tiny.Freeze();
                _stillTiny = tiny;
            }
        }
        _stillOn = show;
        // scaling a full frame with the best filter on every mouse move is what would stutter: plain while it moves
        RenderOptions.SetBitmapScalingMode(this, show ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality);
        InvalidateVisual();
    }

    public bool ShowsStill => _stillOn;

    private void DrawStill(DrawingContext dc)
    {
        if (_p == null || _still == null || _stillClip == null) return;
        dc.DrawRectangle(Brushes.Black, null, _canvas);
        if (_p.Canvas.Background == CanvasBackground.Blur && _stillTiny != null)
        {
            double a = _still.PixelWidth / (double)Math.Max(1, _still.PixelHeight);
            double w = Math.Max(_canvas.Width, _canvas.Height * a), h = w / a;
            dc.DrawImage(_stillTiny, new Rect(_canvas.X + (_canvas.Width - w) / 2, _canvas.Y + (_canvas.Height - h) / 2, w, h));
            dc.DrawRectangle(StillDim, null, _canvas);
        }
        foreach (var l in _p.VideoLayers())
        {
            if (l is VideoLayer vl && !_p.VisibleAt(vl, _f)) continue;
            var frame = Rect.Intersect(FrameOf(l), _canvas);
            if (frame.IsEmpty || frame.Width < 1 || frame.Height < 1) continue;
            var v = EditProject.VideoRect(_stillClip, l, _canvas.Width, _canvas.Height);
            v.Offset(_canvas.X, _canvas.Y);
            dc.PushClip(new RectangleGeometry(frame));
            if (l.Flip) dc.PushTransform(new ScaleTransform(-1, 1, v.X + v.Width / 2, v.Y + v.Height / 2));
            dc.DrawImage(_still, v);
            if (l.Flip) dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>New state from the editor. Redraws only when something visible changed.</summary>
    public void Update(EditProject p, Rect canvas, double f, EditOverlay? selected, bool formatMode, bool force = false)
    {
        bool layoutChanged = canvas != _canvas || !ReferenceEquals(p, _p);
        _p = p;
        _canvas = canvas;
        _f = f;
        // called on every frame while playing: no new sets, only a redraw when what is on screen changes
        _shownNow.Clear();
        foreach (var o in p.Overlays)
            if (o is not BannerOverlay && (p.VisibleAt(o, f) || ReferenceEquals(o, selected))) _shownNow.Add(o.Id);
        bool changed = force || layoutChanged || !ReferenceEquals(selected, _sel) || formatMode != _format || !_shownNow.SetEquals(_shown);
        _sel = selected;
        _format = formatMode;
        (_shown, _shownNow) = (_shownNow, _shown);
        if (changed) InvalidateVisual();
    }

    private HashSet<Guid> _shownNow = new();

    private double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private Captions.Layout LayoutOf(TextOverlay t)
    {
        string key = FormattableString.Invariant($"{Captions.LookKey(t)}|{_canvas.Width:0.0}|{_canvas.Height:0.0}");
        if (_layouts.TryGetValue(t.Id, out var c) && c.Key == key) return c.Layout;
        var l = Captions.Arrange(t, _canvas.Width, _canvas.Height, Ppd);
        _layouts[t.Id] = (key, l);
        return l;
    }

    /// <summary>Where an overlay is on screen (layer coordinates).</summary>
    public Rect ItemRect(EditOverlay o)
    {
        var c = new Point(_canvas.X + o.X * _canvas.Width, _canvas.Y + o.Y * _canvas.Height);
        switch (o)
        {
            case TextOverlay t:
                var b = LayoutOf(t).Bounds;
                return new Rect(c.X + b.X, c.Y + b.Y, b.Width, b.Height);
            case EmojiOverlay:
                double s = o.Size * _canvas.Height;
                return new Rect(c.X - s / 2, c.Y - s / 2, s, s);
            case ImageOverlay im:
                double ih = o.Size * _canvas.Height, iw = ih * im.Aspect;
                return new Rect(c.X - iw / 2, c.Y - ih / 2, iw, ih);
            case BannerOverlay bn:
                double w = o.Size * _canvas.Width, h = w * bn.Asset.Box.Height / Math.Max(1.0, bn.Asset.Box.Width);
                return new Rect(c.X - w / 2, c.Y - h / 2, w, h);
            case VideoLayer vl:
                return FrameOf(vl);
        }
        return Rect.Empty;
    }

    // ------------------------------------------------------------------ video layers

    /// <summary>The layer whose frame and video are being set: a selected video layer, or the video itself (Format tool).</summary>
    private IVideoPlacement? Active => _p == null ? null : _sel is VideoLayer vl && _p.Overlays.Contains(vl) ? vl : _format && _p.FirstVideo.HasVideo ? _p.Canvas : null;

    private bool HasLayers => _p != null && _p.Overlays.Any(o => o is VideoLayer);

    /// <summary>The video's own frame gets handles once it shares the canvas (or no longer takes all of it).</summary>
    private bool FrameHandles(IVideoPlacement l) => l is VideoLayer || HasLayers || _p is { } p && !p.Canvas.WholeFrame;

    private Rect FrameOf(IVideoPlacement l)
    {
        var r = EditProject.FrameRect(l, _canvas.Width, _canvas.Height);
        r.Offset(_canvas.X, _canvas.Y);
        return r;
    }

    private static Point[] Grips(Rect r) => new[]
    {
        r.TopLeft, new Point(r.X + r.Width / 2, r.Top), r.TopRight, new Point(r.Right, r.Y + r.Height / 2),
        r.BottomRight, new Point(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.Left, r.Y + r.Height / 2),
    };

    private static readonly int[] GripEdges = { Left | Top, Top, Right | Top, Right, Right | Bottom, Bottom, Left | Bottom, Left };

    private static Point MoveKnob(Rect r) => new(r.X + r.Width / 2, r.Top + Math.Min(MoveKnobY, r.Height / 2));

    /// <summary>The frame's handle under a point: its edges, 0 for the move knob, -1 for none.</summary>
    private int HitGrip(Point p, Rect frame)
    {
        if ((p - MoveKnob(frame)).Length <= Knob + 2) return 0;
        var g = Grips(frame);
        for (int i = 0; i < g.Length; i++)
            if (Math.Abs(p.X - g[i].X) <= GripHit && Math.Abs(p.Y - g[i].Y) <= GripHit) return GripEdges[i];
        return -1;
    }

    /// <summary>The topmost video under a point: a layer on screen now, else the video itself.</summary>
    private IVideoPlacement? HitVideo(Point p)
    {
        if (_p == null || !_p.FirstVideo.HasVideo || !_canvas.Contains(p)) return null;
        foreach (var o in _p.Overlays.AsEnumerable().Reverse())
            if (o is VideoLayer vl && _shown.Contains(vl.Id) && FrameOf(vl).Contains(p)) return vl;
        return FrameOf(_p.Canvas).Contains(p) ? _p.Canvas : null;
    }

    // ------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_p == null || _canvas.Width <= 0) return;
        dc.PushClip(new RectangleGeometry(_canvas));
        if (_stillOn) DrawStill(dc);
        foreach (var o in _p.Overlays)
        {
            if (o is BannerOverlay or VideoLayer || !_shown.Contains(o.Id)) continue;
            bool live = _p.VisibleAt(o, _f);
            if (!live) dc.PushOpacity(0.45); // selected, but not on screen at this moment
            var centre = new Point(_canvas.X + o.X * _canvas.Width, _canvas.Y + o.Y * _canvas.Height);
            if (o.Flip) dc.PushTransform(new ScaleTransform(-1, 1, centre.X, centre.Y));
            if (o is TextOverlay t) Captions.Draw(dc, LayoutOf(t), centre);
            else if (o is EmojiOverlay e)
            {
                var img = AppleEmoji.Image(e.Unified);
                if (img != null) dc.DrawImage(img, ItemRect(e));
            }
            else if (o is ImageOverlay im)
            {
                var img = EditImages.Preview(im.Path);
                if (img != null) dc.DrawImage(img, ItemRect(im));
            }
            if (o.Flip) dc.Pop();
            if (!live) dc.Pop();
        }
        dc.Pop();

        var active = Active;
        if (active != null && (HasLayers || !_p.Canvas.WholeFrame))
        {
            // the other frames, faintly: where the picture is shared out
            foreach (var l in _p.VideoLayers())
                if (!ReferenceEquals(l, active) && (l is not VideoLayer vl || _shown.Contains(vl.Id))) dc.DrawRectangle(null, OtherPen, Inset(FrameOf(l)));
        }
        if (_format || _drag == Drag.Pan) dc.DrawRectangle(null, FramePen, new Rect(_canvas.X + 0.5, _canvas.Y + 0.5, _canvas.Width - 1, _canvas.Height - 1));
        if (active != null && FrameHandles(active))
        {
            var r = FrameOf(active);
            dc.DrawRectangle(null, LayerShade, Inset(r));
            dc.DrawRectangle(null, LayerPen, Inset(r));
            foreach (var g in Grips(r)) dc.DrawRoundedRectangle(Brushes.White, KnobEdge, new Rect(g.X - Grip, g.Y - Grip, 2 * Grip, 2 * Grip), 2.5, 2.5);
            // the knob that moves the whole layer: four arrows
            var k = MoveKnob(r);
            dc.DrawEllipse(Brushes.White, KnobEdge, k, Knob, Knob);
            var a = new Pen(KnobFill, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawLine(a, new Point(k.X - 6, k.Y), new Point(k.X + 6, k.Y));
            dc.DrawLine(a, new Point(k.X, k.Y - 6), new Point(k.X, k.Y + 6));
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var tip = new Point(k.X + 6 * dx, k.Y + 6 * dy);
                dc.DrawLine(a, tip, new Point(tip.X - 2.2 * dx - 2.2 * dy, tip.Y - 2.2 * dy - 2.2 * dx));
                dc.DrawLine(a, tip, new Point(tip.X - 2.2 * dx + 2.2 * dy, tip.Y - 2.2 * dy + 2.2 * dx));
            }
        }
        var gb = _guideBox.IsEmpty ? _canvas : _guideBox;
        if (_guideX) dc.DrawLine(GuidePen, new Point(gb.X + gb.Width / 2, gb.Top), new Point(gb.X + gb.Width / 2, gb.Bottom));
        if (_guideY) dc.DrawLine(GuidePen, new Point(gb.Left, gb.Y + gb.Height / 2), new Point(gb.Right, gb.Y + gb.Height / 2));

        if (_sel != null && _sel is not VideoLayer && _p.Overlays.Contains(_sel))
        {
            var r = ItemRect(_sel);
            r.Inflate(4, 4);
            dc.DrawRectangle(null, SelPen, r);
            // × top-left (delete), ↘ bottom-right (resize)
            dc.DrawEllipse(KnobFill, new Pen(Brushes.White, 1.2), r.TopLeft, Knob, Knob);
            var x = new Pen(Brushes.White, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(x, new Point(r.Left - 3.5, r.Top - 3.5), new Point(r.Left + 3.5, r.Top + 3.5));
            dc.DrawLine(x, new Point(r.Left + 3.5, r.Top - 3.5), new Point(r.Left - 3.5, r.Top + 3.5));
            dc.DrawEllipse(Brushes.White, new Pen(KnobFill, 1.2), r.BottomRight, Knob, Knob);
            var a = new Pen(KnobFill, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(a, new Point(r.Right - 4, r.Bottom - 4), new Point(r.Right + 4, r.Bottom + 4));
            dc.DrawLine(a, new Point(r.Right + 4, r.Bottom + 4), new Point(r.Right + 4, r.Bottom - 0.5));
            dc.DrawLine(a, new Point(r.Right + 4, r.Bottom + 4), new Point(r.Right + 0.5, r.Bottom + 4));
        }
    }

    private static Rect Inset(Rect r) => r.Width > 2 && r.Height > 2 ? new Rect(r.X + 0.75, r.Y + 0.75, r.Width - 1.5, r.Height - 1.5) : r;

    // ------------------------------------------------------------------ mouse

    private EditOverlay? HitItem(Point p)
    {
        if (_p == null) return null;
        // topmost first: a banner lies on top of everything, then captions, pictures and emoji, the last in the list in
        // front (video layers are frames, see HitVideo)
        foreach (var o in _p.Overlays.AsEnumerable().Reverse().OrderBy(o => o is BannerOverlay ? 0 : 1))
        {
            if (o is VideoLayer) continue;
            bool visible = o is BannerOverlay ? _p.VisibleAt(o, _f) : _shown.Contains(o.Id);
            if (!visible && !ReferenceEquals(o, _sel)) continue;
            var r = ItemRect(o);
            r.Inflate(4, 4);
            if (r.Contains(p)) return o;
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // not taken here: the press goes on to the player (dragging the picture moves the window)
        if (!PressAt(e.GetPosition(this), e.ClickCount)) return;
        if (_drag != Drag.None) CaptureMouse();
        e.Handled = true;
    }

    /// <summary>The left button went down at a point of this layer. False = nothing here took it.</summary>
    internal bool PressAt(Point p, int clicks = 1)
    {
        if (_p == null) return false;
        _press = p;
        _moved = false;
        _guideBox = Rect.Empty;
        if (_sel != null && _sel is not VideoLayer && _p.Overlays.Contains(_sel))
        {
            var r = ItemRect(_sel);
            r.Inflate(4, 4);
            if ((p - r.TopLeft).Length <= Knob + 2) { DeleteRequested?.Invoke(_sel); return true; }
            if ((p - r.BottomRight).Length <= Knob + 3)
            {
                _drag = Drag.Resize;
                _startSize = _sel.Size;
                var c = new Point(_canvas.X + _sel.X * _canvas.Width, _canvas.Y + _sel.Y * _canvas.Height);
                _startDist = Math.Max(4, (p - c).Length);
                return true;
            }
        }
        // the frame of the layer being set: its handles and its move knob come before what lies under them
        if (Active is { } active && FrameHandles(active))
        {
            int grip = HitGrip(p, FrameOf(active));
            if (grip >= 0)
            {
                _video = active;
                _startFrame = new Rect(active.FX, active.FY, active.FW, active.FH);
                _edges = grip;
                _drag = grip == 0 ? Drag.FrameMove : Drag.Frame;
                return true;
            }
        }
        var hit = HitItem(p);
        if (hit != null)
        {
            if (clicks == 2 && hit is TextOverlay t) { EditTextRequested?.Invoke(t); return true; }
            if (!ReferenceEquals(hit, _sel)) { _sel = hit; Selected?.Invoke(hit); InvalidateVisual(); }
            _drag = Drag.Move;
            _startX = hit.X;
            _startY = hit.Y;
            return true;
        }
        var video = HitVideo(p);
        if (video is VideoLayer layer)
        {
            if (!ReferenceEquals(layer, _sel)) { _sel = layer; Selected?.Invoke(layer); InvalidateVisual(); }
            BeginPan(layer);
            return true;
        }
        if (_canvas.Contains(p))
        {
            if (_sel != null) { _sel = null; Selected?.Invoke(null); InvalidateVisual(); }
            if (video != null && !_format && HasLayers)
            {
                // layers share the picture: a click on the video itself picks it up, like any of them
                _format = true;
                VideoSelected?.Invoke();
            }
            if (video != null && _format)
            {
                BeginPan(_p.Canvas);
                return true;
            }
        }
        return false;
    }

    private void BeginPan(IVideoPlacement l)
    {
        _video = l;
        _drag = Drag.Pan;
        _startX = l.CX;
        _startY = l.CY;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MoveTo(e.GetPosition(this));
    }

    /// <summary>The mouse is at a point of this layer: what is held follows it; with nothing held the cursor says what is under it.</summary>
    internal void MoveTo(Point p)
    {
        if (_p == null) return;
        if (_drag == Drag.None)
        {
            Cursor = HoverCursor(p);
            return;
        }
        var d = p - _press;
        if (d.Length > 2) _moved = true;
        switch (_drag)
        {
            case Drag.Move when _sel != null:
            {
                double nx = _startX + d.X / _canvas.Width, ny = _startY + d.Y / _canvas.Height;
                // pink guide + snap when the middle lines up with the frame's middle, like TikTok
                _guideBox = Rect.Empty;
                _guideX = Math.Abs((nx - 0.5) * _canvas.Width) < Snap;
                _guideY = Math.Abs((ny - 0.5) * _canvas.Height) < Snap;
                if (_guideX) nx = 0.5;
                if (_guideY) ny = 0.5;
                Moved?.Invoke(_sel, Math.Clamp(nx, -0.2, 1.2), Math.Clamp(ny, -0.2, 1.2), false);
                break;
            }
            case Drag.Resize when _sel != null:
                var c = new Point(_canvas.X + _sel.X * _canvas.Width, _canvas.Y + _sel.Y * _canvas.Height);
                _guideX = _guideY = false;
                Resized?.Invoke(_sel, _startSize * Math.Max(4, (p - c).Length) / _startDist, false);
                break;
            case Drag.Pan when _video != null:
            {
                var fr = FrameOf(_video);
                if (fr.Width < 1 || fr.Height < 1) break;
                double nx = _startX + d.X / fr.Width, ny = _startY + d.Y / fr.Height;
                // the video's middle snaps to its frame's middle
                _guideBox = fr;
                _guideX = Math.Abs((nx - 0.5) * fr.Width) < Snap;
                _guideY = Math.Abs((ny - 0.5) * fr.Height) < Snap;
                if (_guideX) nx = 0.5;
                if (_guideY) ny = 0.5;
                VideoPanned?.Invoke(_video, nx, ny, false);
                break;
            }
            case Drag.Frame when _video != null:
            case Drag.FrameMove when _video != null:
                _guideX = _guideY = false;
                FrameChanged?.Invoke(_video, DraggedFrame(d), _drag == Drag.FrameMove, false);
                break;
        }
        InvalidateVisual();
    }

    /// <summary>
    /// The frame being dragged: held edges follow the mouse and catch on the canvas's edges and middle and on the other
    /// layers' edges; a frame stays on the canvas and keeps a size that can still be grabbed.
    /// </summary>
    private Rect DraggedFrame(Vector d)
    {
        double dx = d.X / _canvas.Width, dy = d.Y / _canvas.Height;
        double l = _startFrame.Left, t = _startFrame.Top, r = _startFrame.Right, b = _startFrame.Bottom;
        double sx = Snap / _canvas.Width, sy = Snap / _canvas.Height;
        var xs = new List<double> { 0, 0.5, 1 };
        var ys = new List<double> { 0, 0.5, 1 };
        foreach (var o in _p!.VideoLayers())
        {
            if (ReferenceEquals(o, _video) || o is VideoLayer vl && !_shown.Contains(vl.Id)) continue;
            xs.Add(o.FX); xs.Add(o.FX + o.FW);
            ys.Add(o.FY); ys.Add(o.FY + o.FH);
        }
        static double Catch(double v, List<double> to, double within)
        {
            double best = v, dist = within;
            foreach (var s in to)
                if (Math.Abs(s - v) < dist) { dist = Math.Abs(s - v); best = s; }
            return best;
        }
        if (_drag == Drag.FrameMove)
        {
            double w = r - l, h = b - t;
            double nl = Math.Clamp(l + dx, 0, 1 - w), nt = Math.Clamp(t + dy, 0, 1 - h);
            // either side may catch
            double cl = Catch(nl, xs, sx), cr = Catch(nl + w, xs, sx);
            nl = cl != nl ? cl : cr != nl + w ? cr - w : nl;
            double ct = Catch(nt, ys, sy), cb = Catch(nt + h, ys, sy);
            nt = ct != nt ? ct : cb != nt + h ? cb - h : nt;
            return new Rect(Math.Clamp(nl, 0, 1 - w), Math.Clamp(nt, 0, 1 - h), w, h);
        }
        double min = EditProject.MinFrame;
        if ((_edges & Left) != 0) l = Math.Clamp(Catch(l + dx, xs, sx), 0, r - min);
        if ((_edges & Right) != 0) r = Math.Clamp(Catch(r + dx, xs, sx), l + min, 1);
        if ((_edges & Top) != 0) t = Math.Clamp(Catch(t + dy, ys, sy), 0, b - min);
        if ((_edges & Bottom) != 0) b = Math.Clamp(Catch(b + dy, ys, sy), t + min, 1);
        return new Rect(l, t, r - l, b - t);
    }

    private Cursor? HoverCursor(Point p)
    {
        if (_p == null) return null;
        if (_sel != null && _sel is not VideoLayer && (p - Inflated(ItemRect(_sel)).BottomRight).Length <= Knob + 3) return Cursors.SizeNWSE;
        if (Active is { } active && FrameHandles(active))
        {
            int grip = HitGrip(p, FrameOf(active));
            if (grip == 0) return Cursors.SizeAll;
            if (grip > 0)
            {
                bool across = (grip & (Left | Right)) != 0, down = (grip & (Top | Bottom)) != 0;
                if (across && down) return grip == (Left | Top) || grip == (Right | Bottom) ? Cursors.SizeNWSE : Cursors.SizeNESW;
                return across ? Cursors.SizeWE : Cursors.SizeNS;
            }
        }
        if (HitItem(p) != null) return Cursors.SizeAll;
        var video = HitVideo(p);
        return video is VideoLayer || video != null && (_format || HasLayers) ? Cursors.SizeAll : null;
    }

    private static Rect Inflated(Rect r) { r.Inflate(4, 4); return r; }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Drag.None) return;
        Release();
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>The left button came up: what was held is let go where it is.</summary>
    internal void Release()
    {
        if (_drag == Drag.None) return;
        var drag = _drag;
        var video = _video;
        _drag = Drag.None;
        _video = null;
        _guideX = _guideY = false;
        _guideBox = Rect.Empty;
        if (_moved && _p != null)
        {
            switch (drag)
            {
                case Drag.Move when _sel != null: Moved?.Invoke(_sel, _sel.X, _sel.Y, true); break;
                case Drag.Resize when _sel != null: Resized?.Invoke(_sel, _sel.Size, true); break;
                case Drag.Pan when video != null: VideoPanned?.Invoke(video, video.CX, video.CY, true); break;
                case Drag.Frame when video != null:
                case Drag.FrameMove when video != null:
                    FrameChanged?.Invoke(video, new Rect(video.FX, video.FY, video.FW, video.FH), drag == Drag.FrameMove, true);
                    break;
            }
        }
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == Drag.None) return;
        // something took the mouse mid-drag: what was set so far stands
        _drag = Drag.None;
        _video = null;
        _guideX = _guideY = false;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (WheelAt(e.GetPosition(this), e.Delta)) e.Handled = true;
    }

    /// <summary>The wheel turned at a point of this layer: an item there changes size, a video layer zooms at that point.</summary>
    internal bool WheelAt(Point p, int delta)
    {
        if (_p == null) return false;
        double k = Math.Pow(1.0012, delta);
        var hit = _sel != null && _sel is not VideoLayer && ItemRect(_sel).Contains(p) ? _sel : HitItem(p);
        if (hit != null)
        {
            if (!ReferenceEquals(hit, _sel)) { _sel = hit; Selected?.Invoke(hit); }
            Resized?.Invoke(hit, hit.Size * k, true);
            return true;
        }
        // a video layer: the one being set when the cursor is in its frame, else the one under the cursor
        var video = Active is { } active && FrameOf(active).Contains(p) ? active : HitVideo(p);
        if (video is VideoLayer layer && !ReferenceEquals(layer, _sel)) { _sel = layer; Selected?.Invoke(layer); }
        if (video is VideoLayer || video != null && _format)
        {
            VideoZoomed?.Invoke(video, k, new Point((p.X - _canvas.X) / _canvas.Width, (p.Y - _canvas.Y) / _canvas.Height));
            return true;
        }
        return false;
    }

    /// <summary>For tests: where a layer's frame, its handles and its move knob are (this layer's coordinates).</summary>
    internal Rect TestFrameOf(IVideoPlacement l) => FrameOf(l);
    internal static Point TestMoveKnob(Rect frame) => MoveKnob(frame);
}
