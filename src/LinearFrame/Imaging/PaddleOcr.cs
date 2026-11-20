using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LinearFrame.Core;

namespace LinearFrame.Imaging;

/// <summary>
/// PaddleOCR's PP-OCRv5 models (Baidu, Apache 2.0) run by ONNX Runtime: the part of text recognition that copes with
/// photos. A detector finds every line of text - any size, slanted, on a crumpled label, on a busy background - and each
/// line is cut out straight. The straightened lines are then read twice at the same time: by Windows' own engine (all of
/// them stacked on one sheet - it knows Polish letters best) and by PP-OCR's Latin-script recogniser (~500 characters - it
/// reads blurry, small or stylised text Windows gives up on); for every line the better reading wins. Word positions come
/// back in the picture's own pixels, so text can be selected right on it. Offline, a few hundred ms.
/// </summary>
internal static class PaddleOcr
{
    public const string DetFile = "ch_PP-OCRv5_det_mobile.onnx";
    public const string RecFile = "latin_PP-OCRv5_rec_mobile.onnx";
    public const string DictFile = "ppocrv5_latin_dict.txt";

    // detection (DB): how big the detector sees the picture (small pictures enlarged, big ones shrunk - below 2000 px the
    // small print of a photographed label gets lost) and its thresholds (as in PaddleOCR / RapidOCR)
    private const int DetMinSide = 736, DetMaxSide = 2000;
    private const float BinThresh = 0.3f, BoxThresh = 0.6f;
    private const double UnclipRatio = 1.5;
    private const int MinBoxSide = 3, MaxCandidates = 1000;
    // recognition (CTC): lines 48 px tall, at least 320 px wide, several at once
    private const int RecHeight = 48, RecMinWidth = 320, RecBatch = 8;
    private const float MinScore = 0.6f;
    // the sheet for Windows' engine: rows 44 px tall (letters ~26 px - what it reads best), well apart
    private const int SheetRow = 44, SheetGap = 20, SheetMargin = 12;
    // models stay loaded for a while after use (captures come in bursts), then the memory goes back
    private static readonly TimeSpan KeepLoaded = TimeSpan.FromSeconds(90);

    private static readonly float[] DetMean = { 0.485f, 0.456f, 0.406f }, DetStd = { 0.229f, 0.224f, 0.225f };

    private static readonly object Gate = new();
    private static Onnx.Session? _det, _rec;
    private static string[]? _classes;
    private static Timer? _release;
    private static bool _broken;

    /// <summary>Timings of the last run (for the log and the dev tools).</summary>
    public static string LastTimings { get; private set; } = "";

    /// <summary>For the dev tools: every line the detector found last time, both readings of it, and which one was taken.</summary>
    internal static List<(Point[] Corners, string Text, float Score, string Windows, bool WindowsWon)> LastLines { get; } = new();

    /// <summary>The runtime and the models are in the engine folder (and didn't fail to load).</summary>
    public static bool Available => !_broken
        && AppPaths.FindEngineFile("onnxruntime.dll") != null && AppPaths.FindEngineFile(DetFile) != null
        && AppPaths.FindEngineFile(RecFile) != null && AppPaths.FindEngineFile(DictFile) != null;

