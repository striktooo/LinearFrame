using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Core;

namespace LinearFrame.Imaging;

public static class ImageIO
{
    public static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodeJpg(BitmapSource bmp, int quality = 92)
    {
        BitmapSource src = bmp;
        if (bmp.Format == PixelFormats.Bgra32 || bmp.Format == PixelFormats.Pbgra32)
            src = Flatten(bmp, Colors.White);
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static BitmapSource Flatten(BitmapSource bmp, Color background)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
            dc.DrawImage(bmp, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgr32, null, 0);
        conv.Freeze();
        return conv;
    }

    public static string Extension(ImageFormatKind f) => f == ImageFormatKind.Jpg ? ".jpg" : ".png";

    public static byte[] Encode(BitmapSource bmp, ImageFormatKind f) => f == ImageFormatKind.Jpg ? EncodeJpg(bmp) : EncodePng(bmp);

    public static string UniquePath(string dir, string baseName, string ext)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '-');
        string p = Path.Combine(dir, baseName + ext);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{baseName} ({i}){ext}");
        return p;
    }

    /// <summary>Saves a capture into the screenshot folder (or overwrites <paramref name="existing"/>).</summary>
    public static string SaveScreenshot(BitmapSource bmp, AppSettings s, string? existing = null, string nameKey = "Capture.FileName")
    {
        string path;
        ImageFormatKind fmt = s.ImageFormat;
        if (existing != null)
        {
            path = existing;
            fmt = Path.GetExtension(existing).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                  Path.GetExtension(existing).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? ImageFormatKind.Jpg : ImageFormatKind.Png;
        }
        else
        {
            var dir = s.ScreenshotFolder;
            Directory.CreateDirectory(dir);
            path = UniquePath(dir, L.F(nameKey, DateTime.Now), Extension(fmt));
        }
        var bytes = Encode(bmp, fmt);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
        return path;
    }

    /// <summary>Writes already-encoded bytes as a new file in the screenshot folder. Thread-safe.</summary>
    public static string SaveScreenshotBytes(byte[] bytes, AppSettings s, string nameKey = "Capture.FileName", ImageFormatKind? format = null)
    {
        var dir = s.ScreenshotFolder;
        Directory.CreateDirectory(dir);
        var path = UniquePath(dir, L.F(nameKey, DateTime.Now), Extension(format ?? s.ImageFormat));
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
        return path;
    }

    public static bool CopyToClipboard(BitmapSource bmp, byte[]? png = null)
    {
        try
        {
            png ??= EncodePng(bmp);
            var data = new DataObject();
            data.SetImage(bmp);
            data.SetData("PNG", new MemoryStream(png), false);
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, true);
                    return true;
                }
                catch (COMException) { Thread.Sleep(60); }
                catch (ExternalException) { Thread.Sleep(60); }
            }
        }
        catch (Exception ex) { Log.Error("Clipboard failed", ex); }
        return false;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static BitmapSource? LoadThumbnail(string path, int decodeWidth)
    {
        try
        {
            int orientation = Probe(path)?.Orientation ?? 1;
            using var fs = OpenRead(path);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (orientation >= 5) bi.DecodePixelHeight = decodeWidth; else bi.DecodePixelWidth = decodeWidth;
            bi.StreamSource = fs;
            bi.EndInit();
            var shown = Orient(bi, orientation);
            if (!shown.IsFrozen) shown.Freeze();
            return shown;
        }
        catch { return null; }
    }

    public static BitmapSource? LoadFull(string path)
    {
        try
        {
            int orientation = Probe(path)?.Orientation ?? 1;
            using var fs = OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = Orient(dec.Frames[0], orientation); // a phone photo stands the right way up
            if (frame.Format != PixelFormats.Bgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            // normalise DPI to 96 so 1 image pixel == 1 unit everywhere
            var pb = PixelBuffer.FromBitmap(frame);
            return pb.ToBitmap();
        }
        catch (Exception ex)
        {
            Log.Error("Load image failed", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ viewing pictures

    /// <summary>What a picture file holds, read from its header only (cheap, any thread).</summary>
    public sealed record ImageInfo(int Width, int Height, int Orientation, int Frames, bool HasAlpha)
    {
        /// <summary>Size the way the picture is shown (the camera's rotation applied).</summary>
        public int ShownWidth => Orientation >= 5 ? Height : Width;
        public int ShownHeight => Orientation >= 5 ? Width : Height;
    }

    public static ImageInfo? Probe(string path)
    {
        try
        {
            using var fs = OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var f = dec.Frames[0];
            return new ImageInfo(f.PixelWidth, f.PixelHeight, ReadOrientation(f), dec.Frames.Count, HasAlpha(f));
        }
        catch { return null; }
    }

    /// <summary>EXIF orientation 1–8 (how the camera was held); 1 when the file says nothing.</summary>
    private static int ReadOrientation(BitmapFrame f)
    {
        try
        {
            if (f.Metadata is not BitmapMetadata md) return 1;
            foreach (var q in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}" })
            {
                object? v = null;
                try { v = md.GetQuery(q); } catch { /* not in this format */ }
                int o = v switch { ushort u => u, short s => s, int i => i, uint ui => (int)ui, _ => 0 };
                if (o is >= 1 and <= 8) return o;
            }
        }
        catch { /* no metadata (BMP, most PNGs) */ }
        return 1;
    }

    private static bool HasAlpha(BitmapFrame f)
    {
        var fmt = f.Format;
        if (fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32 || fmt == PixelFormats.Rgba64 || fmt == PixelFormats.Prgba64
            || fmt == PixelFormats.Rgba128Float || fmt == PixelFormats.Prgba128Float) return true;
        try { return f.Palette != null && f.Palette.Colors.Any(c => c.A < 255); } catch { return false; }
    }

    /// <summary>Turns the stored pixels the way the camera was held (EXIF orientation).</summary>
    public static BitmapSource Orient(BitmapSource src, int orientation)
    {
        if (orientation is < 2 or > 8) return src;
        var t = new TransformGroup();
        if (orientation is 2 or 4 or 5 or 7) t.Children.Add(new ScaleTransform(orientation == 4 ? 1 : -1, orientation == 4 ? -1 : 1));
        double angle = orientation switch { 3 => 180, 5 => 270, 6 => 90, 7 => 90, 8 => 270, _ => 0 };
        if (angle != 0) t.Children.Add(new RotateTransform(angle));
        return new TransformedBitmap(src, t);
    }

    /// <summary>
    /// The picture ready to be shown: colour profile applied, the camera's rotation applied, at most
    /// <paramref name="maxSide"/> pixels on its longer side (0 = every pixel). JPEGs shrink while decoding, so a
    /// screen-sized copy of a 50-megapixel photo takes a few milliseconds. Frozen - safe to hand to the UI thread.
    /// </summary>
    public static BitmapSource Decode(string path, ImageInfo info, int maxSide)
    {
        using var fs = OpenRead(path);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = fs;
        if (maxSide > 0 && Math.Max(info.Width, info.Height) > maxSide)
        {
            if (info.Width >= info.Height) bi.DecodePixelWidth = maxSide; else bi.DecodePixelHeight = maxSide;
        }
        bi.EndInit();
        BitmapSource src = Orient(bi, info.Orientation);
        // one plain format the GPU takes as it is (CMYK, 16-bit, grey and palette pictures are converted once, here)
        var want = info.HasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        if (src.Format != want) src = new FormatConvertedBitmap(src, want, null, 0);
        var cached = new CachedBitmap(src, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }

    /// <summary>Moving pictures (GIF, WebP, PNG, AVIF animations) belong in the player, still ones in the viewer.</summary>
    public static bool IsAnimated(string path, ImageInfo? info)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".gif") return info?.Frames > 1;
            if (ext == ".apng") return true;
            Span<byte> head = stackalloc byte[64];
            int n;
            using (var fs = OpenRead(path)) n = fs.Read(head);
            head = head[..n];
            // WebP: RIFF....WEBPVP8X with the "animation" flag
            if (n >= 21 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8) && head[12..16].SequenceEqual("VP8X"u8))
                return (head[20] & 0x02) != 0;
            // AVIF image sequence: ftyp brand "avis"
            if (n >= 12 && head[4..8].SequenceEqual("ftyp"u8) && head[8..12].SequenceEqual("avis"u8)) return true;
            // APNG: an acTL chunk before the first IDAT
            if (n >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return HasPngChunkBeforeData(path, "acTL"u8.ToArray());
        }
        catch { /* unreadable: let the viewer say so */ }
        return false;
    }

    private static bool HasPngChunkBeforeData(string path, byte[] type)
    {
        using var fs = OpenRead(path);
        var buf = new byte[8];
        fs.Position = 8;
        for (int i = 0; i < 64 && fs.Read(buf, 0, 8) == 8; i++)
        {
            int len = buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3];
            if (buf.AsSpan(4, 4).SequenceEqual(type)) return true;
            if (buf.AsSpan(4, 4).SequenceEqual("IDAT"u8) || len < 0) return false;
            fs.Position += len + 4; // data + CRC
        }
        return false;
    }
}
