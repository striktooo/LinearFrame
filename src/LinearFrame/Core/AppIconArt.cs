using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinearFrame.Core;

/// <summary>
/// The app icon drawn as vectors: a squircle with a soft gradient, viewfinder corners and a play glyph.
/// Rendered at the exact pixel size needed (tray icon at any DPI, .ico generation for the exe).
/// </summary>
internal static class AppIconArt
{
    public static DrawingVisual Draw(double size)
    {
        var dv = new DrawingVisual();
        using var dc = dv.RenderOpen();
        double s = size;
        bool tiny = s <= 24;

        // Squircle (superellipse n=5) - the "continuous corner" look of app icons.
        double inset = tiny ? 0 : s * 0.04;
        var squircle = Superellipse(new Rect(inset, inset, s - inset * 2, s - inset * 2), 5.0);

        var bg = new LinearGradientBrush
        {
            StartPoint = new Point(0.1, 0),
            EndPoint = new Point(0.9, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x7B, 0x61, 0xFF), 0.0),
                new GradientStop(Color.FromRgb(0x4C, 0x6F, 0xFF), 0.45),
                new GradientStop(Color.FromRgb(0x1F, 0xB6, 0xFF), 1.0),
            }
        };
        bg.Freeze();
        dc.DrawGeometry(bg, null, squircle);

        // soft top light
        if (!tiny)
        {
            var sheen = new LinearGradientBrush(Color.FromArgb(0x38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            sheen.Freeze();
            dc.PushClip(squircle);
            dc.DrawRectangle(sheen, null, new Rect(0, 0, s, s * 0.55));
            dc.Pop();
        }

        // viewfinder corners
        double m = s * (tiny ? 0.22 : 0.25);
        double len = s * (tiny ? 0.2 : 0.17);
        double th = Math.Max(1.6, s * (tiny ? 0.11 : 0.065));
        var pen = new Pen(Brushes.White, th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        var corners = new StreamGeometry();
        using (var g = corners.Open())
        {
            void Corner(Point a, Point b, Point c)
            {
                g.BeginFigure(a, false, false);
                g.LineTo(b, true, true);
                g.LineTo(c, true, true);
            }
            double l = m, t = m, r = s - m, b = s - m;
            Corner(new Point(l, t + len), new Point(l, t), new Point(l + len, t));
            Corner(new Point(r - len, t), new Point(r, t), new Point(r, t + len));
            Corner(new Point(r, b - len), new Point(r, b), new Point(r - len, b));
            Corner(new Point(l + len, b), new Point(l, b), new Point(l, b - len));
        }
        corners.Freeze();
        dc.DrawGeometry(null, pen, corners);

        // play glyph
        if (!tiny || s >= 20)
        {
            double tri = s * (tiny ? 0.24 : 0.2);
            double cx = s * 0.5 + tri * 0.12, cy = s * 0.5;
            var play = new StreamGeometry();
            using (var g = play.Open())
            {
                g.BeginFigure(new Point(cx - tri * 0.45, cy - tri * 0.55), true, true);
                g.LineTo(new Point(cx + tri * 0.6, cy), true, true);
                g.LineTo(new Point(cx - tri * 0.45, cy + tri * 0.55), true, true);
            }
            play.Freeze();
            var rounded = new Pen(Brushes.White, Math.Max(1, s * 0.03)) { LineJoin = PenLineJoin.Round };
            rounded.Freeze();
            dc.DrawGeometry(Brushes.White, rounded, play);
        }
        return dv;
    }

    public static BitmapSource Render(int px)
    {
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Draw(px));
        rtb.Freeze();
        return rtb;
    }

    public static byte[] RenderPng(int px)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Render(px)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Creates a Win32 HICON straight from PNG bytes (supported since Windows Vista).</summary>
    public static IntPtr CreateHIcon(int px)
    {
        var png = RenderPng(px);
        return Native.CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, px, px, 0);
    }

    /// <summary>Writes a multi-size .ico (PNG-compressed entries).</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = sizes.Select(RenderPng).ToArray();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int sz = sizes[i];
            w.Write((byte)(sz >= 256 ? 0 : sz));
            w.Write((byte)(sz >= 256 ? 0 : sz));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }

    private static Geometry Superellipse(Rect r, double n)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double a = r.Width / 2, b = r.Height / 2, cx = r.X + a, cy = r.Y + b;
            const int steps = 96;
            for (int i = 0; i <= steps; i++)
            {
                double t = 2 * Math.PI * i / steps;
                double ct = Math.Cos(t), st = Math.Sin(t);
                double x = cx + a * Math.Sign(ct) * Math.Pow(Math.Abs(ct), 2 / n);
                double y = cy + b * Math.Sign(st) * Math.Pow(Math.Abs(st), 2 / n);
                if (i == 0) ctx.BeginFigure(new Point(x, y), true, true);
                else ctx.LineTo(new Point(x, y), true, true);
            }
        }
        g.Freeze();
        return g;
    }
}
