using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Core;

namespace LinearFrame.Imaging;

/// <summary>Plain BGRA32 pixel buffer (stride = width * 4) for fast processing off the UI thread.</summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public int Stride => Width * 4;

    public PixelBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new byte[(long)width * height * 4];
    }

    public PixelBuffer(int width, int height, byte[] data)
    {
        Width = width;
        Height = height;
        Data = data;
    }

    public static PixelBuffer FromBitmap(BitmapSource src)
    {
        BitmapSource b = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var pb = new PixelBuffer(b.PixelWidth, b.PixelHeight);
        b.CopyPixels(pb.Data, pb.Stride, 0);
        if (src.Format == PixelFormats.Bgr32 || src.Format == PixelFormats.Bgr24) pb.ForceOpaque();
        return pb;
    }

    public void ForceOpaque()
    {
        var d = Data;
        for (int i = 3; i < d.Length; i += 4) d[i] = 255;
    }

    public BitmapSource ToBitmap()
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Data, Stride);
        bmp.Freeze();
        return bmp;
    }

    public PixelBuffer Crop(Int32Rect r)
    {
        var dst = new PixelBuffer(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(Data, ((r.Y + y) * Width + r.X) * 4, dst.Data, y * dst.Stride, r.Width * 4);
        return dst;
    }

    public Color GetPixel(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        int i = (y * Width + x) * 4;
        return Color.FromArgb(Data[i + 3], Data[i + 2], Data[i + 1], Data[i]);
    }

    public bool HasTransparency()
    {
        var d = Data;
        for (int i = 3; i < d.Length; i += 4) if (d[i] != 255) return true;
        return false;
    }
}

public static class ExportSizes
{
    public static string Label(ExportSize s) => L.T("Size." + s);

    public static (int W, int H)? Box(ExportSize s) => s switch
    {
        ExportSize.FullHD => (1920, 1080),
        ExportSize.QHD => (2560, 1440),
        ExportSize.UHD4K => (3840, 2160),
        ExportSize.UHD8K => (7680, 4320),
        _ => null,
    };

    /// <summary>Scale that fits (w × h) into the target box (rotated for portrait images).</summary>
    public static double ScaleFor(ExportSize s, int w, int h)
    {
        var box = Box(s);
        if (box == null || w <= 0 || h <= 0) return 1.0;
        var (bw, bh) = box.Value;
        if (h > w) (bw, bh) = (bh, bw);
        return Math.Min(bw / (double)w, bh / (double)h);
    }

    public static (int W, int H) TargetSize(ExportSize s, int w, int h)
    {
        // a frame that is (almost) exactly 16:9 lands exactly on 1920×1080 / 2560×1440 / 3840×2160 / 7680×4320
        if (Box(s) is var (bw, bh) && w > 0 && h > 0)
        {
            if (h > w) (bw, bh) = (bh, bw);
            if (Math.Abs(w / (double)h - bw / (double)bh) < 0.006 * (bw / (double)bh)) return (bw, bh);
        }
        double k = ScaleFor(s, w, h);
        return (Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
    }
}
