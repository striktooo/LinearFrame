using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LinearFrame.Player.Edit;

/// <summary>
/// Lies over the video in the player's overlay while editing: draws captions and emoji exactly as they will be in the
/// finished video, and lets you pick them up - drag to move (snaps to the middle), the corner handle or the wheel to
/// resize, the × to delete, a double-click on a caption to type. With the Format tool open, dragging the video moves it
/// inside the frame and the wheel zooms it.
/// </summary>
public sealed class EditCanvasLayer : FrameworkElement
{
    private static readonly Pen SelPen = Frozen(new Pen(Brushes.White, 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
    private static readonly Pen FramePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255)), 1));
    private static readonly Pen GuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x8E)), 1));
    private static readonly Brush KnobFill = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)));
    private const double Snap = 7, Knob = 11;

    private EditProject? _p;
    private Rect _canvas;
    private double _f;
    private EditOverlay? _sel;
    private bool _format;
    private readonly Dictionary<Guid, (string Key, Captions.Layout Layout)> _layouts = new();
    private HashSet<Guid> _shown = new();

    private enum Drag { None, Move, Resize, Pan }
    private Drag _drag;
    private Point _press;
    private double _startX, _startY, _startSize, _startDist;
    private bool _guideX, _guideY, _moved;

    public event Action<EditOverlay?>? Selected;
    public event Action<EditOverlay, double, double, bool>? Moved;
    public event Action<EditOverlay, double, bool>? Resized;
    public event Action<EditOverlay>? DeleteRequested;
    public event Action<TextOverlay>? EditTextRequested;
    public event Action<double, double, bool>? VideoPanned;
    public event Action<double>? VideoZoomed;

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
        string key = $"{t.Text}|{t.Style}|{t.Color}|{t.Size:0.0000}|{_canvas.Width:0.0}|{_canvas.Height:0.0}";
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
            case BannerOverlay bn:
                double w = o.Size * _canvas.Width, h = w * bn.Asset.Box.Height / Math.Max(1.0, bn.Asset.Box.Width);
                return new Rect(c.X - w / 2, c.Y - h / 2, w, h);
        }
        return Rect.Empty;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_p == null || _canvas.Width <= 0) return;
        dc.PushClip(new RectangleGeometry(_canvas));
        foreach (var o in _p.Overlays)
        {
            if (o is BannerOverlay || !_shown.Contains(o.Id)) continue;
            bool live = _p.VisibleAt(o, _f);
            if (!live) dc.PushOpacity(0.45); // selected, but not on screen at this moment
            if (o is TextOverlay t)
            {
                var l = LayoutOf(t);
                Captions.Draw(dc, l, new Point(_canvas.X + t.X * _canvas.Width, _canvas.Y + t.Y * _canvas.Height));
            }
            else if (o is EmojiOverlay e)
            {
                var img = AppleEmoji.Image(e.Unified);
                if (img != null) dc.DrawImage(img, ItemRect(e));
            }
            if (!live) dc.Pop();
        }
        dc.Pop();

        if (_format || _drag == Drag.Pan) dc.DrawRectangle(null, FramePen, new Rect(_canvas.X + 0.5, _canvas.Y + 0.5, _canvas.Width - 1, _canvas.Height - 1));
        if (_guideX) dc.DrawLine(GuidePen, new Point(_canvas.X + _canvas.Width / 2, _canvas.Top), new Point(_canvas.X + _canvas.Width / 2, _canvas.Bottom));
        if (_guideY) dc.DrawLine(GuidePen, new Point(_canvas.Left, _canvas.Y + _canvas.Height / 2), new Point(_canvas.Right, _canvas.Y + _canvas.Height / 2));

        if (_sel != null && _p.Overlays.Contains(_sel))
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

    // ------------------------------------------------------------------ mouse

    private EditOverlay? HitItem(Point p)
    {
        if (_p == null) return null;
        // topmost first: captions/emoji are drawn above banners
        foreach (var o in _p.Overlays.AsEnumerable().Reverse().OrderBy(o => o is BannerOverlay ? 1 : 0))
        {
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
        if (_p == null) return;
        var p = e.GetPosition(this);
        _press = p;
        _moved = false;
        if (_sel != null && _p.Overlays.Contains(_sel))
        {
            var r = ItemRect(_sel);
            r.Inflate(4, 4);
            if ((p - r.TopLeft).Length <= Knob + 2) { DeleteRequested?.Invoke(_sel); e.Handled = true; return; }
            if ((p - r.BottomRight).Length <= Knob + 3)
            {
                _drag = Drag.Resize;
                _startSize = _sel.Size;
                var c = new Point(_canvas.X + _sel.X * _canvas.Width, _canvas.Y + _sel.Y * _canvas.Height);
                _startDist = Math.Max(4, (p - c).Length);
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }
        var hit = HitItem(p);
        if (hit != null)
        {
            if (e.ClickCount == 2 && hit is TextOverlay t) { EditTextRequested?.Invoke(t); e.Handled = true; return; }
            if (!ReferenceEquals(hit, _sel)) { _sel = hit; Selected?.Invoke(hit); InvalidateVisual(); }
            _drag = Drag.Move;
            _startX = hit.X;
            _startY = hit.Y;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (_canvas.Contains(p))
        {
            if (_sel != null) { _sel = null; Selected?.Invoke(null); InvalidateVisual(); }
            if (_format && _p.FirstVideo.HasVideo)
            {
                _drag = Drag.Pan;
                _startX = _p.Canvas.CX;
                _startY = _p.Canvas.CY;
                CaptureMouse();
                e.Handled = true;
            }
            // otherwise the press goes on to the player: dragging the picture moves the window
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_p == null) return;
        var p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            Cursor = _sel != null && (p - Inflated(ItemRect(_sel)).BottomRight).Length <= Knob + 3 ? Cursors.SizeNWSE
                : HitItem(p) != null ? Cursors.SizeAll : _format && _canvas.Contains(p) ? Cursors.SizeAll : null;
            return;
        }
        var d = p - _press;
        if (d.Length > 2) _moved = true;
        double nx = _startX + d.X / _canvas.Width, ny = _startY + d.Y / _canvas.Height;
        // pink guide + snap when the middle lines up with the frame's middle, like TikTok
        _guideX = Math.Abs((nx - 0.5) * _canvas.Width) < Snap;
        _guideY = Math.Abs((ny - 0.5) * _canvas.Height) < Snap;
        if (_guideX) nx = 0.5;
        if (_guideY) ny = 0.5;
        switch (_drag)
        {
            case Drag.Move when _sel != null:
                Moved?.Invoke(_sel, Math.Clamp(nx, -0.2, 1.2), Math.Clamp(ny, -0.2, 1.2), false);
                break;
            case Drag.Resize when _sel != null:
                var c = new Point(_canvas.X + _sel.X * _canvas.Width, _canvas.Y + _sel.Y * _canvas.Height);
                _guideX = _guideY = false;
                Resized?.Invoke(_sel, _startSize * Math.Max(4, (p - c).Length) / _startDist, false);
                break;
            case Drag.Pan:
                VideoPanned?.Invoke(nx, ny, false);
                break;
        }
        InvalidateVisual();
    }

    private static Rect Inflated(Rect r) { r.Inflate(4, 4); return r; }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        _guideX = _guideY = false;
        ReleaseMouseCapture();
        if (_moved && _p != null)
        {
            switch (drag)
            {
                case Drag.Move when _sel != null: Moved?.Invoke(_sel, _sel.X, _sel.Y, true); break;
                case Drag.Resize when _sel != null: Resized?.Invoke(_sel, _sel.Size, true); break;
                case Drag.Pan: VideoPanned?.Invoke(_p.Canvas.CX, _p.Canvas.CY, true); break;
            }
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_p == null) return;
        var p = e.GetPosition(this);
        double k = Math.Pow(1.0012, e.Delta);
        var hit = _sel != null && ItemRect(_sel).Contains(p) ? _sel : HitItem(p);
        if (hit != null)
        {
            if (!ReferenceEquals(hit, _sel)) { _sel = hit; Selected?.Invoke(hit); }
            Resized?.Invoke(hit, hit.Size * k, true);
            e.Handled = true;
        }
        else if (_format && _canvas.Contains(p))
        {
            VideoZoomed?.Invoke(k);
            e.Handled = true;
        }
    }
}
