using System.Windows;
using System.Windows.Media;

namespace LinearFrame.Imaging;

public static class ImageOps
{
    /// <summary>
    /// Finds the dominant colour along the image border - the "background" of a UI screenshot.
    /// Used to fill the canvas automatically when the frame is extended.
    /// </summary>
    public static Color DetectBackground(PixelBuffer img)
    {
        int w = img.Width, h = img.Height;
        if (w == 0 || h == 0) return Colors.White;
        var counts = new Dictionary<int, (int n, long b, long g, long r)>();
        var d = img.Data;
        int perimeter = 2 * (w + h);
        int step = Math.Max(1, perimeter / 6000);

        void Sample(int x, int y)
        {
            int i = (y * w + x) * 4;
            int b = d[i], g = d[i + 1], r = d[i + 2];
            int key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
            counts.TryGetValue(key, out var c);
            counts[key] = (c.n + 1, c.b + b, c.g + g, c.r + r);
        }

        // two-pixel deep ring
        for (int depth = 0; depth < Math.Min(2, Math.Min(w, h)); depth++)
        {
            for (int x = 0; x < w; x += step) { Sample(x, depth); Sample(x, h - 1 - depth); }
            for (int y = 0; y < h; y += step) { Sample(depth, y); Sample(w - 1 - depth, y); }
        }

        var best = counts.MaxBy(kv => kv.Value.n).Value;
        if (best.n == 0) return Colors.White;
        return Color.FromRgb((byte)(best.r / best.n), (byte)(best.g / best.n), (byte)(best.b / best.n));
    }