    /// <summary>
    /// The text in <paramref name="px"/>. <paramref name="windows"/>: Windows' engine for the sheet of straightened lines
    /// (null: PP-OCR reads alone).
    /// </summary>
    public static OcrText? Recognize(PixelBuffer px, CancellationToken ct, Func<PixelBuffer, CancellationToken, List<OcrLine>?>? windows)
    {
        lock (Gate)
        {
            if (!Load()) return null;
            _release?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                var sw = Stopwatch.StartNew();
                var src = Opaque(px);
                var found = Detect(src, DetMaxSide, ct, out string detInfo);
                long detMs = sw.ElapsedMilliseconds;
                // every line as it reads; a line running up or down the page also the other way round (which way its
                // letters stand shows only when they are read: the better reading decides)
                var crops = new List<Crop>();
                var lineOf = new List<int>();
                for (int i = 0; i < found.Count; i++)
                    foreach (var q in found[i].Flipped is { } f ? new[] { found[i].Main, f } : new[] { found[i].Main })
                    {
                        crops.Add(new Crop(q, src.Width, src.Height));
                        lineOf.Add(i);
                    }
                // Windows' engine reads the straightened lines on another thread while PP-OCR reads them here
                long winMs = 0;
                var theirsTask = windows == null || crops.Count == 0 ? null : Task.Run(() =>
                {
                    var t = Stopwatch.StartNew();
                    try { return ReadWithWindows(src, crops, windows, ct); }
                    finally { winMs = t.ElapsedMilliseconds; }
                }, ct);
                // When the reading below is cancelled (the viewer moved on to the next picture) nobody waits for this
                // task any more - its own "cancelled" must not surface later as an unobserved error in the log.
                theirsTask?.Forget();
                var mine = ReadCrops(src, crops, ct, out string recInfo);
                List<OcrWord>[]? theirs = null;
                if (theirsTask != null)
                {
                    try { theirs = theirsTask.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log.Warn("OCR sheet: " + ex.Message); }
                }
                var lines = new List<Crop>();
                var chosen = new List<OcrWord>?[found.Count];
                int byWindows = 0;
                LastLines.Clear();
                for (int i = 0; i < found.Count; i++)
                {
                    // the way round that reads best
                    int best = -1;
                    for (int c = 0; c < crops.Count; c++)
                        if (lineOf[c] == i && (best < 0 || mine[c].Score > mine[best].Score)) best = c;
                    lines.Add(crops[best]);
                    chosen[i] = Choose(mine[best], theirs?[best], out bool windowsWon, out string theirText);
                    if (windowsWon) byWindows++;
                    // PP-OCR's own reading: Polish letters it dropped put back from the Windows spelling dictionary
                    else if (chosen[i] is { Count: > 0 } words) chosen[i] = SpellFix.Fix(words);
                    var q = crops[best].Q;
                    LastLines.Add((new[] { q.TL, q.TR, q.BR, q.BL }, mine[best].Text, mine[best].Score, theirText, windowsWon));
                }
                LastTimings = $"{px.Width}×{px.Height}: detect {detMs} ms ({detInfo}; {found.Count} lines), " +
                              $"read {sw.ElapsedMilliseconds - detMs} ms (PP-OCR {recInfo}; Windows {winMs} ms, {byWindows} of its readings taken)";
                return new OcrText(JoinPieces(lines, chosen));
            }
            finally { ScheduleRelease(); }
        }
    }

    // ------------------------------------------------------------------ loading

    private static bool Load()
    {
        if (_det != null && _rec != null && _classes != null) return true;
        if (_broken) return false;
        try
        {
            var sw = Stopwatch.StartNew();
            // the recogniser's classes: CTC "blank", the dictionary, and a space at the end (PaddleOCR's use_space_char)
            var dict = File.ReadAllLines(AppPaths.FindEngineFile(DictFile)!, Encoding.UTF8).Where(l => l.Length > 0);
            _classes = new[] { "" }.Concat(dict).Append(" ").ToArray();
            _det ??= new Onnx.Session(AppPaths.FindEngineFile(DetFile)!);
            _rec ??= new Onnx.Session(AppPaths.FindEngineFile(RecFile)!);
            Log.Info($"PP-OCRv5 loaded in {sw.ElapsedMilliseconds} ms ({_classes.Length} classes)");
            return true;
        }
        catch (Exception ex)
        {
            _broken = true;
            Log.Warn("PP-OCR unavailable, Windows OCR is used: " + ex.Message);
            Unload();
            return false;
        }
    }

    private static void ScheduleRelease()
    {
        _release ??= new Timer(_ => { lock (Gate) Unload(); });
        _release.Change(KeepLoaded, Timeout.InfiniteTimeSpan);
    }

    private static void Unload()
    {
        _det?.Dispose();
        _rec?.Dispose();
        _det = _rec = null;
    }

    /// <summary>The models are in memory (they stay <see cref="KeepLoaded"/> after the last reading) - for the log.</summary>
    internal static bool Loaded => _det != null || _rec != null;

    /// <summary>Transparent pixels on white (a logo, a sticker) - the models read opaque pictures.</summary>
    private static PixelBuffer Opaque(PixelBuffer px)
    {
        var d = px.Data;
        bool clear = false;
        for (int i = 3; i < d.Length; i += 4) if (d[i] != 255) { clear = true; break; }
        if (!clear) return px;
        var o = new PixelBuffer(px.Width, px.Height);
        var e = o.Data;
        for (int i = 0; i < d.Length; i += 4)
        {
            int a = d[i + 3], na = 255 - a;
            e[i] = (byte)((d[i] * a + 255 * na + 127) / 255);
            e[i + 1] = (byte)((d[i + 1] * a + 255 * na + 127) / 255);
            e[i + 2] = (byte)((d[i + 2] * a + 255 * na + 127) / 255);
            e[i + 3] = 255;
        }
        return o;
    }

    /// <summary>
    /// The picture's colour at (<paramref name="x"/>, <paramref name="y"/>) - pixel centres at .5 - averaged over a
    /// footprint of <paramref name="fx"/>×<paramref name="fy"/> source pixels when shrinking (no shimmer on small text).
    /// Outside the picture its edge continues.
    /// </summary>
    private static void Sample(PixelBuffer px, double x, double y, double fx, double fy, out float b, out float g, out float r)
    {
        int nx = fx > 1.5 ? Math.Min(4, (int)Math.Ceiling(fx)) : 1, ny = fy > 1.5 ? Math.Min(4, (int)Math.Ceiling(fy)) : 1;
        float sb = 0, sg = 0, sr = 0;
        var d = px.Data;
        int w = px.Width, h = px.Height;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                double sx = x + (nx == 1 ? 0 : ((i + 0.5) / nx - 0.5) * fx) - 0.5;
                double sy = y + (ny == 1 ? 0 : ((j + 0.5) / ny - 0.5) * fy) - 0.5;
                if (sx < 0) sx = 0; else if (sx > w - 1) sx = w - 1;
                if (sy < 0) sy = 0; else if (sy > h - 1) sy = h - 1;
                int x0 = (int)sx, y0 = (int)sy;
                int x1 = x0 + 1 < w ? x0 + 1 : x0, y1 = y0 + 1 < h ? y0 + 1 : y0;
                float ax = (float)(sx - x0), ay = (float)(sy - y0);
                int i00 = (y0 * w + x0) * 4, i10 = (y0 * w + x1) * 4, i01 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;
                float w00 = (1 - ax) * (1 - ay), w10 = ax * (1 - ay), w01 = (1 - ax) * ay, w11 = ax * ay;
                sb += d[i00] * w00 + d[i10] * w10 + d[i01] * w01 + d[i11] * w11;
                sg += d[i00 + 1] * w00 + d[i10 + 1] * w10 + d[i01 + 1] * w01 + d[i11 + 1] * w11;
                sr += d[i00 + 2] * w00 + d[i10 + 2] * w10 + d[i01 + 2] * w01 + d[i11 + 2] * w11;
            }
        float n = nx * ny;
        b = sb / n;
        g = sg / n;
        r = sr / n;
    }

    // ------------------------------------------------------------------ detection

    /// <summary>
    /// A text line in the picture: its corners (picture pixels) as its reader sees them - top-left, top-right, bottom-right,
    /// bottom-left of the letters. For a line running down the page "top-left" is a corner on its right side.
    /// </summary>
    private readonly record struct Quad(Point TL, Point TR, Point BR, Point BL);

    /// <summary>A found line: a rectangle at any angle - centre, its long axis, half length and half width.</summary>
    private readonly record struct RotRect(Point Centre, Vector Axis, double HalfLong, double HalfShort);

    private static List<(Quad Main, Quad? Flipped)> Detect(PixelBuffer px, int maxSide, CancellationToken ct, out string info)
    {
        var sw = Stopwatch.StartNew();
        int w = px.Width, h = px.Height;
        // a margin around the picture (its edge pixels continued): text at the very edge of a tight selection is still found
        int pad = Math.Clamp(Math.Min(w, h) / 16, 6, 32);
        int pw = w + 2 * pad, ph = h + 2 * pad;
        double k = Math.Min(pw, ph) < DetMinSide ? (double)DetMinSide / Math.Min(pw, ph) : 1;
        if (Math.Max(pw, ph) * k > maxSide) k = (double)maxSide / Math.Max(pw, ph);
        int dw = Math.Max(32, (int)Math.Round(pw * k / 32) * 32), dh = Math.Max(32, (int)Math.Round(ph * k / 32) * 32);
        double sx = (double)pw / dw, sy = (double)ph / dh; // detector pixel → padded picture pixel

        var input = new float[3 * dw * dh];
        int plane = dw * dh;
        Parallel.For(0, dh, y =>
        {
            for (int x = 0; x < dw; x++)
            {
                Sample(px, (x + 0.5) * sx - pad, (y + 0.5) * sy - pad, sx, sy, out float b, out float g, out float r);
                int o = y * dw + x;
                // BGR planes, ImageNet mean / std per plane - how PaddleOCR feeds its detector
                input[o] = (b / 255f - DetMean[0]) / DetStd[0];
                input[plane + o] = (g / 255f - DetMean[1]) / DetStd[1];
                input[2 * plane + o] = (r / 255f - DetMean[2]) / DetStd[2];
            }
        });
        ct.ThrowIfCancellationRequested();
        long prepMs = sw.ElapsedMilliseconds;
        var (prob, shape) = _det!.Run(input, new long[] { 1, 3, dh, dw });
        long runMs = sw.ElapsedMilliseconds - prepMs;
        int W = (int)shape[^1], H = (int)shape[^2];
        if (DumpDir != null) DumpMap(prob, W, H, $"det-{W}x{H}.png");

        // text pixels, grown by one pixel right and down (RapidOCR's 2×2 dilation: broken strokes join up)
        var mask = new byte[W * H];
        for (int i = 0; i < mask.Length; i++) mask[i] = prob[i] > BinThresh ? (byte)1 : (byte)0;
        for (int y = H - 1; y >= 0; y--)
            for (int x = W - 1; x >= 0; x--)
            {
                int i = y * W + x;
                if (mask[i] != 0) continue;
                if (x > 0 && mask[i - 1] != 0 || y > 0 && (mask[i - W] != 0 || x > 0 && mask[i - W - 1] != 0)) mask[i] = 1;
            }

        var quads = new List<(Quad Main, Quad? Flipped)>();
        var stack = new Stack<int>();
        var pixels = new List<int>();
        for (int start = 0; start < mask.Length && quads.Count < MaxCandidates; start++)
        {
            if (mask[start] == 0) continue;
            // one connected blob (8 neighbours); visited pixels are cleared
            pixels.Clear();
            mask[start] = 0;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                pixels.Add(p);
                int py = p / W, pxx = p - py * W;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = py + dy;
                    if (yy < 0 || yy >= H) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = pxx + dx;
                        if (xx < 0 || xx >= W) continue;
                        int q = yy * W + xx;
                        if (mask[q] == 0) continue;
                        mask[q] = 0;
                        stack.Push(q);
                    }
                }
            }
            if (pixels.Count < 6) continue;
            if (BoxOf(pixels, W, prob, out var rect))
            {
                // back to the picture's pixels (without the margin)
                var (main, flipped) = Orient(rect, p => new Point(p.X * sx - pad, p.Y * sy - pad));
                if (Dist(main.TL, main.TR) <= 3 || Dist(main.TL, main.BL) <= 3) continue;
                quads.Add((main, flipped));
            }
        }
        info = $"{dw}×{dh}: prepare {prepMs} ms, model {runMs} ms, boxes {sw.ElapsedMilliseconds - prepMs - runMs} ms";
        return quads;
    }

    /// <summary>
    /// The blob's smallest rotated rectangle, grown back to the size of the text (DB "unclip": the detector marks a shrunk
    /// core of each line). False for specks and weak finds - scored by the mean text probability of the blob itself
    /// (PaddleOCR's "slow" score), so a bent line - small digits along a barcode, bigger ones after them - isn't punished
    /// for the empty corners of its rectangle.
    /// </summary>
    private static bool BoxOf(List<int> pixels, int W, float[] prob, out RotRect rect)
    {
        rect = default;
        // the blob's outline is enough for its hull: the leftmost and rightmost pixel of every row
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (int p in pixels) { int y = p / W; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        int rows = maxY - minY + 1;
        var left = new int[rows];
        var right = new int[rows];
        Array.Fill(left, int.MaxValue);
        Array.Fill(right, int.MinValue);
        foreach (int p in pixels)
        {
            int y = p / W, x = p - y * W, r = y - minY;
            if (x < left[r]) left[r] = x;
            if (x > right[r]) right[r] = x;
        }
        var pts = new List<Point>(rows * 2);
        for (int r = 0; r < rows; r++)
        {
            if (left[r] == int.MaxValue) continue;
            pts.Add(new Point(left[r], minY + r));
            if (right[r] != left[r]) pts.Add(new Point(right[r], minY + r));
        }
        var hull = Hull(pts);
        if (hull.Count < 3) return false;

        // rotating calipers: the hull edge whose direction gives the smallest rectangle
        double best = double.MaxValue;
        Vector bu = default, bv = default;
        double a0 = 0, a1 = 0, b0 = 0, b1 = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var e = hull[(i + 1) % hull.Count] - hull[i];
            double len = e.Length;
            if (len < 1e-9) continue;
            var u = e / len;
            var v = new Vector(-u.Y, u.X);
            double mina = double.MaxValue, maxa = double.MinValue, minb = double.MaxValue, maxb = double.MinValue;
            foreach (var p in hull)
            {
                double a = p.X * u.X + p.Y * u.Y, b = p.X * v.X + p.Y * v.Y;
                if (a < mina) mina = a;
                if (a > maxa) maxa = a;
                if (b < minb) minb = b;
                if (b > maxb) maxb = b;
            }
            double area = (maxa - mina) * (maxb - minb);
            if (area < best) { best = area; bu = u; bv = v; a0 = mina; a1 = maxa; b0 = minb; b1 = maxb; }
        }
        double rw = a1 - a0, rh = b1 - b0;
        if (Math.Min(rw, rh) < MinBoxSide) return false;

        // mean probability of the blob's text pixels (the 2×2 growth around them left out)
        double sum = 0;
        int n = 0;
        foreach (int p in pixels)
        {
            if (prob[p] <= BinThresh) continue;
            sum += prob[p];
            n++;
        }
        if (n == 0 || sum / n < BoxThresh) return false;

        // unclip: grow every side by area × ratio / perimeter
        double d = rw * rh * UnclipRatio / (2 * (rw + rh));
        a0 -= d; a1 += d; b0 -= d; b1 += d;
        if (Math.Min(a1 - a0, b1 - b0) < MinBoxSide + 2) return false;
        // pixel centres → pixel corners (+0.5)
        var centre = (Point)(bu * ((a0 + a1) / 2) + bv * ((b0 + b1) / 2)) + new Vector(0.5, 0.5);
        rect = a1 - a0 >= b1 - b0
            ? new RotRect(centre, bu, (a1 - a0) / 2, (b1 - b0) / 2)
            : new RotRect(centre, bv, (b1 - b0) / 2, (a1 - a0) / 2);
        return true;
    }

    /// <summary>
    /// A found rectangle's corners in reading order, mapped by <paramref name="back"/>: left to right for a line lying across
    /// the page; for one standing up the page downwards (a line turned clockwise), plus the other way round (turned
    /// counter-clockwise) - only reading it tells which one is right.
    /// </summary>
    private static (Quad Main, Quad? Flipped) Orient(RotRect r, Func<Point, Point> back)
    {
        var u = r.Axis;
        bool upright = r.HalfLong >= 1.5 * r.HalfShort && Math.Abs(u.Y) > Math.Abs(u.X);
        Vector along;
        double ha, hd;
        if (upright)
        {
            along = u.Y >= 0 ? u : -u;
            ha = r.HalfLong;
            hd = r.HalfShort;
        }
        else
        {
            // the axis nearer the horizontal reads left to right
            var v = new Vector(-u.Y, u.X);
            if (Math.Abs(u.X) >= Math.Abs(v.X)) { along = u.X >= 0 ? u : -u; ha = r.HalfLong; hd = r.HalfShort; }
            else { along = v.X >= 0 ? v : -v; ha = r.HalfShort; hd = r.HalfLong; }
        }
        Quad Make(Vector a)
        {
            var down = new Vector(-a.Y, a.X); // the letters' "down": a quarter turn clockwise from the reading direction
            return new Quad(back(r.Centre - a * ha - down * hd), back(r.Centre + a * ha - down * hd),
                back(r.Centre + a * ha + down * hd), back(r.Centre - a * ha + down * hd));
        }
        return (Make(along), upright ? Make(-along) : null);
    }

    /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise.</summary>
    private static List<Point> Hull(List<Point> pts)
    {
        pts.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        if (pts.Count < 3) return pts;
        static double Cross(Point o, Point a, Point b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new Point[pts.Count * 2];
        int k = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            while (k >= 2 && Cross(h[k - 2], h[k - 1], pts[i]) <= 0) k--;
            h[k++] = pts[i];
        }
        for (int i = pts.Count - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Cross(h[k - 2], h[k - 1], pts[i]) <= 0) k--;
            h[k++] = pts[i];
        }
        return h.Take(k - 1).ToList();
    }

    private static double Dist(Point a, Point b) => (a - b).Length;

    // ------------------------------------------------------------------ recognition

    /// <summary>
    /// A line cut out of the picture, upright: <c>Map(u, v)</c> - u along the reading direction (0..W), v down the letters
    /// (0..H) - gives the picture point.
    /// </summary>
    private sealed class Crop
    {
        public readonly Quad Q;
        public readonly double W, H;
        private readonly int _picW, _picH;

        public Crop(Quad q, int picW, int picH)
        {
            Q = q;
            W = Math.Max(Dist(q.TL, q.TR), Dist(q.BL, q.BR));
            H = Math.Max(Dist(q.TL, q.BL), Dist(q.TR, q.BR));
            _picW = picW;
            _picH = picH;
        }

        public Point Map(double u, double v)
        {
            double s = u / W, t = v / H;
            double x = (1 - s) * (1 - t) * Q.TL.X + s * (1 - t) * Q.TR.X + s * t * Q.BR.X + (1 - s) * t * Q.BL.X;
            double y = (1 - s) * (1 - t) * Q.TL.Y + s * (1 - t) * Q.TR.Y + s * t * Q.BR.Y + (1 - s) * t * Q.BL.Y;
            return new Point(x, y);
        }

        public Point Centre => Map(W / 2, H / 2);

        /// <summary>The reading direction in the picture (unit length).</summary>
        public Vector Along
        {
            get
            {
                var d = Map(W, H / 2) - Map(0, H / 2);
                return d.Length < 1e-9 ? new Vector(1, 0) : d / d.Length;
            }
        }

        /// <summary>A stretch of the line (crop coordinates) as a box in the picture (inside it).</summary>
        public Rect Box(double u0, double v0, double u1, double v1)
        {
            var pts = new[] { Map(u0, v0), Map(u1, v0), Map(u1, v1), Map(u0, v1) };
            double l = Math.Clamp(pts.Min(p => p.X), 0, _picW), t = Math.Clamp(pts.Min(p => p.Y), 0, _picH);
            double r = Math.Clamp(pts.Max(p => p.X), 0, _picW), b = Math.Clamp(pts.Max(p => p.Y), 0, _picH);
            return new Rect(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
        }
    }

    /// <summary>LF_OCR_DUMP=folder: the dev tools save every line exactly as the recogniser gets it.</summary>
    private static readonly string? DumpDir = Environment.GetEnvironmentVariable("LF_OCR_DUMP");

    /// <summary>
    /// Lines go to the recogniser a quarter wider than they are: narrow label fonts then still get enough steps per letter
    /// for the small marks (ń, ę) to survive. LF_OCR_STRETCH overrides it for experiments.
    /// </summary>
    private static readonly double Stretch = double.TryParse(Environment.GetEnvironmentVariable("LF_OCR_STRETCH"), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double s) ? s : 1.25;

    /// <summary>PP-OCR's reading of every line (same order as <paramref name="crops"/>).</summary>
    private static (List<OcrWord> Words, string Text, float Score)[] ReadCrops(PixelBuffer px, List<Crop> crops, CancellationToken ct, out string info)
    {
        var sw = Stopwatch.StartNew();
        long runMs = 0;
        var result = new (List<OcrWord> Words, string Text, float Score)[crops.Count];
        // similar lengths together: less padding in a batch
        var order = Enumerable.Range(0, crops.Count).OrderBy(i => crops[i].W / crops[i].H).ToArray();
        var classes = _classes!;
        for (int start = 0; start < order.Length; start += RecBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = order.Skip(start).Take(RecBatch).Select(i => crops[i]).ToList();
            int width = Math.Max(RecMinWidth, batch.Max(c => (int)Math.Ceiling(RecHeight * c.W / c.H * Stretch)));
            width = (width + 7) / 8 * 8;
            int plane = RecHeight * width;
            var input = new float[batch.Count * 3 * plane]; // 0 = the grey PaddleOCR pads with
            var used = new int[batch.Count];
            Parallel.For(0, batch.Count, n =>
            {
                var c = batch[n];
                int rw = Math.Clamp((int)Math.Ceiling(RecHeight * c.W / c.H * Stretch), 1, width);
                used[n] = rw;
                double fu = c.W / rw, fv = c.H / RecHeight;
                int baseIndex = n * 3 * plane;
                for (int y = 0; y < RecHeight; y++)
                    for (int x = 0; x < rw; x++)
                    {
                        var p = c.Map((x + 0.5) * fu, (y + 0.5) * fv);
                        Sample(px, p.X, p.Y, fu, fv, out float b, out float g, out float r);
                        int o = baseIndex + y * width + x;
                        input[o] = b / 127.5f - 1;
                        input[plane + o] = g / 127.5f - 1;
                        input[2 * plane + o] = r / 127.5f - 1;
                    }
            });
            if (DumpDir != null) Dump(input, batch.Count, width, start);
            long t0 = sw.ElapsedMilliseconds;
            var (prob, shape) = _rec!.Run(input, new long[] { batch.Count, 3, RecHeight, width });
            runMs += sw.ElapsedMilliseconds - t0;
            int T = (int)shape[1], C = (int)shape[2];
            if (C != classes.Length) Log.Warn($"PP-OCR: the recogniser has {C} classes, the dictionary {classes.Length}");
            double stride = (double)width / T;
            for (int n = 0; n < batch.Count; n++)
            {
                var line = Decode(prob, n, T, C, classes, out float score);
                var words = line.Count == 0 ? new List<OcrWord>() : Words(line, batch[n], stride, batch[n].W / used[n]);
                result[order[start + n]] = (words, string.Concat(line.Select(g => g.Text)).Trim(), score);
            }
        }
        info = $"{crops.Count} lines in {sw.ElapsedMilliseconds} ms, model {runMs} ms";
        return result;
    }

    /// <summary>
    /// Windows' engine reads the straightened lines: stacked into a sheet (dark letters on light, ~26 px tall, a gap between
    /// rows), one call per sheet; every word goes back to the line it sits in. Per line: its words in reading order.
    /// </summary>
    private static List<OcrWord>[] ReadWithWindows(PixelBuffer px, List<Crop> crops, Func<PixelBuffer, CancellationToken, List<OcrLine>?> windows, CancellationToken ct)
    {
        var result = new List<(double U, OcrWord Word)>[crops.Count];
        for (int i = 0; i < result.Length; i++) result[i] = new();
        int limit = TextRecognizer.SheetLimit, pitch = SheetRow + SheetGap;
        int perSheet = Math.Max(1, (limit - 2 * SheetMargin) / pitch);
        for (int first = 0; first < crops.Count; first += perSheet)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(perSheet, crops.Count - first);
            var scale = new double[count];
            var widths = new int[count];
            for (int r = 0; r < count; r++)
            {
                var c = crops[first + r];
                scale[r] = SheetRow / c.H;
                widths[r] = Math.Clamp((int)Math.Ceiling(c.W * scale[r]), 1, limit - 2 * SheetMargin);
            }
            int sw = widths.Max() + 2 * SheetMargin, sh = count * pitch + 2 * SheetMargin - SheetGap;
            var sheet = new PixelBuffer(sw, sh);
            Array.Fill(sheet.Data, (byte)255);
            Parallel.For(0, count, r =>
            {
                var c = crops[first + r];
                double k = scale[r], f = 1 / k;
                int top = SheetMargin + r * pitch, w = widths[r];
                var row = new byte[w * SheetRow * 3];
                long sum = 0;
                for (int y = 0; y < SheetRow; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var p = c.Map((x + 0.5) * f, (y + 0.5) * f);
                        Sample(px, p.X, p.Y, f, f, out float b, out float g, out float rr);
                        int o = (y * w + x) * 3;
                        row[o] = (byte)(b + 0.5f);
                        row[o + 1] = (byte)(g + 0.5f);
                        row[o + 2] = (byte)(rr + 0.5f);
                        sum += (row[o] * 114 + row[o + 1] * 587 + row[o + 2] * 299) / 1000;
                    }
                // light letters on a dark background are turned around: the sheet is light all over
                bool invert = sum < (long)w * SheetRow * 110;
                var d = sheet.Data;
                for (int y = 0; y < SheetRow; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 3, t = ((top + y) * sheet.Width + SheetMargin + x) * 4;
                        d[t] = invert ? (byte)(255 - row[o]) : row[o];
                        d[t + 1] = invert ? (byte)(255 - row[o + 1]) : row[o + 1];
                        d[t + 2] = invert ? (byte)(255 - row[o + 2]) : row[o + 2];
                    }
            });
            var lines = windows(sheet, ct);
            if (lines == null) continue;
            foreach (var word in lines.SelectMany(l => l.Words))
            {
                double cy = word.Box.Y + word.Box.Height / 2;
                int r = (int)Math.Floor((cy - SheetMargin) / pitch);
                if (r < 0 || r >= count) continue;
                double top = SheetMargin + r * pitch;
                if (cy > top + SheetRow) continue; // in the gap between two lines
                var c = crops[first + r];
                double k = scale[r];
                double u0 = Math.Clamp((word.Box.Left - SheetMargin) / k, 0, c.W), u1 = Math.Clamp((word.Box.Right - SheetMargin) / k, 0, c.W);
                double v0 = Math.Clamp((word.Box.Top - top) / k, 0, c.H), v1 = Math.Clamp((word.Box.Bottom - top) / k, 0, c.H);
                result[first + r].Add((u0, new OcrWord(word.Text, c.Box(u0, v0, u1, v1))));
            }
        }
        return result.Select(l => l.OrderBy(x => x.U).Select(x => x.Word).ToList()).ToArray();
    }

    /// <summary>
    /// Pieces of one line put back together (the detector splits a line at wide gaps: "Faktura" | "VAT nr ..."), measured
    /// along the line's own slant - so tilted lines on a photo don't get mixed up with the ones above and below - and the
    /// lines put in reading order: down the page, then along it.
    /// </summary>
    private static List<OcrLine> JoinPieces(List<Crop> crops, List<OcrWord>?[] words)
    {
        var idx = Enumerable.Range(0, crops.Count).Where(i => words[i] is { Count: > 0 }).ToList();
        var parent = Enumerable.Range(0, crops.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        foreach (int i in idx)
            foreach (int j in idx)
            {
                if (j <= i) continue;
                Crop a = crops[i], b = crops[j];
                var u = a.Along;
                if (Math.Abs(Vector.CrossProduct(u, b.Along)) > 0.17) continue; // more than ~10° apart
                var v = new Vector(-u.Y, u.X);
                var d = b.Centre - a.Centre;
                if (Math.Abs(d * v) > 0.5 * Math.Min(a.H, b.H)) continue;     // not on the same line
                double bc = d * u, gap = Math.Max(0, Math.Abs(bc) - (a.W + b.W) / 2);
                if (gap < 2.2 * Math.Max(a.H, b.H)) parent[Find(i)] = Find(j);
            }

        // the page's direction: the long lines' average slant
        var dir = idx.Aggregate(new Vector(0, 0), (s, i) => s + crops[i].Along * crops[i].W);
        dir = dir.Length < 1e-9 ? new Vector(1, 0) : dir / dir.Length;
        var down = new Vector(-dir.Y, dir.X);
        static Point Mid(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
        return idx.GroupBy(Find)
            .Select(g =>
            {
                var along = crops[g.OrderByDescending(i => crops[i].W).First()].Along;
                var line = g.SelectMany(i => words[i]!).OrderBy(w => (Vector)Mid(w.Box) * along).ToList();
                var centre = g.Select(i => (Vector)crops[i].Centre).Aggregate((s, c) => s + c) / g.Count();
                return (Line: new OcrLine(line), Down: centre * down, Along: centre * dir, Height: g.Max(i => crops[i].H));
            })
            .OrderBy(l => l.Down).ThenBy(l => l.Along)
            .Aggregate(new List<(OcrLine Line, double Down, double Along, double Height)>(), (list, l) =>
            {
                // lines side by side (columns, "Label   value" far apart) keep left-to-right order within a row
                int at = list.Count;
                while (at > 0 && Math.Abs(list[at - 1].Down - l.Down) < 0.5 * Math.Min(list[at - 1].Height, l.Height) && list[at - 1].Along > l.Along) at--;
                list.Insert(at, l);
                return list;
            })
            .Select(l => l.Line).ToList();
    }

    /// <summary>
    /// The better of the two readings of a line. When they say the same up to accents, Windows' wins (it gets ą, ł, ż right);
    /// when they differ - or Windows' misses a part - PP-OCR's, if it is fairly sure of it: Windows' engine tends to drop
    /// or make things up on photos.
    /// </summary>
    private static List<OcrWord>? Choose((List<OcrWord> Words, string Text, float Score) mine, List<OcrWord>? theirs, out bool windowsWon, out string theirText)
    {
        theirText = theirs == null ? "" : string.Join(" ", theirs.Select(w => w.Text)).Trim();
        // one or two letters are only believed when the model is very sure (a logo is not a "C")
        int letters = mine.Text.Count(c => !char.IsWhiteSpace(c));
        bool mineOk = mine.Words.Count > 0 && mine.Score >= MinScore && (letters > 2 || mine.Score >= 0.85f);
        windowsWon = false;
        if (theirText.Length == 0) return mineOk ? mine.Words : null;
        string a = Fold(theirText), b = Fold(mine.Text);
        bool same = Similarity(a, b) >= 0.8 && a.Length >= b.Length * 0.85;
        if (!mineOk || same || mine.Score < 0.7)
        {
            windowsWon = true;
            return theirs;
        }
        return mine.Words;
    }

    /// <summary>
    /// For comparing readings: lower case, no spaces, no accents; ł and t count as l (PP-OCR's Latin model reads Polish ł
    /// as t or l), so "hasło" and "hasto" are the same reading.
    /// </summary>
    internal static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            if (char.IsWhiteSpace(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            char c = char.ToLowerInvariant(ch);
            sb.Append(c switch { 'ł' or 't' => 'l', 'ø' => 'o', 'đ' => 'd', 'ı' => 'i', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>1 = the same, 0 = nothing in common (edit distance over the longer length).</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return a.Length == b.Length ? 1 : 0;
        return 1 - (double)Distance(a, b) / Math.Max(a.Length, b.Length);
    }

    /// <summary>Edit distance: letters added, dropped or changed to get from one to the other.</summary>
    internal static int Distance(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return Math.Max(a.Length, b.Length);
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>The detector's text probability as a grey picture (dev tools).</summary>
    private static void DumpMap(float[] prob, int w, int h, string name)
    {
        try
        {
            Directory.CreateDirectory(DumpDir!);
            var o = new PixelBuffer(w, h);
            for (int i = 0; i < w * h; i++)
            {
                byte v = (byte)Math.Clamp(prob[i] * 255, 0, 255);
                o.Data[i * 4] = o.Data[i * 4 + 1] = o.Data[i * 4 + 2] = v;
                o.Data[i * 4 + 3] = 255;
            }
            File.WriteAllBytes(Path.Combine(DumpDir!, name), ImageIO.EncodePng(o.ToBitmap()));
        }
        catch (Exception ex) { Log.Warn("OCR dump: " + ex.Message); }
    }

    private static void Dump(float[] input, int count, int width, int first)
    {
        try
        {
            Directory.CreateDirectory(DumpDir!);
            int plane = RecHeight * width;
            for (int n = 0; n < count; n++)
            {
                var o = new PixelBuffer(width, RecHeight);
                for (int i = 0; i < plane; i++)
                {
                    int b = n * 3 * plane + i;
                    o.Data[i * 4] = (byte)Math.Clamp((input[b] + 1) * 127.5f, 0, 255);
                    o.Data[i * 4 + 1] = (byte)Math.Clamp((input[plane + b] + 1) * 127.5f, 0, 255);
                    o.Data[i * 4 + 2] = (byte)Math.Clamp((input[2 * plane + b] + 1) * 127.5f, 0, 255);
                    o.Data[i * 4 + 3] = 255;
                }
                File.WriteAllBytes(Path.Combine(DumpDir!, $"line{first + n:000}.png"), ImageIO.EncodePng(o.ToBitmap()));
            }
        }
        catch (Exception ex) { Log.Warn("OCR dump: " + ex.Message); }
    }

    private readonly record struct Glyph(string Text, int T0, int T1, float P);

    /// <summary>CTC greedy decoding: the best class per step, repeats merged, blanks dropped; the score is the mean confidence.</summary>
    private static List<Glyph> Decode(float[] prob, int n, int T, int C, string[] classes, out float score)
    {
        var glyphs = new List<Glyph>();
        int prev = 0;
        float sum = 0;
        for (int t = 0; t < T; t++)
        {
            int o = (n * T + t) * C, best = 0;
            float p = prob[o];
            for (int c = 1; c < C; c++)
                if (prob[o + c] > p) { p = prob[o + c]; best = c; }
            if (best == 0) { prev = 0; continue; }
            if (best == prev) { var g = glyphs[^1]; glyphs[^1] = g with { T1 = t }; continue; }
            prev = best;
            glyphs.Add(new Glyph(best < classes.Length ? classes[best] : "", t, t, p));
            sum += p;
        }
        score = glyphs.Count == 0 ? 0 : sum / glyphs.Count;
        return glyphs;
    }

    /// <summary>
    /// The line's words with their boxes: every character sits where its CTC steps are; a word reaches halfway to its
    /// neighbours. <paramref name="stride"/>: input pixels per step; <paramref name="scale"/>: crop pixels per input pixel.
    /// </summary>
    private static List<OcrWord> Words(List<Glyph> line, Crop crop, double stride, double scale)
    {
        int n = line.Count;
        var centre = new double[n];
        for (int i = 0; i < n; i++) centre[i] = (line[i].T0 + line[i].T1 + 1) / 2.0 * stride * scale;
        double pitch = n > 1 ? (centre[n - 1] - centre[0]) / (n - 1) : crop.W;
        double Left(int i) => i > 0 ? (centre[i - 1] + centre[i]) / 2 : Math.Max(0, centre[0] - pitch / 2);
        double Right(int i) => i < n - 1 ? (centre[i] + centre[i + 1]) / 2 : Math.Min(crop.W, centre[n - 1] + pitch / 2);

        var words = new List<OcrWord>();
        int a = -1;
        for (int i = 0; i <= n; i++)
        {
            bool gap = i == n || line[i].Text == " ";
            if (!gap) { if (a < 0) a = i; continue; }
            if (a < 0) continue;
            var text = string.Concat(line.Skip(a).Take(i - a).Select(g => g.Text));
            double u0 = n == 1 ? 0 : Left(a), u1 = n == 1 ? crop.W : Right(i - 1);
            words.Add(new OcrWord(text, crop.Box(u0, 0, u1, crop.H)));
            a = -1;
        }
        return words;
    }
}
