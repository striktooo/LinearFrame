using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Imaging;

namespace LinearFrame.Editor;

public enum ToolKind { Select, Pen, Highlighter, Arrow, Line, Rect, Ellipse, Text, Counter, Pixelate, Crop }

/// <summary>A markup object. All coordinates are in image pixels (independent of zoom and DPI).</summary>
public abstract class Annotation
{
    public Color Color { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    public double Width { get; set; } = 6;

    public abstract Rect Bounds { get; }
    public abstract Annotation Clone();
    public abstract void Offset(Vector v);
    public virtual bool HitTest(Point p, double tolerance) => Inflate(Bounds, tolerance).Contains(p);

    /// <summary>Drawn under the soft-shadow layer (highlighter, pixelate) or on it (everything else).</summary>
    public virtual bool IsBaseLayer => false;

    protected static Rect Inflate(Rect r, double d) { r.Inflate(d, d); return r; }

    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-6) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}

public sealed class StrokeAnnotation : Annotation
{
    public List<Point> Points { get; set; } = new();
    public bool Highlighter { get; set; }
    public override bool IsBaseLayer => Highlighter;

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            double x0 = Points.Min(p => p.X), y0 = Points.Min(p => p.Y), x1 = Points.Max(p => p.X), y1 = Points.Max(p => p.Y);
            return Inflate(new Rect(x0, y0, x1 - x0, y1 - y0), StrokeWidth / 2);
        }
    }

    public double StrokeWidth => Highlighter ? Width * 3.2 : Width;

    public override Annotation Clone() => new StrokeAnnotation { Color = Color, Width = Width, Highlighter = Highlighter, Points = new List<Point>(Points) };

    public override void Offset(Vector v)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] += v;
    }

    public override bool HitTest(Point p, double tol)
    {
        double t = tol + StrokeWidth / 2;
        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegment(p, Points[i - 1], Points[i]) <= t) return true;
        return Points.Count == 1 && (Points[0] - p).Length <= t;
    }
}

public sealed class LineAnnotation : Annotation
{
    public Point A { get; set; }
    public Point B { get; set; }
    public bool Arrow { get; set; }

    public override Rect Bounds => Inflate(new Rect(A, B), Width * (Arrow ? 3 : 1));
    public override Annotation Clone() => new LineAnnotation { Color = Color, Width = Width, A = A, B = B, Arrow = Arrow };
    public override void Offset(Vector v) { A += v; B += v; }
    public override bool HitTest(Point p, double tol) => DistanceToSegment(p, A, B) <= tol + Width * (Arrow ? 1.5 : 0.75);
}

public enum ShapeKind { Rect, Ellipse, Pixelate }

public sealed class ShapeAnnotation : Annotation
{
    public Rect Rect { get; set; }
    public ShapeKind Kind { get; set; }
    public override bool IsBaseLayer => Kind == ShapeKind.Pixelate;

    // cached mosaic for pixelate
    internal BitmapSource? Mosaic;
    internal Rect MosaicRect;

    public override Rect Bounds => Inflate(Rect, Width / 2);
    public override Annotation Clone() => new ShapeAnnotation { Color = Color, Width = Width, Rect = Rect, Kind = Kind };
    public override void Offset(Vector v) { var r = Rect; r.Offset(v); Rect = r; Mosaic = null; }

    public override bool HitTest(Point p, double tol)
    {
        if (Kind == ShapeKind.Pixelate) return Inflate(Rect, tol).Contains(p);
        var outer = Inflate(Rect, tol + Width / 2);
        if (!outer.Contains(p)) return false;
        var inner = Rect;
        double d = tol + Width / 2;
        if (inner.Width > 2 * d && inner.Height > 2 * d) inner.Inflate(-d, -d); else return true;
        if (Kind == ShapeKind.Ellipse)
        {
            // distance to the ellipse outline, approximately
            double rx = Rect.Width / 2, ry = Rect.Height / 2;
            if (rx < 1 || ry < 1) return true;
            double nx = (p.X - (Rect.X + rx)) / rx, ny = (p.Y - (Rect.Y + ry)) / ry;
            double r = Math.Sqrt(nx * nx + ny * ny);
            return Math.Abs(r - 1) * Math.Min(rx, ry) <= d;
        }
        return !inner.Contains(p);
    }
}

