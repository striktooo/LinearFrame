using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LinearFrame.Editor;

/// <summary>
/// Interactive canvas of the editor. Three layers: base (fill + screenshot + highlighter/mosaic),
/// marks (shapes with a soft shadow) and UI (selection handles, crop frame). Coordinates are image pixels.
/// </summary>
public sealed class MarkupView : FrameworkElement
{
    private readonly DrawingVisual _base = new();
    private readonly DrawingVisual _marks = new();
    private readonly DrawingVisual _ui = new();
    private readonly DropShadowEffect _shadow = new() { Opacity = 0.35, Direction = 270, RenderingBias = RenderingBias.Performance };
    private static readonly Brush Checker = MakeChecker();
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));

    private double _dpi = 1.0;
    private Annotation? _draft;
    private bool _pressed;
    private Point _pressImg;
    private Point _lastImg;
    private bool _movedSinceCheckpoint;
    private int _handle = -1;          // selection or crop handle being dragged
    private Annotation? _resizeOrig;   // clone of the selected annotation when a resize started
    private Rect _cropAtPress;
    private bool _eyedropper;

    public MarkupDocument Doc { get; private set; } = null!;
    public double Zoom { get; private set; } = 1.0;
    public double ViewScale => Zoom / _dpi;
    public Rect Region { get; private set; }
    public ToolKind Tool { get; set; } = ToolKind.Arrow;
    public Color CurrentColor { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    public double CurrentWidth { get; set; } = 6;
    public Annotation? Selected { get; private set; }
    public bool CropMode { get; private set; }
    public Rect CropFrame { get; private set; }
    public double? CropRatio { get; set; }

    public event Action? Changed;
    public event Action? SelectionChanged;
    public event Action? ZoomChanged;
    public event Action? CropFrameChanged;
    public event Action<Color>? ColorPicked;
    public event Action<TextAnnotation, bool>? TextEditRequested;

    public MarkupView()
    {
        AddVisualChild(_base);
        AddVisualChild(_marks);
        AddVisualChild(_ui);
        _marks.Effect = _shadow;
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;
        Loaded += (_, _) => { _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; RenderAll(); };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _dpi = newDpi.DpiScaleX;
        InvalidateMeasure();
        RenderAll();
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static Brush MakeChecker()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 16, 16));
            var gray = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE6));
            dc.DrawRectangle(gray, null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(gray, null, new Rect(8, 8, 8, 8));
        }
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => _base, 1 => _marks, _ => _ui };

    public void Load(MarkupDocument doc)
    {
        Doc = doc;
        Selected = null;
        CropMode = false;
        UpdateRegion();
        RenderAll();
    }

    // ------------------------------------------------------------------ layout / zoom

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Doc == null) return new Size(0, 0);
        return new Size(Math.Max(1, Region.Width * ViewScale), Math.Max(1, Region.Height * ViewScale));
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    public void SetZoom(double zoom)
    {
        Zoom = Math.Clamp(zoom, 0.05, 16);
        RenderOptions.SetBitmapScalingMode(_base, Zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        InvalidateMeasure();
        RenderAll();
        ZoomChanged?.Invoke();
    }

    public double FitZoom(Size viewport, double margin = 48)
    {
        if (Doc == null || Region.Width <= 0) return 1;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double zx = (viewport.Width - margin * 2) * dpi / Region.Width;
        double zy = (viewport.Height - margin * 2) * dpi / Region.Height;
        return Math.Clamp(Math.Min(Math.Min(zx, zy), 1.0), 0.05, 1.0);
    }

    private void UpdateRegion()
    {
        if (CropMode)
        {
            var u = Rect.Union(new Rect(0, 0, Doc.Width, Doc.Height), CropFrame);
            double pad = Math.Max(48, Math.Max(Doc.Width, Doc.Height) * 0.22);
            u.Inflate(pad, pad);
            Region = u;
        }
        else Region = new Rect(Doc.Frame.X, Doc.Frame.Y, Doc.Frame.Width, Doc.Frame.Height);
        InvalidateMeasure();
    }

    public Point ToImage(Point view) => new(view.X / ViewScale + Region.X, view.Y / ViewScale + Region.Y);
    public Point ToView(Point img) => new((img.X - Region.X) * ViewScale, (img.Y - Region.Y) * ViewScale);
    private Rect ToView(Rect r) => new(ToView(r.TopLeft), ToView(r.BottomRight));
    private Matrix ViewMatrix => new(ViewScale, 0, 0, ViewScale, -Region.X * ViewScale, -Region.Y * ViewScale);

    // ------------------------------------------------------------------ rendering

    public void RenderAll()
    {
        if (Doc == null) return;
        RenderBase();
        RenderMarks();
        RenderUi();
    }

    private void RenderBase()
    {
        using var dc = _base.RenderOpen();
        dc.PushTransform(new MatrixTransform(ViewMatrix));
        Rect canvas = CropMode ? CropFrame : new Rect(Doc.Frame.X, Doc.Frame.Y, Doc.Frame.Width, Doc.Frame.Height);
        var fill = Doc.EffectiveFill;
        if (fill.A > 0)
        {
            var b = new SolidColorBrush(fill);
            b.Freeze();
            dc.DrawRectangle(b, null, canvas);
        }
        else
        {
            dc.Pop();
            dc.DrawRectangle(Checker, null, ToView(canvas));
            dc.PushTransform(new MatrixTransform(ViewMatrix));
        }
        dc.DrawImage(Doc.Image, new Rect(0, 0, Doc.Width, Doc.Height));
        foreach (var a in Doc.Items.Where(i => i.IsBaseLayer)) AnnotationRenderer.Draw(dc, a, Doc);
        if (_draft is { IsBaseLayer: true }) AnnotationRenderer.Draw(dc, _draft, Doc);
        dc.Pop();
    }

    private void RenderMarks()
    {
        _shadow.BlurRadius = 5 * ViewScale;
        _shadow.ShadowDepth = 1.6 * ViewScale;
        using var dc = _marks.RenderOpen();
        dc.PushTransform(new MatrixTransform(ViewMatrix));
        foreach (var a in Doc.Items.Where(i => !i.IsBaseLayer)) AnnotationRenderer.Draw(dc, a, Doc);
        if (_draft is { IsBaseLayer: false }) AnnotationRenderer.Draw(dc, _draft, Doc);
        dc.Pop();
    }

    private void RenderUi()
    {
        using var dc = _ui.RenderOpen();
        double W = ActualWidth, H = ActualHeight;
        if (CropMode)
        {
            var f = ToView(CropFrame);
            dc.DrawRectangle(DimBrush, null, new Rect(0, 0, Math.Max(W, 1), Math.Max(0, f.Top)));
            dc.DrawRectangle(DimBrush, null, new Rect(0, f.Bottom, Math.Max(W, 1), Math.Max(0, H - f.Bottom)));
            dc.DrawRectangle(DimBrush, null, new Rect(0, f.Top, Math.Max(0, f.Left), f.Height));
            dc.DrawRectangle(DimBrush, null, new Rect(f.Right, f.Top, Math.Max(0, W - f.Right), f.Height));

            var thin = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), 1);
            for (int i = 1; i < 3; i++)
            {
                double x = f.Left + f.Width * i / 3, y = f.Top + f.Height * i / 3;
                dc.DrawLine(thin, new Point(x, f.Top), new Point(x, f.Bottom));
                dc.DrawLine(thin, new Point(f.Left, y), new Point(f.Right, y));
            }
            dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), f);
            // iOS-like corner brackets
            var bracket = new Pen(Brushes.White, 4) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
            double L = Math.Min(22, Math.Min(f.Width, f.Height) / 3);
            void Corner(Point p, double dx, double dy)
            {
                dc.DrawLine(bracket, p, new Point(p.X + dx * L, p.Y));
                dc.DrawLine(bracket, p, new Point(p.X, p.Y + dy * L));
            }
            Corner(f.TopLeft, 1, 1); Corner(f.TopRight, -1, 1); Corner(f.BottomRight, -1, -1); Corner(f.BottomLeft, 1, -1);
            var mid = new Pen(Brushes.White, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double m = Math.Min(10, f.Width / 6);
            double n = Math.Min(10, f.Height / 6);
            dc.DrawLine(mid, new Point(f.Left + f.Width / 2 - m, f.Top), new Point(f.Left + f.Width / 2 + m, f.Top));
            dc.DrawLine(mid, new Point(f.Left + f.Width / 2 - m, f.Bottom), new Point(f.Left + f.Width / 2 + m, f.Bottom));
            dc.DrawLine(mid, new Point(f.Left, f.Top + f.Height / 2 - n), new Point(f.Left, f.Top + f.Height / 2 + n));
            dc.DrawLine(mid, new Point(f.Right, f.Top + f.Height / 2 - n), new Point(f.Right, f.Top + f.Height / 2 + n));
            return;
        }

        if (Selected != null)
        {
            var dash = new Pen(AccentBrush, 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            var b = ToView(Selected.Bounds);
            b.Inflate(4, 4);
            dc.DrawRectangle(null, dash, b);
            foreach (var hp in SelectionHandles(Selected))
                dc.DrawEllipse(Brushes.White, new Pen(AccentBrush, 1.6), ToView(hp), 5, 5);
        }
    }

    // ------------------------------------------------------------------ selection handles

    private static Point[] SelectionHandles(Annotation a) => a switch
    {
        LineAnnotation l => new[] { l.A, l.B },
        ShapeAnnotation s => new[] { s.Rect.TopLeft, s.Rect.TopRight, s.Rect.BottomRight, s.Rect.BottomLeft },
        _ => Array.Empty<Point>(),
    };

    private int HitSelectionHandle(Point viewPos)
    {
        if (Selected == null) return -1;
        var pts = SelectionHandles(Selected);
        for (int i = 0; i < pts.Length; i++)
            if ((ToView(pts[i]) - viewPos).Length <= 9) return i;
        return -1;
    }

    private Annotation? HitAnnotation(Point img)
    {
        double tol = 5 / ViewScale;
        for (int i = Doc.Items.Count - 1; i >= 0; i--)
            if (Doc.Items[i].HitTest(img, tol)) return Doc.Items[i];
        return null;
    }

    public void Select(Annotation? a)
    {
        Selected = a;
        RenderUi();
        SelectionChanged?.Invoke();
    }

    public void DeleteSelected()
    {
        if (Selected == null) return;
        Doc.Checkpoint();
        Doc.Items.Remove(Selected);
        Selected = null;
        RenderAll();
        SelectionChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Applies the current colour/width to the selected annotation.</summary>
    public void ApplyStyleToSelection(bool color, bool width)
    {
        if (Selected == null) return;
        Doc.Checkpoint();
        if (color) Selected.Color = CurrentColor;
        if (width) Selected.Width = CurrentWidth;
        if (Selected is ShapeAnnotation sh) sh.Mosaic = null;
        RenderAll();
        Changed?.Invoke();
    }

    public void StartEyedropper()
    {
        _eyedropper = true;
        Cursor = Cursors.Pen;
    }

    // ------------------------------------------------------------------ crop & extend

    public void EnterCrop()
    {
        if (CropMode) return;
        Select(null);
        CropMode = true;
        CropFrame = new Rect(Doc.Frame.X, Doc.Frame.Y, Doc.Frame.Width, Doc.Frame.Height);
        CropRatio = null;
        UpdateRegion();
        RenderAll();
    }

    public void CancelCrop()
    {
        if (!CropMode) return;
        CropMode = false;
        _eyedropper = false;
        UpdateRegion();
        RenderAll();
    }

    public void ApplyCrop()
    {
        if (!CropMode) return;
        var f = CropFrame;
        var r = new Int32Rect((int)Math.Round(f.X), (int)Math.Round(f.Y), Math.Max(1, (int)Math.Round(f.Width)), Math.Max(1, (int)Math.Round(f.Height)));
        if (r != Doc.Frame)
        {
            Doc.Checkpoint();
            Doc.Frame = r;
        }
        CropMode = false;
        _eyedropper = false;
        UpdateRegion();
        RenderAll();
        Changed?.Invoke();
    }

    public void ResetCrop()
    {
        CropFrame = new Rect(0, 0, Doc.Width, Doc.Height);
        CropRatio = null;
        UpdateRegion();
        RenderAll();
        CropFrameChanged?.Invoke();
    }

    /// <summary>Frames the visible screenshot with a margin and an optional aspect ratio (extending with the background).</summary>
    public void ApplyRatioAndMargin(double? ratio, double marginFraction)
    {
        CropRatio = ratio;
        var content = Rect.Intersect(new Rect(Doc.Frame.X, Doc.Frame.Y, Doc.Frame.Width, Doc.Frame.Height), new Rect(0, 0, Doc.Width, Doc.Height));
        if (content.IsEmpty) content = new Rect(0, 0, Doc.Width, Doc.Height);
        double margin = Math.Round(Math.Min(content.Width, content.Height) * marginFraction);
        var box = content;
        box.Inflate(margin, margin);
        if (ratio is double k && k > 0)
        {
            double w = box.Width, h = box.Height;
            if (w / h < k) w = h * k; else h = w / k;
            var c = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            box = new Rect(c.X - w / 2, c.Y - h / 2, w, h);
        }
        CropFrame = new Rect(Math.Round(box.X), Math.Round(box.Y), Math.Round(box.Width), Math.Round(box.Height));
        UpdateRegion();
        RenderAll();
        CropFrameChanged?.Invoke();
    }

    /// <summary>0..7 like the capture overlay (TL, T, TR, R, BR, B, BL, L), 8 = move, -1 = none.</summary>
    private int HitCropHandle(Point viewPos)
    {
        var f = ToView(CropFrame);
        double t = 14;
        var pts = new[]
        {
            f.TopLeft, new Point(f.Left + f.Width / 2, f.Top), f.TopRight, new Point(f.Right, f.Top + f.Height / 2),
            f.BottomRight, new Point(f.Left + f.Width / 2, f.Bottom), f.BottomLeft, new Point(f.Left, f.Top + f.Height / 2),
        };
        for (int i = 0; i < pts.Length; i += 2)
            if ((pts[i] - viewPos).Length <= t) return i;
        for (int i = 1; i < pts.Length; i += 2)
        {
            bool horizontalEdge = i is 1 or 5;
            if (horizontalEdge && Math.Abs(viewPos.Y - pts[i].Y) <= t && viewPos.X > f.Left && viewPos.X < f.Right) return i;
            if (!horizontalEdge && Math.Abs(viewPos.X - pts[i].X) <= t && viewPos.Y > f.Top && viewPos.Y < f.Bottom) return i;
        }
        return f.Contains(viewPos) ? 8 : -1;
    }

    private void DragCrop(Point img)
    {
        var s = _cropAtPress;
        double dx = img.X - _pressImg.X, dy = img.Y - _pressImg.Y;
        double l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
        const double min = 8;
        switch (_handle)
        {
            case 8: l += dx; r += dx; t += dy; b += dy; break;
            case 0: l = Math.Min(l + dx, r - min); t = Math.Min(t + dy, b - min); break;
            case 1: t = Math.Min(t + dy, b - min); break;
            case 2: r = Math.Max(r + dx, l + min); t = Math.Min(t + dy, b - min); break;
            case 3: r = Math.Max(r + dx, l + min); break;
            case 4: r = Math.Max(r + dx, l + min); b = Math.Max(b + dy, t + min); break;
            case 5: b = Math.Max(b + dy, t + min); break;
            case 6: l = Math.Min(l + dx, r - min); b = Math.Max(b + dy, t + min); break;
            case 7: l = Math.Min(l + dx, r - min); break;
        }
        if (CropRatio is double k && _handle is 0 or 2 or 4 or 6)
        {
            // keep the ratio, anchored at the opposite corner
            double w = r - l, h = b - t;
            if (w / h > k) h = w / k; else w = h * k;
            if (_handle is 0 or 6) l = r - w; else r = l + w;
            if (_handle is 0 or 2) t = b - h; else b = t + h;
        }
        else if (_handle is 1 or 3 or 5 or 7) CropRatio = null;
        CropFrame = new Rect(Math.Round(l), Math.Round(t), Math.Round(r - l), Math.Round(b - t));
        RenderBase();
        RenderUi();
        CropFrameChanged?.Invoke();
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Doc == null) return;
        Focus();
        if (PointerDown(e.GetPosition(this), e.ClickCount)) e.Handled = true;
    }

    private void Grab()
    {
        if (IsVisible) CaptureMouse();
    }

    /// <summary>Pointer pressed at a view position (also used by the self-test). Returns true when handled.</summary>
    internal bool PointerDown(Point vp, int clickCount)
    {
        var p = ToImage(vp);
        _pressImg = p;
        _lastImg = p;
        _movedSinceCheckpoint = false;

        if (_eyedropper)
        {
            _eyedropper = false;
            Cursor = CropMode ? Cursors.Arrow : Cursors.Cross;
            var c = Doc.Pixels.GetPixel((int)p.X, (int)p.Y);
            ColorPicked?.Invoke(c);
            return true;
        }

        if (CropMode)
        {
            _handle = HitCropHandle(vp);
            if (_handle < 0) return false;
            _cropAtPress = CropFrame;
            _pressed = true;
            Grab();
            return true;
        }

        switch (Tool)
        {
            case ToolKind.Select:
            {
                _handle = HitSelectionHandle(vp);
                if (_handle >= 0 && Selected != null)
                {
                    _resizeOrig = Selected.Clone();
                }
                else
                {
                    var hit = HitAnnotation(p);
                    if (hit is TextAnnotation ta && clickCount == 2)
                    {
                        Select(null);
                        TextEditRequested?.Invoke(ta, false);
                        return true;
                    }
                    Select(hit);
                    if (hit == null) return false;
                }
                _pressed = true;
                Grab();
                break;
            }
            case ToolKind.Pen:
            case ToolKind.Highlighter:
                _draft = new StrokeAnnotation { Color = CurrentColor, Width = CurrentWidth, Highlighter = Tool == ToolKind.Highlighter, Points = { p } };
                _pressed = true;
                Grab();
                break;
            case ToolKind.Arrow:
            case ToolKind.Line:
                _draft = new LineAnnotation { Color = CurrentColor, Width = CurrentWidth, A = p, B = p, Arrow = Tool == ToolKind.Arrow };
                _pressed = true;
                Grab();
                break;
            case ToolKind.Rect:
            case ToolKind.Ellipse:
            case ToolKind.Pixelate:
                _draft = new ShapeAnnotation
                {
                    Color = CurrentColor,
                    Width = CurrentWidth,
                    Rect = new Rect(p, p),
                    Kind = Tool == ToolKind.Rect ? ShapeKind.Rect : Tool == ToolKind.Ellipse ? ShapeKind.Ellipse : ShapeKind.Pixelate,
                };
                _pressed = true;
                Grab();
                break;
            case ToolKind.Text:
            {
                var existing = HitAnnotation(p) as TextAnnotation;
                if (existing != null) TextEditRequested?.Invoke(existing, false);
                else
                {
                    var t = new TextAnnotation { Color = CurrentColor, Width = CurrentWidth };
                    t.Position = new Point(p.X, p.Y - t.FontSize * 0.6);
                    TextEditRequested?.Invoke(t, true);
                }
                return true;
            }
            case ToolKind.Counter:
                Doc.Checkpoint();
                Doc.Items.Add(new CounterAnnotation { Color = CurrentColor, Width = CurrentWidth, Center = p, Number = Doc.NextCounter() });
                RenderMarks();
                Changed?.Invoke();
                return true;
        }
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Doc == null) return;
        PointerMove(e.GetPosition(this), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    internal void PointerMove(Point vp, bool shift)
    {
        var p = ToImage(vp);

        if (!_pressed)
        {
            UpdateHoverCursor(vp, p);
            return;
        }

        if (CropMode)
        {
            DragCrop(p);
            return;
        }

        switch (_draft)
        {
            case StrokeAnnotation s:
                if ((p - s.Points[^1]).Length * ViewScale >= 1.2)
                {
                    if (shift && s.Points.Count >= 1) { var a = s.Points[0]; s.Points.Clear(); s.Points.Add(a); s.Points.Add(Snap(a, p)); }
                    else s.Points.Add(p);
                }
                if (s.IsBaseLayer) RenderBase(); else RenderMarks();
                return;
            case LineAnnotation l:
                l.B = shift ? Snap(l.A, p) : p;
                RenderMarks();
                return;
            case ShapeAnnotation sh:
            {
                var a = _pressImg;
                var b = p;
                if (shift)
                {
                    double d = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                    b = new Point(a.X + Math.Sign(b.X - a.X) * d, a.Y + Math.Sign(b.Y - a.Y) * d);
                }
                sh.Rect = new Rect(a, b);
                sh.Mosaic = null;
                if (sh.IsBaseLayer) RenderBase(); else RenderMarks();
                return;
            }
        }

        if (Tool == ToolKind.Select && Selected != null)
        {
            if (!_movedSinceCheckpoint && (p - _pressImg).Length * ViewScale > 2)
            {
                // snapshot taken before the first real change, holding the original state
                var sel = Selected;
                int idx = Doc.Items.IndexOf(sel);
                Annotation? original = _resizeOrig;
                if (_handle < 0) original = sel.Clone();
                // put the original back temporarily so the checkpoint stores the pre-change state
                if (idx >= 0 && original != null)
                {
                    Doc.Items[idx] = original;
                    Doc.Checkpoint();
                    Doc.Items[idx] = sel;
                }
                _movedSinceCheckpoint = true;
            }
            if (!_movedSinceCheckpoint) return;

            if (_handle >= 0 && _resizeOrig != null) ResizeSelected(_handle, p, shift);
            else
            {
                Selected.Offset(p - _lastImg);
                if (Selected is ShapeAnnotation sh2) sh2.Mosaic = null;
            }
            _lastImg = p;
            if (Selected.IsBaseLayer) RenderBase(); else RenderMarks();
            RenderUi();
        }
    }

    private static Point Snap(Point a, Point b)
    {
        var v = b - a;
        double ang = Math.Atan2(v.Y, v.X);
        double step = Math.PI / 4;
        ang = Math.Round(ang / step) * step;
        double len = v.Length;
        return new Point(a.X + Math.Cos(ang) * len, a.Y + Math.Sin(ang) * len);
    }

    private void ResizeSelected(int handle, Point p, bool shift)
    {
        switch (Selected)
        {
            case LineAnnotation l:
                if (handle == 0) l.A = shift ? Snap(l.B, p) : p;
                else l.B = shift ? Snap(l.A, p) : p;
                break;
            case ShapeAnnotation s when _resizeOrig is ShapeAnnotation o:
            {
                var r = o.Rect;
                Point anchor = handle switch
                {
                    0 => r.BottomRight,
                    1 => r.BottomLeft,
                    2 => r.TopLeft,
                    _ => r.TopRight,
                };
                s.Rect = new Rect(anchor, p);
                s.Mosaic = null;
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        PointerUp();
    }

    internal void PointerUp()
    {
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();

        if (CropMode)
        {
            _handle = -1;
            UpdateRegion();
            RenderAll();
            CropFrameChanged?.Invoke();
            return;
        }

        if (_draft != null)
        {
            var d = _draft;
            _draft = null;
            bool valid = d switch
            {
                StrokeAnnotation s => s.Points.Count >= 2 || (s.Points.Count == 1),
                LineAnnotation l => (l.B - l.A).Length * ViewScale >= 4,
                ShapeAnnotation sh => sh.Rect.Width * ViewScale >= 4 && sh.Rect.Height * ViewScale >= 4,
                _ => true,
            };
            if (valid)
            {
                if (d is StrokeAnnotation { Points.Count: 1 } s1) s1.Points.Add(s1.Points[0] + new Vector(0.5, 0.5));
                Doc.Checkpoint();
                Doc.Items.Add(d);
                Changed?.Invoke();
            }
            RenderAll();
            return;
        }

        if (_movedSinceCheckpoint) Changed?.Invoke();
        _handle = -1;
        _resizeOrig = null;
        RenderUi();
    }

    private void UpdateHoverCursor(Point vp, Point p)
    {
        if (_eyedropper) { Cursor = Cursors.Pen; return; }
        if (CropMode)
        {
            Cursor = HitCropHandle(vp) switch
            {
                0 or 4 => Cursors.SizeNWSE,
                2 or 6 => Cursors.SizeNESW,
                1 or 5 => Cursors.SizeNS,
                3 or 7 => Cursors.SizeWE,
                8 => Cursors.SizeAll,
                _ => Cursors.Arrow,
            };
            return;
        }
        if (Tool == ToolKind.Select)
        {
            if (HitSelectionHandle(vp) >= 0) Cursor = Cursors.SizeAll;
            else Cursor = HitAnnotation(p) != null ? Cursors.Hand : Cursors.Arrow;
            return;
        }
        Cursor = Tool == ToolKind.Text ? Cursors.IBeam : Cursors.Cross;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RenderUi();
    }

    /// <summary>Called by the editor after undo/redo or text edits.</summary>
    public void Refresh()
    {
        if (Selected != null && !Doc.Items.Contains(Selected)) Selected = null;
        UpdateRegion();
        RenderAll();
    }
}