    /// <summary>Average colour of each block - a mosaic that hides the content for good.</summary>
    public static PixelBuffer? Pixelate(PixelBuffer img, Int32Rect area, int block)
    {
        area = Intersect(area, new Int32Rect(0, 0, img.Width, img.Height));
        if (area.Width <= 0 || area.Height <= 0) return null;
        block = Math.Max(2, block);
        int cols = (area.Width + block - 1) / block, rows = (area.Height + block - 1) / block;
        var dst = new PixelBuffer(cols, rows);
        var s = img.Data;
        for (int by = 0; by < rows; by++)
        for (int bx = 0; bx < cols; bx++)
        {
            int x0 = area.X + bx * block, y0 = area.Y + by * block;
            int x1 = Math.Min(x0 + block, area.X + area.Width), y1 = Math.Min(y0 + block, area.Y + area.Height);
            long sb = 0, sg = 0, sr = 0, n = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * img.Width;
                for (int x = x0; x < x1; x++)
                {
                    int i = (row + x) * 4;
                    sb += s[i]; sg += s[i + 1]; sr += s[i + 2]; n++;
                }
            }
            int o = (by * cols + bx) * 4;
            if (n == 0) n = 1;
            dst.Data[o] = (byte)(sb / n);
            dst.Data[o + 1] = (byte)(sg / n);
            dst.Data[o + 2] = (byte)(sr / n);
            dst.Data[o + 3] = 255;
        }
        return dst;
    }

    public static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        int x1 = Math.Min(a.X + a.Width, b.X + b.Width), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return x1 <= x0 || y1 <= y0 ? new Int32Rect(0, 0, 0, 0) : new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    // ------------------------------------------------------------------ resampling

    private readonly struct Contrib
    {
        public readonly int Start;
        public readonly float[] Weights;
        public readonly int Near0, Near1;
        public Contrib(int start, float[] weights, int n0, int n1) { Start = start; Weights = weights; Near0 = n0; Near1 = n1; }
    }

    private static float Lanczos3(float x)
    {
        if (x == 0f) return 1f;
        if (x <= -3f || x >= 3f) return 0f;
        float px = MathF.PI * x;
        return 3f * MathF.Sin(px) * MathF.Sin(px / 3f) / (px * px);
    }

    private static Contrib[] BuildContribs(int srcLen, int dstLen)
    {
        double scale = dstLen / (double)srcLen;
        double filterScale = Math.Max(1.0, 1.0 / scale); // widen the kernel when shrinking
        double support = 3.0 * filterScale;
        var result = new Contrib[dstLen];
        for (int i = 0; i < dstLen; i++)
        {
            double center = (i + 0.5) / scale - 0.5;
            int start = (int)Math.Floor(center - support) + 1;
            int end = (int)Math.Floor(center + support);
            var w = new float[end - start + 1];
            float sum = 0;
            for (int j = start; j <= end; j++)
            {
                float v = Lanczos3((float)((j - center) / filterScale));
                w[j - start] = v;
                sum += v;
            }
            if (sum != 0) for (int k = 0; k < w.Length; k++) w[k] /= sum;
            int n0 = Math.Clamp((int)Math.Floor(center), 0, srcLen - 1);
            int n1 = Math.Clamp(n0 + 1, 0, srcLen - 1);
            result[i] = new Contrib(start, w, n0, n1);
        }
        return result;
    }

    /// <summary>
    /// High quality Lanczos-3 resize with anti-ringing (keeps UI edges crisp without halos when upscaling).
    /// Runs in parallel; safe to call from any thread.
    /// </summary>
    public static PixelBuffer Resize(PixelBuffer src, int dstW, int dstH)
    {
        if (dstW == src.Width && dstH == src.Height) return new PixelBuffer(src.Width, src.Height, (byte[])src.Data.Clone());
        int sw = src.Width, sh = src.Height;
        var cx = BuildContribs(sw, dstW);
        var cy = BuildContribs(sh, dstH);
        bool upX = dstW > sw, upY = dstH > sh;
        const float antiring = 0.8f;

        // horizontal pass -> float buffer (sh rows × dstW × 4)
        var tmp = new float[(long)sh * dstW * 4];
        var s = src.Data;
        Parallel.For(0, sh, y =>
        {
            int srow = y * sw;
            long trow = (long)y * dstW * 4;
            for (int x = 0; x < dstW; x++)
            {
                var c = cx[x];
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < c.Weights.Length; k++)
                {
                    int sx = Math.Clamp(c.Start + k, 0, sw - 1);
                    int i = (srow + sx) * 4;
                    float wgt = c.Weights[k];
                    b += s[i] * wgt; g += s[i + 1] * wgt; r += s[i + 2] * wgt; a += s[i + 3] * wgt;
                }
                if (upX)
                {
                    int i0 = (srow + c.Near0) * 4, i1 = (srow + c.Near1) * 4;
                    b = AntiRing(b, s[i0], s[i1], antiring);
                    g = AntiRing(g, s[i0 + 1], s[i1 + 1], antiring);
                    r = AntiRing(r, s[i0 + 2], s[i1 + 2], antiring);
                    a = AntiRing(a, s[i0 + 3], s[i1 + 3], antiring);
                }
                long o = trow + x * 4;
                tmp[o] = b; tmp[o + 1] = g; tmp[o + 2] = r; tmp[o + 3] = a;
            }
        });

        // vertical pass -> bytes
        var dst = new PixelBuffer(dstW, dstH);
        var d = dst.Data;
        Parallel.For(0, dstH, y =>
        {
            var c = cy[y];
            int drow = y * dstW * 4;
            long n0 = (long)c.Near0 * dstW * 4, n1 = (long)c.Near1 * dstW * 4;
            for (int x = 0; x < dstW; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < c.Weights.Length; k++)
                {
                    int sy = Math.Clamp(c.Start + k, 0, sh - 1);
                    long i = ((long)sy * dstW + x) * 4;
                    float wgt = c.Weights[k];
                    b += tmp[i] * wgt; g += tmp[i + 1] * wgt; r += tmp[i + 2] * wgt; a += tmp[i + 3] * wgt;
                }
                if (upY)
                {
                    long i0 = n0 + x * 4, i1 = n1 + x * 4;
                    b = AntiRing(b, tmp[i0], tmp[i1], antiring);
                    g = AntiRing(g, tmp[i0 + 1], tmp[i1 + 1], antiring);
                    r = AntiRing(r, tmp[i0 + 2], tmp[i1 + 2], antiring);
                    a = AntiRing(a, tmp[i0 + 3], tmp[i1 + 3], antiring);
                }
                int o = drow + x * 4;
                d[o] = ToByte(b); d[o + 1] = ToByte(g); d[o + 2] = ToByte(r); d[o + 3] = ToByte(a);
            }
        });
        return dst;
    }

    private static float AntiRing(float v, float a, float b, float strength)
    {
        float lo = Math.Min(a, b), hi = Math.Max(a, b);
        float clamped = v < lo ? lo : v > hi ? hi : v;
        return v + (clamped - v) * strength;
    }

    private static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
}
