using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;

namespace LinearFrame.Imaging;

/// <summary>
/// Reads QR codes in a picture (ISO/IEC 18004): a screenshot of a code on a web page as well as a photo of one on paper,
/// turned, at an angle, with some damage. Finder patterns are found by their 1:1:3:1:1 rhythm, the code is sampled
/// through a perspective transform, its format and version are read, and Reed-Solomon error correction repairs what the
/// picture got wrong before the data is decoded. Several codes in one picture are all read.
/// </summary>
internal static class QrReader
{
    /// <summary>A code that was read: its text and where it is in the picture (corners clockwise from top-left, pixels).</summary>
    public sealed record Code(string Text, Point[] Corners)
    {
        public Rect Bounds
        {
            get
            {
                double l = Corners.Min(p => p.X), t = Corners.Min(p => p.Y), r = Corners.Max(p => p.X), b = Corners.Max(p => p.Y);
                return new Rect(l, t, r - l, b - t);
            }
        }

        /// <summary>A web address to open (with "https://" added when the code only says "www...").</summary>
        public string? Link
        {
            get
            {
                var t = Text.Trim();
                if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return Uri.TryCreate(t, UriKind.Absolute, out _) ? t : null;
                if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && !t.Contains(' '))
                    return Uri.TryCreate("https://" + t, UriKind.Absolute, out _) ? "https://" + t : null;
                return null;
            }
        }
    }

    /// <summary>All the QR codes in a picture (empty when there are none).</summary>
    public static List<Code> Read(PixelBuffer px)
    {
        var found = new List<Code>();
        try
        {
            // big photos are read at up to ~2000 px (a code there is big too); the coordinates are scaled back
            int f = 1;
            while (Math.Max(px.Width, px.Height) / f > 2200) f++;
            var gray = Gray(px, f, out int w, out int h);
            // dark on light, light on dark; then once more with speckles smoothed away (a grainy photo, a noisy scan)
            for (int pass = 0; pass < 2 && found.Count == 0; pass++)
            {
                if (pass == 1) gray = Median3(gray, w, h);
                foreach (bool invert in new[] { false, true })
                {
                    var bits = Binarize(gray, w, h, invert);
                    foreach (var c in Find(bits, w, h))
                    {
                        var code = new Code(c.Text, c.Corners.Select(p => new Point(p.X * f, p.Y * f)).ToArray());
                        if (!found.Any(x => x.Text == code.Text && (x.Bounds.Contains(code.Bounds.TopLeft) || code.Bounds.Contains(x.Bounds.TopLeft))))
                            found.Add(code);
                    }
                    if (found.Count > 0) break; // dark-on-light found: no need to try light-on-dark
                }
            }
        }
        catch (Exception ex) { Core.Log.Warn("QR reading: " + ex.Message); }
        return found;
    }

    // =================================================================== picture → black and white

    private static byte[] Gray(PixelBuffer px, int f, out int w, out int h)
    {
        w = px.Width / f;
        h = px.Height / f;
        var g = new byte[w * h];
        var d = px.Data;
        int stride = px.Width * 4;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int yy = 0; yy < f; yy++)
                {
                    int o = (y * f + yy) * stride + x * f * 4;
                    for (int xx = 0; xx < f; xx++, o += 4)
                    {
                        // transparent counts as white (a code on a transparent PNG)
                        int a = d[o + 3];
                        int lum = (d[o + 2] * 299 + d[o + 1] * 587 + d[o] * 114) / 1000;
                        sum += (lum * a + 255 * (255 - a)) / 255;
                    }
                }
                g[y * w + x] = (byte)(sum / (f * f));
            }
        return g;
    }

    /// <summary>
    /// 3 × 3 median: single wrong pixels go, edges stay. Done as a sorting network (19 compare-and-swaps give the middle
    /// one of nine) on a whole run of pixels at once - sorting every pixel's neighbourhood on its own took 150 ms per
    /// megapixel, and every picture without a code (nearly every screenshot) went through it.
    /// </summary>
    internal static byte[] Median3(byte[] g, int w, int h)
    {
        var r = (byte[])g.Clone(); // the outermost pixels stay as they are
        if (w < 3 || h < 3) return r;
        int n = System.Numerics.Vector<byte>.Count;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            ReadOnlySpan<byte> a = g.AsSpan(row - w, w), b = g.AsSpan(row, w), c = g.AsSpan(row + w, w);
            var dst = r.AsSpan(row, w);
            int x = 1;
            if (System.Numerics.Vector.IsHardwareAccelerated)
            {
                for (; x + n <= w - 1; x += n)
                {
                    var p0 = new System.Numerics.Vector<byte>(a[(x - 1)..]); var p1 = new System.Numerics.Vector<byte>(a[x..]); var p2 = new System.Numerics.Vector<byte>(a[(x + 1)..]);
                    var p3 = new System.Numerics.Vector<byte>(b[(x - 1)..]); var p4 = new System.Numerics.Vector<byte>(b[x..]); var p5 = new System.Numerics.Vector<byte>(b[(x + 1)..]);
                    var p6 = new System.Numerics.Vector<byte>(c[(x - 1)..]); var p7 = new System.Numerics.Vector<byte>(c[x..]); var p8 = new System.Numerics.Vector<byte>(c[(x + 1)..]);
                    V(ref p1, ref p2); V(ref p4, ref p5); V(ref p7, ref p8);
                    V(ref p0, ref p1); V(ref p3, ref p4); V(ref p6, ref p7);
                    V(ref p1, ref p2); V(ref p4, ref p5); V(ref p7, ref p8);
                    V(ref p0, ref p3); V(ref p5, ref p8); V(ref p4, ref p7);
                    V(ref p3, ref p6); V(ref p1, ref p4); V(ref p2, ref p5);
                    V(ref p4, ref p7); V(ref p4, ref p2); V(ref p6, ref p4);
                    V(ref p4, ref p2);
                    p4.CopyTo(dst[x..]);
                }
            }
            for (; x < w - 1; x++)
            {
                byte p0 = a[x - 1], p1 = a[x], p2 = a[x + 1], p3 = b[x - 1], p4 = b[x], p5 = b[x + 1], p6 = c[x - 1], p7 = c[x], p8 = c[x + 1];
                S(ref p1, ref p2); S(ref p4, ref p5); S(ref p7, ref p8);
                S(ref p0, ref p1); S(ref p3, ref p4); S(ref p6, ref p7);
                S(ref p1, ref p2); S(ref p4, ref p5); S(ref p7, ref p8);
                S(ref p0, ref p3); S(ref p5, ref p8); S(ref p4, ref p7);
                S(ref p3, ref p6); S(ref p1, ref p4); S(ref p2, ref p5);
                S(ref p4, ref p7); S(ref p4, ref p2); S(ref p6, ref p4);
                S(ref p4, ref p2);
                dst[x] = p4;
            }
        }
        return r;

        // the smaller of the two into the first, the bigger into the second
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void V(ref System.Numerics.Vector<byte> lo, ref System.Numerics.Vector<byte> hi)
        {
            var min = System.Numerics.Vector.Min(lo, hi);
            hi = System.Numerics.Vector.Max(lo, hi);
            lo = min;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void S(ref byte lo, ref byte hi)
        {
            if (lo > hi) (lo, hi) = (hi, lo);
        }
    }

    /// <summary>Local thresholds (8 px blocks, averaged over 5 × 5 blocks) - copes with shadows and gradients on photos.</summary>
    private static bool[] Binarize(byte[] g, int w, int h, bool invert)
    {
        const int B = 8;
        int bw = (w + B - 1) / B, bh = (h + B - 1) / B;
        var avg = new int[bw * bh];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int sum = 0, n = 0, min = 255, max = 0;
                for (int y = by * B; y < Math.Min(h, by * B + B); y++)
                    for (int x = bx * B; x < Math.Min(w, bx * B + B); x++)
                    {
                        int v = g[y * w + x];
                        sum += v;
                        n++;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                int a = sum / Math.Max(1, n);
                if (max - min <= 24)
                {
                    // a flat block: white unless its neighbours say it's inside a dark area
                    a = min / 2;
                    if (by > 0 && bx > 0)
                    {
                        int nb = (avg[(by - 1) * bw + bx] + 2 * avg[by * bw + bx - 1] + avg[(by - 1) * bw + bx - 1]) / 4;
                        if (min < nb) a = nb;
                    }
                }
                avg[by * bw + bx] = a;
            }
        var bits = new bool[w * h];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int sum = 0, n = 0;
                for (int yy = Math.Max(0, by - 2); yy <= Math.Min(bh - 1, by + 2); yy++)
                    for (int xx = Math.Max(0, bx - 2); xx <= Math.Min(bw - 1, bx + 2); xx++) { sum += avg[yy * bw + xx]; n++; }
                int t = sum / n;
                for (int y = by * B; y < Math.Min(h, by * B + B); y++)
                    for (int x = bx * B; x < Math.Min(w, bx * B + B); x++)
                        bits[y * w + x] = (g[y * w + x] <= t) != invert;
            }
        return bits;
    }

    // =================================================================== finder patterns

    private sealed class Finder
    {
        public double X, Y, Size;
        public int Count = 1;
    }

    private static bool Ratio(int[] s)
    {
        int total = s[0] + s[1] + s[2] + s[3] + s[4];
        if (total < 7) return false;
        double m = total / 7.0, v = m / 2;
        return Math.Abs(m - s[0]) < v && Math.Abs(m - s[1]) < v && Math.Abs(3 * m - s[2]) < 3 * v && Math.Abs(m - s[3]) < v && Math.Abs(m - s[4]) < v;
    }

    private static List<Finder> FindFinders(bool[] b, int w, int h)
    {
        var list = new List<Finder>();
        int skip = Math.Max(1, h / 300);
        var s = new int[5];
        for (int y = skip - 1; y < h; y += skip)
        {
            Array.Clear(s);
            int state = 0;
            for (int x = 0; x < w; x++)
            {
                if (b[y * w + x])
                {
                    if ((state & 1) == 1) state++;
                    s[state]++;
                }
                else if ((state & 1) == 0)
                {
                    if (state == 4)
                    {
                        if (Ratio(s)) Confirm(b, w, h, s, y, x, list);
                        s[0] = s[2]; s[1] = s[3]; s[2] = s[4]; s[3] = 1; s[4] = 0;
                        state = 3;
                    }
                    else s[++state]++;
                }
                else s[state]++;
            }
            if (state == 4 && Ratio(s)) Confirm(b, w, h, s, y, w, list);
        }
        return list.Where(f => f.Count >= 2 || skip > 1).ToList();
    }

    private static void Confirm(bool[] b, int w, int h, int[] s, int y, int end, List<Finder> list)
    {
        int total = s[0] + s[1] + s[2] + s[3] + s[4];
        double cx = end - s[4] - s[3] - s[2] / 2.0;
        double? cy = Cross(b, w, h, (int)cx, y, s[2], total, vertical: true);
        if (cy == null) return;
        double? cx2 = Cross(b, w, h, (int)cx, (int)cy.Value, s[2], total, vertical: false);
        if (cx2 == null) return;
        double size = total / 7.0;
        foreach (var f in list)
        {
            if (Math.Abs(cy.Value - f.Y) <= size && Math.Abs(cx2.Value - f.X) <= size && Math.Abs(size - f.Size) <= Math.Max(1, f.Size))
            {
                // the same pattern seen on another row: average it in
                f.X = (f.X * f.Count + cx2.Value) / (f.Count + 1);
                f.Y = (f.Y * f.Count + cy.Value) / (f.Count + 1);
                f.Size = (f.Size * f.Count + size) / (f.Count + 1);
                f.Count++;
                return;
            }
        }
        list.Add(new Finder { X = cx2.Value, Y = cy.Value, Size = size });
    }

    /// <summary>Checks the 1:1:3:1:1 rhythm across the candidate (up/down or left/right) and returns its centre.</summary>
    private static double? Cross(bool[] b, int w, int h, int x, int y, int maxCenter, int origTotal, bool vertical)
    {
        int len = vertical ? h : w;
        int at = vertical ? y : x;
        bool Px(int i) => vertical ? b[i * w + x] : b[y * w + i];
        var s = new int[5];
        int i = at;
        while (i >= 0 && Px(i)) { s[2]++; i--; }
        if (i < 0) return null;
        while (i >= 0 && !Px(i) && s[1] <= maxCenter) { s[1]++; i--; }
        if (i < 0 || s[1] > maxCenter) return null;
        while (i >= 0 && Px(i) && s[0] <= maxCenter) { s[0]++; i--; }
        if (s[0] > maxCenter) return null;
        i = at + 1;
        while (i < len && Px(i)) { s[2]++; i++; }
        if (i == len) return null;
        while (i < len && !Px(i) && s[3] < maxCenter) { s[3]++; i++; }
        if (i == len || s[3] >= maxCenter) return null;
        while (i < len && Px(i) && s[4] < maxCenter) { s[4]++; i++; }
        if (s[4] >= maxCenter) return null;
        int total = s[0] + s[1] + s[2] + s[3] + s[4];
        if (5 * Math.Abs(total - origTotal) >= 2 * origTotal) return null;
        if (!Ratio(s)) return null;
        return i - s[4] - s[3] - s[2] / 2.0;
    }

    // =================================================================== finding and sampling codes

    private sealed record Found(string Text, Point[] Corners);

    private static IEnumerable<Found> Find(bool[] b, int w, int h)
    {
        var finders = FindFinders(b, w, h).OrderByDescending(f => f.Count).Take(12).ToList();
        var used = new HashSet<Finder>();
        var results = new List<Found>();
        // every three patterns that could be one code (similar size, nearly a right angle), best fitting first
        var triples = new List<(double Score, Finder A, Finder B, Finder C)>();
        for (int i = 0; i < finders.Count; i++)
            for (int j = i + 1; j < finders.Count; j++)
                for (int k = j + 1; k < finders.Count; k++)
                {
                    var (a, bb, c) = (finders[i], finders[j], finders[k]);
                    double ms = (a.Size + bb.Size + c.Size) / 3;
                    if (Math.Max(a.Size, Math.Max(bb.Size, c.Size)) > 1.6 * Math.Min(a.Size, Math.Min(bb.Size, c.Size))) continue;
                    var (tl, tr, bl) = Order(a, bb, c);
                    double d1 = Dist(tl, tr), d2 = Dist(tl, bl);
                    if (d1 < 7 * ms || d2 < 7 * ms || Math.Max(d1, d2) > 1.4 * Math.Min(d1, d2) + 2 * ms) continue;
                    double cos = ((tr.X - tl.X) * (bl.X - tl.X) + (tr.Y - tl.Y) * (bl.Y - tl.Y)) / (d1 * d2);
                    if (Math.Abs(cos) > 0.35) continue;
                    triples.Add((Math.Abs(cos) + Math.Abs(d1 - d2) / Math.Max(d1, d2), tl, tr, bl));
                }
        foreach (var t in triples.OrderBy(t => t.Score))
        {
            if (used.Contains(t.A) || used.Contains(t.B) || used.Contains(t.C)) continue;
            var r = Decode(b, w, h, t.A, t.B, t.C);
            if (r == null) continue;
            results.Add(r);
            used.Add(t.A);
            used.Add(t.B);
            used.Add(t.C);
        }
        return results;
    }

    private static double Dist(Finder a, Finder b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Top-left is opposite the longest side; top-right and bottom-left follow from the turning direction.</summary>
    private static (Finder TL, Finder TR, Finder BL) Order(Finder a, Finder b, Finder c)
    {
        double ab = Dist(a, b), bc = Dist(b, c), ac = Dist(a, c);
        Finder tl, p, q;
        if (bc >= ab && bc >= ac) { tl = a; p = b; q = c; }
        else if (ac >= ab && ac >= bc) { tl = b; p = a; q = c; }
        else { tl = c; p = a; q = b; }
        double cross = (p.X - tl.X) * (q.Y - tl.Y) - (p.Y - tl.Y) * (q.X - tl.X);
        return cross > 0 ? (tl, p, q) : (tl, q, p);
    }

    private static Found? Decode(bool[] b, int w, int h, Finder tl, Finder tr, Finder bl)
    {
        double ms = (tl.Size + tr.Size + bl.Size) / 3;
        int dim = (int)Math.Round((Dist(tl, tr) + Dist(tl, bl)) / 2 / ms) + 7;
        switch (dim & 3)
        {
            case 0: dim++; break;
            case 2: dim--; break;
            case 3: dim += 2; break;
        }
        // a slightly wrong estimate of the size is tried around too (the version info settles it for big codes)
        foreach (int d in new[] { dim, dim - 4, dim + 4 })
        {
            if (d < 21 || d > 177) continue;
            var r = DecodeAt(b, w, h, tl, tr, bl, d, ms);
            if (r != null) return r;
        }
        return null;
    }

    private static Found? DecodeAt(bool[] b, int w, int h, Finder tl, Finder tr, Finder bl, int dim, double ms)
    {
        int version = (dim - 17) / 4;
        // the bottom-right reference: the alignment pattern if there is one, else the fourth corner of the parallelogram
        double brX = tr.X - tl.X + bl.X, brY = tr.Y - tl.Y + bl.Y;
        double refX = brX, refY = brY, refM = dim - 3.5;
        if (version >= 2)
        {
            double k = 1 - 3.0 / (dim - 7);
            double ex = tl.X + k * (brX - tl.X), ey = tl.Y + k * (brY - tl.Y);
            var al = FindAlignment(b, w, h, ex, ey, ms);
            if (al is Point p) { refX = p.X; refY = p.Y; refM = dim - 6.5; }
            else { refX = ex; refY = ey; refM = dim - 6.5; }
        }
        var tf = Perspective.Quad(3.5, 3.5, dim - 3.5, 3.5, refM, refM, 3.5, dim - 3.5,
                                  tl.X, tl.Y, tr.X, tr.Y, refX, refY, bl.X, bl.Y);
        var m = new bool[dim, dim];
        for (int y = 0; y < dim; y++)
            for (int x = 0; x < dim; x++)
            {
                var (px, py) = tf.Map(x + 0.5, y + 0.5);
                int ix = (int)px, iy = (int)py;
                if (ix < 0 || iy < 0 || ix >= w || iy >= h) return null;
                m[y, x] = b[iy * w + ix];
            }
        var text = QrDecoder.Decode(m, dim);
        if (text == null) return null;
        var corners = new[] { tf.Map(0, 0), tf.Map(dim, 0), tf.Map(dim, dim), tf.Map(0, dim) }.Select(c => new Point(c.X, c.Y)).ToArray();
        return new Found(text, corners);
    }

    /// <summary>The small 5×5 square near the bottom-right corner (codes from version 2): black centre, white ring, black ring.</summary>
    private static Point? FindAlignment(bool[] b, int w, int h, double ex, double ey, double ms)
    {
        int r = (int)Math.Ceiling(ms * 5);
        double best = -1;
        Point? at = null;
        int step = Math.Max(1, (int)(ms / 3));
        for (int y = (int)ey - r; y <= ey + r; y += step)
            for (int x = (int)ex - r; x <= ex + r; x += step)
            {
                int score = 0;
                for (int j = -2; j <= 2; j++)
                    for (int i = -2; i <= 2; i++)
                    {
                        int sx = (int)(x + i * ms), sy = (int)(y + j * ms);
                        if (sx < 0 || sy < 0 || sx >= w || sy >= h) { score -= 2; continue; }
                        bool want = Math.Max(Math.Abs(i), Math.Abs(j)) != 1;
                        if (b[sy * w + sx] == want) score++;
                    }
                double d = ((x - ex) * (x - ex) + (y - ey) * (y - ey)) / (ms * ms * 100);
                if (score - d > best) { best = score - d; at = new Point(x, y); }
            }
        return best >= 21 ? at : null;
    }

    /// <summary>A projective map from the code's module grid to the picture (four point pairs).</summary>
    private readonly struct Perspective
    {
        private readonly double a11, a12, a13, a21, a22, a23, a31, a32, a33;

        private Perspective(double a11, double a21, double a31, double a12, double a22, double a32, double a13, double a23, double a33)
        {
            this.a11 = a11; this.a12 = a12; this.a13 = a13;
            this.a21 = a21; this.a22 = a22; this.a23 = a23;
            this.a31 = a31; this.a32 = a32; this.a33 = a33;
        }

        public (double X, double Y) Map(double x, double y)
        {
            double d = a13 * x + a23 * y + a33;
            return ((a11 * x + a21 * y + a31) / d, (a12 * x + a22 * y + a32) / d);
        }

        public static Perspective Quad(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3,
                                       double u0, double v0, double u1, double v1, double u2, double v2, double u3, double v3)
            => SquareToQuad(u0, v0, u1, v1, u2, v2, u3, v3).Times(SquareToQuad(x0, y0, x1, y1, x2, y2, x3, y3).Adjoint());

        private static Perspective SquareToQuad(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
        {
            double dx3 = x0 - x1 + x2 - x3, dy3 = y0 - y1 + y2 - y3;
            if (Math.Abs(dx3) < 1e-9 && Math.Abs(dy3) < 1e-9)
                return new Perspective(x1 - x0, x2 - x1, x0, y1 - y0, y2 - y1, y0, 0, 0, 1);
            double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
            double den = dx1 * dy2 - dx2 * dy1;
            double a13 = (dx3 * dy2 - dx2 * dy3) / den, a23 = (dx1 * dy3 - dx3 * dy1) / den;
            return new Perspective(x1 - x0 + a13 * x1, x3 - x0 + a23 * x3, x0, y1 - y0 + a13 * y1, y3 - y0 + a23 * y3, y0, a13, a23, 1);
        }

        private Perspective Adjoint() => new(
            a22 * a33 - a23 * a32, a23 * a31 - a21 * a33, a21 * a32 - a22 * a31,
            a13 * a32 - a12 * a33, a11 * a33 - a13 * a31, a12 * a31 - a11 * a32,
            a12 * a23 - a13 * a22, a13 * a21 - a11 * a23, a11 * a22 - a12 * a21);

        private Perspective Times(Perspective o) => new(
            a11 * o.a11 + a21 * o.a12 + a31 * o.a13, a11 * o.a21 + a21 * o.a22 + a31 * o.a23, a11 * o.a31 + a21 * o.a32 + a31 * o.a33,
            a12 * o.a11 + a22 * o.a12 + a32 * o.a13, a12 * o.a21 + a22 * o.a22 + a32 * o.a23, a12 * o.a31 + a22 * o.a32 + a32 * o.a33,
            a13 * o.a11 + a23 * o.a12 + a33 * o.a13, a13 * o.a21 + a23 * o.a22 + a33 * o.a23, a13 * o.a31 + a23 * o.a32 + a33 * o.a33);
    }
}

/// <summary>The bit matrix of a QR code → its text: format, version, unmasking, codewords, error correction, segments.</summary>
internal static class QrDecoder
{
    // error correction per version and level (L, M, Q, H): EC codewords per block, blocks and data codewords of group 1, of group 2
    private static readonly int[][] Ec = ("7,1,19,0,0;10,1,16,0,0;13,1,13,0,0;17,1,9,0,0;10,1,34,0,0;16,1,28,0,0;22,1,22,0,0;28,1,16,0,0;15,1,55,0,0;26,1,44,0,0;18,2,17,0,0;22,2,13,0,0;20,1,80,0,0;18,2,32,0,0;26,2,24,0,0;16,4,9,0,0;26,1,108,0,0;24,2,43,0,0;18,2,15,2,16;22,2,11,2,12;18,2,68,0,0;16,4,27,0,0;24,4,19,0,0;28,4,15,0,0;20,2,78,0,0;18,4,31,0,0;18,2,14,4,15;26,4,13,1,14;24,2,97,0,0;22,2,38,2,39;22,4,18,2,19;26,4,14,2,15;30,2,116,0,0;22,3,36,2,37;20,4,16,4,17;24,4,12,4,13;18,2,68,2,69;26,4,43,1,44;24,6,19,2,20;28,6,15,2,16;20,4,81,0,0;30,1,50,4,51;28,4,22,4,23;24,3,12,8,13;24,2,92,2,93;22,6,36,2,37;26,4,20,6,21;28,7,14,4,15;26,4,107,0,0;22,8,37,1,38;24,8,20,4,21;22,12,11,4,12;30,3,115,1,116;24,4,40,5,41;20,11,16,5,17;24,11,12,5,13;22,5,87,1,88;24,5,41,5,42;30,5,24,7,25;24,11,12,7,13;24,5,98,1,99;28,7,45,3,46;24,15,19,2,20;30,3,15,13,16;28,1,107,5,108;28,10,46,1,47;28,1,22,15,23;28,2,14,17,15;30,5,120,1,121;26,9,43,4,44;28,17,22,1,23;28,2,14,19,15;28,3,113,4,114;26,3,44,11,45;26,17,21,4,22;26,9,13,16,14;28,3,107,5,108;26,3,41,13,42;30,15,24,5,25;28,15,15,10,16;28,4,116,4,117;26,17,42,0,0;28,17,22,6,23;30,19,16,6,17;28,2,111,7,112;28,17,46,0,0;30,7,24,16,25;24,34,13,0,0;30,4,121,5,122;28,4,47,14,48;30,11,24,14,25;30,16,15,14,16;30,6,117,4,118;28,6,45,14,46;30,11,24,16,25;30,30,16,2,17;26,8,106,4,107;28,8,47,13,48;30,7,24,22,25;30,22,15,13,16;28,10,114,2,115;28,19,46,4,47;28,28,22,6,23;30,33,16,4,17;30,8,122,4,123;28,22,45,3,46;30,8,23,26,24;30,12,15,28,16;30,3,117,10,118;28,3,45,23,46;30,4,24,31,25;30,11,15,31,16;30,7,116,7,117;28,21,45,7,46;30,1,23,37,24;30,19,15,26,16;30,5,115,10,116;28,19,47,10,48;30,15,24,25,25;30,23,15,25,16;30,13,115,3,116;28,2,46,29,47;30,42,24,1,25;30,23,15,28,16;30,17,115,0,0;28,10,46,23,47;30,10,24,35,25;30,19,15,35,16;30,17,115,1,116;28,14,46,21,47;30,29,24,19,25;30,11,15,46,16;30,13,115,6,116;28,14,46,23,47;30,44,24,7,25;30,59,16,1,17;30,12,121,7,122;28,12,47,26,48;30,39,24,14,25;30,22,15,41,16;30,6,121,14,122;28,6,47,34,48;30,46,24,10,25;30,2,15,64,16;30,17,122,4,123;28,29,46,14,47;30,49,24,10,25;30,24,15,46,16;30,4,122,18,123;28,13,46,32,47;30,48,24,14,25;30,42,15,32,16;30,20,117,4,118;28,40,47,7,48;30,43,24,22,25;30,10,15,67,16;30,19,118,6,119;28,18,47,31,48;30,34,24,34,25;30,20,15,61,16")
        .Split(';').Select(r => r.Split(',').Select(int.Parse).ToArray()).ToArray();

    // centres of the alignment patterns, versions 2-40
    private static readonly int[][] Align = ("6,18;6,22;6,26;6,30;6,34;6,22,38;6,24,42;6,26,46;6,28,50;6,30,54;6,32,58;6,34,62;6,26,46,66;6,26,48,70;6,26,50,74;6,30,54,78;6,30,56,82;6,30,58,86;6,34,62,90;6,28,50,72,94;6,26,50,74,98;6,30,54,78,102;6,28,54,80,106;6,32,58,84,110;6,30,58,86,114;6,34,62,90,118;6,26,50,74,98,122;6,30,54,78,102,126;6,26,52,78,104,130;6,30,56,82,108,134;6,34,60,86,112,138;6,30,58,86,114,142;6,34,62,90,118,146;6,30,54,78,102,126,150;6,24,50,76,102,128,154;6,28,54,80,106,132,158;6,32,58,84,110,136,162;6,26,54,82,110,138,166;6,30,58,86,114,142,170")
        .Split(';').Select(r => r.Split(',').Select(int.Parse).ToArray()).ToArray();

    private static int Msb(int v) => 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

    /// <summary>The BCH remainder protecting format and version information.</summary>
    private static int Bch(int value, int poly)
    {
        int msb = Msb(poly);
        value <<= msb - 1;
        while (Msb(value) >= msb) value ^= poly << (Msb(value) - msb);
        return value;
    }

    private static readonly int[] FormatCodes = Enumerable.Range(0, 32).Select(d => ((d << 10) | Bch(d, 0x537)) ^ 0x5412).ToArray();
    private static readonly int[] VersionCodes = Enumerable.Range(0, 41).Select(v => v < 7 ? -1 : (v << 12) | Bch(v, 0x1F25)).ToArray();

    private static int Closest(int bits, int[] codes, out int distance)
    {
        int best = -1;
        distance = int.MaxValue;
        for (int i = 0; i < codes.Length; i++)
        {
            if (codes[i] < 0) continue;
            int d = System.Numerics.BitOperations.PopCount((uint)(bits ^ codes[i]));
            if (d < distance) { distance = d; best = i; }
        }
        return best;
    }

    public static string? Decode(bool[,] m, int dim)
    {
        // ---- format: error correction level and mask (two copies; the closer one wins)
        int f1 = 0, f2 = 0;
        for (int c = 0; c <= 5; c++) f1 = (f1 << 1) | (m[8, c] ? 1 : 0);
        f1 = (f1 << 1) | (m[8, 7] ? 1 : 0);
        f1 = (f1 << 1) | (m[8, 8] ? 1 : 0);
        f1 = (f1 << 1) | (m[7, 8] ? 1 : 0);
        for (int r = 5; r >= 0; r--) f1 = (f1 << 1) | (m[r, 8] ? 1 : 0);
        for (int r = dim - 1; r >= dim - 7; r--) f2 = (f2 << 1) | (m[r, 8] ? 1 : 0);
        for (int c = dim - 8; c < dim; c++) f2 = (f2 << 1) | (m[8, c] ? 1 : 0);
        int fa = Closest(f1, FormatCodes, out int da), fb = Closest(f2, FormatCodes, out int db);
        int format = da <= db ? fa : fb;
        if (Math.Min(da, db) > 3) return null;
        int levelBits = format >> 3, mask = format & 7;
        int level = levelBits switch { 1 => 0, 0 => 1, 3 => 2, _ => 3 }; // L M Q H

        int version = (dim - 17) / 4;
        if (version >= 7)
        {
            int v1 = 0, v2 = 0;
            for (int i = 5; i >= 0; i--)
                for (int j = dim - 9; j >= dim - 11; j--) v1 = (v1 << 1) | (m[i, j] ? 1 : 0);
            for (int j = 5; j >= 0; j--)
                for (int i = dim - 9; i >= dim - 11; i--) v2 = (v2 << 1) | (m[i, j] ? 1 : 0);
            int va = Closest(v1, VersionCodes, out int dva), vb = Closest(v2, VersionCodes, out int dvb);
            int v = dva <= dvb ? va : vb;
            if (Math.Min(dva, dvb) <= 3 && v != version) return null; // sampled at the wrong size - the caller tries others
        }

        // ---- function patterns (not data)
        var fn = new bool[dim, dim];
        void Region(int top, int left, int height, int width)
        {
            for (int y = top; y < top + height; y++)
                for (int x = left; x < left + width; x++)
                    if (y >= 0 && x >= 0 && y < dim && x < dim) fn[y, x] = true;
        }
        Region(0, 0, 9, 9);
        Region(0, dim - 8, 9, 8);
        Region(dim - 8, 0, 8, 9);
        Region(6, 9, 1, dim - 17);
        Region(9, 6, dim - 17, 1);
        if (version >= 2)
        {
            var pos = Align[version - 2];
            foreach (int y in pos)
                foreach (int x in pos)
                {
                    if (y == 6 && x == 6 || y == 6 && x == pos[^1] || y == pos[^1] && x == 6) continue;
                    Region(y - 2, x - 2, 5, 5);
                }
        }
        if (version >= 7)
        {
            Region(0, dim - 11, 6, 3);
            Region(dim - 11, 0, 3, 6);
        }

        // ---- the codewords, read in the zigzag order, unmasked
        bool Mask(int i, int j) => mask switch
        {
            0 => (i + j) % 2 == 0,
            1 => i % 2 == 0,
            2 => j % 3 == 0,
            3 => (i + j) % 3 == 0,
            4 => (i / 2 + j / 3) % 2 == 0,
            5 => i * j % 2 + i * j % 3 == 0,
            6 => (i * j % 2 + i * j % 3) % 2 == 0,
            _ => ((i + j) % 2 + i * j % 3) % 2 == 0,
        };
        var e = Ec[(version - 1) * 4 + level];
        int ecPer = e[0], n1 = e[1], d1 = e[2], n2 = e[3], d2 = e[4];
        int total = n1 * (d1 + ecPer) + n2 * (d2 + ecPer);
        var cw = new byte[total];
        int bit = 0, count = 0;
        bool up = true;
        for (int j = dim - 1; j > 0; j -= 2)
        {
            if (j == 6) j--;
            for (int c = 0; c < dim; c++)
            {
                int i = up ? dim - 1 - c : c;
                for (int k = 0; k < 2; k++)
                {
                    int x = j - k;
                    if (fn[i, x]) continue;
                    bool v = m[i, x] ^ Mask(i, x);
                    if (count < total)
                    {
                        cw[count] = (byte)((cw[count] << 1) | (v ? 1 : 0));
                        if (++bit == 8) { bit = 0; count++; }
                    }
                }
            }
            up = !up;
        }
        if (count < total) return null;

        // ---- blocks: de-interleaved, error-corrected
        int blocks = n1 + n2;
        var data = new List<byte>[blocks];
        var blockCw = new byte[blocks][];
        for (int bI = 0; bI < blocks; bI++) blockCw[bI] = new byte[(bI < n1 ? d1 : d2) + ecPer];
        int idx = 0;
        int maxData = Math.Max(d1, d2);
        for (int i = 0; i < maxData; i++)
            for (int bI = 0; bI < blocks; bI++)
            {
                int dl = bI < n1 ? d1 : d2;
                if (i < dl) blockCw[bI][i] = cw[idx++];
            }
        for (int i = 0; i < ecPer; i++)
            for (int bI = 0; bI < blocks; bI++)
            {
                int dl = bI < n1 ? d1 : d2;
                blockCw[bI][dl + i] = cw[idx++];
            }
        var bytes = new List<byte>();
        for (int bI = 0; bI < blocks; bI++)
        {
            if (!ReedSolomon.Correct(blockCw[bI], ecPer)) return null;
            int dl = bI < n1 ? d1 : d2;
            for (int i = 0; i < dl; i++) bytes.Add(blockCw[bI][i]);
        }
        return Segments(bytes.ToArray(), version);
    }

    // =================================================================== data segments

    private const string Alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private static string? Segments(byte[] data, int version)
    {
        var r = new BitReader(data);
        var sb = new StringBuilder();
        Encoding? eci = null;
        while (r.Left >= 4)
        {
            int mode = r.Read(4);
            if (mode == 0) break;
            switch (mode)
            {
                case 1: // numeric
                {
                    int n = r.Read(version <= 9 ? 10 : version <= 26 ? 12 : 14);
                    while (n >= 3) { sb.Append(r.Read(10).ToString("D3")); n -= 3; }
                    if (n == 2) sb.Append(r.Read(7).ToString("D2"));
                    else if (n == 1) sb.Append(r.Read(4));
                    break;
                }
                case 2: // letters, digits and a few signs
                {
                    int n = r.Read(version <= 9 ? 9 : version <= 26 ? 11 : 13);
                    while (n >= 2) { int v = r.Read(11); sb.Append(Alnum[Math.Min(44, v / 45)]).Append(Alnum[v % 45]); n -= 2; }
                    if (n == 1) sb.Append(Alnum[Math.Min(44, r.Read(6))]);
                    break;
                }
                case 4: // bytes
                {
                    int n = r.Read(version <= 9 ? 8 : 16);
                    var bytes = new byte[n];
                    for (int i = 0; i < n; i++) bytes[i] = (byte)r.Read(8);
                    sb.Append(DecodeBytes(bytes, eci));
                    break;
                }
                case 8: // kanji (Shift JIS)
                {
                    int n = r.Read(version <= 9 ? 8 : version <= 26 ? 10 : 12);
                    var bytes = new byte[n * 2];
                    for (int i = 0; i < n; i++)
                    {
                        int v = r.Read(13);
                        int a = v / 0xC0 << 8 | v % 0xC0;
                        a += a < 0x1F00 ? 0x8140 : 0xC140;
                        bytes[2 * i] = (byte)(a >> 8);
                        bytes[2 * i + 1] = (byte)a;
                    }
                    try
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        sb.Append(Encoding.GetEncoding(932).GetString(bytes));
                    }
                    catch { sb.Append('?', n); }
                    break;
                }
                case 7: // ECI: the character set of the bytes that follow
                {
                    int v = r.Read(8);
                    if ((v & 0x80) == 0x80) v = (v & 0x3F) << 8 | r.Read(8);
                    else if ((v & 0xC0) == 0xC0) v = (v & 0x1F) << 16 | r.Read(16);
                    eci = v switch
                    {
                        26 => Encoding.UTF8,
                        3 or 1 => Encoding.Latin1,
                        _ => Try(() => { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return v switch { 4 => Encoding.GetEncoding("iso-8859-2"), 22 => Encoding.GetEncoding(1251), 23 => Encoding.GetEncoding(1252), 21 => Encoding.GetEncoding(1250), _ => null }; }),
                    };
                    break;
                }
                case 3: r.Read(16); break; // structured append header
                case 5: break;             // FNC1, first position
                case 9: r.Read(8); break;  // FNC1, second position
                default: return sb.Length > 0 ? sb.ToString() : null;
            }
        }
        return sb.ToString();
    }

    private static Encoding? Try(Func<Encoding?> f)
    {
        try { return f(); } catch { return null; }
    }

    /// <summary>UTF-8 when it is valid UTF-8 (almost every code made today), otherwise the old Latin-1 the standard names.</summary>
    private static string DecodeBytes(byte[] bytes, Encoding? eci)
    {
        if (eci != null) return eci.GetString(bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private sealed class BitReader
    {
        private readonly byte[] _d;
        private int _pos;
        public BitReader(byte[] d) { _d = d; }
        public int Left => _d.Length * 8 - _pos;

        public int Read(int n)
        {
            if (n > Left) { _pos = _d.Length * 8; return 0; }
            int v = 0;
            for (int i = 0; i < n; i++, _pos++) v = (v << 1) | ((_d[_pos >> 3] >> (7 - (_pos & 7))) & 1);
            return v;
        }
    }
}

/// <summary>Reed-Solomon error correction over GF(256) as QR codes use it (x^8 + x^4 + x^3 + x^2 + 1, generator base 0).</summary>
internal static class ReedSolomon
{
    private static readonly int[] Exp = new int[512], Log = new int[256];

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = x;
            Log[x] = i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];
    private static int Inv(int a) => Exp[255 - Log[a]];

    // polynomials: coefficients from the highest power down
    private static int[] Trim(int[] p)
    {
        int i = 0;
        while (i < p.Length - 1 && p[i] == 0) i++;
        return i == 0 ? p : p[i..];
    }

    private static int Degree(int[] p) => p.Length - 1;
    private static bool IsZero(int[] p) => p[0] == 0;

    private static int Eval(int[] p, int x)
    {
        if (x == 0) return p[^1];
        int r = p[0];
        for (int i = 1; i < p.Length; i++) r = Mul(r, x) ^ p[i];
        return r;
    }

    private static int[] Add(int[] a, int[] b)
    {
        if (IsZero(a)) return b;
        if (IsZero(b)) return a;
        if (a.Length < b.Length) (a, b) = (b, a);
        var r = (int[])a.Clone();
        int off = a.Length - b.Length;
        for (int i = 0; i < b.Length; i++) r[off + i] ^= b[i];
        return Trim(r);
    }

    private static int[] Times(int[] a, int[] b)
    {
        if (IsZero(a) || IsZero(b)) return new[] { 0 };
        var r = new int[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++) r[i + j] ^= Mul(a[i], b[j]);
        return Trim(r);
    }

    private static int[] Monomial(int degree, int c)
    {
        if (c == 0) return new[] { 0 };
        var r = new int[degree + 1];
        r[0] = c;
        return r;
    }

    private static int[] TimesMonomial(int[] a, int degree, int c)
    {
        if (c == 0) return new[] { 0 };
        var r = new int[a.Length + degree];
        for (int i = 0; i < a.Length; i++) r[i] = Mul(a[i], c);
        return Trim(r);
    }

    /// <summary>Repairs a block in place (data + EC codewords). False when it has more errors than can be fixed.</summary>
    public static bool Correct(byte[] block, int ec)
    {
        var received = block.Select(b => (int)b).ToArray();
        var syn = new int[ec];
        bool clean = true;
        for (int i = 0; i < ec; i++)
        {
            int v = Eval(received, Exp[i]);
            syn[ec - 1 - i] = v;
            if (v != 0) clean = false;
        }
        if (clean) return true;

        // Euclid: the error locator (sigma) and evaluator (omega)
        int[] rLast = Monomial(ec, 1), r = Trim(syn), tLast = new[] { 0 }, t = new[] { 1 };
        if (Degree(rLast) < Degree(r)) (rLast, r) = (r, rLast);
        while (2 * Degree(r) >= ec)
        {
            var rLastLast = rLast;
            var tLastLast = tLast;
            rLast = r;
            tLast = t;
            if (IsZero(rLast)) return false;
            r = rLastLast;
            var q = new[] { 0 };
            int dlt = Inv(rLast[0]);
            while (Degree(r) >= Degree(rLast) && !IsZero(r))
            {
                int diff = Degree(r) - Degree(rLast);
                int scale = Mul(r[0], dlt);
                q = Add(q, Monomial(diff, scale));
                r = Add(r, TimesMonomial(rLast, diff, scale));
            }
            t = Add(Times(q, tLast), tLastLast);
            if (Degree(r) >= Degree(rLast)) return false;
        }
        int s0 = t[^1];
        if (s0 == 0) return false;
        int inv = Inv(s0);
        var sigma = t.Select(c => Mul(c, inv)).ToArray();
        var omega = r.Select(c => Mul(c, inv)).ToArray();

        // Chien search: where the errors are
        int errors = Degree(sigma);
        var loc = new List<int>();
        for (int i = 1; i < 256 && loc.Count < errors; i++)
            if (Eval(sigma, i) == 0) loc.Add(Inv(i));
        if (loc.Count != errors) return false;

        // Forney: how big they are
        for (int i = 0; i < loc.Count; i++)
        {
            int xiInv = Inv(loc[i]);
            int den = 1;
            for (int j = 0; j < loc.Count; j++)
            {
                if (i == j) continue;
                int term = Mul(loc[j], xiInv);
                den = Mul(den, (term & 1) == 0 ? term | 1 : term & ~1);
            }
            int mag = Mul(Eval(omega, xiInv), Inv(den));
            int pos = received.Length - 1 - Log[loc[i]];
            if (pos < 0) return false;
            received[pos] ^= mag;
        }
        for (int i = 0; i < block.Length; i++) block[i] = (byte)received[i];
        return true;
    }
}
