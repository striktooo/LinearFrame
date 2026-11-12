using System.Windows;

namespace LinearFrame.Imaging;

/// <summary>
/// Compares a normal (1×) screenshot with a high-resolution re-render of the same thing:
/// where exactly it is in the big picture, whether it really shows the same content, and whether the big picture
/// carries real extra detail (a window that was merely stretched by Windows does not).
/// </summary>
public static class ImageMatch
{
    /// <summary>Greyscale picture as bytes (BT.601 luma).</summary>
    public sealed class Gray
    {
        public readonly int Width, Height;
        public readonly byte[] Data;
        public Gray(int w, int h) { Width = w; Height = h; Data = new byte[w * h]; }
    }

    public static Gray ToGray(PixelBuffer src) => ToGray(src, new Int32Rect(0, 0, src.Width, src.Height));

    public static Gray ToGray(PixelBuffer src, Int32Rect r)
    {
        var g = new Gray(r.Width, r.Height);
        var d = src.Data;
        Parallel.For(0, r.Height, y =>
        {
            int si = ((r.Y + y) * src.Width + r.X) * 4, di = y * r.Width;
            for (int x = 0; x < r.Width; x++, si += 4)
                g.Data[di + x] = (byte)((d[si] * 29 + d[si + 1] * 150 + d[si + 2] * 77 + 128) >> 8);
        });
        return g;
    }

