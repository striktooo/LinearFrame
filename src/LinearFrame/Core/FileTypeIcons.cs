using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinearFrame.Core;

/// <summary>
/// File icons for the formats LinearFrame opens: a plain white page with the kind of file drawn on it in one flat colour
/// (blue film = video, pink note = music, green picture = photo, orange picture = moving picture) and the format written on
/// a label (MP4, MKV, MP3, JPG...), so files of different kinds are told apart at a glance in a folder.
/// </summary>
internal static class FileTypeIcons
{
    /// <summary>Bump when the drawing changes: icons on disk are made again.</summary>
    public const int DesignVersion = 1;

    public enum Kind { Video, Audio, Image, Animated }

    public static Color ColorOf(Kind k) => k switch
    {
        Kind.Video => Color.FromRgb(0x0A, 0x84, 0xFF),
        Kind.Audio => Color.FromRgb(0xFF, 0x37, 0x5F),
        Kind.Image => Color.FromRgb(0x30, 0xB0, 0x50),
        _ => Color.FromRgb(0xFF, 0x9F, 0x0A),
    };

    public static Kind KindOf(string ext)
    {
        ext = ext.ToLowerInvariant();
        if (Player.MediaFormats.Animated.Contains(ext)) return Kind.Animated;
        if (Player.MediaFormats.Audio.Contains(ext)) return Kind.Audio;
        if (Player.MediaFormats.Images.Contains(ext)) return Kind.Image;
        return Kind.Video;
    }

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static DrawingVisual Draw(Kind kind, string ext, double s)
    {
        var dv = new DrawingVisual();
        using var dc = dv.RenderOpen();
        var color = new SolidColorBrush(ColorOf(kind));
        color.Freeze();
        bool small = s < 48;

        // the page: white, a fine grey edge, the top right corner folded
        double w = Math.Round(s * (small ? 0.78 : 0.74)), h = Math.Round(s * (small ? 0.94 : 0.92));
        double x0 = Math.Round((s - w) / 2), y0 = Math.Round((s - h) / 2);
        double fold = Math.Round(w * 0.3), r = Math.Max(1, s * 0.055);
        var page = new StreamGeometry();
        using (var g = page.Open())
        {
            g.BeginFigure(new Point(x0 + r, y0), true, true);
            g.LineTo(new Point(x0 + w - fold, y0), true, true);
            g.LineTo(new Point(x0 + w, y0 + fold), true, true);
            g.LineTo(new Point(x0 + w, y0 + h - r), true, true);
            g.ArcTo(new Point(x0 + w - r, y0 + h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(x0 + r, y0 + h), true, true);
            g.ArcTo(new Point(x0, y0 + h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(x0, y0 + r), true, true);
            g.ArcTo(new Point(x0 + r, y0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
        }
        page.Freeze();
        double edge = Math.Max(1, s / 48);
        var edgePen = new Pen(new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xBE)), edge) { LineJoin = PenLineJoin.Round };
        edgePen.Freeze();
        dc.DrawGeometry(Brushes.White, edgePen, page);
        var dog = new StreamGeometry();
        using (var g = dog.Open())
        {
            g.BeginFigure(new Point(x0 + w - fold, y0), true, true);
            g.LineTo(new Point(x0 + w - fold, y0 + fold - r * 0.6), true, true);
            g.ArcTo(new Point(x0 + w - fold + r * 0.6, y0 + fold), new Size(r * 0.6, r * 0.6), 0, false, SweepDirection.Counterclockwise, true, true);
            g.LineTo(new Point(x0 + w, y0 + fold), true, true);
        }
        dog.Freeze();
        var dogFill = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE8));
        dogFill.Freeze();
        dc.DrawGeometry(dogFill, edgePen, dog);

        // the kind of file, in its colour
        double gs = small ? w * 0.62 : w * 0.5;
        double gcx = x0 + w / 2, gcy = small ? y0 + h * 0.58 : y0 + h * 0.43;
        DrawGlyph(dc, kind, new Rect(gcx - gs / 2, gcy - gs / 2, gs, gs), color, s);

        // the format on a label
        if (!small)
        {
            string text = ext.TrimStart('.').ToUpperInvariant();
            if (text.Length > 4) text = text[..4];
            double fs = s * (text.Length >= 4 ? 0.13 : 0.155);
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, fs, Brushes.White, 1.0);
            double lw = Math.Min(w - s * 0.1, ft.Width + s * 0.12), lh = Math.Round(s * 0.2);
            var label = new Rect(Math.Round(gcx - lw / 2), Math.Round(y0 + h - lh - s * 0.075), Math.Round(lw), lh);
            dc.DrawRoundedRectangle(color, null, label, lh * 0.28, lh * 0.28);
            dc.DrawText(ft, new Point(label.X + (label.Width - ft.Width) / 2, label.Y + (label.Height - ft.Height) / 2));
        }
        return dv;
    }

    private static void DrawGlyph(DrawingContext dc, Kind kind, Rect b, Brush brush, double s)
    {
        double u = b.Width / 24;
        dc.PushTransform(new TranslateTransform(b.X, b.Y));
        dc.PushTransform(new ScaleTransform(u, u));
        var round = new Pen(brush, s < 32 ? 2.6 : 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        round.Freeze();
        switch (kind)
        {
            case Kind.Video:
                // a play button
                dc.DrawGeometry(brush, new Pen(brush, 2.4) { LineJoin = PenLineJoin.Round }, Geometry.Parse("M7.5,4.5 L19.5,12 L7.5,19.5 Z"));
                break;
            case Kind.Audio:
                // a pair of notes
                dc.DrawGeometry(null, round, Geometry.Parse("M9,17.5 V5.5 L19.5,3.5 V15.5"));
                dc.DrawGeometry(brush, null, Geometry.Parse("M3.6,17.6 A2.9,2.9 0 1 0 9.4,17.6 A2.9,2.9 0 1 0 3.6,17.6 Z M14.1,15.6 A2.9,2.9 0 1 0 19.9,15.6 A2.9,2.9 0 1 0 14.1,15.6 Z"));
                dc.DrawGeometry(null, round, Geometry.Parse("M9,8.6 L19.5,6.6"));
                break;
            default:
                // a picture: a sun over two hills
                dc.DrawGeometry(brush, null, Geometry.Parse("M1.5,20.5 L8.5,10.5 L13,16.5 L15.5,13.5 L22.5,20.5 Z"));
                dc.DrawEllipse(brush, null, new Point(17.2, 6.3), 2.7, 2.7);
                if (kind == Kind.Animated)
                {
                    // moving picture: little motion lines
                    dc.DrawGeometry(null, round, Geometry.Parse("M2,4 H8 M2,7.5 H5.5"));
                }
                break;
        }
        dc.Pop();
        dc.Pop();
    }

    private static byte[] Png(Kind kind, string ext, int px)
    {
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Draw(kind, ext, px));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A multi-size .ico for one format (16 px … 256 px).</summary>
    public static void WriteIco(string path, string ext)
    {
        var kind = KindOf(ext);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        var images = sizes.Select(px => Png(kind, ext, px)).ToArray();
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int sz = sizes[i];
                bw.Write((byte)(sz >= 256 ? 0 : sz));
                bw.Write((byte)(sz >= 256 ? 0 : sz));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write(images[i].Length);
                bw.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
        }
        File.Move(tmp, path, true);
    }

    /// <summary>Where the icons live (made by the app itself - nothing to ship).</summary>
    public static string Dir => Path.Combine(AppPaths.LocalDir, "icons", "v" + DesignVersion);

    /// <summary>The icon file for a format, made if it isn't there yet.</summary>
    public static string Ensure(string ext)
    {
        var path = Path.Combine(Dir, ext.TrimStart('.').ToLowerInvariant() + ".ico");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Dir);
            WriteIco(path, ext);
        }
        return path;
    }
}
