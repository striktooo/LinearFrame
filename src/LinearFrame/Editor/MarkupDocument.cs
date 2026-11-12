using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using LinearFrame.Core;
using LinearFrame.Imaging;

namespace LinearFrame.Editor;

public enum FillMode { Auto, Color, Transparent }

/// <summary>A screenshot being marked up: original pixels + frame (crop/extend) + annotations. Non-destructive.</summary>
public sealed class MarkupDocument
{
    private sealed record Snapshot(Int32Rect Frame, FillMode Fill, Color FillColor, List<Annotation> Items);

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    public BitmapSource Image { get; }
    public PixelBuffer Pixels { get; }
    public int Width => Pixels.Width;
    public int Height => Pixels.Height;
    public Int32Rect ImageRect => new(0, 0, Width, Height);

    /// <summary>Visible canvas in image pixels. May extend beyond the image (filled with the background).</summary>
    public Int32Rect Frame { get; set; }
    public FillMode Fill { get; set; } = FillMode.Auto;
    public Color FillColor { get; set; } = Colors.White;
    public Color DetectedBackground { get; }
    public List<Annotation> Items { get; private set; } = new();

    public string? FilePath { get; set; }
    public ExportSize ExportSize { get; set; }
    public bool Dirty { get; set; }

    /// <summary>Stroke widths scale with the screenshot so markup looks the same on small and huge captures.</summary>
    public double BaseUnit => Math.Clamp(Math.Sqrt(Math.Max(Width, Height)) / 9.0, 1.6, 7.0);

    public Color EffectiveFill => Fill switch
    {
        FillMode.Auto => DetectedBackground,
        FillMode.Color => FillColor,
        _ => Colors.Transparent,
    };

    public MarkupDocument(BitmapSource image)
    {
        Pixels = PixelBuffer.FromBitmap(image);
        Image = image.Format == PixelFormats.Bgra32 && Math.Abs(image.DpiX - 96) < 0.1 ? image : Pixels.ToBitmap();
        if (!Image.IsFrozen) Image.Freeze();
        Frame = ImageRect;
        DetectedBackground = ImageOps.DetectBackground(Pixels);
        FillColor = DetectedBackground;
    }

    public static MarkupDocument FromFile(string path)
    {
        var bmp = ImageIO.LoadFull(path) ?? throw new InvalidOperationException("Cannot read image: " + path);
        return new MarkupDocument(bmp) { FilePath = path, ExportSize = App.Instance?.Settings.DefaultExportSize ?? ExportSize.Original };
    }

    public bool IsPristine => Items.Count == 0 && Frame == ImageRect;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private Snapshot Take() => new(Frame, Fill, FillColor, Items.Select(i => i.Clone()).ToList());

    private void Restore(Snapshot s)
    {
        Frame = s.Frame;
        Fill = s.Fill;
        FillColor = s.FillColor;
        Items = s.Items.Select(i => i.Clone()).ToList();
    }

    /// <summary>Call before every change.</summary>
    public void Checkpoint()
    {
        _undo.Push(Take());
        if (_undo.Count > 120)
        {
            var keep = _undo.Take(100).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
        _redo.Clear();
        Dirty = true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Take());
        Restore(_undo.Pop());
        Dirty = true;
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Take());
        Restore(_redo.Pop());
        Dirty = true;
        return true;
    }

    public int NextCounter() => Items.OfType<CounterAnnotation>().Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;
}

/// <summary>Renders the final image: background fill + (Lanczos-scaled) screenshot + vector markup at full resolution.</summary>
public static class Exporter
{
    public sealed record Prepared(BitmapSource? Scaled, Int32Rect Visible, int Width, int Height, double Sx, double Sy);

    public static (int W, int H) OutputSize(MarkupDocument doc, ExportSize size) =>
        ExportSizes.TargetSize(size, doc.Frame.Width, doc.Frame.Height);

