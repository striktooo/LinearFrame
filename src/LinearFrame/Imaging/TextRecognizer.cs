using System.Runtime.InteropServices;
using System.Windows;
using LinearFrame.Core;

namespace LinearFrame.Imaging;

/// <summary>A recognised word and where it is in the picture (pixels).</summary>
public sealed record OcrWord(string Text, Rect Box);

/// <summary>A line of text: its words in reading order.</summary>
public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));
    public Rect Box => Words.Count == 0 ? Rect.Empty : Words.Skip(1).Aggregate(Words[0].Box, (r, w) => Rect.Union(r, w.Box));
}

/// <summary>All the text found in a picture.</summary>
public sealed record OcrText(IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// Text recognition, two engines working together:
/// * the one built into Windows (Windows.Media.Ocr - offline, in the languages of the user's Windows, so Polish letters come
///   out right; called through WinRT directly, no 25 MB projection library) reads screen text quickly and very well;
/// * PaddleOCR's PP-OCRv5 (<see cref="PaddleOcr"/>, engine folder) finds text where Windows' engine gets lost - photos,
///   labels, slanted or curved pages, text on busy backgrounds - and reads what Windows can't.
/// Screens go to Windows' engine; photos (or a screen where it finds nothing) are taken apart line by line by PP-OCR and
/// each straightened line is read by both. Positions come back in the picture's own pixels, so the text can be selected
/// right on it.
/// </summary>
public static unsafe class TextRecognizer
{
    private static readonly object Gate = new();
    private static IntPtr _engine;
    private static uint _maxDim;
    private static bool _tried;

    /// <summary>Text can be read: the PP-OCR models are there, or Windows has an OCR language (practically every Windows 10/11).</summary>
    public static bool Available
    {
        get
        {
            if (PaddleOcr.Available) return true;
            lock (Gate) { EnsureEngine(); return _engine != IntPtr.Zero; }
        }
    }

    /// <summary>Set LF_OCR=windows to compare with the old engine (dev tools).</summary>
    private static readonly bool WindowsOnly = Environment.GetEnvironmentVariable("LF_OCR") == "windows";

    public static Task<OcrText?> RecognizeAsync(PixelBuffer px, CancellationToken ct = default) => Task.Run(() => Recognize(px, ct), ct);

    public static OcrText? Recognize(PixelBuffer px, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool paddle = !WindowsOnly && PaddleOcr.Available;
        string why = "";
        bool photo = paddle && LooksLikePhoto(px, out why);
        OcrText? windows = null;
        if (!photo)
        {
            // a screen: Windows' engine reads it best and in a few dozen ms
            windows = RecognizeWindows(px, ct);
            if (!paddle || windows is { IsEmpty: false })
            {
                Log.Info($"OCR (Windows) {px.Width}×{px.Height} ({why}): {sw.ElapsedMilliseconds} ms, {windows?.Lines.Count ?? 0} lines");
                return windows;
            }
        }
        try
        {
            bool win = WindowsAvailable;
            var text = PaddleOcr.Recognize(px, ct, win ? ReadSheet : null);
            if (text != null)
            {
                Log.Info($"OCR (PP-OCRv5{(win ? " + Windows" : "")}, {(photo ? "photo" : "screen")}, {why}) {PaddleOcr.LastTimings}, {sw.ElapsedMilliseconds} ms in all, {text.Lines.Count} lines");
                return text;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn("PP-OCR failed, Windows OCR instead: " + ex.Message); }
        return windows ?? RecognizeWindows(px, ct);
    }

    private static bool WindowsAvailable
    {
        get { lock (Gate) { EnsureEngine(); return _engine != IntPtr.Zero; } }
    }

    /// <summary>
    /// For PP-OCR: Windows' engine on a sheet of straightened lines, as is (no enlarging, no deskew). Lines in the sheet's
    /// pixels, or null. Sheets taller than the engine accepts are read in parts by the caller (<see cref="SheetLimit"/>).
    /// </summary>
    private static List<OcrLine>? ReadSheet(PixelBuffer sheet, CancellationToken ct)
    {
        lock (Gate)
        {
            EnsureEngine();
            if (_engine == IntPtr.Zero) return null;
            ct.ThrowIfCancellationRequested();
            return Run(sheet, ct, out _);
        }
    }

    /// <summary>The largest side Windows' engine reads (it says so itself; 4000 when it doesn't).</summary>
    internal static int SheetLimit
    {
        get { lock (Gate) { EnsureEngine(); return (int)Math.Min(_maxDim > 0 ? _maxDim : 4000, 10000); } }
    }

    /// <summary>
    /// A photo (camera noise and soft shading nearly everywhere) rather than a screen (flat areas, crisp edges): only a
    /// sample of neighbouring pixels is compared, so it takes a millisecond or two.
    /// </summary>
    internal static bool LooksLikePhoto(PixelBuffer px, out string why)
    {
        int w = px.Width, h = px.Height;
        int step = Math.Max(1, (int)Math.Sqrt((double)w * h / 200_000));
        long flat = 0, soft = 0, n = 0;
        var d = px.Data;
        for (int y = 0; y < h; y += step)
            for (int x = 0; x + 1 < w; x += step)
            {
                int i = (y * w + x) * 4;
                int m = Math.Max(Math.Abs(d[i] - d[i + 4]), Math.Max(Math.Abs(d[i + 1] - d[i + 5]), Math.Abs(d[i + 2] - d[i + 6])));
                if (m == 0) flat++;
                else if (m <= 10) soft++;
                n++;
            }
        double f = n == 0 ? 1 : (double)flat / n, s = n == 0 ? 0 : (double)soft / n;
        why = $"flat {f:0.00}, soft {s:0.00}";
        return f < 0.4 && s > 0.3;
    }

    private static OcrText? RecognizeWindows(PixelBuffer px, CancellationToken ct)
    {
        lock (Gate)
        {
            try
            {
                EnsureEngine();
                if (_engine == IntPtr.Zero) return null;
                // how much bigger the engine gets to see it: small UI text needs room, photos must fit the engine's limit
                double k = Math.Max(px.Width, px.Height) <= 900 && (long)px.Width * px.Height < 700_000 ? 3
                    : (long)px.Width * px.Height < 4_000_000 ? 2 : 1;
                double max = Math.Min(_maxDim > 0 ? _maxDim : 4000, 6000);
                k = Math.Min(k, max / Math.Max(px.Width, px.Height));
                var src = Math.Abs(k - 1) < 0.01 ? px : ImageOps.Resize(px, Math.Max(1, (int)Math.Round(px.Width * k)), Math.Max(1, (int)Math.Round(px.Height * k)));
                ct.ThrowIfCancellationRequested();
                var lines = Run(src, ct, out double angle);
                if (lines == null) return null;
                // a page photographed at an angle: read once more straightened, keep what reads better
                if (Math.Abs(angle) >= 1.0 && Math.Abs(angle) <= 45)
                {
                    var (turned, back) = Rotate(src, -angle);
                    var again = Run(turned, ct, out _);
                    if (again != null && Letters(again) > Letters(lines))
                        lines = again.Select(l => new OcrLine(l.Words.Select(w => new OcrWord(w.Text, back(w.Box))).ToList())).ToList();
                }
                // back to the picture's own pixels; pieces of one line (split at wide gaps) joined again
                var scaled = lines.Select(l => new OcrLine(l.Words.Select(w => new OcrWord(w.Text,
                    new Rect(w.Box.X / k, w.Box.Y / k, w.Box.Width / k, w.Box.Height / k))).ToList())).ToList();
                return new OcrText(JoinLines(scaled));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("OCR: " + ex.Message);
                return null;
            }
        }
    }

    // ---- for experiments (the self-test's --ocr-exp)
    internal static List<OcrLine>? RunRaw(PixelBuffer px, out double angle)
    {
        lock (Gate)
        {
            EnsureEngine();
            angle = 0;
            return _engine == IntPtr.Zero ? null : Run(px, CancellationToken.None, out angle);
        }
    }

    internal static PixelBuffer RotateForTest(PixelBuffer px, double degrees) => Rotate(px, degrees).Turned;

    private static int Letters(List<OcrLine> lines) => lines.Sum(l => l.Words.Sum(w => w.Text.Count(char.IsLetterOrDigit)));

    /// <summary>
    /// Pieces of one line (the engines split lines at wide gaps: code, tables, "Label: value") are put back together, left
    /// to right: two pieces belong together when they share most of their height and the gap between them is under ~2
    /// line heights - and so does everything chained to them that way. Far apart (another column) they stay separate.
    /// </summary>
    internal static List<OcrLine> JoinLines(List<OcrLine> lines)
    {
        int n = lines.Count;
        var boxes = lines.Select(l => l.Box).ToArray();
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                Rect a = boxes[i], b = boxes[j];
                double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                if (overlap <= Math.Min(a.Height, b.Height) * 0.5) continue;
                double gap = b.Left > a.Right ? b.Left - a.Right : a.Left > b.Right ? a.Left - b.Right : 0;
                if (gap < Math.Max(a.Height, b.Height) * 2.2) parent[Find(i)] = Find(j);
            }
        return Enumerable.Range(0, n).GroupBy(Find)
            .Select(g => new OcrLine(g.SelectMany(i => lines[i].Words).OrderBy(w => w.Box.Left).ToList()))
            .Where(l => l.Words.Count > 0)
            .OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left).ToList();
    }

    /// <summary>The picture turned by <paramref name="degrees"/> (white around it), and how to map a box back.</summary>
    private static (PixelBuffer Turned, Func<Rect, Rect> Back) Rotate(PixelBuffer src, double degrees)
    {
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        int w = src.Width, h = src.Height;
        int nw = (int)Math.Ceiling(Math.Abs(w * cos) + Math.Abs(h * sin)), nh = (int)Math.Ceiling(Math.Abs(w * sin) + Math.Abs(h * cos));
        var dst = new PixelBuffer(nw, nh);
        double cx = w / 2.0, cy = h / 2.0, ncx = nw / 2.0, ncy = nh / 2.0;
        var s = src.Data;
        var d = dst.Data;
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                // where this pixel comes from in the original (inverse rotation), bilinear
                double dx = x + 0.5 - ncx, dy = y + 0.5 - ncy;
                double sx = cos * dx + sin * dy + cx - 0.5, sy = -sin * dx + cos * dy + cy - 0.5;
                int o = (y * nw + x) * 4;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                if (x0 < 0 || y0 < 0 || x0 >= w - 1 || y0 >= h - 1) { d[o] = d[o + 1] = d[o + 2] = d[o + 3] = 255; continue; }
                double fx = sx - x0, fy = sy - y0;
                int i00 = (y0 * w + x0) * 4, i10 = i00 + 4, i01 = i00 + w * 4, i11 = i01 + 4;
                for (int c = 0; c < 4; c++)
                    d[o + c] = (byte)(s[i00 + c] * (1 - fx) * (1 - fy) + s[i10 + c] * fx * (1 - fy) + s[i01 + c] * (1 - fx) * fy + s[i11 + c] * fx * fy + 0.5);
            }
        Rect Back(Rect r)
        {
            // the box's corners turned back into the original picture
            var pts = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft }.Select(p =>
            {
                double dx = p.X - ncx, dy = p.Y - ncy;
                return new Point(cos * dx + sin * dy + cx, -sin * dx + cos * dy + cy);
            }).ToArray();
            double l = pts.Min(p => p.X), t = pts.Min(p => p.Y);
            return new Rect(l, t, pts.Max(p => p.X) - l, pts.Max(p => p.Y) - t);
        }
        return (dst, Back);
    }

    // =================================================================== WinRT plumbing

    [DllImport("combase.dll")] private static extern int RoInitialize(int type);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    private static Guid IID_IOcrEngineStatics = new("5bffa85a-3384-3540-9940-699120d428a8");
    private static Guid IID_ISoftwareBitmapStatics = new("df0385db-672f-4a9d-806e-c2442f343e86");
    private static Guid IID_IBufferFactory = new("71af914d-c10f-484b-bc50-14bc623b3a27");
    private static Guid IID_IBufferByteAccess = new("905a0fef-bc53-11df-8c49-001e4fc686da");
    private static Guid IID_IAsyncInfo = new("00000036-0000-0000-c000-000000000046");
    private static Guid IID_ILanguageFactory = new("9b0252ac-0c27-44f8-b792-9793fb66c63e");

    private static void* Slot(IntPtr obj, int i) => (*(void***)obj)[i];
    private static void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(p, 2))(p); }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException(what, hr);
    }

    private static IntPtr Factory(string cls, Guid iid)
    {
        Check(WindowsCreateString(cls, cls.Length, out var h), "string");
        try
        {
            Check(RoGetActivationFactory(h, ref iid, out var f), cls);
            return f;
        }
        finally { WindowsDeleteString(h); }
    }

    private static string Str(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var p = WindowsGetStringRawBuffer(h, out uint len);
        var s = new string(p, 0, (int)len);
        WindowsDeleteString(h);
        return s;
    }

    private static void EnsureEngine()
    {
        if (_tried) return;
        _tried = true;
        RoInitialize(1); // multithreaded (fine if the thread already is)
        var statics = Factory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);
        try
        {
            uint max;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(statics, 6))(statics, &max);
            _maxDim = max;
            IntPtr engine;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(statics, 10))(statics, &engine), "TryCreateFromUserProfileLanguages");
            if (engine == IntPtr.Zero)
            {
                // no OCR for the profile languages: English is there on almost every Windows
                engine = FromLanguage(statics, "en-US");
            }
            _engine = engine;
            Log.Info(_engine != IntPtr.Zero ? $"OCR ready (max {max} px)" : "OCR: no recognizer language installed");
        }
        finally { Release(statics); }
    }

    private static IntPtr FromLanguage(IntPtr statics, string tag)
    {
        var lf = Factory("Windows.Globalization.Language", IID_ILanguageFactory);
        try
        {
            Check(WindowsCreateString(tag, tag.Length, out var h), "string");
            IntPtr lang;
            try { Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(lf, 6))(lf, h, &lang), "CreateLanguage"); }
            finally { WindowsDeleteString(h); }
            try
            {
                IntPtr engine;
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 9))(statics, lang, &engine);
                return engine;
            }
            finally { Release(lang); }
        }
        finally { Release(lf); }
    }

    private static List<OcrLine>? Run(PixelBuffer px, CancellationToken ct, out double angle)
    {
        angle = 0;
        IntPtr buffer = IntPtr.Zero, bitmap = IntPtr.Zero, op = IntPtr.Zero, info = IntPtr.Zero, result = IntPtr.Zero, lines = IntPtr.Zero;
        try
        {
            // the pixels into a WinRT buffer...
            uint size = (uint)(px.Width * px.Height * 4);
            var bf = Factory("Windows.Storage.Streams.Buffer", IID_IBufferFactory);
            try { Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(bf, 6))(bf, size, &buffer), "Buffer.Create"); }
            finally { Release(bf); }
            IntPtr access;
            Check(Marshal.QueryInterface(buffer, in IID_IBufferByteAccess, out access), "IBufferByteAccess");
            try
            {
                byte* dst;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, int>)Slot(access, 3))(access, &dst), "Buffer");
                Marshal.Copy(px.Data, 0, (IntPtr)dst, (int)size);
            }
            finally { Release(access); }
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(buffer, 8))(buffer, size), "put_Length");

            // ...a SoftwareBitmap (BGRA8) from it...
            var sb = Factory("Windows.Graphics.Imaging.SoftwareBitmap", IID_ISoftwareBitmapStatics);
            try { Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, int, IntPtr*, int>)Slot(sb, 9))(sb, buffer, 87, px.Width, px.Height, &bitmap), "CreateCopyFromBuffer"); }
            finally { Release(sb); }

            // ...and recognised (asynchronous: waited for here, on this background thread)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(_engine, 6))(_engine, bitmap, &op), "RecognizeAsync");
            Check(Marshal.QueryInterface(op, in IID_IAsyncInfo, out info), "IAsyncInfo");
            int status;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 7))(info, &status), "Status");
                if (status != 0) break;
                if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > 20000)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(info, 9))(info); // Cancel
                    ct.ThrowIfCancellationRequested();
                    return null;
                }
                Thread.Sleep(3);
            }
            if (status != 1) return null; // canceled or error
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(op, 8))(op, &result), "GetResults");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(result, 6))(result, &lines), "Lines");
            IntPtr angleRef;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(result, 7))(result, &angleRef) >= 0 && angleRef != IntPtr.Zero)
            {
                double v;
                if (((delegate* unmanaged[Stdcall]<IntPtr, double*, int>)Slot(angleRef, 6))(angleRef, &v) >= 0) angle = v;
                Release(angleRef);
            }

            var list = new List<OcrLine>();
            float* r = stackalloc float[4];
            foreach (var line in Items(lines))
            {
                IntPtr words = IntPtr.Zero;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(line, 6))(line, &words), "Words");
                    var ws = new List<OcrWord>();
                    foreach (var word in Items(words))
                    {
                        try
                        {
                            IntPtr h;
                            ((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Slot(word, 6))(word, r);
                            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(word, 7))(word, &h);
                            var t = Str(h);
                            if (t.Length > 0) ws.Add(new OcrWord(t, new Rect(r[0], r[1], Math.Max(0, r[2]), Math.Max(0, r[3]))));
                        }
                        finally { Release(word); }
                    }
                    if (ws.Count > 0) list.Add(new OcrLine(ws));
                }
                finally
                {
                    Release(words);
                    Release(line);
                }
            }
            return list;
        }
        finally
        {
            Release(lines);
            Release(result);
            if (info != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(info, 10))(info); Release(info); } // Close
            Release(op);
            if (bitmap != IntPtr.Zero)
            {
                // SoftwareBitmap is IClosable: let go of its pixels right away
                var closable = new Guid("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
                if (Marshal.QueryInterface(bitmap, in closable, out var c) >= 0) { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(c, 6))(c); Release(c); }
                Release(bitmap);
            }
            Release(buffer);
        }
    }

    /// <summary>The items of an IVectorView (each one AddRef'd - the caller releases them).</summary>
    private static List<IntPtr> Items(IntPtr vector)
    {
        var list = new List<IntPtr>();
        uint n;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(vector, 7))(vector, &n);
        for (uint i = 0; i < n; i++)
        {
            IntPtr item;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(vector, 6))(vector, i, &item) >= 0 && item != IntPtr.Zero)
                list.Add(item);
        }
        return list;
    }
}