public sealed class TextAnnotation : Annotation
{
    public Point Position { get; set; }
    public string Text { get; set; } = "";
    public double FontSize => Math.Max(14, Width * 4.2);
    internal bool Editing { get; set; }

    public override Rect Bounds
    {
        get
        {
            var ft = AnnotationRenderer.MakeText(this, 1.0);
            return new Rect(Position, new Size(Math.Max(ft.WidthIncludingTrailingWhitespace, 8), ft.Height));
        }
    }

    public override Annotation Clone() => new TextAnnotation { Color = Color, Width = Width, Position = Position, Text = Text };
    public override void Offset(Vector v) => Position += v;
}

public sealed class CounterAnnotation : Annotation
{
    public Point Center { get; set; }
    public int Number { get; set; } = 1;
    public double Radius => Math.Max(13, Width * 2.6);

    public override Rect Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);
    public override Annotation Clone() => new CounterAnnotation { Color = Color, Width = Width, Center = Center, Number = Number };
    public override void Offset(Vector v) => Center += v;
    public override bool HitTest(Point p, double tol) => (p - Center).Length <= Radius + tol;
}

/// <summary>Draws annotations. The same code renders the editor view and the exported file.</summary>
public static class AnnotationRenderer
{
    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface CounterFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static FormattedText MakeText(TextAnnotation t, double pixelsPerDip)
    {
        var brush = new SolidColorBrush(t.Color);
        brush.Freeze();
        return new FormattedText(string.IsNullOrEmpty(t.Text) ? " " : t.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            TextFace, t.FontSize, brush, pixelsPerDip);
    }

    public static void Draw(DrawingContext dc, Annotation a, MarkupDocument doc)
    {
        switch (a)
        {
            case StrokeAnnotation s: DrawStroke(dc, s); break;
            case LineAnnotation l: if (l.Arrow) DrawArrow(dc, l); else DrawLine(dc, l); break;
            case ShapeAnnotation sh: DrawShape(dc, sh, doc); break;
            case TextAnnotation t: DrawText(dc, t); break;
            case CounterAnnotation c: DrawCounter(dc, c); break;
        }
    }