    /// <summary>Heavy pixel work (resampling) that may run on a worker thread.</summary>
    public static Prepared PrepareBase(MarkupDocument doc, ExportSize size)
    {
        var f = doc.Frame;
        var (W, H) = OutputSize(doc, size);
        double sx = W / (double)f.Width, sy = H / (double)f.Height;
        var visible = ImageOps.Intersect(f, doc.ImageRect);
        if (visible.Width <= 0 || visible.Height <= 0) return new Prepared(null, visible, W, H, sx, sy);
        if (W == f.Width && H == f.Height)
        {
            var crop = visible == doc.ImageRect ? doc.Image : doc.Pixels.Crop(visible).ToBitmap();
            return new Prepared(crop, visible, W, H, 1, 1);
        }
        // scale the visible part of the screenshot so its edges land on whole output pixels
        int x0 = (int)Math.Round((visible.X - f.X) * sx), y0 = (int)Math.Round((visible.Y - f.Y) * sy);
        int x1 = (int)Math.Round((visible.X + visible.Width - f.X) * sx), y1 = (int)Math.Round((visible.Y + visible.Height - f.Y) * sy);
        var scaled = ImageOps.Resize(doc.Pixels.Crop(visible), Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)).ToBitmap();
        return new Prepared(scaled, visible, W, H, sx, sy);
    }

    /// <summary>Composition (must run on a thread with a Dispatcher - the UI thread).</summary>
    public static BitmapSource Compose(MarkupDocument doc, Prepared p)
    {
        var f = doc.Frame;
        int W = p.Width, H = p.Height;
        double sx = p.Sx, sy = p.Sy;

        if (doc.Items.Count == 0 && f == doc.ImageRect && W == f.Width && H == f.Height) return doc.Image;
        if (doc.Items.Count == 0 && p.Scaled != null && p.Visible == f && p.Scaled.PixelWidth == W && p.Scaled.PixelHeight == H) return p.Scaled;

        var root = new ContainerVisual();
        var baseLayer = new DrawingVisual();
        var toCanvas = new MatrixTransform(sx, 0, 0, sy, -f.X * sx, -f.Y * sy);
        using (var dc = baseLayer.RenderOpen())
        {
            var fill = doc.EffectiveFill;
            if (fill.A > 0)
            {
                var b = new SolidColorBrush(fill);
                b.Freeze();
                dc.DrawRectangle(b, null, new Rect(0, 0, W, H));
            }
            if (p.Scaled != null)
            {
                double x = Math.Round((p.Visible.X - f.X) * sx), y = Math.Round((p.Visible.Y - f.Y) * sy);
                dc.DrawImage(p.Scaled, new Rect(x, y, p.Scaled.PixelWidth, p.Scaled.PixelHeight));
            }
            dc.PushTransform(toCanvas);
            foreach (var a in doc.Items.Where(i => i.IsBaseLayer)) AnnotationRenderer.Draw(dc, a, doc);
            dc.Pop();
        }
        root.Children.Add(baseLayer);

        var marks = doc.Items.Where(i => !i.IsBaseLayer).ToList();
        if (marks.Count > 0)
        {
            double s = (sx + sy) / 2;
            var annoLayer = new DrawingVisual
            {
                Effect = new DropShadowEffect { BlurRadius = 5 * s, ShadowDepth = 1.6 * s, Opacity = 0.35, Direction = 270, RenderingBias = RenderingBias.Quality },
            };
            using (var dc = annoLayer.RenderOpen())
            {
                dc.PushTransform(toCanvas);
                foreach (var a in marks) AnnotationRenderer.Draw(dc, a, doc);
                dc.Pop();
            }
            root.Children.Add(annoLayer);
        }

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    public static async Task<BitmapSource> RenderAsync(MarkupDocument doc, ExportSize size)
    {
        var prepared = await Task.Run(() => PrepareBase(doc, size));
        return Compose(doc, prepared);
    }

    public static BitmapSource Render(MarkupDocument doc, ExportSize size) => Compose(doc, PrepareBase(doc, size));
}
