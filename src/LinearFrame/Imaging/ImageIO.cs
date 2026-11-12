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

    public static BitmapSource? LoadThumbnail(string path, int decodeWidth)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = decodeWidth;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    public static BitmapSource? LoadFull(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = dec.Frames[0];
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
}