    private static Pen RoundPen(Brush b, double w)
    {
        var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static SolidColorBrush Brush(Color c, double alpha = 1)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(c.A * alpha), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>Smooth path through the points (quadratic curves via mid-points).</summary>
    public static Geometry SmoothPath(IReadOnlyList<Point> pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (pts.Count == 0) return g;
            ctx.BeginFigure(pts[0], false, false);
            if (pts.Count == 1) ctx.LineTo(pts[0] + new Vector(0.01, 0), true, true);
            else if (pts.Count == 2) ctx.LineTo(pts[1], true, true);
            else
            {
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    var mid = new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                    ctx.QuadraticBezierTo(pts[i], mid, true, true);
                }
                ctx.LineTo(pts[^1], true, true);
            }
        }
        g.Freeze();
        return g;
    }

    private static void DrawStroke(DrawingContext dc, StrokeAnnotation s)
    {
        if (s.Points.Count == 0) return;
        var geo = SmoothPath(s.Points);
        if (s.Highlighter)
        {
            var pen = new Pen(Brush(s.Color, 0.42), s.StrokeWidth) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
        else dc.DrawGeometry(null, RoundPen(Brush(s.Color), s.Width), geo);
    }

    private static void DrawLine(DrawingContext dc, LineAnnotation l)
    {
        dc.DrawLine(RoundPen(Brush(l.Color), l.Width), l.A, l.B);
    }

    /// <summary>Tapered arrow with a filled head and a thin white rim (reads well on any background).</summary>
    public static Geometry ArrowGeometry(Point a, Point b, double width)
    {
        var v = b - a;
        double len = v.Length;
        var g = new StreamGeometry();
        if (len < 0.5) { g.Freeze(); return g; }
        v /= len;
        var n = new Vector(-v.Y, v.X);
        double headLen = Math.Min(len * 0.6, Math.Max(width * 3.8, 12));
        double headHalf = Math.Max(width * 2.1, 7);
        double tail = Math.Max(width * 0.3, 1.0);
        double neck = Math.Max(width * 0.64, 1.8);
        var baseCenter = b - v * headLen;
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a + n * tail, true, true);
            ctx.LineTo(baseCenter + n * neck, true, true);
            ctx.LineTo(baseCenter + n * headHalf, true, true);
            ctx.LineTo(b, true, true);
            ctx.LineTo(baseCenter - n * headHalf, true, true);
            ctx.LineTo(baseCenter - n * neck, true, true);
            ctx.LineTo(a - n * tail, true, true);
        }
        g.Freeze();
        return g;
    }

    private static void DrawArrow(DrawingContext dc, LineAnnotation l)
    {
        var geo = ArrowGeometry(l.A, l.B, l.Width);
        var rim = new Pen(Brush(Colors.White, 0.95), Math.Max(1.2, l.Width * 0.28)) { LineJoin = PenLineJoin.Round };
        rim.Freeze();
        dc.DrawGeometry(Brush(l.Color), rim, geo);
    }

    private static void DrawShape(DrawingContext dc, ShapeAnnotation s, MarkupDocument doc)
    {
        var r = s.Rect;
        if (r.Width < 0.5 || r.Height < 0.5) return;
        switch (s.Kind)
        {
            case ShapeKind.Rect:
                double rad = Math.Min(Math.Max(3, s.Width * 1.4), Math.Min(r.Width, r.Height) / 2);
                dc.DrawRoundedRectangle(null, RoundPen(Brush(s.Color), s.Width), r, rad, rad);
                break;
            case ShapeKind.Ellipse:
                dc.DrawEllipse(null, RoundPen(Brush(s.Color), s.Width), new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            case ShapeKind.Pixelate:
                DrawPixelate(dc, s, doc);
                break;
        }
    }

    private static void DrawPixelate(DrawingContext dc, ShapeAnnotation s, MarkupDocument doc)
    {
        var area = new Int32Rect((int)Math.Floor(s.Rect.X), (int)Math.Floor(s.Rect.Y), (int)Math.Ceiling(s.Rect.Width), (int)Math.Ceiling(s.Rect.Height));
        area = ImageOps.Intersect(area, new Int32Rect(0, 0, doc.Pixels.Width, doc.Pixels.Height));
        if (area.Width <= 0 || area.Height <= 0) return;
        var target = new Rect(area.X, area.Y, area.Width, area.Height);
        if (s.Mosaic == null || s.MosaicRect != target)
        {
            int block = Math.Max(6, (int)Math.Round(Math.Min(area.Width, area.Height) / 9.0));
            block = Math.Min(block, 24);
            var mosaic = ImageOps.Pixelate(doc.Pixels, area, block);
            if (mosaic == null) return;
            s.Mosaic = mosaic.ToBitmap();
            s.MosaicRect = target;
        }
        // nearest-neighbour stretch -> crisp squares; the last block may be partial so draw at block grid size
        int cols = s.Mosaic.PixelWidth, rows = s.Mosaic.PixelHeight;
        double blockW = area.Width / (double)cols, blockH = area.Height / (double)rows;
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        using (var gdc = group.Open())
            gdc.DrawImage(s.Mosaic, new Rect(area.X, area.Y, cols * blockW, rows * blockH));
        dc.PushClip(new RectangleGeometry(target));
        dc.DrawDrawing(group);
        dc.Pop();
    }

    private static void DrawText(DrawingContext dc, TextAnnotation t)
    {
        if (string.IsNullOrEmpty(t.Text) || t.Editing) return;
        var ft = MakeText(t, 1.0);
        dc.DrawText(ft, t.Position);
    }

    private static void DrawCounter(DrawingContext dc, CounterAnnotation c)
    {
        double r = c.Radius;
        var rim = new Pen(Brushes.White, Math.Max(1.5, r * 0.12));
        rim.Freeze();
        dc.DrawEllipse(Brush(c.Color), rim, c.Center, r, r);
        bool light = c.Color.R * 0.299 + c.Color.G * 0.587 + c.Color.B * 0.114 > 186;
        var ft = new FormattedText(c.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            CounterFace, r * 1.15, light ? Brushes.Black : Brushes.White, 1.0);
        dc.DrawText(ft, new Point(c.Center.X - ft.Width / 2, c.Center.Y - ft.Height / 2));
    }
}