    /// <summary>Shrinks by a whole factor, averaging every n×n block (what a 1× render of the same content looks like).</summary>
    public static Gray Shrink(Gray src, int n)
    {
        if (n <= 1) return src;
        int w = src.Width / n, h = src.Height / n;
        var g = new Gray(Math.Max(1, w), Math.Max(1, h));
        if (w < 1 || h < 1) return g;
        int area = n * n, half = area / 2;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int yy = 0; yy < n; yy++)
                {
                    int row = (y * n + yy) * src.Width + x * n;
                    for (int xx = 0; xx < n; xx++) sum += src.Data[row + xx];
                }
                g.Data[y * w + x] = (byte)((sum + half) / area);
            }
        });
        return g;
    }

    /// <summary>3×3 box blur - evens out the differences between ClearType at 1× and smooth text rendered at 4×.</summary>
    public static Gray Blur(Gray src)
    {
        int w = src.Width, h = src.Height;
        var g = new Gray(w, h);
        if (w < 3 || h < 3) { Array.Copy(src.Data, g.Data, src.Data.Length); return g; }
        Parallel.For(0, h, y =>
        {
            int y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1), sum = 0, count = 0;
                for (int yy = y0; yy <= y1; yy++)
                    for (int xx = x0; xx <= x1; xx++) { sum += src.Data[yy * w + xx]; count++; }
                g.Data[y * w + x] = (byte)(sum / count);
            }
        });
        return g;
    }

    public readonly record struct Location(int Dx, int Dy, double Difference);

    /// <summary>
    /// Finds <paramref name="template"/> inside <paramref name="area"/>, looking up to <paramref name="radius"/> pixels
    /// around the place where it is expected. Returns the offset from the expected place and the mean absolute
    /// difference (0–255) there. Flat content matches everywhere, so equal scores prefer the expected place.
    /// </summary>
    public static Location Locate(Gray area, Gray template, int expectX, int expectY, int radius)
    {
        int tw = template.Width, th = template.Height;
        int ox = expectX, oy = expectY;

        // sample at most ~40k points of the template (evenly spread) - plenty for a reliable score, and fast
        int step = Math.Max(1, (int)Math.Sqrt(tw * (double)th / 40000));
        double Diff(int dx, int dy)
        {
            long sum = 0; int n = 0;
            for (int y = 0; y < th; y += step)
            {
                int ti = y * tw, ai = (oy + dy + y) * area.Width + ox + dx;
                for (int x = 0; x < tw; x += step, n++) sum += Math.Abs(template.Data[ti + x] - area.Data[ai + x]);
            }
            return n == 0 ? 255 : sum / (double)n;
        }

        var offsets = new List<(int dx, int dy)>();
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x0 = ox + dx, y0 = oy + dy;
                if (x0 >= 0 && y0 >= 0 && x0 + tw <= area.Width && y0 + th <= area.Height) offsets.Add((dx, dy));
            }
        if (offsets.Count == 0) return new Location(0, 0, 255);
        var scores = new double[offsets.Count];
        Parallel.For(0, offsets.Count, i => scores[i] = Diff(offsets[i].dx, offsets[i].dy));

        int best = -1; double bestScore = double.MaxValue;
        for (int i = 0; i < offsets.Count; i++)
        {
            // a tiny pull towards the expected place breaks ties on flat or repeating content
            double s = scores[i] + 0.004 * (Math.Abs(offsets[i].dx) + Math.Abs(offsets[i].dy));
            if (s < bestScore) { bestScore = s; best = i; }
        }
        return new Location(offsets[best].dx, offsets[best].dy, scores[best]);
    }

    /// <summary>
    /// How alike the structure of two pictures is, independent of brightness and contrast (zero-mean normalised
    /// correlation, −1…1): the same content gives ≈0.9, different content clearly less - even in a low-contrast dark UI,
    /// where plain differences are small no matter what is shown. NaN when either picture is flat (nothing to compare).
    /// </summary>
    public static double Correlation(Gray area, Gray template, int x0, int y0)
    {
        int tw = template.Width, th = template.Height;
        if (x0 < 0 || y0 < 0 || x0 + tw > area.Width || y0 + th > area.Height) return double.NaN;
        int step = Math.Max(1, (int)Math.Sqrt(tw * (double)th / 160000));
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; long n = 0;
        for (int y = 0; y < th; y += step)
        {
            int ti = y * tw, ai = (y0 + y) * area.Width + x0;
            for (int x = 0; x < tw; x += step, n++)
            {
                double a = area.Data[ai + x], b = template.Data[ti + x];
                sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
            }
        }
        if (n == 0) return double.NaN;
        double va = saa / n - sa / n * (sa / n), vb = sbb / n - sb / n * (sb / n);
        if (va < 1.5 || vb < 1.5) return double.NaN; // flat
        return (sab / n - sa / n * (sb / n)) / Math.Sqrt(va * vb);
    }

    /// <summary>Mean squared step between neighbouring pixels - "how much sharp edge" a picture has per pixel.</summary>
    public static double EdgeEnergy(Gray g)
    {
        int w = g.Width, h = g.Height;
        if (w < 2 || h < 2) return 0;
        int rowStep = Math.Max(1, h / 1200); // big pictures: every n-th row is enough
        double total = 0; long count = 0;
        object gate = new();
        Parallel.For(0, (h - 1) / rowStep, () => (Sum: 0.0, N: 0L), (i, _, acc) =>
        {
            int y = i * rowStep, row = y * w;
            long s = 0;
            for (int x = 0; x < w - 1; x++)
            {
                int a = g.Data[row + x], dx = g.Data[row + x + 1] - a, dy = g.Data[row + w + x] - a;
                s += dx * dx + dy * dy;
            }
            return (acc.Sum + s, acc.N + (w - 1));
        }, acc => { lock (gate) { total += acc.Sum; count += acc.N; } });
        return count == 0 ? 0 : total / count;
    }

    /// <summary>
    /// How much sharper a re-render at <paramref name="scale"/>× is than the same picture merely stretched:
    /// about 1 for a stretched bitmap, about <paramref name="scale"/> for content really drawn at that size.
    /// (Sharp edges keep their height but get <c>scale</c>× longer; a stretched edge is also smeared over <c>scale</c> pixels.)
    /// Returns NaN for flat content, where there is nothing to judge.
    /// </summary>
    public static double DetailGain(Gray hd, Gray normal, double scale)
    {
        double e1 = EdgeEnergy(normal), eN = EdgeEnergy(hd);
        if (e1 < 0.5) return double.NaN;
        return eN * scale * scale / e1;
    }

    /// <summary>Cheap fingerprint of a picture (to notice "nothing changed since the last look").</summary>
    public static ulong Fingerprint(PixelBuffer p)
    {
        ulong hash = 1469598103934665603UL;
        var d = p.Data;
        int step = Math.Max(4, (d.Length / 60000) & ~3);
        for (int i = 0; i + 2 < d.Length; i += step)
        {
            hash = (hash ^ d[i]) * 1099511628211UL;
            hash = (hash ^ d[i + 1]) * 1099511628211UL;
            hash = (hash ^ d[i + 2]) * 1099511628211UL;
        }
        return hash;
    }
}
