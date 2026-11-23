using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Capture;
using LinearFrame.Core;
using LinearFrame.Editor;
using LinearFrame.Imaging;
using LinearFrame.Player;
using LinearFrame.Player.Edit;
using LinearFrame.UI;
using LinearFrame.Viewer;

namespace LinearFrame.Dev;

/// <summary>
/// Developer checks that run without touching the desktop:
///   LinearFrame.exe --make-icon path.ico
///   LinearFrame.exe --selftest outDir
///   LinearFrame.exe --render-ui outDir
/// </summary>
internal static class SelfTest
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);

    private static readonly StringBuilder Report = new();
    private static int _failures;

    public static int Run(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "--make-icon":
                    AppIconArt.WriteIco(args[1]);
                    return 0;
                case "--ocr-exp":
                    return OcrExp(args[1], args[2]);
                case "--ocr-file":
                    return OcrFile(args[1], args[2]);
                case "--ocr-test":
                    return OcrTest(args[1]);
                case "--qr-test":
                    return QrTest(args[1]);
                case "--icon-preview":
                    IconPreview(args[1]);
                    return 0;
                case "--render-ui":
                    RenderUi(args.Length > 1 ? args[1] : ".");
                    return 0;
                case "--marketing":
                    Marketing.Run(args.Length > 1 ? args[1] : ".");
                    return 0;
                case "--compose":
                    Marketing.Compose(args[1], args[2]);
                    return 0;
                default:
                    var dir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "linearframe-selftest");
                    Directory.CreateDirectory(dir);
                    RunAll(dir);
                    File.WriteAllText(Path.Combine(dir, "report.txt"), Report.ToString());
                    return _failures;
            }
        }
        catch (Exception ex)
        {
            Report.AppendLine("FATAL " + ex);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "linearframe-selftest-fatal.txt"), Report.ToString());
            return 99;
        }
    }

    /// <summary>LF_SELFTEST_ONLY="edit,ffmpeg: generate" runs only the checks whose names contain one of the parts.</summary>
    private static readonly string[]? Only = Environment.GetEnvironmentVariable("LF_SELFTEST_ONLY")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void Check(string name, Func<string> test)
    {
        if (Only != null && !Only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase))) return;
        var sw = Stopwatch.StartNew();
        try
        {
            var info = test();
            Report.AppendLine($"PASS  {name} ({sw.ElapsedMilliseconds} ms) {info}");
        }
        catch (Exception ex)
        {
            _failures++;
            Report.AppendLine($"FAIL  {name} ({sw.ElapsedMilliseconds} ms) {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>A sheet with the app icon and the file icons at several sizes, on light and dark (LinearFrame.exe --icon-preview out.png).</summary>
    private static void IconPreview(string path)
    {
        string[] exts = { ".mp4", ".mkv", ".mov", ".mp3", ".flac", ".m4a", ".jpg", ".png", ".heic", ".gif" };
        int W = 1500, H = 760;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var light = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF6));
            var dark = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x22));
            dc.DrawRectangle(light, null, new Rect(0, 0, W, H / 2));
            dc.DrawRectangle(dark, null, new Rect(0, H / 2, W, H / 2));
            for (int half = 0; half < 2; half++)
            {
                double y = half * H / 2 + 24;
                double x = 24;
                foreach (int s in new[] { 256, 64, 32, 16 })
                {
                    var bmp = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(AppIconArt.Draw(s));
                    dc.DrawImage(bmp, new Rect(x, y + (256 - s) / 2.0, s, s));
                    x += s + 18;
                }
                x += 20;
                foreach (var e in exts)
                {
                    foreach (int s in new[] { 96, 32, 16 })
                    {
                        var bmp = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
                        bmp.Render(FileTypeIcons.Draw(FileTypeIcons.KindOf(e), e, s));
                        double yy = y + (s == 96 ? 0 : s == 32 ? 110 : 156);
                        double xx = x + (s == 96 ? 0 : (96 - s) / 2.0);
                        dc.DrawImage(bmp, new Rect(xx, yy, s, s));
                    }
                    x += 108;
                    if (x > W - 110) { x = 24 + 256 + 64 + 32 + 16 + 4 * 18 + 20; y += 190; }
                }
            }
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        Save(rtb, path);
    }

    /// <summary>Reads every picture listed in dir/cases.json and compares the QR text (LinearFrame.exe --qr-test dir).</summary>
    private static int QrTest(string dir)
    {
        var cases = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "cases.json")))!;
        var sb = new StringBuilder();
        int ok = 0;
        foreach (var (file, want) in cases)
        {
            var bmp = ImageIO.LoadFull(Path.Combine(dir, file));
            if (bmp == null) { sb.AppendLine($"FAIL  {file}: cannot load"); continue; }
            var px = PixelBuffer.FromBitmap(bmp);
            var sw = Stopwatch.StartNew();
            var codes = QrReader.Read(px);
            long ms = sw.ElapsedMilliseconds;
            string got = string.Join("|", codes.Select(c => c.Text).OrderBy(x => x, StringComparer.Ordinal));
            string expect = string.Join("|", want.Split('|', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal));
            bool pass = got == expect;
            if (pass) ok++;
            string shown = got.Length > 60 ? got[..60] + "…" : got;
            sb.AppendLine($"{(pass ? "PASS" : "FAIL")}  {file,-18} {px.Width}x{px.Height} {ms,4} ms  {(pass ? shown : $"got '{shown}'")}");
        }
        sb.Insert(0, $"QR: {ok}/{cases.Count}\n");
        File.WriteAllText(Path.Combine(dir, "qr-report.txt"), sb.ToString());
        return cases.Count - ok;
    }

    /// <summary>Renders some text and reads it back with Windows OCR (LinearFrame.exe --ocr-test dir).</summary>
    private static int OcrTest(string dir)
    {
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        int fails = 0;
        var samples = new (string Name, string Font, double Size, string Text, int W, int H, double Angle)[]
        {
            ("ui-12px", "Segoe UI", 12, "Plik  Edycja  Widok  Pomoc\nZapisz zmiany przed zamknięciem?\nUstawienia konta: hasło, e-mail, powiadomienia", 520, 90, 0),
            ("doc-pl", "Calibri", 22, "Zażółć gęślą jaźń. Szybki brązowy lis\nprzeskakuje nad leniwym psem 1234.\nKontakt: biuro@example.com, tel. 600 700 800", 760, 150, 0),
            ("code", "Consolas", 15, "var total = items.Sum(x => x.Price * 2);\nif (total > 100) Console.WriteLine(\"ok\");", 640, 70, 0),
            ("tilted", "Segoe UI", 26, "Zdjęcie kartki pod kątem\nteż powinno dać się odczytać", 640, 140, 6),
            ("handwriting-like", "Ink Free", 34, "Kupić mleko i chleb\nspotkanie o 17:30", 620, 160, 0),
            ("arial-14", "Arial", 14, "Faktura VAT nr 2026/11/0457 z dnia 20.11.2026\nNabywca: Jan Kowalski, ul. Długa 15, 00-950 Warszawa\nDo zapłaty: 1 234,56 zł (słownie: tysiąc dwieście)", 620, 80, 0),
            ("times-18", "Times New Roman", 18, "Rozdział pierwszy. Wtedy właśnie poczuł,\nże coś się zmieniło - świat był cichszy niż zwykle.", 600, 70, 0),
            ("condensed", "Bahnschrift SemiCondensed", 20, "Przesyłka kurierska: odbiór w paczkomacie\nKod odbioru 482913, ważny do piątku 23:59", 560, 70, 0),
            ("verdana-11", "Verdana", 11, "Wyślij wiadomość · Odpowiedz wszystkim · Przekaż dalej\nOstatnio widziany: wczoraj o 22:41", 520, 50, 0),
        };
        foreach (var s in samples)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, s.W, s.H));
                if (s.Angle != 0) dc.PushTransform(new RotateTransform(s.Angle, s.W / 2.0, s.H / 2.0));
                var ft = new FormattedText(s.Text, CultureInfo.GetCultureInfo("pl-PL"), FlowDirection.LeftToRight, new Typeface(s.Font), s.Size, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(16, s.Angle != 0 ? 50 : 8));
                if (s.Angle != 0) dc.Pop();
            }
            var bmp = RenderVisual(dv, s.W, s.H);
            Save(bmp, Path.Combine(dir, s.Name + ".png"));
            var sw = Stopwatch.StartNew();
            var r = TextRecognizer.Recognize(PixelBuffer.FromBitmap(bmp));
            long ms = sw.ElapsedMilliseconds;
            string got = r?.Text.Replace("\r", "") ?? "(no OCR)";
            // a word-level score: how many of the expected words came back exactly
            var want = s.Text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var have = got.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            int hit = want.Count(w => have.Contains(w));
            if (hit < want.Length * 0.8) fails++;
            sb.AppendLine($"== {s.Name}: {hit}/{want.Length} words, {ms} ms, {r?.Lines.Count ?? 0} lines");
            sb.AppendLine(got);
        }
        File.WriteAllText(Path.Combine(dir, "ocr-report.txt"), sb.ToString());
        return fails;
    }

    /// <summary>A "photo" of a label: a sheet of paper turned 7° on a brown table, camera noise over it all.</summary>
    private static BitmapSource MakePhotoLabel(string[] lines, string? upright = null)
    {
        const int W = 1200, H = 900;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x6B, 0x5A, 0x4A)), null, new Rect(0, 0, W, H));
            dc.PushTransform(new RotateTransform(-7, W / 2.0, H / 2.0));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF1, 0xEF, 0xEA)), null, new Rect(200, 170, 800, 560));
            double y = 220;
            foreach (var l in lines)
            {
                var ft = new FormattedText(l, CultureInfo.GetCultureInfo("pl-PL"), FlowDirection.LeftToRight, new Typeface("Arial"), 38,
                    new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26)), 1.0);
                dc.DrawText(ft, new Point(250, y));
                y += 100;
            }
            if (upright != null)
            {
                // a number running down the page, like the one beside a parcel's barcode
                dc.PushTransform(new RotateTransform(90, 950, 200));
                dc.DrawText(new FormattedText(upright, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 26,
                    new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26)), 1.0), new Point(950, 200));
                dc.Pop();
            }
            dc.Pop();
        }
        var px = PixelBuffer.FromBitmap(RenderVisual(dv, W, H));
        var rnd = new Random(7);
        var d = px.Data;
        for (int i = 0; i < d.Length; i += 4)
        {
            int n = rnd.Next(-12, 13);
            for (int c = 0; c < 3; c++) d[i + c] = (byte)Math.Clamp(d[i + c] + n + rnd.Next(-4, 5), 0, 255);
        }
        return px.ToBitmap();
    }

    /// <summary>OCR of one picture: the text, the time, and a copy with the found words framed (LinearFrame.exe --ocr-file pic outDir).</summary>
    private static int OcrFile(string file, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var bmp = ImageIO.LoadFull(file) ?? throw new Exception("cannot load " + file);
        var px = PixelBuffer.FromBitmap(bmp);
        TextRecognizer.Recognize(new PixelBuffer(64, 64)); // engine start-up outside the timing
        var sb = new StringBuilder();
        OcrText? r = null;
        // LF_OCR_RUNS=3: the same picture several times (the first run of a size is slower)
        int runs = int.TryParse(Environment.GetEnvironmentVariable("LF_OCR_RUNS"), out int n) ? Math.Clamp(n, 1, 10) : 1;
        for (int run = 0; run < runs; run++)
        {
            var sw = Stopwatch.StartNew();
            r = TextRecognizer.Recognize(px);
            long ms = sw.ElapsedMilliseconds;
            // what the process keeps afterwards (managed garbage collected first: the rest is the engines' memory)
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var self = Process.GetCurrentProcess();
            sb.AppendLine($"{Path.GetFileName(file)} {px.Width}x{px.Height}: {ms} ms, {r?.Lines.Count ?? 0} lines, {r?.Lines.Sum(l => l.Words.Count) ?? 0} words, private {self.PrivateMemorySize64 >> 20} MB after · {PaddleOcr.LastTimings}");
        }
        if (r != null)
            foreach (var l in r.Lines) sb.AppendLine($"[{l.Box.X:0},{l.Box.Y:0} {l.Box.Width:0}x{l.Box.Height:0}] {l.Text}");
        sb.AppendLine().AppendLine("Every line PP-OCR found: its score and reading | Windows' reading (* = taken)");
        foreach (var (c, text, score, win, winWon) in PaddleOcr.LastLines)
            sb.AppendLine(FormattableString.Invariant($"  {score:0.00} [{c.Min(p => p.X):0},{c.Min(p => p.Y):0}-{c.Max(p => p.X):0},{c.Max(p => p.Y):0}]  {(winWon ? "" : "*")}{text}  |  {(winWon ? "*" : "")}{win}"));
        File.WriteAllText(Path.Combine(outDir, "ocr.txt"), sb.ToString());
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(bmp, new Rect(0, 0, px.Width, px.Height));
            double t = Math.Max(2, px.Width / 600.0);
            var pen = new Pen(Brushes.Lime, t);
            var fill = new SolidColorBrush(Color.FromArgb(0x40, 0, 255, 0));
            if (r != null)
                foreach (var l in r.Lines)
                    foreach (var w in l.Words) dc.DrawRectangle(fill, pen, w.Box);
            // the detector's lines: blue when read, red when dropped
            foreach (var (c, _, score, _, _) in PaddleOcr.LastLines)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(c[0], false, true);
                    ctx.PolyLineTo(c.Skip(1).ToList(), true, false);
                }
                dc.DrawGeometry(null, new Pen(score >= 0.5f ? Brushes.DeepSkyBlue : Brushes.Red, t * 0.6), g);
            }
        }
        Save(RenderVisual(dv, px.Width, px.Height), Path.Combine(outDir, "ocr-boxes.png"));
        return 0;
    }

    /// <summary>OCR of one picture prepared in several ways, to see what the engine likes (LinearFrame.exe --ocr-exp pic outDir).</summary>
    private static int OcrExp(string file, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var bmp = ImageIO.LoadFull(file) ?? throw new Exception("cannot load " + file);
        var px = PixelBuffer.FromBitmap(bmp);
        TextRecognizer.RunRaw(new PixelBuffer(64, 64), out _);
        var sb = new StringBuilder();
        PixelBuffer Scale(PixelBuffer p, double k) => ImageOps.Resize(p, (int)(p.Width * k), (int)(p.Height * k));
        PixelBuffer Gray(PixelBuffer p, bool stretch)
        {
            var g = new PixelBuffer(p.Width, p.Height);
            var d = p.Data; var o = g.Data;
            int n = p.Width * p.Height;
            var lum = new byte[n];
            for (int i = 0; i < n; i++) lum[i] = (byte)((d[i * 4 + 2] * 299 + d[i * 4 + 1] * 587 + d[i * 4] * 114) / 1000);
            byte lo = 0, hi = 255;
            if (stretch)
            {
                var h = new int[256]; foreach (var v in lum) h[v]++;
                int acc = 0; for (int i = 0; i < 256; i++) { acc += h[i]; if (acc > n / 100) { lo = (byte)i; break; } }
                acc = 0; for (int i = 255; i >= 0; i--) { acc += h[i]; if (acc > n / 100) { hi = (byte)i; break; } }
            }
            for (int i = 0; i < n; i++)
            {
                int v = hi > lo ? Math.Clamp((lum[i] - lo) * 255 / (hi - lo), 0, 255) : lum[i];
                o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = (byte)v; o[i * 4 + 3] = 255;
            }
            return g;
        }
        void Try(string name, PixelBuffer p)
        {
            var sw = Stopwatch.StartNew();
            var lines = TextRecognizer.RunRaw(p, out double angle) ?? new List<OcrLine>();
            long ms = sw.ElapsedMilliseconds;
            int words = lines.Sum(l => l.Words.Count);
            sb.AppendLine($"== {name}: {p.Width}x{p.Height}, {ms} ms, angle {angle:0.0}, {lines.Count} lines, {words} words");
            foreach (var l in lines) sb.AppendLine("   " + l.Text);
        }
        Try("raw", px);
        Try("x0.75", Scale(px, 0.75));
        Try("x0.5", Scale(px, 0.5));
        Try("x1.5", Scale(px, 1.5));
        Try("gray+stretch", Gray(px, true));
        Try("rotate +8", TextRecognizer.RotateForTest(px, 8));
        Try("rotate -8", TextRecognizer.RotateForTest(px, -8));
        Try("rotate +15", TextRecognizer.RotateForTest(px, 15));
        Try("rotate -15", TextRecognizer.RotateForTest(px, -15));
        Try("rotate +90", TextRecognizer.RotateForTest(px, 90));
        File.WriteAllText(Path.Combine(outDir, "ocr-exp.txt"), sb.ToString());
        return 0;
    }

    private static void Save(BitmapSource bmp, string path) => File.WriteAllBytes(path, ImageIO.EncodePng(bmp));

    private static void RunAll(string dir)
    {
        Report.AppendLine($"LinearFrame self-test {DateTime.Now:O}");

        Check("app icon", () =>
        {
            Save(AppIconArt.Render(256), Path.Combine(dir, "icon-256.png"));
            var strip = new DrawingVisual();
            using (var dc = strip.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34)), null, new Rect(0, 0, 420, 80));
                double x = 10;
                foreach (int s in new[] { 16, 20, 24, 32, 48, 64 })
                {
                    dc.DrawImage(AppIconArt.Render(s), new Rect(x, 8, s, s));
                    x += s + 14;
                }
            }
            Save(RenderVisual(strip, 420, 80), Path.Combine(dir, "icon-sizes.png"));
            var h = AppIconArt.CreateHIcon(16);
            bool ok = h != IntPtr.Zero;
            if (ok) Native.DestroyIcon(h);
            if (!ok) throw new Exception("CreateIconFromResourceEx failed");
            return "hicon ok";
        });

        Check("icon sheet", () =>
        {
            var names = Icons.Names.ToList();
            int cols = 10, cell = 64, rows = (names.Count + cols - 1) / cols;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, cols * cell, rows * cell));
                var face = new Typeface("Segoe UI");
                for (int i = 0; i < names.Count; i++)
                {
                    var iv = new IconView { Icon = names[i], Width = 28, Height = 28, Foreground = Brushes.Black };
                    iv.Measure(new Size(28, 28));
                    iv.Arrange(new Rect(0, 0, 28, 28));
                    var rtbIcon = new RenderTargetBitmap(28, 28, 96, 96, PixelFormats.Pbgra32);
                    rtbIcon.Render(iv);
                    double x = (i % cols) * cell, y = (i / cols) * cell;
                    dc.DrawImage(rtbIcon, new Rect(x + 18, y + 6, 28, 28));
                    var ft = new FormattedText(names[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9, Brushes.Gray, 1.0);
                    dc.DrawText(ft, new Point(x + (cell - ft.Width) / 2, y + 42));
                }
            }
            Save(RenderVisual(dv, cols * cell, rows * cell), Path.Combine(dir, "icons.png"));
            return $"{names.Count} icons";
        });

        Check("monitors + screen capture", () =>
        {
            var mons = MonitorInfo.All();
            var virt = MonitorInfo.VirtualBounds(mons);
            ScreenCapturer.Capture(virt); // warm-up (first GDI call is slow)
            var sw = Stopwatch.StartNew();
            var pb = ScreenCapturer.Capture(virt);
            long ms = sw.ElapsedMilliseconds;
            // content sanity: not a single flat colour
            var c0 = pb.GetPixel(pb.Width / 3, pb.Height / 3);
            int distinct = 0;
            for (int i = 0; i < 64; i++) if (pb.GetPixel(i * pb.Width / 64, pb.Height / 2) != c0) distinct++;
            var wins = WindowFinder.VisibleWindows();
            return $"{mons.Count} monitors [{string.Join("; ", mons.Select(m => $"{m.Bounds.Width}x{m.Bounds.Height}@{m.Scale:0.##}"))}], virtual {virt.Width}x{virt.Height}, capture {ms} ms, {distinct}/64 varied samples, {wins.Count} windows";
        });

        var ui = MakeFakeUi(520, 300);
        Save(ui.ToBitmap(), Path.Combine(dir, "fake-ui.png"));

        Check("background detection", () =>
        {
            var c = ImageOps.DetectBackground(ui);
            if (Math.Abs(c.R - 0x2B) > 4 || Math.Abs(c.G - 0x2D) > 4 || Math.Abs(c.B - 0x31) > 4) throw new Exception($"got {c}");
            return c.ToString();
        });

        Check("lanczos upscale 4x", () =>
        {
            var big = ImageOps.Resize(ui, ui.Width * 4, ui.Height * 4);
            Save(big.ToBitmap(), Path.Combine(dir, "upscale-4x.png"));
            return $"{big.Width}x{big.Height}";
        });

        Check("lanczos 8K timing", () =>
        {
            var sw = Stopwatch.StartNew();
            var (w, h) = ExportSizes.TargetSize(ExportSize.UHD8K, ui.Width, ui.Height);
            var big = ImageOps.Resize(ui, w, h);
            long t1 = sw.ElapsedMilliseconds;
            var png = ImageIO.EncodePng(big.ToBitmap());
            return $"{w}x{h} resize {t1} ms, png {sw.ElapsedMilliseconds - t1} ms ({png.Length / 1024} KB)";
        });

        Check("export sizes", () =>
        {
            var a = ExportSizes.TargetSize(ExportSize.UHD4K, 800, 450);
            var b = ExportSizes.TargetSize(ExportSize.FullHD, 300, 800);
            if (a != (3840, 2160)) throw new Exception($"4K got {a}");
            if (b.H != 1920) throw new Exception($"portrait FHD got {b}");
            return $"{a} / {b}";
        });

        Check("markup render (all tools, extended frame, 2K)", () =>
        {
            var doc = new MarkupDocument(ui.ToBitmap());
            double u = doc.BaseUnit * 1.8;
            doc.Items.Add(new LineAnnotation { A = new Point(60, 250), B = new Point(200, 150), Arrow = true, Width = u, Color = Color.FromRgb(0xFF, 0x3B, 0x30) });
            doc.Items.Add(new LineAnnotation { A = new Point(40, 118), B = new Point(250, 118), Width = u * 0.7, Color = Color.FromRgb(0x00, 0x7A, 0xFF) });
            doc.Items.Add(new ShapeAnnotation { Rect = new Rect(300, 40, 180, 80), Kind = ShapeKind.Rect, Width = u * 0.8, Color = Color.FromRgb(0x34, 0xC7, 0x59) });
            doc.Items.Add(new ShapeAnnotation { Rect = new Rect(320, 150, 160, 90), Kind = ShapeKind.Ellipse, Width = u * 0.8, Color = Color.FromRgb(0xFF, 0x95, 0x00) });
            doc.Items.Add(new ShapeAnnotation { Rect = new Rect(40, 170, 140, 40), Kind = ShapeKind.Pixelate });
            doc.Items.Add(new StrokeAnnotation { Highlighter = true, Width = u, Color = Color.FromRgb(0xFF, 0xCC, 0x00), Points = { new Point(40, 80), new Point(120, 82), new Point(230, 80) } });
            doc.Items.Add(new StrokeAnnotation { Width = u * 0.6, Color = Color.FromRgb(0xAF, 0x52, 0xDE), Points = { new Point(250, 260), new Point(270, 240), new Point(300, 270), new Point(330, 235) } });
            doc.Items.Add(new CaptionAnnotation { Center = new Point(390, 262), Text = "Kliknij tutaj 👆", Size = 0.07, Color = Colors.White, CanvasWidth = doc.Width, CanvasHeight = doc.Height });
            doc.Items.Add(new CounterAnnotation { Center = new Point(500, 30), Number = 1, Width = u, Color = Color.FromRgb(0xFF, 0x3B, 0x30) });
            doc.Items.Add(new CounterAnnotation { Center = new Point(500, 80), Number = 2, Width = u, Color = Color.FromRgb(0xFF, 0x3B, 0x30) });
            doc.Frame = new Int32Rect(-80, -40, ui.Width + 160, ui.Height + 80);
            var bmp = Exporter.Render(doc, ExportSize.QHD);
            Save(bmp, Path.Combine(dir, "markup-2k.png"));
            var orig = Exporter.Render(doc, ExportSize.Original);
            Save(orig, Path.Combine(dir, "markup-1x.png"));
            return $"{bmp.PixelWidth}x{bmp.PixelHeight}";
        });

        Check("transparent extend", () =>
        {
            var doc = new MarkupDocument(ui.ToBitmap()) { Fill = FillMode.Transparent, Frame = new Int32Rect(-50, -50, ui.Width + 100, ui.Height + 100) };
            var bmp = Exporter.Render(doc, ExportSize.Original);
            var pb = PixelBuffer.FromBitmap(bmp);
            if (pb.GetPixel(2, 2).A != 0) throw new Exception("corner not transparent");
            return "corner alpha 0";
        });

        Check("editor scenario (draw, move, resize, undo/redo, crop+extend 16:9)", () => EditorScenario(dir));

        Check("shortcuts: single key, mouse buttons, settings round-trip", () =>
        {
            var end = new HotkeyGesture(System.Windows.Input.ModifierKeys.None, System.Windows.Input.Key.End);
            var m5 = new HotkeyGesture(System.Windows.Input.ModifierKeys.None, HotMouse.XButton2);
            var ctrlM4 = new HotkeyGesture(System.Windows.Input.ModifierKeys.Control, HotMouse.XButton1);
            if (end.ToString() != "End" || !end.IsLoneKey || end.VirtualKey != 0x23) throw new Exception($"End: {end} vk {end.VirtualKey}");
            if (m5.ToString() != L.T("Hotkey.Mouse5") || !m5.IsMouse || m5.IsLoneKey) throw new Exception($"M5: {m5}");
            if (!ctrlM4.ToString().StartsWith("Ctrl + ")) throw new Exception($"Ctrl+M4: {ctrlM4}");
            if (m5.SameAs(ctrlM4) || !m5.SameAs(m5.Clone()) || end.SameAs(m5)) throw new Exception("SameAs");
            foreach (var k in new[] { System.Windows.Input.Key.A, System.Windows.Input.Key.D4, System.Windows.Input.Key.Space, System.Windows.Input.Key.OemTilde, System.Windows.Input.Key.NumPad5 })
                if (!HotkeyBox.IsTypingKey(k)) throw new Exception($"{k} should need a modifier");
            foreach (var k in new[] { System.Windows.Input.Key.End, System.Windows.Input.Key.Home, System.Windows.Input.Key.F13, System.Windows.Input.Key.Snapshot, System.Windows.Input.Key.Pause, System.Windows.Input.Key.Insert })
                if (HotkeyBox.IsTypingKey(k)) throw new Exception($"{k} should work alone");

            var s = new AppSettings { RegionHotkey = end, RegionHotkey2 = m5 };
            var json = System.Text.Json.JsonSerializer.Serialize(s, AppSettings.Json);
            var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Json)!;
            if (!back.RegionHotkey.SameAs(end) || back.RegionHotkey2.Mouse != HotMouse.XButton2) throw new Exception("round-trip");
            // a 1.0.0 settings file (with the old full-screen shortcut) still loads
            var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                "{\"RegionHotkey\":{\"Modifiers\":\"Control, Shift\",\"Key\":\"D4\"},\"FullscreenHotkey\":{\"Modifiers\":\"Control, Shift\",\"Key\":\"D3\"}}", AppSettings.Json)!;
            if (old.RegionHotkey.ToString() != "Ctrl + Shift + 4" || old.RegionHotkey2 == null || !old.RegionHotkey2.IsEmpty) throw new Exception("old settings");

            // the mouse hook installs and goes away cleanly (no bindings -> nothing is swallowed meanwhile)
            var sw = Stopwatch.StartNew();
            using (var hook = new MouseShortcutHook(IntPtr.Zero, 0))
                if (!hook.IsActive) throw new Exception("mouse hook not installed");
            return $"End / M5 / Ctrl+M4 ok, JSON ok, mouse hook on+off {sw.ElapsedMilliseconds} ms";
        });

        Check("settings: the file is written exactly as before and older files load (generated JSON code)", () =>
        {
            // the way it was done up to 1.2.0: worked out from the classes at run time
            var old = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            };
            var samples = new[]
            {
                new AppSettings(),
                new AppSettings
                {
                    Language = "pl", RegionHotkey = new HotkeyGesture(System.Windows.Input.ModifierKeys.None, System.Windows.Input.Key.End),
                    RegionHotkey2 = new HotkeyGesture(System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt, HotMouse.XButton2),
                    ClearClipboardHotkey = new HotkeyGesture(), SaveFolder = @"D:\Zrzuty ekranu\żółć", ImageFormat = ImageFormatKind.Jpg,
                    DefaultExportSize = ExportSize.UHD4K, HdEnabled = true, HdScale = 3, LastUpdateCheckUtc = new DateTime(2026, 11, 20, 18, 5, 9, DateTimeKind.Utc),
                    SkippedUpdate = "1.9.0", PlayerVolume = 62.5, PlayerMuted = true, BannerPath = @"C:\Banery\baner.mov",
                    RecentMedia = { @"C:\Filmy\a b.mp4", @"C:\Muzyka\ąę.mp3" }, RecentEmoji = { "🔥", "❤️", "👍🏽" },
                },
                // keys that have two names (Enter = Return, PageUp = Prior, PrintScreen = Snapshot)
                new AppSettings { RegionHotkey = new HotkeyGesture(System.Windows.Input.ModifierKeys.Shift, System.Windows.Input.Key.Enter),
                    RegionHotkey2 = new HotkeyGesture(System.Windows.Input.ModifierKeys.None, System.Windows.Input.Key.Snapshot),
                    ClearClipboardHotkey = new HotkeyGesture(System.Windows.Input.ModifierKeys.Windows | System.Windows.Input.ModifierKeys.Shift, System.Windows.Input.Key.PageUp) },
            };
            foreach (var s in samples)
            {
                string before = System.Text.Json.JsonSerializer.Serialize(s, old);
                string now = System.Text.Json.JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings);
                if (before != now)
                {
                    var a = before.Split('\n'); var b = now.Split('\n');
                    int at = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i] != b[i], -1);
                    throw new Exception($"the file differs at line {at + 1}: was '{(at >= 0 ? a[at].Trim() : "")}', is '{(at >= 0 ? b[at].Trim() : "")}' ({a.Length} / {b.Length} lines)");
                }
                // a file written the old way reads back the same through the new code, and the other way round
                var back = System.Text.Json.JsonSerializer.Deserialize(before, SettingsJson.Default.AppSettings)!;
                var cross = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(now, old)!;
                if (System.Text.Json.JsonSerializer.Serialize(back, old) != before || System.Text.Json.JsonSerializer.Serialize(cross, old) != before)
                    throw new Exception("a settings file does not read back the same");
            }
            // sloppy hand-edited files: numbers for enums, missing and unknown entries, null lists
            var odd = System.Text.Json.JsonSerializer.Deserialize(
                "{\"ImageFormat\":1,\"DefaultExportSize\":\"QHD\",\"RegionHotkey\":{\"Modifiers\":6,\"Key\":\"F13\"},\"RecentMedia\":null,\"Nonsense\":{\"a\":[1,2]},\"HdScale\":2}",
                SettingsJson.Default.AppSettings)!;
            if (odd.ImageFormat != ImageFormatKind.Jpg || odd.DefaultExportSize != ExportSize.QHD || odd.HdScale != 2 || odd.RecentMedia != null
                || odd.RegionHotkey.Modifiers != (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) || odd.RegionHotkey.Key != System.Windows.Input.Key.F13)
                throw new Exception("a hand-edited file reads differently");
            return $"{samples.Length} settings files identical to the old format ({System.Text.Json.JsonSerializer.Serialize(samples[1], SettingsJson.Default.AppSettings).Length} characters), old and odd files load";
        });

        Check("live shortcuts: injected Mouse 5 and End fire (and are swallowed)", () =>
        {
            const int MouseMsg = Native.WM_APP + 77;
            int mouseFired = 0, keyFired = 0;
            var sink = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("LinearFrame.TestSink")
            {
                Width = 0, Height = 0, WindowStyle = unchecked((int)Native.WS_POPUP), PositionX = -32000, PositionY = -32000,
            });
            sink.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (m == MouseMsg) { mouseFired++; handled = true; }
                if (m == Native.WM_HOTKEY && w.ToInt32() == 0x7777) { keyFired++; handled = true; }
                return IntPtr.Zero;
            });
            try
            {
                using (var hook = new MouseShortcutHook(sink.Handle, MouseMsg))
                {
                    hook.SetBindings(new[] { (HotMouse.XButton2, 0u) });
                    Inject.MouseX(2);
                    Pump(TimeSpan.FromSeconds(1), () => mouseFired > 0);
                }
                string key;
                if (Native.RegisterHotKey(sink.Handle, 0x7777, 0x4000, 0x23)) // End alone (MOD_NOREPEAT)
                {
                    Inject.Key(0x23, extended: true);
                    Pump(TimeSpan.FromSeconds(1), () => keyFired > 0);
                    Native.UnregisterHotKey(sink.Handle, 0x7777);
                    key = $"End fired {keyFired}×";
                    if (keyFired != 1) throw new Exception(key);
                }
                else key = "End is taken by another app here (skipped)";
                if (mouseFired != 1) throw new Exception($"Mouse 5 fired {mouseFired}×");
                return $"Mouse 5 fired once, {key}";
            }
            finally { sink.Dispose(); }
        });

        Check("displays (read-only)", () =>
        {
            var sw = Stopwatch.StartNew();
            var list = Displays.Query();
            long ms = sw.ElapsedMilliseconds;
            if (!list.Any(d => d.Attached)) throw new Exception("no attached display");
            var v = VirtualScreen.Find();
            return $"{ms} ms: " + string.Join("; ", list.Where(d => d.Attached || d.IsVirtual).Select(d =>
                $"{d.Device.Replace(@"\\.\", "")} {d.Width}x{d.Height}@{d.Frequency} at {d.X},{d.Y} {d.ScalePercent}%{(d.Primary ? " primary" : "")}{(d.IsVirtual ? (d.Attached ? " VIRTUAL" : " VIRTUAL (off)") : "")}"))
                + $"; HD driver present: {HdSetup.DevicePresent()}, virtual screen: {v != null}";
        });

        Check("HD: which window owns a selection", () =>
        {
            Int32Rect R(int x, int y, int w, int h) => new(x, y, w, h);
            var front = new WindowEntry(new IntPtr(1), R(100, 100, 400, 300));
            var back = new WindowEntry(new IntPtr(2), R(0, 0, 1920, 1040));
            var list = new List<WindowEntry> { front, back };
            var inside = HdCapture.PickWindow(list, R(150, 150, 100, 50));
            var behind = HdCapture.PickWindow(list, R(700, 500, 200, 100));
            var crossing = HdCapture.PickWindow(list, R(450, 350, 200, 100));
            var desktop = HdCapture.PickWindow(new List<WindowEntry> { front }, R(900, 700, 50, 50));
            if (inside.Window != front || behind.Window != back) throw new Exception("wrong window");
            if (crossing.Failure != HdFailure.SeveralWindows || desktop.Failure != HdFailure.Desktop) throw new Exception($"{crossing.Failure} / {desktop.Failure}");
            return "front, back, crossing an edge, desktop ok";
        });

        Check("HD: a real 2×/3×/4× render is told apart from a stretched picture and from other content", () =>
        {
            var notes = new List<string>();
            var normal = MakeFakeUi(520, 300, 1, crisp: true); // what is on screen: hinted, pixel-snapped text
            var sel = new Int32Rect(24, 24, 440, 130);
            var frozen = normal.Crop(sel);
            var fg = ImageMatch.ToGray(frozen);
            var fb = ImageMatch.Blur(fg);
            foreach (int k in new[] { 2, 3, 4 })
            {
                int margin = 16 * k, w = sel.Width * k, h = sel.Height * k;
                Int32Rect Area(int shiftX, int shiftY) => new((sel.X - shiftX) * k - margin, (sel.Y - shiftY) * k - margin, w + 2 * margin, h + 2 * margin);

                // a) the app really drew itself k times bigger - and its content sits 3 px right / 2 px up of where expected
                var real = MakeFakeUi(520, 300, k);
                var v = HdCapture.Judge(real.Crop(Area(3, -2)), margin, margin, w, h, k, fg, fb);
                // (hinted on-screen text sits up to a pixel away from where smooth text is - one pixel of play is expected)
                if (!v.Match || Math.Abs(v.Dx - 3) > 1 || Math.Abs(v.Dy + 2) > 1) throw new Exception($"{k}×: real render not found (match {v.Match}, difference {v.Difference:0.0}, alike {v.Similarity:0.00}, shift {v.Dx},{v.Dy})");
                if (!(v.Gain >= HdCapture.GainGood(k))) throw new Exception($"{k}×: real render has gain {v.Gain:0.00}, needs {HdCapture.GainGood(k):0.00}");
                if (v.Crop!.Width != w || v.Crop.Height != h) throw new Exception("crop size");
                if (k == 4) Save(v.Crop.ToBitmap(), Path.Combine(dir, "hd-real-4x.png"));

                // b) Windows only stretched the old picture (an app that ignores the new scaling) - same content, no detail
                var stretched = Stretch(normal, k);
                var v2 = HdCapture.Judge(stretched.Crop(Area(0, 0)), margin, margin, w, h, k, fg, fb);
                if (!v2.Match) throw new Exception($"{k}×: stretched picture should still be recognised (difference {v2.Difference:0.0}, alike {v2.Similarity:0.00})");
                if (!(v2.Gain < HdCapture.GainMin(k))) throw new Exception($"{k}×: a stretched picture passed as real detail (gain {v2.Gain:0.00})");
                if (k == 4) Save(v2.Crop!.ToBitmap(), Path.Combine(dir, "hd-stretched-4x.png"));

                // c) the window shows something else now (same dark theme, other content)
                var other = MakeFakeUi(520, 300, k, variant: true);
                var v3 = HdCapture.Judge(other.Crop(Area(0, 0)), margin, margin, w, h, k, fg, fb);
                if (v3.Match) throw new Exception($"{k}×: other content passed as the selection (difference {v3.Difference:0.0}, alike {v3.Similarity:0.00})");

                notes.Add($"{k}×: real gain {v.Gain:0.00} (alike {v.Similarity:0.00}, difference {v.Difference:0.0}, shift {v.Dx},{v.Dy}), stretched {v2.Gain:0.00}, other content alike {v3.Similarity:0.00}");
            }
            // flat content: nothing to judge, nothing to object to
            var flat = new PixelBuffer(100, 60); flat.ForceOpaque();
            var flatHd = new PixelBuffer(100 * 4 + 128, 60 * 4 + 128); flatHd.ForceOpaque();
            var vf = HdCapture.Judge(flatHd, 64, 64, 400, 240, 4, ImageMatch.ToGray(flat), ImageMatch.Blur(ImageMatch.ToGray(flat)));
            if (!vf.Match || !double.IsNaN(vf.Gain) || vf.Dx != 0 || vf.Dy != 0) throw new Exception("flat content");
            return string.Join("; ", notes);
        });

        Check("HD: driver set-up plumbing (read-only)", () =>
        {
            var (cls, name) = HdSetup.InfClass(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "display.inf"));
            if (name != "Display" || cls != new Guid("4d36e968-e325-11ce-bfc1-08002be10318")) throw new Exception($"display.inf → {name} {cls}");
            bool present = HdSetup.DevicePresent();
            var own = System.Xml.Linq.XDocument.Parse(HdSetup.SettingsXml(null));
            var kept = System.Xml.Linq.XDocument.Parse(HdSetup.SettingsXml(
                "<?xml version='1.0' encoding='utf-8'?><vdd_settings><monitors><count>3</count></monitors><global><g_refresh_rate>60</g_refresh_rate><g_refresh_rate>144</g_refresh_rate></global>" +
                "<resolutions><resolution><width>800</width><height>600</height><refresh_rate>30</refresh_rate></resolution></resolutions><options><HardwareCursor>true</HardwareCursor></options></vdd_settings>"));
            foreach (var doc in new[] { own, kept })
            {
                var res = doc.Root!.Element("resolutions")!.Elements("resolution").ToList();
                if (res.Count != 4 || doc.Root.Element("monitors")!.Element("count")!.Value != "1" || doc.Root.Element("global")!.Elements("g_refresh_rate").Count() != 1)
                    throw new Exception("settings XML: " + doc);
                if (!res.Any(r => r.Element("width")!.Value == "7680" && r.Element("height")!.Value == "4320")) throw new Exception("8K mode missing");
            }
            if (kept.Root!.Element("options")?.Element("HardwareCursor")?.Value != "true") throw new Exception("the driver's own settings were not kept");
            return $"display.inf → {name}; driver present: {present}; settings XML ok";
        });

        Check("HD: live round-trip of a window via another monitor (mechanics only, same scaling)", () =>
        {
            var mons = MonitorInfo.All();
            var src = mons.FirstOrDefault(m => m.Primary) ?? mons[0];
            var dst = mons.FirstOrDefault(m => m != src);
            if (dst == null) return "skipped (needs a second monitor)";

            var face = new FontFamily("Segoe UI");
            var panel = new StackPanel { Margin = new Thickness(18), Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)) };
            panel.Children.Add(new TextBlock { Text = "LinearFrame HD", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = face });
            panel.Children.Add(new TextBlock { Text = "Okno testowe — przenosiny na drugi ekran i z powrotem.", FontSize = 13, Foreground = Brushes.Gainsboro, FontFamily = face, Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new Border { Height = 44, Width = 200, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2)), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = "Zapisz zmiany", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = face } });
            double k = src.Scale;
            var win = new Window
            {
                Title = "LinearFrame HD test", Width = 440, Height = 300, Topmost = true, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = (src.WorkArea.X + src.WorkArea.Width / 2 - 220 * k) / k, Top = (src.WorkArea.Y + src.WorkArea.Height / 2 - 150 * k) / k,
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)), Content = panel,
            };
            win.Show();
            try
            {
                Pump(TimeSpan.FromMilliseconds(700), () => false); // let it appear (open animation) and paint
                var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                Native.GetWindowRect(hwnd, out var r0);
                var entries = WindowFinder.Snapshot();
                // the inner part of the window, in the monitor's own pixels
                var sel = new Int32Rect(r0.Left + (int)(40 * k) - src.Bounds.X, r0.Top + (int)(60 * k) - src.Bounds.Y, (int)(320 * k), (int)(170 * k));
                var frozen = ScreenCapturer.Capture(new Int32Rect(src.Bounds.X + sel.X, src.Bounds.Y + sel.Y, sel.Width, sel.Height), quiet: true);
                var target = new VirtualScreen.Armed("test", dst.Bounds, dst.WorkArea, (int)Math.Round(dst.Scale * 100), (uint)Math.Round(dst.Scale * 96));

                HdResult? res = null;
                var task = Task.Run(() => res = HdCapture.RenderOn(entries, src, sel, frozen, 1, (_, _, _) => target, sameScaleOk: true, Stopwatch.StartNew()));
                Pump(TimeSpan.FromSeconds(10), () => task.IsCompleted);
                if (!task.IsCompleted) throw new Exception("the round-trip did not finish");
                Pump(TimeSpan.FromMilliseconds(150), () => false);
                Native.GetWindowRect(hwnd, out var r1);
                if (res?.Image == null) throw new Exception($"no picture: {res?.Failure} ({res?.Detail})");
                if (r1.Left != r0.Left || r1.Top != r0.Top || r1.Width != r0.Width || r1.Height != r0.Height) throw new Exception($"window not put back: {r1} instead of {r0}");
                Save(res.Image.ToBitmap(), Path.Combine(dir, "hd-roundtrip.png"));
                return $"{res.Image.Width}x{res.Image.Height} at {res.Scale:0.##}× via {dst.Bounds.Width}x{dst.Bounds.Height}; window back at {r1}; {res.Detail}";
            }
            finally { win.Close(); }
        });

        // dev: the real HD capture (real virtual screen) on test windows in typical places - LF_HD_PROBE=1 only, it puts
        // windows and the virtual screen on the desktop for a while
        if (Environment.GetEnvironmentVariable("LF_HD_PROBE") == "1")
            Check("HD live probe: test windows in several places, real virtual screen", () =>
            {
                // the real monitors only (the virtual screen may still be on the desktop from a capture a moment ago)
                VirtualScreen.Park(force: true);
                Pump(TimeSpan.FromMilliseconds(1500), () => false);
                bool Real(MonitorInfo m)
                {
                    var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
                    return Native.GetMonitorInfo(m.Handle, ref info) && !VirtualScreen.IsVirtualDevice(info.szDevice);
                }
                var mons = MonitorInfo.All().Where(Real).OrderBy(m => !m.Primary).ThenBy(m => m.Bounds.X).ToList();
                var face = new FontFamily("Segoe UI");
                FrameworkElement Content()
                {
                    var panel = new StackPanel { Margin = new Thickness(18) };
                    for (int i = 0; i < 14; i++)
                        panel.Children.Add(new TextBlock
                        {
                            Text = $"{i + 1}. Linijka testowa okna HD - ZAŻÓŁĆ gęślą jaźń 0123456789 ({i * 37 % 100}%)",
                            FontSize = 13 + i % 3 * 2, Foreground = Brushes.White, FontFamily = face, Margin = new Thickness(0, 0, 0, 6),
                        });
                    return new Border { Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)), Child = panel };
                }
                var report = new List<string>();
                // does putting the virtual screen on the desktop move windows by itself? (Windows 11 remembers where
                // windows were per set of monitors)
                if (mons.Count > 1)
                {
                    VirtualScreen.Park(force: true);
                    Pump(TimeSpan.FromMilliseconds(1500), () => false);
                    var probe = new Window { Title = "LinearFrame HD probe", Width = 900, Height = 700, Topmost = true, ShowActivated = false, ShowInTaskbar = false, Content = Content() };
                    probe.Show();
                    var ph = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
                    string Rect() { Native.GetWindowRect(ph, out var r); return $"{r.Left},{r.Top} {r.Width}x{r.Height} dpi {Native.GetDpiForWindow(ph)}"; }
                    string created = Rect();
                    Native.SetWindowPos(ph, IntPtr.Zero, 2930, 206, 900, 700, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                    Pump(TimeSpan.FromMilliseconds(600), () => false);
                    string placed = Rect();
                    var armed = VirtualScreen.Arm(7680, 4320, 400);
                    Pump(TimeSpan.FromMilliseconds(1500), () => false);
                    string attached = Rect();
                    VirtualScreen.Park(force: true);
                    Pump(TimeSpan.FromMilliseconds(1500), () => false);
                    string detached = Rect();
                    probe.Close();
                    report.Add($"attach test: created {created}; placed {placed}; virtual screen {(armed == null ? "-" : $"{armed.Bounds.X},{armed.Bounds.Y}")} on: {attached}; off: {detached}");
                }
                foreach (var (name, mi, fx, fy, w, h, max, s) in new (string, int, double, double, double, double, bool, (double X, double Y, double W, double H))[]
                         {
                             ("middle", 0, 0.3, 0.25, 900, 600, false, (0.1, 0.15, 0.5, 0.4)),
                             ("middle, lower right part", 0, 0.3, 0.25, 900, 600, false, (0.5, 0.6, 0.45, 0.35)),
                             ("bottom right corner", 0, -1, -1, 900, 600, false, (0.5, 0.55, 0.45, 0.4)),
                             ("maximized", 0, 0.2, 0.2, 900, 600, true, (0.1, 0.1, 0.4, 0.3)),
                             ("maximized, bottom", 0, 0.2, 0.2, 900, 600, true, (0.5, 0.75, 0.45, 0.2)),
                             ("second screen, right", 1, -1, 0.2, 900, 700, false, (0.5, 0.3, 0.45, 0.4)),
                             ("second screen, maximized", 1, 0.2, 0.2, 900, 700, true, (0.3, 0.4, 0.45, 0.3)),
                             ("tall, bottom", 0, 0.1, 0.02, 600, 1000, false, (0.1, 0.8, 0.8, 0.15)),
                             ("frameless maximized", 0, 0.2, 0.2, 900, 700, true, (0.3, 0.4, 0.45, 0.3)),
                             ("frameless maximized, second screen", 1, 0.2, 0.2, 900, 700, true, (0.3, 0.6, 0.45, 0.3)),
                         })
                {
                    if (mi >= mons.Count) { report.Add($"{name}: skipped"); continue; }
                    VirtualScreen.Park(force: true); // each window starts like a real capture does: without the virtual screen around
                    Pump(TimeSpan.FromMilliseconds(500), () => false);
                    var mon = mons[mi];
                    double k = mon.Scale;
                    var wa = mon.WorkArea;
                    double left = fx < 0 ? wa.X + wa.Width - (w + 10) * k : wa.X + wa.Width * fx, top = fy < 0 ? wa.Y + wa.Height - (h + 10) * k : wa.Y + wa.Height * fy;
                    // placed in pixels right after it shows
                    var win = new Window
                    {
                        Title = "LinearFrame HD probe", Width = w, Height = h, Topmost = true, ShowActivated = false, ShowInTaskbar = false, Content = Content(),
                    };
                    if (name.StartsWith("frameless"))
                    {
                        // like LinearFrame's own windows (and many Electron apps): no Windows frame, maximized exactly to the work area
                        System.Windows.Shell.WindowChrome.SetWindowChrome(win, new System.Windows.Shell.WindowChrome
                        {
                            CaptionHeight = 0, ResizeBorderThickness = new Thickness(8), GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
                        });
                        WindowHelper.Attach(win);
                    }
                    win.Show();
                    Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(win).Handle, IntPtr.Zero, (int)left, (int)top, (int)(w * k), (int)(h * k),
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                    if (max) win.WindowState = WindowState.Maximized;
                    try
                    {
                        Pump(TimeSpan.FromMilliseconds(700), () => false);
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                        Native.GetWindowRect(hwnd, out var r0);
                        var entries = WindowFinder.Snapshot();
                        var sel = new Int32Rect((int)(r0.Left + s.X * r0.Width) - mon.Bounds.X, (int)(r0.Top + s.Y * r0.Height) - mon.Bounds.Y, (int)(s.W * r0.Width), (int)(s.H * r0.Height));
                        var frozen = ScreenCapturer.Capture(new Int32Rect(mon.Bounds.X + sel.X, mon.Bounds.Y + sel.Y, sel.Width, sel.Height), quiet: true);
                        HdResult? res = null;
                        string slug = new string(name.Where(char.IsLetterOrDigit).ToArray());
                        HdCapture.DebugLastLook = px => Save(px.ToBitmap(), Path.Combine(dir, $"hd-look-{slug}.png"));
                        Save(frozen.ToBitmap(), Path.Combine(dir, $"hd-frozen-{slug}.png"));
                        var fg0 = Native.GetForegroundWindow();
                        var task = Task.Run(() => res = HdCapture.Render(entries, mon, sel, frozen, 4));
                        Pump(TimeSpan.FromSeconds(20), () => task.IsCompleted);
                        HdCapture.DebugLastLook = null;
                        bool fgChanged = Native.GetForegroundWindow() != fg0;
                        Pump(TimeSpan.FromMilliseconds(150), () => false);
                        Native.GetWindowRect(hwnd, out var r1);
                        bool back = r1.Left == r0.Left && r1.Top == r0.Top && r1.Width == r0.Width && r1.Height == r0.Height;
                        report.Add($"{name}: window {r0.Left},{r0.Top} {r0.Width}x{r0.Height}, selection {sel.X},{sel.Y} {sel.Width}x{sel.Height} -> " +
                                   (res?.Image != null ? $"{res.Image.Width}x{res.Image.Height} at {res.Scale:0.##}×" : $"FAILED {res?.Failure}") +
                                   $" [{res?.Detail}]{(back ? "" : $" NOT BACK: {r1}")}{(fgChanged ? " FOREGROUND CHANGED" : "")}");
                    }
                    finally { win.Close(); }
                }

                // a real browser window (Chromium - like Brave or the Claude app), in a throw-away profile
                string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
                if (File.Exists(edge))
                {
                    string page = Path.Combine(dir, "hd-probe.html");
                    File.WriteAllText(page, "<html><head><title>HD probe page</title></head><body style='background:#2b2d31;color:#fff;font:15px Segoe UI;margin:24px'>" +
                        string.Concat(Enumerable.Range(1, 40).Select(i => $"<p style='margin:0 0 8px'>{i}. Linijka testowa w przeglądarce - ZAŻÓŁĆ gęślą jaźń 0123456789</p>")) + "</body></html>", Encoding.UTF8);
                    var psi = new ProcessStartInfo(edge) { UseShellExecute = false };
                    foreach (var a in new[] { "--user-data-dir=" + Path.Combine(dir, "edge-hd-profile"), "--no-first-run", "--no-default-browser-check", "--disable-sync",
                                              "--window-position=500,250", "--window-size=900,600", "--app=" + new Uri(page).AbsoluteUri })
                        psi.ArgumentList.Add(a);
                    using var browser = Process.Start(psi);
                    IntPtr bw = IntPtr.Zero;
                    IntPtr Find()
                    {
                        IntPtr found = IntPtr.Zero;
                        Native.EnumWindows((h, _) =>
                        {
                            var sb = new StringBuilder(256);
                            Native.GetWindowText(h, sb, sb.Capacity);
                            if (Native.IsWindowVisible(h) && sb.ToString().Contains("HD probe page")) { found = h; return false; }
                            return true;
                        }, IntPtr.Zero);
                        return found;
                    }
                    try
                    {
                        Pump(TimeSpan.FromSeconds(20), () => (bw = Find()) != IntPtr.Zero);
                        if (bw == IntPtr.Zero) report.Add("browser: no window came up");
                        else
                        {
                            Pump(TimeSpan.FromMilliseconds(2000), () => false);
                            var mon = mons[0];
                            foreach (var (name, max, s) in new (string, bool, (double X, double Y, double W, double H))[]
                                     {
                                         ("browser", false, (0.1, 0.2, 0.5, 0.4)), ("browser, lower right part", false, (0.5, 0.6, 0.45, 0.35)),
                                         ("browser maximized", true, (0.2, 0.3, 0.5, 0.4)), ("browser maximized, bottom", true, (0.5, 0.8, 0.45, 0.15)),
                                         ("browser maximized on the second screen", true, (0.3, 0.4, 0.45, 0.3)),
                                     })
                            {
                                if (name.Contains("second") && mons.Count > 1)
                                {
                                    // over to the right-hand screen and maximized there
                                    Native.ShowWindow(bw, Native.SW_RESTORE);
                                    Pump(TimeSpan.FromMilliseconds(600), () => false);
                                    Native.SetWindowPos(bw, IntPtr.Zero, mons[1].WorkArea.X + 300, mons[1].WorkArea.Y + 200, 900, 600, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                                    Pump(TimeSpan.FromMilliseconds(600), () => false);
                                    mon = mons[1];
                                }
                                if (max) { Native.ShowWindow(bw, 3); Pump(TimeSpan.FromMilliseconds(1200), () => false); }
                                Native.GetWindowRect(bw, out var r0);
                                var entries = WindowFinder.Snapshot();
                                var sel = new Int32Rect((int)(r0.Left + s.X * r0.Width) - mon.Bounds.X, (int)(r0.Top + s.Y * r0.Height) - mon.Bounds.Y, (int)(s.W * r0.Width), (int)(s.H * r0.Height));
                                sel = ImageOps.Intersect(sel, new Int32Rect(0, 0, mon.Bounds.Width, mon.Bounds.Height));
                                var frozen = ScreenCapturer.Capture(new Int32Rect(mon.Bounds.X + sel.X, mon.Bounds.Y + sel.Y, sel.Width, sel.Height), quiet: true);
                                HdResult? res = null;
                                var task = Task.Run(() => res = HdCapture.Render(entries, mon, sel, frozen, 4));
                                Pump(TimeSpan.FromSeconds(20), () => task.IsCompleted);
                                Pump(TimeSpan.FromMilliseconds(400), () => false);
                                Native.GetWindowRect(bw, out var r1);
                                bool back = r1.Left == r0.Left && r1.Top == r0.Top && r1.Width == r0.Width && r1.Height == r0.Height;
                                report.Add($"{name}: window {r0.Left},{r0.Top} {r0.Width}x{r0.Height}, selection {sel.X},{sel.Y} {sel.Width}x{sel.Height} -> " +
                                           (res?.Image != null ? $"{res.Image.Width}x{res.Image.Height} at {res.Scale:0.##}×" : $"FAILED {res?.Failure}") +
                                           $" [{res?.Detail}]{(back ? "" : $" NOT BACK: {r1}")}");
                            }
                        }
                    }
                    finally
                    {
                        if (bw != IntPtr.Zero) Native.PostMessage(bw, 0x0010, IntPtr.Zero, IntPtr.Zero);
                        try { if (browser != null && !browser.WaitForExit(5000)) browser.Kill(true); } catch { /* gone */ }
                    }
                }
                return Environment.NewLine + string.Join(Environment.NewLine, report);
            });

        Check("update: the smallest package that fits the installed copy is taken", () =>
        {
            string app = Path.Combine(dir, "upd-app");
            string net = Path.Combine(app, "runtime", "shared", "Microsoft.NETCore.App", "10.0.9");
            Directory.CreateDirectory(Path.Combine(app, "engine"));
            Directory.CreateDirectory(net);
            File.WriteAllBytes(Path.Combine(app, "engine", "a.bin"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(app, "engine", "b.bin"), new byte[] { 9, 9 });
            File.WriteAllBytes(Path.Combine(net, "core.dll"), new byte[] { 5, 5, 5 });
            static string H(params byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
            string? Ask(string list, string[] left) => Task.Run(() => Updater.PackageHashAsync(list, app, left, default)).GetAwaiter().GetResult();
            string zip = H(7);
            string engine = $"{H(1, 2, 3, 4)}  engine/a.bin\r\n{H(9, 9)}  engine/b.bin\r\n";
            string update = $"{zip}  LinearFrame-9.9.9-update.zip\r\n{engine}";                 // the app and .NET
            string appOnly = $"{zip}  LinearFrame-9.9.9-app.zip\r\n{engine}{H(5, 5, 5)}  runtime/shared/Microsoft.NETCore.App/10.0.9/core.dll\r\n";
            if (Ask(update, Updater.EngineOnly) != zip) throw new Exception("matching engine not accepted");
            if (Ask(appOnly, Updater.EngineAndRuntime) != zip) throw new Exception("matching engine and .NET not accepted");
            if (Ask(appOnly, Updater.EngineOnly) != null) throw new Exception("a list naming .NET files must not pass as the package that brings .NET along");
            if (Ask(update, Updater.EngineAndRuntime) != null) throw new Exception("a list that says nothing about .NET must not be enough for the app-only package");
            if (Ask(update + $"{H(5)}  engine/missing.dll\n", Updater.EngineOnly) != null) throw new Exception("a missing engine file must mean: full package");
            if (Ask($"{zip}  x.zip\n{H(1)}  ../outside.bin\n", Updater.EngineOnly) != null) throw new Exception("paths outside the engine folder must be refused");
            if (Ask($"{zip}  x.zip\n{engine}{H(1)}  runtime/../engine/a.bin\n", Updater.EngineAndRuntime) != null) throw new Exception("paths climbing out of a folder must be refused");
            if (Ask("not a checksum list", Updater.EngineOnly) != null) throw new Exception("garbage accepted");
            if (Ask($"{zip}  only-the-zip.zip\n", Updater.EngineOnly) != null) throw new Exception("a list without any expected file accepted");
            // an update brought another .NET: the app-only package no longer fits, the one with .NET still does
            File.WriteAllBytes(Path.Combine(net, "core.dll"), new byte[] { 5, 5, 6 });
            if (Ask(appOnly, Updater.EngineAndRuntime) != null) throw new Exception("a changed .NET must mean: the package with .NET");
            if (Ask(update, Updater.EngineOnly) != zip) throw new Exception("a changed .NET must not rule out the package that brings .NET along");
            File.WriteAllBytes(Path.Combine(app, "engine", "b.bin"), new byte[] { 9, 8 });
            if (Ask(update, Updater.EngineOnly) != null) throw new Exception("a changed engine must mean: full package");
            return "app only / app and .NET / full: matching, changed .NET, changed engine, missing file, outside path, garbage: ok";
        });

        Check("update: .NET versions an update has replaced are removed, the one in use stays", () =>
        {
            string rt = Path.Combine(dir, "upd-rt", "runtime");
            string[] parents = { Path.Combine(rt, "host", "fxr"), Path.Combine(rt, "shared", "Microsoft.NETCore.App"), Path.Combine(rt, "shared", "Microsoft.WindowsDesktop.App") };
            foreach (var v in new[] { "10.0.7", "10.0.9", "10.0.12" })
                foreach (var parent in parents)
                {
                    Directory.CreateDirectory(Path.Combine(parent, v));
                    File.WriteAllText(Path.Combine(parent, v, "x.dll"), v);
                }
            File.WriteAllText(Path.Combine(rt, "dotnet-LICENSE.txt"), "kept");
            bool All(string v) => parents.All(parent => File.Exists(Path.Combine(parent, v, "x.dll")));
            bool None(string v) => parents.All(parent => !Directory.Exists(Path.Combine(parent, v)));
            // a build that does not run on this folder (a dev build on the installed .NET, the old one-file exe) removes nothing
            if (Updater.RemoveOldRuntimes(rt, @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.9") != 0
                || Updater.RemoveOldRuntimes(rt, null) != 0 || Updater.RemoveOldRuntimes(rt, "") != 0 || !All("10.0.7"))
                throw new Exception("a copy that does not run on this runtime folder removed something from it");
            int n = Updater.RemoveOldRuntimes(rt, Path.Combine(rt, "shared", "Microsoft.NETCore.App", "10.0.9"));
            if (n != 3 || !None("10.0.7")) throw new Exception($"the replaced version 10.0.7 was not removed everywhere ({n} folders)");
            if (!All("10.0.9")) throw new Exception("the version in use was touched");
            if (!All("10.0.12")) throw new Exception("a newer version (10.0.12 is more than 10.0.9) was removed");
            if (!File.Exists(Path.Combine(rt, "dotnet-LICENSE.txt"))) throw new Exception("a file beside the versions was removed");
            if (Updater.RemoveOldRuntimes(rt, Path.Combine(rt, "shared", "Microsoft.NETCore.App", "10.0.9")) != 0) throw new Exception("second pass removed something");
            return "older removed (3 folders), in use and newer kept, foreign runtime folders untouched";
        });

        Check("update: what the old one-file exe unpacked is removed only where no running copy uses it", () =>
        {
            string unpack = Path.Combine(dir, "upd-unpacked");
            if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
            string busy = Path.Combine(unpack, "buildA"), idle = Path.Combine(unpack, "buildB");
            Directory.CreateDirectory(busy);
            Directory.CreateDirectory(idle);
            // a real library, so Windows can load it the way a running copy has its unpacked libraries loaded
            string lib = Path.Combine(Environment.SystemDirectory, "version.dll");
            foreach (var d in new[] { busy, idle })
            {
                File.Copy(lib, Path.Combine(d, "loaded.dll"));
                File.Copy(lib, Path.Combine(d, "not-loaded-yet.dll"));
            }
            IntPtr h = LoadLibraryExW(Path.Combine(busy, "loaded.dll"), IntPtr.Zero, 0x20 /* as a program image, nothing is run */);
            if (h == IntPtr.Zero) throw new Exception("the test library could not be loaded");
            try
            {
                int n = Updater.RemoveUnpackedLibraries(unpack);
                if (n != 1 || Directory.Exists(idle)) throw new Exception($"the folder nobody uses was not removed ({n})");
                if (!File.Exists(Path.Combine(busy, "loaded.dll")) || !File.Exists(Path.Combine(busy, "not-loaded-yet.dll")))
                    throw new Exception("a file was taken away from a build whose library is loaded - that copy would fail later");
            }
            finally { FreeLibrary(h); }
            if (Updater.RemoveUnpackedLibraries(unpack) != 1 || Directory.Exists(unpack)) throw new Exception("after that copy has ended its folder must go too (and the empty parent with it)");
            if (Updater.RemoveUnpackedLibraries(unpack) != 0) throw new Exception("nothing left, yet something was removed");
            return "unused build removed, a build in use left whole (also its not-yet-loaded files), removed once it is free";
        });

        Check("rehearsal: a clip and a picture are opened out of sight - nothing shows, nothing takes the focus, nothing is kept", () =>
        {
            var (clip, picture) = App.RehearsalFiles();
            if (clip == null || !File.Exists(clip)) throw new Exception("the built-in clip is missing");
            if (picture == null || ImageIO.Probe(picture) is not { ShownWidth: 960, ShownHeight: 600 }) throw new Exception("the rehearsal picture was not made");
            var s = App.Instance!.Settings;
            bool muted = s.PlayerMuted;
            double volume = s.PlayerVolume;
            int recent = s.RecentMedia.Count;
            var seen = new HashSet<string>();
            int inFront = 0, looks = 0;
            // while a rehearsal runs: every window of ours that is "shown" must be out of sight (cloaked) and never in front
            void Look()
            {
                looks++;
                var front = Native.GetForegroundWindow();
                foreach (Window w in Application.Current.Windows)
                {
                    if (!w.IsVisible) continue;
                    var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    if (h == IntPtr.Zero) continue;
                    Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int));
                    if (cloaked == 0) seen.Add(w.Title);
                    if (h == front) inFront++;
                }
            }

            string? player = null, viewer = null;
            var pw = PlayerWindow.Rehearse(clip, how => player = how);
            Pump(TimeSpan.FromSeconds(10), () => { Look(); return player != null; });
            if (player == null) { pw.Close(); throw new Exception("the player's rehearsal never ended"); }
            var vw = ImageViewerWindow.Rehearse(picture, how => viewer = how);
            Pump(TimeSpan.FromSeconds(8), () => { Look(); return viewer != null; });
            if (viewer == null) { vw.Close(); throw new Exception("the viewer's rehearsal never ended"); }
            Pump(TimeSpan.FromMilliseconds(300), () => { Look(); return false; });

            if (seen.Count > 0) throw new Exception("could be seen: " + string.Join(", ", seen));
            if (inFront > 0) throw new Exception($"a rehearsal window was the window in front ({inFront} times)");
            if (pw.IsVisible || vw.IsVisible) throw new Exception("a rehearsal window stayed open");
            if (s.PlayerMuted != muted || s.PlayerVolume != volume) throw new Exception("the rehearsal changed the player's sound settings");
            if (s.RecentMedia.Count != recent) throw new Exception("the rehearsal clip was added to the recent files");
            if (!player.Contains("complete")) throw new Exception("the clip did not get to its first picture: " + player);
            if (viewer.Contains("could not")) throw new Exception("the picture was not read: " + viewer);
            return $"clip in {player}; picture in {viewer}; looked {looks} times: nothing visible, nothing in front";
        });

        Check("overlay: cost of drawing one frame without the GPU (1920×1080)", () =>
        {
            // The software rasteriser is what an overlay window uses when it is not GPU-accelerated; drawing into a bitmap
            // goes through the same code, so this is the time one full redraw (a moving selection) would take.
            var (session, overlay) = CaptureSession.CreateForTest(App.Instance!.Settings, MakeFakeUi(1920, 1080));
            var rootEl = (FrameworkElement)overlay.Content;
            rootEl.Measure(new Size(1920, 1080));
            rootEl.Arrange(new Rect(0, 0, 1920, 1080));
            rootEl.UpdateLayout();
            var rtb = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
            overlay.SelectForTest(new Int32Rect(100, 100, 300, 200));
            rtb.Render(rootEl); // warm-up
            var sw = Stopwatch.StartNew();
            const int Frames = 30;
            for (int i = 0; i < Frames; i++)
            {
                overlay.SelectForTest(new Int32Rect(100 + i * 12, 100 + i * 6, 300 + i * 30, 200 + i * 20));
                rtb.Clear();
                rtb.Render(rootEl);
            }
            double ms = sw.Elapsed.TotalMilliseconds / Frames;
            session.Cancel();
            return $"{ms:0.0} ms per full frame (≈ {1000 / ms:0} fps)";
        });

        Check("memory: what a capture leaves behind is given back (also with a window open)", () =>
        {
            static long PrivateMb() { using var p = Process.GetCurrentProcess(); return p.PrivateMemorySize64 / 1048576; }
            long peak = 0;
            void Round()
            {
                // three "monitors" worth of frozen screens, like a capture on a 3-monitor desk
                var sessions = Enumerable.Range(0, 3).Select(_ => CaptureSession.CreateForTest(App.Instance!.Settings, MakeFakeUi(1920, 1080))).ToList();
                foreach (var (_, overlay) in sessions)
                {
                    var rootEl = (FrameworkElement)overlay.Content;
                    rootEl.Measure(new Size(1920, 1080));
                    rootEl.Arrange(new Rect(0, 0, 1920, 1080));
                    overlay.SelectForTest(new Int32Rect(300, 200, 600, 400));
                }
                peak = Math.Max(peak, PrivateMb());
                foreach (var (session, _) in sessions) session.Cancel();
            }
            // the very first capture also loads what every later one reuses (styles, fonts, WPF's own caches): that is
            // not something a capture "leaves behind", so one goes first and the counting starts after it
            Round();
            Pump(TimeSpan.FromMilliseconds(300), () => false);
            Memory.TrimNow(releasePages: false);
            Pump(TimeSpan.FromMilliseconds(200), () => false);
            long before = PrivateMb();
            for (int i = 0; i < 4; i++) Round();
            // like the real app: the message loop turns (closed windows let go of their pictures), then the clean-up runs
            Pump(TimeSpan.FromMilliseconds(300), () => false);
            Memory.TrimNow(releasePages: false);
            Pump(TimeSpan.FromMilliseconds(200), () => false);
            long after = PrivateMb();
            var gc = GC.GetGCMemoryInfo();
            string detail = $"private memory {before} MB → peak {peak} MB → {after} MB after clean-up (managed heap {GC.GetTotalMemory(false) / 1048576} MB, committed {gc.TotalCommittedBytes / 1048576} MB, windows alive {Application.Current.Windows.Count})";
            if (after - before > 40) throw new Exception("too much stayed behind: " + detail);
            return detail;
        });

        Check("Start menu shortcut (.lnk in the test folder)", () =>
        {
            var link = Path.Combine(dir, "LinearFrame-test.lnk");
            StartMenuShortcut.Create(link);
            var target = StartMenuShortcut.ReadTarget(link);
            if (!string.Equals(target, AppPaths.ExePath, StringComparison.OrdinalIgnoreCase)) throw new Exception($"target {target}");
            return $"{new FileInfo(link).Length} bytes → {Path.GetFileName(target)}";
        });

        Check("autostart: a sign-in task registers without admin rights, reads back and goes away (test name)", () =>
        {
            string name = $"LinearFrame self-test ({Environment.UserName})";
            Autostart.Delete(name);
            try
            {
                if (!Autostart.Register(name, AppPaths.ExePath)) throw new Exception("could not register");
                var here = Autostart.Query(name, AppPaths.ExePath);
                var moved = Autostart.Query(name, @"C:\Somewhere else\LinearFrame.exe");
                if (here != Autostart.TaskState.Current) throw new Exception($"read back as {here}");
                if (moved != Autostart.TaskState.Outdated) throw new Exception($"a moved app reads as {moved}");
                var xml = System.Xml.Linq.XDocument.Parse(Autostart.BuildXml(AppPaths.ExePath));
                System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                var set = xml.Root!.Element(ns + "Settings")!;
                if (set.Element(ns + "Priority")!.Value != "5" || set.Element(ns + "ExecutionTimeLimit")!.Value != "PT0S" || set.Element(ns + "DisallowStartIfOnBatteries")!.Value != "false")
                    throw new Exception("task settings");
            }
            finally { Autostart.Delete(name); }
            if (Autostart.Query(name, AppPaths.ExePath) != Autostart.TaskState.Missing) throw new Exception("not removed");
            return $"registered, read back (current; moved folder = outdated), removed; real task name would be '{Autostart.TaskName}'";
        });

        Check("pictures: camera rotation (EXIF 1–8), screen-sized decoding, moving pictures", () => PictureChecks(dir));

        Check("pictures: zooming keeps the point under the cursor (or the middle) in place", () =>
        {
            var view = new ZoomImageView();
            var bmp = RenderVisual(new DrawingVisual(), 400, 300);
            view.SetPicture(bmp, 4000, 3000, false);
            view.Measure(new Size(1000, 700));
            view.Arrange(new Rect(0, 0, 1000, 700));
            view.UpdateLayout();
            var setZoom = typeof(ZoomImageView).GetMethod("SetZoom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var report = new List<string>();
            foreach (var at in new[] { new Point(500, 350), new Point(700, 250), new Point(300, 500) })
            {
                view.ShowWhole();
                var before = view.ToPicture(at);
                for (int step = 0; step < 6; step++)
                {
                    setZoom.Invoke(view, new object[] { view.Zoom * 1.24, at });
                    var now = view.ToView(before); // where that picture point is on screen now (whole-pixel rounding allowed)
                    if ((now - at).Length > 3) throw new Exception($"zoom {view.Zoom:0.00} at {at}: the point went to {now}"); // rounding to whole pixels at a small zoom grows a little
                }
                view.ZoomBy(1.5);
                report.Add($"{at.X:0},{at.Y:0} → {view.Zoom:0.00}x");
            }
            // the zoom buttons work on the middle
            view.ShowWhole();
            var mid = view.ToPicture(new Point(500, 350));
            view.ZoomBy(2);
            var mid2 = view.ToView(mid);
            if ((mid2 - new Point(500, 350)).Length > 1.5) throw new Exception($"zoom button moved the middle to {mid2}");
            // and out, smaller than the whole-picture view (a small picture: smaller than 100 %)
            view.ShowWhole();
            double fit = view.Zoom;
            view.ZoomBy(0.5);
            if (view.Zoom > fit * 0.55) throw new Exception($"can't zoom out below the whole picture ({fit:0.000} → {view.Zoom:0.000})");
            var small = new ZoomImageView();
            small.SetPicture(bmp, 300, 200, false);
            small.Measure(new Size(1000, 700));
            small.Arrange(new Rect(0, 0, 1000, 700));
            small.ZoomBy(0.5);
            if (small.Zoom > 0.55) throw new Exception($"a small picture can't go below 100 % ({small.Zoom:0.00})");
            report.Add($"out to {view.Zoom / fit:0.00}× the whole view, a small one to {small.Zoom:0%}");
            return string.Join(", ", report);
        });

        Check("pictures: the wheel glides - the spot under the cursor stays put, further notches only move the goal", () =>
        {
            var view = new ZoomImageView();
            var bmp = RenderVisual(new DrawingVisual(), 2000, 1500); // (the screen-sized copy of a 12 MP photo)
            view.SetPicture(bmp, 4000, 3000, false);
            view.Measure(new Size(1000, 700));
            view.Arrange(new Rect(0, 0, 1000, 700));
            view.UpdateLayout();
            view.ZoomBy(0.5 / view.Zoom); // bigger than the window both ways: nothing but the cursor decides where it goes
            if (view.Scaling != BitmapScalingMode.HighQuality) throw new Exception("at rest the picture should be scaled carefully");
            var report = new List<string>();
            foreach (double hz in new[] { 400.0, 144, 60 })
            {
                view.ZoomBy(0.5 / view.Zoom);
                var at = new Point(700, 250);
                var spot = view.ToPicture(at);
                double start = view.Zoom, goal = start * 1.24 * 1.24 * 1.24; // three notches of the wheel
                double frame = 1000 / hz, ms = 0, last = start, worst = 0, jump = 1;
                int frames = 0;
                view.GlideForTest(start * 1.24, at);
                if (view.Scaling != BitmapScalingMode.Linear) throw new Exception("while it moves the picture should be scaled the quick way");
                bool there = false;
                while (!there && frames < 4000)
                {
                    // two more notches while it is on its way (40 and 90 ms in): the goal moves on, the movement doesn't start over
                    if (ms < 40 && ms + frame >= 40 || ms < 90 && ms + frame >= 90) view.GlideForTest(view.GlideGoal * 1.24, at);
                    there = view.GlideStepForTest(frame);
                    frames++;
                    ms += frame;
                    if (view.Zoom < last * (1 - 1e-12)) throw new Exception($"{hz} Hz: the zoom went backwards");
                    jump = Math.Max(jump, view.Zoom / last);
                    last = view.Zoom;
                    worst = Math.Max(worst, (view.ToView(spot) - at).Length); // (drawn on whole pixels: up to 0.7 px is rounding)
                }
                if (!there) throw new Exception($"{hz} Hz: it never arrived");
                if (Math.Abs(view.Zoom - goal) > goal * 1e-9) throw new Exception($"{hz} Hz: ended at {view.Zoom:0.0000}, the wheel asked for {goal:0.0000}");
                if (worst > 1.0) throw new Exception($"{hz} Hz: the spot under the cursor wandered {worst:0.0} px");
                if (ms < 150 || ms > 650) throw new Exception($"{hz} Hz: the glide took {ms:0} ms");
                // no frame may do most of the way at once (that would be the old jump)
                if (jump > Math.Pow(1.24, 3 * frame / 55)) throw new Exception($"{hz} Hz: one frame zoomed by ×{jump:0.000}");
                if (view.Scaling != BitmapScalingMode.HighQuality) throw new Exception($"{hz} Hz: at rest the picture must be scaled carefully again");
                report.Add($"{hz:0} Hz: {frames} frames, {ms:0} ms, biggest step ×{jump:0.000}, spot within {worst:0.00} px");
            }
            // and out again, past the whole-picture view: it stops at the smallest zoom
            view.GlideForTest(0.0001, new Point(500, 350));
            for (int i = 0; i < 4000 && !view.GlideStepForTest(2.5); i++) { }
            view.ShowWhole();
            double fit = view.Zoom;
            view.GlideForTest(0.0001, new Point(500, 350));
            for (int i = 0; i < 4000 && !view.GlideStepForTest(2.5); i++) { }
            if (Math.Abs(view.Zoom - fit * 0.2) > fit * 0.2 * 1e-6) throw new Exception($"zooming out ended at {view.Zoom / fit:0.000}× the whole view");
            return string.Join("; ", report);
        });

        Check("photo editor: the Text tool makes a caption like the video editor's (type, corner resize keeps the lines, saved)", () =>
        {
            var pics = Path.Combine(dir, "pictures");
            Directory.CreateDirectory(pics);
            var steps = Stopwatch.StartNew();
            var times = new List<string>();
            void Step(string what) { times.Add($"{what} {steps.ElapsedMilliseconds}"); steps.Restart(); }
            var doc = MarkupDocument.FromFile(MakeJpeg(Path.Combine(pics, "caption.jpg"), 1600, 1000, 1));
            Step("load");
            var ed = new EditorWindow(doc);
            var root = (FrameworkElement)ed.Content;
            ed.Content = null;
            root.Measure(new Size(1180, 760));
            root.Arrange(new Rect(0, 0, 1180, 760));
            root.UpdateLayout();
            ed.View.SetZoom(0.5);
            root.UpdateLayout();
            ed.View.Tool = ToolKind.Text;
            Step("window");
            ed.View.PointerDown(ed.View.ToView(new Point(800, 500)), 1);
            Step("new caption");
            var cap = doc.Items.OfType<CaptionAnnotation>().SingleOrDefault() ?? throw new Exception("no caption made");
            if (ed.CaptionBar.Visibility != Visibility.Visible) throw new Exception("the caption tool didn't show up");
            ed.Caption.TestType("Siema wszystkim, to jest dłuższy napis do sprawdzenia");
            Step("typed");
            if (cap.Text != "Siema wszystkim, to jest dłuższy napis do sprawdzenia" || cap.WrapEm <= 0) throw new Exception($"text '{cap.Text}', wrap {cap.WrapEm}");
            int lines = cap.Layout().Lines.Count;
            double size = cap.Size, width = cap.Bounds.Width;
            // drag the bottom-right corner outwards: bigger, same lines
            var corner = ed.View.ToView(cap.Bounds.BottomRight);
            ed.View.PointerDown(corner, 1);
            ed.View.PointerMove(corner + new Vector(60, 40), false);
            ed.View.PointerMove(corner + new Vector(120, 80), false);
            ed.View.PointerUp();
            Step("resized");
            if (cap.Size <= size * 1.1) throw new Exception($"the corner drag didn't enlarge it ({size:0.000} → {cap.Size:0.000})");
            if (cap.Layout().Lines.Count != lines) throw new Exception($"{lines} lines became {cap.Layout().Lines.Count}");
            // in the saved picture
            var before = Exporter.Render(new MarkupDocument(doc.Image), ExportSize.Original);
            var after = Exporter.Render(doc, ExportSize.Original);
            Step("exported");
            var a = PixelBuffer.FromBitmap(before);
            var b = PixelBuffer.FromBitmap(after);
            int changed = 0;
            var box = cap.Bounds;
            for (int y = (int)box.Top; y < (int)box.Bottom; y += 2)
                for (int x = (int)box.Left; x < (int)box.Right; x += 2)
                {
                    if (x < 0 || y < 0 || x >= a.Width || y >= a.Height) continue;
                    int i = (y * a.Width + x) * 4;
                    if (Math.Abs(a.Data[i] - b.Data[i]) + Math.Abs(a.Data[i + 1] - b.Data[i + 1]) + Math.Abs(a.Data[i + 2] - b.Data[i + 2]) > 60) changed++;
                }
            if (changed < 200) throw new Exception($"the caption isn't in the saved picture ({changed} pixels changed)");
            Step("compared");
            doc.Dirty = false; // closing must not ask "discard?" - that would put a real window on the screen
            ed.Close();
            Step("closed");
            return $"{lines} lines kept, size {size:0.000} → {cap.Size:0.000}, {changed} pixels drawn ({string.Join(", ", times)} ms)";
        });

        Check("photo editor: zooming keeps the spot under the cursor (or the middle) in place", () =>
        {
            var pics = Path.Combine(dir, "pictures");
            Directory.CreateDirectory(pics);
            var ed = new EditorWindow(MarkupDocument.FromFile(MakeJpeg(Path.Combine(pics, "zoom.jpg"), 3000, 2000, 1)));
            var root = (FrameworkElement)ed.Content;
            ed.Content = null;
            root.Measure(new Size(1180, 760));
            root.Arrange(new Rect(0, 0, 1180, 760));
            root.UpdateLayout();
            var setZoom = typeof(EditorWindow).GetMethod("SetZoom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var report = new List<string>();
            foreach (var (z, at) in new (double, Point?)[] { (1.0, new Point(300, 200)), (1.6, new Point(300, 200)), (2.4, new Point(700, 400)), (1.2, null) })
            {
                var anchor = at ?? new Point(ed.Scroller.ViewportWidth / 2, ed.Scroller.ViewportHeight / 2);
                var spot = ed.Scroller.TranslatePoint(anchor, ed.View);
                double fx = spot.X / ed.View.ActualWidth, fy = spot.Y / ed.View.ActualHeight;
                setZoom.Invoke(ed, new object?[] { z, at });
                root.UpdateLayout();
                var now = ed.View.TranslatePoint(new Point(fx * ed.View.ActualWidth, fy * ed.View.ActualHeight), ed.Scroller);
                if ((now - anchor).Length > 1.5) throw new Exception($"zoom {z} at {anchor}: the spot went to {now}");
                report.Add($"{z:0.0}x ok");
            }
            ed.Close();
            return string.Join(", ", report);
        });

        Check("text recognition: Polish text read back from a rendered picture, lines kept", () =>
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 110));
                var ft = new FormattedText("Zażółć gęślą jaźń 2026\nKontakt: biuro@example.com", CultureInfo.GetCultureInfo("pl-PL"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 24, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(14, 12));
            }
            var bmp = RenderVisual(dv, 640, 110);
            var r = TextRecognizer.Recognize(PixelBuffer.FromBitmap(bmp)) ?? throw new Exception("no OCR engine");
            string t = r.Text.Replace("\r", "");
            if (!t.Contains("Zażółć gęślą jaźń") || !t.Contains("biuro@example.com") || r.Lines.Count != 2) throw new Exception(t);
            var w = r.Lines[0].Words[0].Box;
            if (w.X < 8 || w.X > 30 || w.Y < 5 || w.Y > 40) throw new Exception($"first word at {w}");
            return t.Replace("\n", " / ");
        });

        Check("text recognition: a photographed label (turned, noisy) - PP-OCR finds the lines, Polish letters right", () =>
        {
            if (!PaddleOcr.Available) throw new Exception("PP-OCR missing in the engine folder");
            var lines = new[] { "Odbiorca: Jan Kowalski", "ul. Źródlana 12/4", "37-204 Tryńcza", "Zawartość: książki i płyty" };
            const string number = "68750089663720432097";
            var px = PixelBuffer.FromBitmap(MakePhotoLabel(lines, number));
            if (!TextRecognizer.LooksLikePhoto(px, out var why)) throw new Exception("not taken for a photo: " + why);
            var r = TextRecognizer.Recognize(px) ?? throw new Exception("no OCR");
            var got = r.Lines.Select(l => l.Text).ToList();
            var want = lines.SelectMany(l => l.Split(' ')).ToList();
            var have = got.SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
            int hit = want.Count(w => have.Contains(w));
            bool upright = got.Any(l => l.Replace(" ", "").Contains(number));
            if (hit < want.Count - 1 || r.Lines.Count != lines.Length + 1 || !upright)
                throw new Exception($"{hit}/{want.Count} words, {r.Lines.Count} lines, upright number {(upright ? "read" : "missing")}: {string.Join(" / ", got)}");
            return $"{hit}/{want.Count} words + the upright number ({why}) · {PaddleOcr.LastTimings}";
        });

        Check("QR: the speckle filter (3×3 median) gives exactly the middle value, at every picture width", () =>
        {
            var rnd = new Random(7);
            var widths = new[] { 3, 4, 5, 17, 31, 32, 33, 34, 35, 63, 64, 65, 66, 67, 131, 640 };
            Span<byte> v = stackalloc byte[9];
            foreach (int w in widths)
            {
                const int h = 9;
                var g = new byte[w * h];
                rnd.NextBytes(g);
                var got = QrReader.Median3(g, w, h);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        byte want = g[y * w + x]; // the outermost pixels stay
                        if (y > 0 && y < h - 1 && x > 0 && x < w - 1)
                        {
                            int k = 0;
                            for (int j = -1; j <= 1; j++)
                                for (int i = -1; i <= 1; i++) v[k++] = g[(y + j) * w + x + i];
                            v.Sort();
                            want = v[4];
                        }
                        if (got[y * w + x] != want) throw new Exception($"width {w}: pixel {x},{y} is {got[y * w + x]}, should be {want}");
                    }
            }
            var big = new byte[1920 * 1080];
            rnd.NextBytes(big);
            QrReader.Median3(big, 1920, 1080);
            var sw = Stopwatch.StartNew();
            QrReader.Median3(big, 1920, 1080);
            double ms = sw.Elapsed.TotalMilliseconds;
            // a screenshot without any code: the whole look must stay a matter of milliseconds
            var none = MakeFakeUi(1920, 1080);
            QrReader.Read(none);
            sw.Restart();
            int found = QrReader.Read(none).Count;
            return $"{widths.Length} widths exact; 1920×1080 filtered in {ms:0.0} ms; a 1920×1080 screen without a code looked through in {sw.ElapsedMilliseconds} ms ({found} codes)";
        });

        Check("capture preview: drawn from a small copy that looks the same as the capture", () =>
        {
            var big = MakeFakeUi(3840, 2160).ToBitmap(); // the size of an HD capture
            var sw = Stopwatch.StartNew();
            var small = CaptureToast.Preview(big);
            long ms = sw.ElapsedMilliseconds;
            if (!small.IsFrozen) throw new Exception("the copy can't be handed to the UI thread");
            if (small.PixelWidth > 1000 || small.PixelWidth < 700 || small.PixelHeight < 400)
                throw new Exception($"the copy is {small.PixelWidth}×{small.PixelHeight}");
            if (Math.Abs(small.PixelWidth / (double)small.PixelHeight - 3840 / 2160.0) > 0.01) throw new Exception("the copy has another shape");
            var tiny = MakeFakeUi(640, 360).ToBitmap();
            if (!ReferenceEquals(CaptureToast.Preview(tiny), tiny)) throw new Exception("a small capture needs no copy");
            // both drawn the way the preview draws them
            BitmapSource Shown(BitmapSource src)
            {
                var dv = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
                using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, 236, 133));
                return RenderVisual(dv, 236, 133);
            }
            double diff = FrameDiff(Shown(big), Shown(small));
            if (diff > 4) throw new Exception($"the preview looks different from the capture (mean difference {diff:0.0} of 255)");
            return $"3840×2160 → {small.PixelWidth}×{small.PixelHeight} in {ms} ms, looks the same (mean difference {diff:0.00} of 255)";
        });

        Check("emoji search: Polish and English words (serce, ogień, śmiech, fire)", () =>
        {
            var cat = Wait(AppleEmoji.CatalogAsync(), 60) ?? throw new Exception("no emoji catalog (offline?)");
            string Top(string q) => string.Concat(AppleEmoji.Search(q).Take(5).Select(e => e.Text));
            var heart = Top("serce");
            var fire = Top("ogien");
            var laugh = Top("śmiech");
            var en = Top("fire");
            if (!heart.Contains("❤") || !fire.Contains("🔥") || !laugh.Contains("😂") || !en.Contains("🔥")) throw new Exception($"serce {heart}, ogien {fire}, śmiech {laugh}, fire {en}");
            return $"serce {heart} · ogien {fire} · śmiech {laugh} · fire {en} ({cat.Count} emoji)";
        });

        Check("file icons: one per format, 16-256 px", () =>
        {
            var icons = Path.Combine(dir, "file-icons");
            Directory.CreateDirectory(icons);
            var info = new List<string>();
            foreach (var ext in new[] { ".mp4", ".mp3", ".jpg", ".gif" })
            {
                var path = Path.Combine(icons, ext.TrimStart('.') + ".ico");
                FileTypeIcons.WriteIco(path, ext);
                var b = File.ReadAllBytes(path);
                int n = BitConverter.ToUInt16(b, 4);
                if (n != 10 || b[6] != 16 || b[6 + 16 * 9] != 0) throw new Exception($"{ext}: {n} sizes");
                info.Add($"{ext} {FileTypeIcons.KindOf(ext)} {b.Length / 1024} KB");
            }
            return string.Join(", ", info);
        });

        Check("clipboard: Ctrl+C+C keeps up to 10 screenshots, a normal copy starts over", () =>
        {
            var before = Clipboard.GetDataObject();
            try
            {
                var shots = Path.Combine(dir, "stack");
                Directory.CreateDirectory(shots);
                var img = RenderVisual(new DrawingVisual(), 8, 8);
                var png = ImageIO.EncodePng(img);
                string Shot(int i) { var p = Path.Combine(shots, $"shot{i}.png"); File.WriteAllBytes(p, png); return p; }
                // a normal copy, then two stacked ones
                ClipboardStack.Delivered(ClipboardStack.BeginForTest(), img, png, Shot(0));
                for (int i = 1; i <= 2; i++)
                {
                    var t = ClipboardStack.BeginForTest();
                    ClipboardStack.SecondForTest(t);
                    ClipboardStack.Delivered(t, img, png, Shot(i));
                }
                var files = Clipboard.GetFileDropList();
                if (files.Count != 3) throw new Exception($"{files.Count} files after a copy and two Ctrl+C+C");
                // twelve more: only the newest ten stay
                for (int i = 3; i <= 14; i++)
                {
                    var t = ClipboardStack.BeginForTest();
                    ClipboardStack.Delivered(t, img, png, Shot(i));
                    ClipboardStack.SecondForTest(t); // the second C may also come after the delivery
                }
                files = Clipboard.GetFileDropList();
                if (files.Count != 10 || !files[9]!.EndsWith("shot14.png")) throw new Exception($"{files.Count} files, last {files[^1]}");
                // something copied elsewhere: the next stack starts from scratch
                ImageIO.CopyText("hello");
                var t2 = ClipboardStack.BeginForTest();
                ClipboardStack.SecondForTest(t2);
                ClipboardStack.Delivered(t2, img, png, Shot(20));
                if (Clipboard.ContainsFileDropList() || !Clipboard.ContainsImage()) throw new Exception("after another copy a new set should start with the one picture");
                // the clear shortcut empties it, and the next Ctrl+C+C starts a new set
                ClipboardStack.Clear();
                if (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList() || Clipboard.ContainsText()) throw new Exception("the clipboard isn't empty after the clear shortcut");
                var t3 = ClipboardStack.BeginForTest();
                ClipboardStack.SecondForTest(t3);
                ClipboardStack.Delivered(t3, img, png, Shot(21));
                if (Clipboard.ContainsFileDropList()) throw new Exception("after clearing, Ctrl+C+C continued the old set");
                return "1 + 2 stacked = 3 files; 15 → newest 10; another copy in between starts over; clear empties it";
            }
            finally
            {
                if (before != null) { try { Clipboard.SetDataObject(before, true); } catch { /* best effort */ } }
            }
        });

        Check("photo editor: a phone photo opens centred and whole", () =>
        {
            var pics = Path.Combine(dir, "pictures");
            Directory.CreateDirectory(pics);
            var path = MakeJpeg(Path.Combine(pics, "phone.jpg"), 4032, 3024, 6);
            var ed = new EditorWindow(MarkupDocument.FromFile(path));
            try
            {
                ed.Show();
                Pump(TimeSpan.FromSeconds(2), () => false);
                var sc = ed.Scroller;
                var tl = ed.View.TranslatePoint(new Point(0, 0), sc);
                double l = tl.X, t = tl.Y, r = sc.ViewportWidth - tl.X - ed.View.ActualWidth, b = sc.ViewportHeight - tl.Y - ed.View.ActualHeight;
                bool bars = sc.ComputedHorizontalScrollBarVisibility == Visibility.Visible || sc.ComputedVerticalScrollBarVisibility == Visibility.Visible;
                Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(ed).Handle, out var wr);
                Save(ScreenCapturer.CaptureBitmap(new Int32Rect(wr.Left, wr.Top, wr.Width, wr.Height)), Path.Combine(dir, "photo-editor.png"));
                string info = $"window {wr.Width}x{wr.Height}, viewport {sc.ViewportWidth:0}x{sc.ViewportHeight:0}, picture {ed.View.ActualWidth:0}x{ed.View.ActualHeight:0} at margins L{l:0} T{t:0} R{r:0} B{b:0}, scrollbars {bars}, zoom {ed.View.Zoom:0.###}";
                if (bars || Math.Abs(l - r) > 2 || Math.Abs(t - b) > 2) throw new Exception(info);
                return info;
            }
            finally { ed.Close(); }
        });

        Check("viewer: a big photo opens fitted, double-click size = actual pixels loads every pixel only then, ← → in name order", () => ViewerScenario(dir));

        TestMedia(dir);
    }

    /// <summary>A test JPEG (stored w×h) whose top-left quarter is red, with the given EXIF orientation.</summary>
    private static string MakeJpeg(string path, int w, int h, int orientation)
    {
        var pb = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool red = x < w / 2 && y < h / 2;
                pb.Data[i] = (byte)(red ? 0x20 : 0xC0); pb.Data[i + 1] = (byte)(red ? 0x20 : 0x60); pb.Data[i + 2] = (byte)(red ? 0xF0 : 0x20); pb.Data[i + 3] = 255;
            }
        var md = new BitmapMetadata("jpg");
        if (orientation > 1) md.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        var enc = new JpegBitmapEncoder { QualityLevel = 95 };
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(pb.ToBitmap(), PixelFormats.Bgr32, null, 0), null, md, null));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    private static string PictureChecks(string dir)
    {
        var pics = Path.Combine(dir, "pictures");
        Directory.CreateDirectory(pics);
        // where the red corner has to end up once the camera's rotation is applied: TL, TR, BR, BL
        var expect = new Dictionary<int, string> { [1] = "TL", [2] = "TR", [3] = "BR", [4] = "BL", [5] = "TL", [6] = "TR", [7] = "BR", [8] = "BL" };
        foreach (var (o, corner) in expect)
        {
            var path = MakeJpeg(Path.Combine(pics, $"orientation-{o}.jpg"), 64, 32, o);
            var info = ImageIO.Probe(path) ?? throw new Exception($"{o}: probe failed");
            if (info.Orientation != o) throw new Exception($"{o}: read orientation {info.Orientation}");
            var bmp = ImageIO.Decode(path, info, 0);
            bool turned = o >= 5;
            if (bmp.PixelWidth != (turned ? 32 : 64) || bmp.PixelHeight != (turned ? 64 : 32) || info.ShownWidth != bmp.PixelWidth)
                throw new Exception($"{o}: {bmp.PixelWidth}x{bmp.PixelHeight}, shown {info.ShownWidth}x{info.ShownHeight}");
            var px = PixelBuffer.FromBitmap(bmp);
            int qw = px.Width / 4, qh = px.Height / 4;
            string found = new[] { ("TL", qw, qh), ("TR", px.Width - qw, qh), ("BR", px.Width - qw, px.Height - qh), ("BL", qw, px.Height - qh) }
                .First(c => px.GetPixel(c.Item2, c.Item3).R > 0xB0).Item1;
            if (found != corner) throw new Exception($"orientation {o}: red corner at {found}, expected {corner}");
            // the editor gets the same, right way up
            var full = ImageIO.LoadFull(path)!;
            if (full.PixelWidth != bmp.PixelWidth) throw new Exception($"{o}: editor got {full.PixelWidth}x{full.PixelHeight}");
        }

        // a 12-megapixel photo: the screen-sized copy is quick, the full one only when needed
        var big = MakeJpeg(Path.Combine(pics, "big.jpg"), 4000, 3000, 6);
        var bi = ImageIO.Probe(big)!;
        var sw = Stopwatch.StartNew();
        var preview = ImageIO.Decode(big, bi, 1920);
        long tPreview = sw.ElapsedMilliseconds;
        sw.Restart();
        var fullBig = ImageIO.Decode(big, bi, 0);
        long tFull = sw.ElapsedMilliseconds;
        if (preview.PixelHeight != 1920 || preview.PixelWidth != 1440) throw new Exception($"preview {preview.PixelWidth}x{preview.PixelHeight}");
        if (fullBig.PixelWidth != 3000 || fullBig.PixelHeight != 4000) throw new Exception($"full {fullBig.PixelWidth}x{fullBig.PixelHeight}");

        // moving pictures go to the player, still ones to the viewer
        string Gif(string name, int frames)
        {
            var enc = new GifBitmapEncoder();
            for (int i = 0; i < frames; i++)
            {
                var f = new PixelBuffer(8, 8);
                f.ForceOpaque();
                f.Data[i * 4] = 255;
                enc.Frames.Add(BitmapFrame.Create(f.ToBitmap()));
            }
            var p = Path.Combine(pics, name);
            using var fs = File.Create(p);
            enc.Save(fs);
            return p;
        }
        var still = Gif("still.gif", 1);
        var moving = Gif("moving.gif", 3);
        if (ImageIO.IsAnimated(still, ImageIO.Probe(still)) || !ImageIO.IsAnimated(moving, ImageIO.Probe(moving))) throw new Exception("GIF animation detection");
        byte[] Webp(byte flags) => "RIFF"u8.ToArray().Concat(new byte[] { 30, 0, 0, 0 }).Concat("WEBPVP8X"u8.ToArray()).Concat(new byte[] { 10, 0, 0, 0, flags, 0, 0, 0 }).Concat(new byte[16]).ToArray();
        File.WriteAllBytes(Path.Combine(pics, "anim.webp"), Webp(0x02));
        File.WriteAllBytes(Path.Combine(pics, "still.webp"), Webp(0x00));
        if (!ImageIO.IsAnimated(Path.Combine(pics, "anim.webp"), null) || ImageIO.IsAnimated(Path.Combine(pics, "still.webp"), null)) throw new Exception("WebP animation detection");
        if (!MediaFormats.IsImage("x.JPG") || !MediaFormats.IsImage("x.heic") || MediaFormats.IsImage("x.mp4")) throw new Exception("format lists");
        return $"8 orientations right way up (viewer and editor); 12 MP photo: screen copy {tPreview} ms, every pixel {tFull} ms; GIF/WebP animation detected";
    }

    private static string ViewerScenario(string dir)
    {
        var folder = Path.Combine(dir, "viewer");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var photo = MakeJpeg(Path.Combine(folder, "photo 2.jpg"), 4000, 2600, 1);
        MakeJpeg(Path.Combine(folder, "photo 10.jpg"), 300, 200, 1);
        MakeJpeg(Path.Combine(folder, "photo 1.jpg"), 300, 200, 8);
        var v = new ImageViewerWindow();
        try
        {
            var sw = Stopwatch.StartNew();
            v.Open(photo);
            Pump(TimeSpan.FromSeconds(6), () => v.View.HasPicture && v.View.ActualWidth > 0);
            long shown = sw.ElapsedMilliseconds;
            if (!v.View.HasPicture) throw new Exception("nothing shown");
            Pump(TimeSpan.FromMilliseconds(300), () => false);
            double fit = v.View.FitZoom;
            if (!v.View.IsFit || fit >= 1 || Math.Abs(v.View.Zoom - fit) > 1e-6) throw new Exception($"not fitted: zoom {v.View.Zoom:0.###}, fit {fit:0.###}");
            int previewW = v.View.BitmapWidth;
            if (previewW >= 4000) throw new Exception("the full picture was decoded although it isn't needed yet");

            v.View.ToggleZoom(); // → actual pixels
            Pump(TimeSpan.FromSeconds(6), () => v.View.BitmapWidth == 4000 && Math.Abs(v.View.Zoom - 1) < 1e-6);
            if (Math.Abs(v.View.Zoom - 1) > 1e-6) throw new Exception($"double-click size: zoom {v.View.Zoom:0.###}");
            if (v.View.BitmapWidth != 4000) throw new Exception($"every pixel never arrived ({v.View.BitmapWidth} px wide)");
            v.View.ToggleZoom(); // → back to the whole picture
            Pump(TimeSpan.FromMilliseconds(500), () => false);
            if (!v.View.IsFit) throw new Exception("did not return to the whole picture");

            // the folder in Explorer's order: "photo 1", "photo 2", "photo 10"
            Pump(TimeSpan.FromSeconds(2), () => false);
            v.Step(1);
            Pump(TimeSpan.FromSeconds(3), () => v.View.PictureWidth == 300);
            string next = Path.GetFileName(v.CurrentPath!);
            v.Step(-1);
            v.Step(-1);
            Pump(TimeSpan.FromSeconds(3), () => v.View.PictureHeight == 300);
            string first = Path.GetFileName(v.CurrentPath!);
            if (next != "photo 10.jpg" || first != "photo 1.jpg") throw new Exception($"order: next {next}, first {first}");
            if (v.View.PictureWidth != 200 || v.View.PictureHeight != 300) throw new Exception($"rotated photo shown {v.View.PictureWidth}x{v.View.PictureHeight}");
            return $"shown after {shown} ms from a {previewW}-px copy (fit {fit * 100:0}%), 100% loaded all 4000 px; → photo 10, ← ← photo 1 (turned upright)";
        }
        finally { v.Close(); }
    }

    /// <summary>Lets window messages (hooks, hotkeys) arrive while a test waits for them.</summary>
    private static void Pump(TimeSpan max, Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed < max)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    /// <summary>Synthetic input for the live shortcut test.</summary>
    private static class Inject
    {
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION u; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

        public static void MouseX(int button)
        {
            var down = new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { mouseData = (uint)button, dwFlags = 0x0080 } } };
            var up = new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { mouseData = (uint)button, dwFlags = 0x0100 } } };
            if (SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>()) != 2) throw new Exception($"SendInput failed ({Marshal.GetLastWin32Error()})");
        }

        public static void Key(ushort vk, bool extended)
        {
            uint ext = extended ? 1u : 0u;
            var down = new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { vk = vk, flags = ext } } };
            var up = new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { vk = vk, flags = ext | 2 } } };
            if (SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>()) != 2) throw new Exception($"SendInput failed ({Marshal.GetLastWin32Error()})");
        }
    }

    private static string EditorScenario(string dir)
    {
        var ui = MakeFakeUi(400, 240);
        var doc = new MarkupDocument(ui.ToBitmap());
        var v = new MarkupView();
        v.Load(doc);
        v.SetZoom(1.0); // 96 DPI in tests -> 1 view unit == 1 image pixel
        v.Measure(new Size(2000, 2000));
        v.Arrange(new Rect(v.DesiredSize));
        Point P(double x, double y) => new(x, y);
        void Drag(Point a, Point b, bool shift = false)
        {
            v.PointerDown(a, 1);
            for (int i = 1; i <= 8; i++) v.PointerMove(new Point(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8), shift);
            v.PointerUp();
        }

        v.Tool = ToolKind.Arrow; Drag(P(40, 200), P(180, 90));
        v.Tool = ToolKind.Rect; Drag(P(220, 40), P(360, 110));
        v.Tool = ToolKind.Pen; Drag(P(60, 60), P(150, 40));
        v.Tool = ToolKind.Counter; v.PointerDown(P(370, 30), 1); v.PointerUp(); v.PointerDown(P(370, 70), 1); v.PointerUp();
        if (doc.Items.Count != 5) throw new Exception($"expected 5 items, got {doc.Items.Count}");
        if (doc.Items.OfType<CounterAnnotation>().Select(c => c.Number).Max() != 2) throw new Exception("counter numbering");

        // select the rectangle and move it 20 px down
        v.Tool = ToolKind.Select;
        v.PointerDown(P(220, 75), 1);
        if (v.Selected is not ShapeAnnotation sh) throw new Exception("rect not selected");
        for (int i = 1; i <= 4; i++) v.PointerMove(P(220, 75 + i * 5), false);
        v.PointerUp();
        if (Math.Abs(sh.Rect.Y - 60) > 0.6) throw new Exception($"move failed, y={sh.Rect.Y}");
        // resize via bottom-right handle
        Drag(P(360, 130), P(380, 150));
        if (Math.Abs(sh.Rect.Right - 380) > 0.6) throw new Exception($"resize failed, right={sh.Rect.Right}");
        // undo twice -> back to original position, redo once
        doc.Undo(); doc.Undo(); v.Refresh();
        var r0 = doc.Items.OfType<ShapeAnnotation>().First().Rect;
        if (Math.Abs(r0.Y - 40) > 0.6) throw new Exception($"undo failed, y={r0.Y}");
        doc.Redo(); v.Refresh();
        if (Math.Abs(doc.Items.OfType<ShapeAnnotation>().First().Rect.Y - 60) > 0.6) throw new Exception("redo failed");

        // crop & extend: 16:9 with a 20% margin, auto background
        v.EnterCrop();
        v.ApplyRatioAndMargin(16.0 / 9, 0.2);
        v.ApplyCrop();
        var f = doc.Frame;
        double ratio = f.Width / (double)f.Height;
        if (Math.Abs(ratio - 16.0 / 9) > 0.02) throw new Exception($"ratio {ratio:0.000}");
        if (f.X >= 0 || f.Y >= 0) throw new Exception("frame was not extended");
        var outImg = Exporter.Render(doc, ExportSize.FullHD);
        if (outImg.PixelWidth != 1920 || outImg.PixelHeight != 1080) throw new Exception($"FHD export {outImg.PixelWidth}x{outImg.PixelHeight}");
        Save(outImg, Path.Combine(dir, "scenario-fhd.png"));
        // the corner of the extended area must carry the detected background colour
        var px = PixelBuffer.FromBitmap(outImg).GetPixel(3, 3);
        var bg = doc.DetectedBackground;
        if (Math.Abs(px.R - bg.R) > 3 || Math.Abs(px.G - bg.G) > 3 || Math.Abs(px.B - bg.B) > 3) throw new Exception($"fill {px} != {bg}");
        return $"frame {f.Width}x{f.Height} at {f.X},{f.Y}; FHD export ok; fill {bg}";
    }

    /// <param name="scale">draw everything this many times bigger (a real render at that scale, not a stretched one)</param>
    /// <param name="crisp">text as it is on a normal screen: hinted and snapped to whole pixels</param>
    /// <param name="variant">another window in the same theme (different texts and layout)</param>
    private static PixelBuffer MakeFakeUi(int w, int h, int scale = 1, bool crisp = false, bool variant = false)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            var mode = crisp ? TextFormattingMode.Display : TextFormattingMode.Ideal;
            FormattedText Text(string t, Typeface f, double size, Brush b) =>
                new(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, f, size, b, null, mode, 1.0);
            var semibold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var regular = new Typeface("Segoe UI");
            var grey = new SolidColorBrush(Color.FromRgb(0xB0, 0xB3, 0xB8));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)), null, new Rect(0, 0, w, h));
            if (!variant)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x38, 0x3A, 0x40)), null, new Rect(24, 24, 240, 120), 10, 10);
                dc.DrawText(Text("Ustawienia konta", semibold, 18, Brushes.White), new Point(40, 40));
                dc.DrawText(Text("Powiadomienia e-mail", regular, 13, grey), new Point(40, 96));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2)), null, new Rect(300, 50, 160, 40), 8, 8);
                dc.DrawText(Text("Zapisz zmiany", semibold, 14, Brushes.White), new Point(332, 60));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3B, 0xA5, 0x5D)), null, new Point(80, 210), 26, 26);
            }
            else
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x38, 0x3A, 0x40)), null, new Rect(150, 40, 300, 96), 10, 10);
                dc.DrawText(Text("Historia płatności", semibold, 18, Brushes.White), new Point(170, 52));
                dc.DrawText(Text("Ostatnie 30 dni — 12 pozycji", regular, 13, grey), new Point(170, 100));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xED, 0x42, 0x45)), null, new Rect(30, 60, 96, 40), 8, 8);
                dc.DrawText(Text("Usuń", semibold, 14, Brushes.White), new Point(58, 70));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3B, 0xA5, 0x5D)), null, new Point(380, 220), 26, 26);
            }
            dc.Pop();
        }
        var rtb = RenderVisual(dv, w * scale, h * scale);
        return PixelBuffer.FromBitmap(rtb);
    }

    /// <summary>A picture blown up k times with plain bilinear filtering - what Windows shows for an app that ignores scaling.</summary>
    private static PixelBuffer Stretch(PixelBuffer src, int k)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.Linear);
        using (var dc = dv.RenderOpen()) dc.DrawImage(src.ToBitmap(), new Rect(0, 0, src.Width * k, src.Height * k));
        return PixelBuffer.FromBitmap(RenderVisual(dv, src.Width * k, src.Height * k));
    }

    private static BitmapSource RenderVisual(Visual v, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    // ------------------------------------------------------------------ media

    private static void TestMedia(string dir)
    {
        if (!Ffmpeg.Available) { Report.AppendLine("SKIP  media tests (ffmpeg not found)"); return; }
        string mp4 = Path.Combine(dir, "test.mp4"), mkv = Path.Combine(dir, "test.mkv"), mp3 = Path.Combine(dir, "test.mp3");

        Check("ffmpeg: generate test media", () =>
        {
            RunFfmpeg($"-y -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 10 -c:v libx264 -g 30 -pix_fmt yuv420p -c:a aac -shortest \"{mp4}\"");
            RunFfmpeg($"-y -i \"{mp4}\" -c copy \"{mkv}\"");
            RunFfmpeg($"-y -f lavfi -i sine=frequency=330:sample_rate=44100 -t 12 -c:a libmp3lame -b:a 192k \"{mp3}\"");
            return "mp4/mkv/mp3 ready";
        });

        foreach (var (input, a, b) in new[] { (mp4, 2.5, 7.5), (mkv, 1.0, 6.0), (mp3, 3.0, 9.0) })
        {
            Check($"lossless trim {Path.GetExtension(input)} {a}-{b}s", () =>
            {
                var output = Path.Combine(dir, "trim-" + Path.GetFileName(input));
                if (File.Exists(output)) File.Delete(output);
                var (ok, err) = Task.Run(() => Ffmpeg.Trim(input, output, a, b, null, CancellationToken.None)).GetAwaiter().GetResult();
                if (!ok) throw new Exception(err);
                double d = ProbeDuration(output);
                double want = b - a;
                // MP4/MOV and audio are cut exactly; MKV starts at the previous keyframe (lossless = no re-encode), GOP here is 1 s
                double tolerance = MediaFormats.IsMp4Family(input) || MediaFormats.IsAudio(input) ? 0.25 : 1.35;
                if (d < want - 0.25 || d > want + tolerance) throw new Exception($"duration {d:0.00}s, expected ~{want:0.00}s");
                return $"duration {d:0.00}s (wanted {want:0.00}s), {new FileInfo(output).Length / 1024} KB";
            });
        }

        Check("keyframe scan", () =>
        {
            var kf = Task.Run(() => Ffmpeg.Keyframes(mp4, CancellationToken.None)).GetAwaiter().GetResult();
            if (kf == null || kf.Length < 5) throw new Exception($"got {kf?.Length ?? 0} keyframes");
            return $"{kf.Length} keyframes: {string.Join(", ", kf.Take(5).Select(x => x.ToString("0.##", CultureInfo.InvariantCulture)))}…";
        });

        foreach (var (input, total, a, b) in new[] { (mp4, 10.0, 3.0, 6.0), (mkv, 10.0, 2.0, 5.0), (mp3, 12.0, 4.0, 8.0) })
        {
            Check($"cut out {Path.GetExtension(input)} {a}-{b}s", () =>
            {
                var kf = MediaFormats.IsAudio(input) ? null : Task.Run(() => Ffmpeg.Keyframes(input, CancellationToken.None)).GetAwaiter().GetResult();
                double end = b;
                if (kf != null) { int i = Array.BinarySearch(kf, b - 0.001); if (i < 0) i = ~i; if (i < kf.Length) end = kf[i]; }
                var output = Path.Combine(dir, "cut-" + Path.GetFileName(input));
                if (File.Exists(output)) File.Delete(output);
                var (ok, err) = Task.Run(() => Ffmpeg.CutOut(input, output, a, end, total, null, CancellationToken.None, kf)).GetAwaiter().GetResult();
                if (!ok) throw new Exception(err);
                double d = ProbeDuration(output);
                double want = total - (end - a);
                if (Math.Abs(d - want) > 0.35) throw new Exception($"duration {d:0.00}s, expected ~{want:0.00}s (removed {a}-{end:0.###})");
                var decodeErrors = DecodeErrors(output);
                if (decodeErrors.Length > 0) throw new Exception("join is not clean: " + decodeErrors.Split('\n')[0]);
                return $"removed {a}-{end:0.###}s -> {d:0.00}s (wanted {want:0.00}s), clean decode";
            });
        }

        Check("filmstrip frame", () =>
        {
            var bmp = Task.Run(() => Ffmpeg.Frame(mp4, 4.0, 90, CancellationToken.None)).GetAwaiter().GetResult();
            if (bmp == null) throw new Exception("no frame");
            Save(bmp, Path.Combine(dir, "frame.png"));
            return $"{bmp.PixelWidth}x{bmp.PixelHeight}";
        });

        Check("waveform", () =>
        {
            var w = Task.Run(() => Ffmpeg.Waveform(mp3, 100, 12, CancellationToken.None)).GetAwaiter().GetResult();
            if (w == null || w.Length != 100) throw new Exception("no waveform");
            var folded = WaveformView.Fold(w, 7);
            if (folded.Length != 7 || Math.Abs(folded.Max() - w.Max()) > 1e-6) throw new Exception("fold");
            return $"max {w.Max():0.00}";
        });

        Check("waveform: a voice without bass, its channels out of phase, a full-scale song - none flat (nor one click everything)", () =>
        {
            // talk-like: 1 kHz in soft 2-second bursts with 2 seconds of silence between them, nothing below it, the
            // right channel the left one upside down - a mono mix of it is silence, and the long files' old 200 Hz
            // reading kept only what is below 100 Hz: both showed a flat line for a recording you can hear fine
            string talk = Path.Combine(dir, "talk.wav");
            const int rate = 44100, secs = 20;
            using (var bw = new BinaryWriter(File.Create(talk)))
            {
                int n = rate * secs;
                bw.Write("RIFF"u8); bw.Write(36 + n * 4); bw.Write("WAVE"u8);
                bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2); bw.Write(rate); bw.Write(rate * 4); bw.Write((short)4); bw.Write((short)16);
                bw.Write("data"u8); bw.Write(n * 4);
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)rate, env = Math.Max(0, Math.Sin(2 * Math.PI * t / 4));
                    short v = (short)(0.5 * env * env * Math.Sin(2 * Math.PI * 1000 * t) * 32767);
                    bw.Write(v);
                    bw.Write((short)-v);
                }
            }
            var w = Task.Run(() => Ffmpeg.Waveform(talk, 40, secs, CancellationToken.None)).GetAwaiter().GetResult() ?? throw new Exception("no waveform");
            int loud = w.Count(v => v > 0.6f), quiet = w.Count(v => v < 0.15f);
            if (loud < 18 || quiet < 18)
                throw new Exception($"{loud} loud and {quiet} quiet of 40: {string.Join(" ", w.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)))}");
            // a loud song touches the very bottom of 16 bits (-32768): reading it used to throw an OverflowException
            // (Math.Abs of a short) - no waveform at all, flat in the player and empty in the editor
            string loudWav = Path.Combine(dir, "loud.wav");
            using (var bw = new BinaryWriter(File.Create(loudWav)))
            {
                int n = rate * 3;
                bw.Write("RIFF"u8); bw.Write(36 + n * 4); bw.Write("WAVE"u8);
                bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2); bw.Write(rate); bw.Write(rate * 4); bw.Write((short)4); bw.Write((short)16);
                bw.Write("data"u8); bw.Write(n * 4);
                for (int i = 0; i < n; i++) { short v = i / 220 % 2 == 0 ? short.MaxValue : short.MinValue; bw.Write(v); bw.Write(v); }
            }
            var lw = Task.Run(() => Ffmpeg.Waveform(loudWav, 30, 3, CancellationToken.None)).GetAwaiter().GetResult();
            if (lw == null || lw.Min() < 0.9f) throw new Exception("a full-scale song: " + (lw == null ? "no waveform" : string.Join(" ", lw.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)))));
            // one click at full scale in a quiet talk: the talk keeps its height
            var fine = Enumerable.Range(0, 4000).Select(i => i % 100 < 50 ? 0.2f : 0.01f).ToList();
            fine[1234] = 1f;
            var env2 = Ffmpeg.Envelope(fine, 400);
            int tall = env2.Count(v => v > 0.85f);
            if (tall < 190) throw new Exception($"with a click, only {tall} of the 200 talking bins stay tall");
            return $"{loud} loud / {quiet} quiet bins of 40; with a click {tall} of 200 talking bins stay tall";
        });

        Check("player: an MP3 shows its whole waveform at once (no Edit), and the layout grows with the window", () =>
        {
            var s = App.Instance!.Settings;
            bool wasMuted = s.PlayerMuted;
            double volume = s.PlayerVolume;
            s.PlayerMuted = true; // nothing audible during the test
            var pw = new PlayerWindow();
            try
            {
                var sw = Stopwatch.StartNew();
                pw.OpenFile(mp3);
                Pump(TimeSpan.FromSeconds(10), () => pw.Wave.Peaks != null && pw.Wave.ActualWidth > 0);
                long ms = sw.ElapsedMilliseconds;
                if (pw.Wave.Peaks == null) throw new Exception("no waveform without pressing Edit");
                Pump(TimeSpan.FromMilliseconds(600), () => false);
                double w0 = pw.Wave.ActualWidth, h0 = pw.Wave.ActualHeight, art0 = pw.ArtBorder.ActualWidth;
                pw.Width = 1200;
                pw.Height = 820;
                Pump(TimeSpan.FromMilliseconds(600), () => false);
                double w1 = pw.Wave.ActualWidth, h1 = pw.Wave.ActualHeight, art1 = pw.ArtBorder.ActualWidth;
                if (w1 <= w0 + 50 || h1 <= h0 || art1 <= art0) throw new Exception($"layout did not grow: waveform {w0:0}x{h0:0} → {w1:0}x{h1:0}, cover {art0:0} → {art1:0}");
                pw.Width = 440;
                pw.Height = 280;
                Pump(TimeSpan.FromMilliseconds(600), () => false);
                if (pw.Wave.ActualHeight < 20 || pw.Wave.ActualWidth < 200) throw new Exception($"small window: waveform {pw.Wave.ActualWidth:0}x{pw.Wave.ActualHeight:0}");
                return $"waveform after {ms} ms; {w0:0}x{h0:0} (cover {art0:0}) → {w1:0}x{h1:0} (cover {art1:0}) at 1200×820 → {pw.Wave.ActualWidth:0}x{pw.Wave.ActualHeight:0} at 440×280";
            }
            finally
            {
                pw.Close();
                s.PlayerMuted = wasMuted;
                s.PlayerVolume = volume;
                s.Save();
            }
        });

        Check("libmpv headless playback", () => MpvHeadless(mp4));

        Check("libmpv: every seek reports back when its picture is there (what the hand on the timeline waits for)", () => MpvSeekLanding(mp4));

        EditChecks(dir, mp4);
    }

    /// <summary>Waits for work that comes back to the UI thread (WPF drawing) while letting the dispatcher run.</summary>
    private static T Wait<T>(Task<T> task, int seconds)
    {
        Pump(TimeSpan.FromSeconds(seconds), () => task.IsCompleted);
        if (!task.IsCompleted) throw new TimeoutException($"still running after {seconds} s");
        return task.GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------ video editor

    private static void Near(double got, double want, string what, double tol = 0.011)
    {
        if (Math.Abs(got - want) > tol) throw new Exception($"{what}: {got:0.###}, expected {want:0.###}");
    }

    /// <summary>Mean difference of two pictures (0..255) - how much two frames of a video differ.</summary>
    private static double FrameDiff(BitmapSource a, BitmapSource b)
    {
        var pa = PixelBuffer.FromBitmap(a);
        var pb = PixelBuffer.FromBitmap(b);
        if (pa.Width != pb.Width || pa.Height != pb.Height) return 255;
        long sum = 0;
        for (int i = 0; i < pa.Data.Length; i += 4)
            sum += Math.Abs(pa.Data[i] - pb.Data[i]) + Math.Abs(pa.Data[i + 1] - pb.Data[i + 1]) + Math.Abs(pa.Data[i + 2] - pb.Data[i + 2]);
        return sum / (pa.Data.Length / 4 * 3.0);
    }

    private static BitmapSource FrameAt(string file, double t, int height = 320) =>
        Task.Run(() => Ffmpeg.Frame(file, t, height, CancellationToken.None)).GetAwaiter().GetResult() ?? throw new Exception($"no frame at {t:0.00}s");

    private static string ProbeStreams(string file)
    {
        var probe = Path.Combine(Path.GetDirectoryName(Ffmpeg.Exe!)!, "ffprobe.exe");
        if (!File.Exists(probe)) probe = "ffprobe";
        var psi = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height,duration", "-of", "compact=p=0:nk=1", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var s = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return s.Replace("\r", "").Replace("\n", " | ");
    }

    private static void EditChecks(string dir, string mp4)
    {
        string portrait = Path.Combine(dir, "edit-portrait.mp4"), rotated = Path.Combine(dir, "edit-rotated.mp4"), banner = Path.Combine(dir, "edit-banner.mov");
        BannerInfo? info = null;

        Check("edit: test media (portrait clip, phone rotation, banner with transparency and sound)", () =>
        {
            RunFfmpeg($"-y -f lavfi -i testsrc2=size=720x1280:rate=30 -f lavfi -i sine=frequency=660:sample_rate=48000 -t 4 -c:v libx264 -g 30 -pix_fmt yuv420p -c:a aac -shortest \"{portrait}\"");
            RunFfmpeg($"-y -display_rotation 90 -i \"{mp4}\" -c copy \"{rotated}\"");
            // 4 s: a transparent 640×360 canvas, the banner itself a box in the middle, with its own sound
            RunFfmpeg($"-y -f lavfi -i \"color=c=red:s=640x360:r=30:d=4,format=rgba,geq=r=255:g=0:b=0:a='255*between(X,120,519)*between(Y,100,259)'\" " +
                      $"-f lavfi -i sine=frequency=880:sample_rate=48000:duration=4 -c:v qtrle -pix_fmt argb -c:a pcm_s16le -shortest \"{banner}\"");
            return ProbeStreams(banner);
        });

        Check("edit: clip probe (picture as shown, phone rotation, frame rate)", () =>
        {
            var a = ClipProbe.ProbeAsync(mp4).GetAwaiter().GetResult() ?? throw new Exception("no probe");
            if (a.Width != 1280 || a.Height != 720 || Math.Abs(a.Duration - 10) > 0.1 || Math.Abs(a.Fps - 30) > 0.01 || !a.HasAudio || !a.HasVideo)
                throw new Exception(ClipProbe.Describe(a));
            var r = ClipProbe.ProbeAsync(rotated).GetAwaiter().GetResult() ?? throw new Exception("no probe (rotated)");
            if (r.Width != 720 || r.Height != 1280) throw new Exception("rotation not applied: " + ClipProbe.Describe(r));
            return ClipProbe.Describe(a) + " · rotated: " + ClipProbe.Describe(r);
        });

        Check("edit: banner analysis (where the banner really is on its transparent canvas)", () =>
        {
            info = Task.Run(() => Banners.AnalyzeAsync(banner)).GetAwaiter().GetResult() ?? throw new Exception("not analysed");
            var b = info.Box;
            if (Math.Abs(info.Duration - 4) > 0.1 || b.X > 120 || b.X < 108 || b.Width < 400 || b.Width > 424 || b.Y > 100 || b.Y < 88 || b.Height < 160 || b.Height > 184 || !info.HasAudio)
                throw new Exception($"{info.Width}x{info.Height} {info.Duration:0.00}s box {b.X},{b.Y} {b.Width}x{b.Height} audio {info.HasAudio}");
            var again = Task.Run(() => Banners.AnalyzeAsync(banner)).GetAwaiter().GetResult();
            if (again?.Box != b) throw new Exception("cached analysis differs");
            // the live preview decodes on the fly: a frame in the middle, then playing on from there
            using var stream = new BannerStream(info, 300);
            var sw = Stopwatch.StartNew();
            byte[]? px = null;
            Pump(TimeSpan.FromSeconds(5), () => (px = stream.Frame(2.0, playing: false)) != null);
            long first = sw.ElapsedMilliseconds;
            if (px == null) throw new Exception("no preview frame");
            int alpha = px[(stream.Height / 2 * stream.Width + stream.Width / 2) * 4 + 3];
            int corner = px[3];
            if (stream.Width != 300 || alpha < 250) throw new Exception($"preview {stream.Width}x{stream.Height}, alpha {alpha}");
            int got = 0;
            for (int i = 1; i <= 30; i++)
            {
                byte[]? q = null;
                double t = 2.0 + i / BannerStream.Fps;
                Pump(TimeSpan.FromMilliseconds(500), () => (q = stream.Frame(t, playing: true)) != null && !ReferenceEquals(q, px));
                if (q != null && !ReferenceEquals(q, px)) { got++; px = q; }
            }
            if (got < 28) throw new Exception($"only {got} of 30 frames while playing on");
            return $"{info.Width}x{info.Height}, {info.Duration:0.00}s, box {b.X},{b.Y} {b.Width}x{b.Height}, sound; preview {stream.Width}x{stream.Height}: first frame in {first} ms, then {got}/30 in a row (corner alpha {corner})";
        });

        Check("edit: cutting a piece out keeps the time line's scale (the rest isn't stretched out)", () =>
        {
            var p = new EditProject(new EditClip { Path = "sample.mp4", Duration = 20, Width = 1280, Height = 720, Fps = 30 });
            var tl = new EditTimeline();
            tl.Measure(new Size(900, 200));
            tl.Arrange(new Rect(0, 0, 900, 200));
            tl.SetProject(p);
            var pps = typeof(EditTimeline).GetField("_pps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            double before = (double)pps.GetValue(tl)!;
            // the end trimmed off: the same scale, and the cut-off end still drawn (dimmed) after the edit
            p.Trim(0, false, 16);
            p.Changed();
            tl.Rebuild();
            var tail = typeof(EditTimeline).GetField("_ghostTail", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            if (tail.GetValue(tl) is not Piece ghost || Math.Abs(ghost.F1 - ghost.F0 - 4) > 0.01) throw new Exception("the trimmed-off end isn't shown");
            if (Math.Abs((double)pps.GetValue(tl)! - before) > 1e-9) throw new Exception("the scale changed after trimming the end");
            p.Trim(0, false, 20);
            p.Changed();
            tl.Rebuild();
            if (!p.Split(5) || !p.Split(12) || !p.Delete(1)) throw new Exception("split/delete");
            p.Changed();
            tl.Rebuild();
            double after = (double)pps.GetValue(tl)!;
            if (Math.Abs(after - before) > 1e-9) throw new Exception($"the scale changed after a cut: {before:0.00} → {after:0.00} px/s");
            p.AddClip(new EditClip { Path = "more.mp4", Duration = 30, Width = 1280, Height = 720, Fps = 30, Offset = 0 });
            p.Changed();
            tl.Rebuild();
            double longer = (double)pps.GetValue(tl)!;
            if (longer >= after) throw new Exception("a longer edit should fit the time line again");
            return $"{before:0.0} px/s kept after cutting 7 s of 20; a joined clip refits to {longer:0.0} px/s";
        });

        Check("edit: a caption running to the end is still there on the last frame", () =>
        {
            var p = new EditProject(new EditClip { Path = "sample.mp4", Duration = 10, Width = 1280, Height = 720, Fps = 30 });
            var toEnd = new TextOverlay { Text = "koniec", Start = 2, End = 10 };
            var earlier = new TextOverlay { Text = "wcześniej", Start = 1, End = 6 };
            p.Overlays.AddRange(new EditOverlay[] { toEnd, earlier });
            double end = p.FinalLength;
            if (!p.VisibleAt(toEnd, end)) throw new Exception("gone on the last frame");
            if (!p.VisibleAt(toEnd, end - 1.0 / 30)) throw new Exception("gone a frame before the end");
            if (p.VisibleAt(earlier, end) || p.VisibleAt(earlier, 6)) throw new Exception("an earlier caption shows past its end");
            if (p.VisibleAt(toEnd, 1.9)) throw new Exception("shows before its start");
            return $"end {end:0.00}s: kept to the last frame, earlier one gone at 6 s";
        });

        Check("edit: time lines (cuts, join, banner freeze starts 0.7 s in and ends 0.4 s before the banner)", () =>
        {
            var p = new EditProject(new EditClip { Path = mp4, Duration = 10, Width = 1280, Height = 720, Fps = 30 });
            if (!p.Split(3) || !p.Split(6) || !p.Delete(1)) throw new Exception("split/delete");
            Near(p.EditedLength, 7, "edited length");
            Near(p.SToE(4.5), 3, "a moment that was cut out lands where the video goes on");
            Near(p.SToE(8), 5, "S→E");
            var (seg, s) = p.EToS(3.5);
            if (seg != 1) throw new Exception($"E→S segment {seg}");
            Near(s, 6.5, "E→S");
            var bi = info ?? new BannerInfo(banner, 1, DateTime.Now, 4, 640, 360, 30, new Int32Rect(116, 96, 408, 168), true);
            var b = new BannerOverlay { Asset = bi, At = 8, Size = 0.6 };
            p.Overlays.Add(b);
            p.Changed();
            var h = p.Holds.Single();
            Near(h.E, 5, "freeze at");
            Near(h.Length, bi.Duration - 1.1, "freeze length = banner − 0.7 − 0.4", 0.04);
            var (f0, f1) = p.BannerSpan(b);
            Near(p.EToF(h.E) - f0, 0.7, "banner comes 0.7 s before the freeze");
            Near(f1 - (p.EToF(h.E) + h.Length), 0.4, "banner stays 0.4 s after the freeze");
            Near(f1 - f0, bi.Duration, "banner on screen for its whole length", 0.04);
            var (e, hold, into) = p.FToE(p.EToF(h.E) + 0.5);
            if (hold != 0) throw new Exception("inside the freeze");
            Near(e, 5, "frozen moment");
            Near(into, 0.5, "into the freeze");
            Near(p.FToE(p.EToF(h.E) + h.Length + 0.5).E, 5.5, "after the freeze");
            string sig = string.Join(" ", p.Pieces().Select(x => x.IsHold ? "[freeze]" : $"{x.SrcIn:0.#}-{x.SrcOut:0.#}"));
            if (sig != "0-3 6-8 [freeze] 8-10") throw new Exception(sig);
            b.At = 0.2; // near the start there is less moving video before the freeze
            p.Changed();
            Near(p.Holds[0].Lead, 0.2, "lead at the very start");
            Near(p.Holds[0].Length, bi.Duration - 0.6, "longer freeze instead", 0.04);
            p.AddClip(new EditClip { Path = portrait, Duration = 4, Width = 720, Height = 1280, Fps = 30 });
            Near(p.EditedLength, 11, "joined");
            var (k, t) = p.EToClip(9);
            if (k != 1) throw new Exception("second clip");
            Near(t, 2, "second clip's own second");
            p.Canvas.Format = CanvasFormat.Vertical;
            var (cw, ch) = p.CanvasSize();
            if (cw != 720 || ch != 1280) throw new Exception($"9:16 canvas of a 720p video {cw}x{ch}"); // not blown up beyond the source
            p.Canvas.ShortSide = 1080;
            var (cw2, ch2) = p.CanvasSize();
            if (cw2 != 1080 || ch2 != 1920) throw new Exception($"9:16 canvas at 1080p {cw2}x{ch2}");
            p.Canvas.ShortSide = 0;
            return $"{sig}; 9:16 = {cw}x{ch}";
        });

        Check("edit: two pieces out - copied bit for bit without B-frames, rendered frame-exactly with them", () =>
        {
            string plain = Path.Combine(dir, "edit-noB.mp4");
            RunFfmpeg($"-y -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 10 -c:v libx264 -bf 0 -g 30 -pix_fmt yuv420p -c:a aac -shortest \"{plain}\"");
            var info = new List<string>();
            foreach (var (src, copyExpected) in new[] { (plain, true), (mp4, false) })
            {
                var clip = ClipProbe.ProbeAsync(src).GetAwaiter().GetResult()!;
                var p = new EditProject(clip);
                foreach (var at in new[] { 2.0, 4.0, 6.0, 8.0 }) p.Split(at);
                p.Delete(3);
                p.Delete(1); // keeps 0-2, 4-6, 8-10
                var (kf, reordered) = Task.Run(() => Ffmpeg.KeyframeScan(src, CancellationToken.None)).GetAwaiter().GetResult();
                bool copy = EditExport.CanCopy(p, kf, reordered);
                if (copy != copyExpected) throw new Exception($"{Path.GetFileName(src)}: copy {copy}, B-frames {reordered}");
                var output = Path.Combine(dir, $"edit-cuts-{(copy ? "copy" : "render")}.mp4");
                if (File.Exists(output)) File.Delete(output);
                var (ok, err) = Wait(EditExport.SaveAsync(p, output, kf, reordered, null, CancellationToken.None), 90);
                if (!ok) throw new Exception(err);
                double d = ProbeDuration(output);
                if (Math.Abs(d - 6) > (copy ? 0.08 : 0.05)) throw new Exception($"{Path.GetFileName(output)}: duration {d:0.000}s, expected 6");
                var errors = DecodeErrors(output);
                if (errors.Length > 0) throw new Exception("not clean: " + errors.Split('\n')[0]);
                // after each cut the picture is the one right after the removed part, to the frame
                foreach (var (o, s) in new[] { (2.5, 4.5), (4.5, 8.5) })
                {
                    double diff = FrameDiff(FrameAt(output, o), FrameAt(src, s));
                    if (diff > (copy ? 0.5 : 4)) throw new Exception($"{Path.GetFileName(output)}: {o} s of the result is not {s} s of the source (diff {diff:0.0})");
                }
                info.Add($"{(copy ? "copy" : "render")} {d:0.000}s");
            }
            return string.Join(", ", info) + ", clean decode, frames match";
        });

        Check("edit: everything rendered (join, caption with emoji, emoji, banner freeze, 9:16 blurred frame)", () =>
        {
            if (info == null) throw new Exception("no banner");
            var a = ClipProbe.ProbeAsync(mp4).GetAwaiter().GetResult()!;
            var b2 = ClipProbe.ProbeAsync(portrait).GetAwaiter().GetResult()!;
            var p = new EditProject(a);
            p.AddClip(b2);
            p.Split(6);
            p.Split(7);
            p.Delete(1); // 6-7 of the first clip goes: 9 s + 4 s
            p.Canvas.Format = CanvasFormat.Vertical;
            p.Canvas.Background = CanvasBackground.Blur;
            p.Overlays.Add(new TextOverlay { Text = "Siema 😂\nLinearFrame", Start = 0, End = 14, X = 0.5, Y = 0.28, Size = 0.04 });
            p.Overlays.Add(new TextOverlay { Text = "Tło", Style = CaptionStyle.Fill, Color = Captions.Palette[3], Start = 0, End = 14, X = 0.5, Y = 0.9, Size = 0.035 });
            p.Overlays.Add(new EmojiOverlay { Unified = "1f525", Start = 1, End = 5, X = 0.82, Y = 0.12, Size = 0.1 });
            var bo = new BannerOverlay { Asset = info, At = 3, X = 0.5, Y = 0.66, Size = 0.9 };
            p.Overlays.Add(bo);
            p.Changed();
            var output = Path.Combine(dir, "edit-all.mp4");
            if (File.Exists(output)) File.Delete(output);
            var sw = Stopwatch.StartNew();
            var (ok, err) = Wait(EditExport.SaveAsync(p, output, null, true, null, CancellationToken.None), 180);
            long ms = sw.ElapsedMilliseconds;
            if (!ok) throw new Exception(err);
            double d = ProbeDuration(output), want = p.FinalLength;
            if (Math.Abs(d - want) > 0.15) throw new Exception($"duration {d:0.00}s, expected {want:0.00}s");
            string streams = ProbeStreams(output);
            if (!streams.Contains("720|1280") || !streams.Contains("audio")) throw new Exception(streams);
            var h = p.Holds.Single();
            double hf = p.EToF(h.E);
            var s1 = FrameAt(output, hf + 0.3);
            var s2 = FrameAt(output, hf + h.Length - 0.3);
            var m1 = FrameAt(output, hf - 0.6);
            var m2 = FrameAt(output, hf - 0.25);
            double still = FrameDiff(s1, s2), moving = FrameDiff(m1, m2);
            if (still > 1.5) throw new Exception($"the video moves during the freeze (diff {still:0.0})");
            if (moving < 1.5) throw new Exception($"the video stands still before the freeze (diff {moving:0.0})");
            // the banner: red where it is placed while it is on screen, gone after it
            var (f0, f1) = p.BannerSpan(bo);
            var on = PixelBuffer.FromBitmap(FrameAt(output, hf + 1));
            var off = PixelBuffer.FromBitmap(FrameAt(output, f1 + 0.4));
            var c1 = on.GetPixel(on.Width / 2, (int)(on.Height * 0.66));
            var c2 = off.GetPixel(off.Width / 2, (int)(off.Height * 0.66));
            if (c1.R < 180 || c1.G > 80) throw new Exception($"no banner during the freeze ({c1})");
            if (c2.R > 150 && c2.G < 60 && c2.B < 60) throw new Exception($"banner still there after its end ({c2})");
            Save(s1, Path.Combine(dir, "edit-all-freeze.png"));
            Save(FrameAt(output, 0.5, 640), Path.Combine(dir, "edit-all-start.png"));
            Save(FrameAt(output, want - 1, 640), Path.Combine(dir, "edit-all-joined.png"));
            return $"{d:0.00}s (wanted {want:0.00}) in {ms} ms with {EditExport.EncoderAsync().GetAwaiter().GetResult()}; freeze diff {still:0.0}, moving diff {moving:0.0}; {streams}";
        });

        var realBanner = Environment.GetEnvironmentVariable("LF_SELFTEST_BANNER");
        if (!string.IsNullOrEmpty(realBanner) && File.Exists(realBanner))
            Check("edit: a real banner (analysis, smooth preview decode, 9:16 render with the freeze)", () =>
            {
                var sw = Stopwatch.StartNew();
                var rb = Task.Run(() => Banners.AnalyzeAsync(realBanner)).GetAwaiter().GetResult() ?? throw new Exception("not analysed");
                long analysed = sw.ElapsedMilliseconds;
                using var stream = new BannerStream(rb, 640);
                sw.Restart();
                byte[]? px = null;
                Pump(TimeSpan.FromSeconds(8), () => (px = stream.Frame(1.0, playing: false)) != null);
                long first = sw.ElapsedMilliseconds;
                if (px == null) throw new Exception("no preview frame");
                sw.Restart();
                int got = 0;
                for (int i = 1; i <= 60; i++)
                {
                    byte[]? q = null;
                    double t = 1.0 + i / BannerStream.Fps;
                    Pump(TimeSpan.FromMilliseconds(400), () => (q = stream.Frame(t, playing: true)) != null && !ReferenceEquals(q, px));
                    if (q != null && !ReferenceEquals(q, px)) { got++; px = q; }
                }
                double fps = got / sw.Elapsed.TotalSeconds;
                var clip = ClipProbe.ProbeAsync(portrait).GetAwaiter().GetResult()!;
                var p = new EditProject(clip);
                p.Canvas.Format = CanvasFormat.Vertical;
                var bo = new BannerOverlay { Asset = rb, At = 1.5, X = 0.5, Y = 0.5, Size = 0.96 };
                p.Overlays.Add(bo);
                p.Changed();
                var output = Path.Combine(dir, "edit-real-banner.mp4");
                if (File.Exists(output)) File.Delete(output);
                sw.Restart();
                var (ok, err) = Wait(EditExport.SaveAsync(p, output, null, true, null, CancellationToken.None), 240);
                long rendered = sw.ElapsedMilliseconds;
                if (!ok) throw new Exception(err);
                double d = ProbeDuration(output), want = p.FinalLength;
                if (Math.Abs(d - want) > 0.15) throw new Exception($"duration {d:0.00}s, expected {want:0.00}s");
                var (f0, f1) = p.BannerSpan(bo);
                Save(FrameAt(output, f0 + 0.35, 640), Path.Combine(dir, "edit-real-banner-in.png"));
                Save(FrameAt(output, (f0 + f1) / 2, 640), Path.Combine(dir, "edit-real-banner-mid.png"));
                return $"box {rb.Box.X},{rb.Box.Y} {rb.Box.Width}x{rb.Box.Height} of {rb.Width}x{rb.Height} ({analysed} ms); preview 640 px: first frame {first} ms, {got}/60 frames at {fps:0} fps; render {d:0.00}s in {rendered} ms";
            });

        Check("captions: making a caption bigger scales it - its lines stay as typed", () =>
        {
            const double cw = 1920, ch = 1080;
            var t = new TextOverlay { Text = "Siema wszystkim, to jest dłuższy napis do sprawdzenia zawijania linii", Size = Captions.DefaultSize(cw, ch) };
            t.WrapEm = Captions.WrapEmFor(t.Size, cw, ch); // typed at the default size
            var at1 = Captions.Arrange(t, cw, ch);
            if (at1.Lines.Count < 2) throw new Exception($"a long caption should wrap at the frame width, got {at1.Lines.Count} line");
            var report = new List<string> { $"1x: {at1.Lines.Count} lines" };
            foreach (double k in new[] { 0.6, 1.5, 2.2, 3.0 })
            {
                t.Size = Captions.DefaultSize(cw, ch) * k;
                var l = Captions.Arrange(t, cw, ch);
                if (l.Lines.Count != at1.Lines.Count) throw new Exception($"at {k}x the caption has {l.Lines.Count} lines instead of {at1.Lines.Count}");
                double ratio = l.Bounds.Width / at1.Bounds.Width;
                if (Math.Abs(ratio - k) > 0.08 * k) throw new Exception($"at {k}x the caption is {ratio:0.00}x as wide");
                report.Add($"{k}x: {l.Lines.Count} lines, {ratio:0.00}x wide");
            }
            return string.Join(", ", report);
        });

        Check("captions: the text box shows its whole line", () =>
        {
            // its padding was taken twice and the 40 px row left 14 px for the 18 px line - the letters' bottoms cut off
            var cp = new CaptionPanel { Width = 740 };
            cp.TextEdit.Text = "Kliknij tutaj 👆 jgq Zażółć ąę";
            cp.Measure(new Size(740, double.PositiveInfinity));
            cp.Arrange(new Rect(cp.DesiredSize));
            cp.UpdateLayout();
            ScrollViewer? Host(DependencyObject d)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                    if (VisualTreeHelper.GetChild(d, i) is var c && (c as ScrollViewer ?? Host(c)) is { } sv) return sv;
                return null;
            }
            var host = Host(cp.TextEdit) ?? throw new Exception("no scroll viewer in the text box");
            if (host.ExtentHeight < 10) throw new Exception($"the line is only {host.ExtentHeight:0.#} px tall");
            if (host.ViewportHeight + 0.5 < host.ExtentHeight)
                throw new Exception($"the line is {host.ExtentHeight:0.#} px tall, the box shows {host.ViewportHeight:0.#} px of it");
            return $"line {host.ExtentHeight:0.#} px, shown {host.ViewportHeight:0.#} px, box {cp.TextEdit.ActualHeight:0.#} px";
        });

        Check("player: a click on the timeline jumps once, a drag follows the hand one picture at a time", () =>
        {
            var log = new List<string>();
            double now = 0; // the helper's clock, in ms
            ScrubSeeker New() => new((t, exact) => log.Add((exact ? "=" : "~") + t.ToString("0.##", CultureInfo.InvariantCulture)), () => now) { Span = 100 };
            string Asked() { var x = string.Join(" ", log); log.Clear(); return x; }
            void Expect(string want, string what) { var got = Asked(); if (got != want) throw new Exception($"{what}: asked for '{got}', should be '{want}'"); }

            var s = New();
            s.Move(10);
            s.Release(10);
            Expect("=10", "a click (one jump - it used to be two, playing the start twice)");
            now += 20; s.Landed();

            // a file that seeks quickly: nothing more is asked for while a picture is on its way (a mouse reports far
            // more spots than the player can show); when it is there, the newest spot comes next - its exact frame
            s.Move(20); s.Move(21); s.Move(22); s.Move(23);
            Expect("=20", "a drag, the first picture still on its way");
            now += 20; s.Landed();
            Expect("=23", "the picture landed: the newest spot, the ones passed meanwhile skipped");
            s.Move(24);
            Expect("", "a picture is on its way again");
            now += 20; s.Landed();
            Expect("=24", "the next spot");
            now += 20; s.Landed();
            Expect("", "the hand rests on an exact frame");
            s.Release(24);
            Expect("", "letting go where the exact frame already is");

            // a file where an exact frame takes long (key frames far apart): a hand that travels gets key frames at once,
            // a hand that slows down or rests the exact frame
            var slow = New();
            slow.Move(10);
            now += 300; slow.Landed();
            Expect("=10", "the press");
            slow.Move(30);
            Expect("~30", "a slow file, the hand travelling (a fifth of the timeline)");
            slow.Move(50);
            now += 15; slow.Landed();
            Expect("~50", "still travelling");
            now += 15; slow.Landed();
            Expect("=50", "the hand rests: the exact frame of where it is");
            slow.Move(50.5);
            now += 300; slow.Landed();
            Expect("=50.5", "the hand moves slowly: exact frames");
            now += 300; slow.Landed();
            slow.Move(80);
            slow.Release(80);
            Expect("~80 =80", "let go on a key frame's picture: the exact frame follows");

            // keys moved it meanwhile: letting go on the old spot still jumps
            var k = New();
            k.Jumped(-1);
            k.Move(5);
            k.Jumped(-1);
            k.Release(5);
            Expect("=5 =5", "after keys");

            // a picture that is never reported doesn't hold the next seek back for ever
            var lost = New();
            lost.Move(1);
            lost.Move(2);
            Expect("=1", "the press");
            Pump(TimeSpan.FromSeconds(3), () => log.Count > 0);
            Expect("=2", "a picture never reported");
            lost.Release(2);
            return "click =10; quick file =20 [=21 =22 skipped] =23 =24; slow file ~30 ~50 =50 =50.5; lost picture recovered";
        });

        Check("player: the control pill fits a phone video's narrow window", () =>
        {
            var report = new List<string>();
            foreach (int w in new[] { 420, 470, 640, 1280 })
            {
                var ov = new PlayerOverlay(null!);
                var root = (FrameworkElement)ov.Content;
                ov.Content = null;
                try
                {
                    ov.SetModes(audio: false, edit: false, fullscreen: false, maximized: false, canEdit: true);
                    ov.UpdatePlayback(51, 96, true, 70, false); // paused: no hide timer
                    root.Measure(new Size(w, 700));
                    root.Arrange(new Rect(0, 0, w, 700));
                    root.UpdateLayout();
                    var pill = ov.Controls.TransformToAncestor(root).TransformBounds(new Rect(ov.Controls.RenderSize));
                    if (pill.Left < 0 || pill.Right > w) throw new Exception($"{w} px: the pill runs {pill.Left:0}..{pill.Right:0}");
                    var shown = new List<(string, Rect)>();
                    foreach (var (name, el) in new (string, FrameworkElement)[]
                             {
                                 ("mute", ov.MuteBtn), ("volume", ov.VolumeSlider), ("back", ov.BackBtn), ("play", ov.PlayBtn), ("forward", ov.ForwardBtn),
                                 ("edit", ov.EditBtn), ("more", ov.MoreBtn), ("full", ov.FullBtn), ("scrubber", ov.Scrubber),
                             })
                    {
                        if (el.Visibility != Visibility.Visible) continue;
                        var r = el.TransformToAncestor(root).TransformBounds(new Rect(el.RenderSize));
                        if (r.Left < pill.Left || r.Right > pill.Right) throw new Exception($"{w} px: {name} sticks out of the pill");
                        foreach (var (other, o) in shown)
                            if (Rect.Intersect(r, o) is { IsEmpty: false } x && x.Width > 0.5 && x.Height > 0.5) throw new Exception($"{w} px: {name} lies over {other}");
                        shown.Add((name, r));
                    }
                    var play = shown.First(x => x.Item1 == "play").Item2;
                    if (Math.Abs(play.Left + play.Width / 2 - (pill.Left + pill.Width / 2)) > 1) throw new Exception($"{w} px: play isn't in the middle");
                    report.Add($"{w}: {shown.Count - 1} buttons");
                }
                finally { ov.Close(); }
            }
            return string.Join(", ", report);
        });

        Check("player: a new video's window has the film's shape to the pixel - no bars, on any screen", () =>
        {
            var videos = new (int W, int H)[]
            {
                (1920, 1080), (1080, 1920), (1280, 720), (720, 1280), (3840, 2160), (2160, 3840), (2560, 1440), (1440, 1080), (640, 480),
                (1920, 800), (1080, 2340), (608, 1080), (886, 1920), (464, 848), (1280, 534), (853, 480), (1023, 767), (1920, 1088),
                (406, 720), (4096, 1716), (320, 240), (1080, 1350), (1080, 1080), (1920, 1200), (960, 1920), (3440, 1440),
            };
            // work areas (the screen without its taskbar) and their scaling
            var screens = new (int W, int H, double Scale)[]
            {
                (1920, 1032, 1), (1920, 1040, 1), (2560, 1392, 1), (2560, 1392, 1.25), (3840, 2112, 1.5), (3840, 2064, 2), (1366, 728, 1), (1536, 824, 1.25),
            };
            int n = 0, forced = 0;
            foreach (var s in screens)
                foreach (var v in videos)
                {
                    int minW = (int)Math.Ceiling(420 * s.Scale), minH = (int)Math.Ceiling(250 * s.Scale);
                    var (w, h) = PlayerLayout.WindowFor(v.W, v.H, s.W, s.H, s.Scale, minW, minH);
                    n++;
                    string what = $"{v.W}x{v.H} on {s.W}x{s.H} at {s.Scale:0.##}: window {w}x{h}";
                    if (w < minW || h < minH) throw new Exception(what + " - smaller than a player window may be");
                    if (w > s.W || h > s.H) throw new Exception(what + " - bigger than the screen");
                    // a picture so narrow (or so flat) that the smallest window can't have its shape on this screen
                    bool noWay = (double)minW / v.W * v.H > s.H * 0.94 + 1 || (double)minH / v.H * v.W > s.W * 0.96 + 1;
                    var fit = PlayerLayout.Fitted(v.W, v.H, w, h);
                    if (fit != (w, h))
                    {
                        if (!noWay) throw new Exception(what + $" - the player fills only {fit.Width}x{fit.Height} of it (bars)");
                        forced++;
                        continue;
                    }
                    // about two thirds of the screen, unless the smallest window asks for more
                    if ((w > s.W * 0.66 + 2 && h > minH) || (h > s.H * 0.78 + 2 && w > minW)) throw new Exception(what + " - bigger than two thirds of the screen");
                    if (Math.Abs((double)w / h - (double)v.W / v.H) > 0.02 * v.W / v.H) throw new Exception(what + " - not the film's shape");
                }
            // the cases the user ran into: a 9:16 clip and a 16:9 clip on a Full HD screen with a 48 px taskbar
            var p = PlayerLayout.WindowFor(1080, 1920, 1920, 1032, 1, 420, 250);
            var l = PlayerLayout.WindowFor(1920, 1080, 1920, 1032, 1, 420, 250);
            return $"{n} windows ({videos.Length} picture sizes on {screens.Length} screens), each filled by its picture; {forced} where the smallest window leaves no other way; " +
                   $"9:16 → {p.Width}x{p.Height}, 16:9 → {l.Width}x{l.Height} on Full HD";
        });

        Check("player: the top shade has the window's round corners, the window buttons sit on the title's line", () =>
        {
            var ov = new PlayerOverlay(null!);
            var root = (FrameworkElement)ov.Content;
            ov.Content = null;
            try
            {
                const int W = 960, H = 540;
                PixelBuffer Shown(bool maximized)
                {
                    ov.SetTitle("film.mp4");
                    ov.SetModes(audio: false, edit: false, fullscreen: false, maximized: maximized, canEdit: true);
                    ov.UpdatePlayback(51, 96, true, 70, false); // paused: no hide timer
                    root.Measure(new Size(W, H));
                    root.Arrange(new Rect(0, 0, W, H));
                    root.UpdateLayout();
                    return PixelBuffer.FromBitmap(RenderVisual(root, W, H)); // (nothing behind it: what the layer itself draws)
                }
                bool rounds = Environment.OSVersion.Version.Build >= 22000; // Windows 11 rounds its windows
                var normal = Shown(false);
                int corner = Math.Max(normal.GetPixel(0, 0).A, normal.GetPixel(W - 1, 0).A), inside = normal.GetPixel(W / 2, 1).A, near = normal.GetPixel(14, 1).A;
                if (inside < 120 || near < 120) throw new Exception($"the shade is missing (alpha {inside} in the middle, {near} next to the corner)");
                if (rounds && corner > 8) throw new Exception($"the shade reaches into the window's round corner (alpha {corner}) - it would stick out as a dark square");
                if (!rounds && corner < 120) throw new Exception("square windows (Windows 10) need the shade in the corner too");
                // the curve itself: 3 px in and 3 px down is still outside a 9 px round corner, 8/8 is inside
                if (rounds && (normal.GetPixel(2, 2).A > 40 || normal.GetPixel(8, 8).A < 100)) throw new Exception("the shade's corner isn't round the way the window's is");
                var max = Shown(true);
                if (max.GetPixel(0, 0).A < 120 || max.GetPixel(W - 1, 0).A < 120) throw new Exception("a maximised window has square corners: the shade must fill them");
                Shown(false);

                Rect Box(FrameworkElement e) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));
                var close = Box(ov.CloseBtn);
                if (close.Top > 0.5 || close.Right < W - 0.5) throw new Exception($"the close button must still reach the corner (it ends at {close.Right:0},{close.Top:0})");
                var icon = Box((FrameworkElement)ov.CloseBtn.Content);
                double iconY = icon.Top + icon.Height / 2, title = Box(ov.TitleText).Top + ov.TitleText.ActualHeight / 2;
                if (iconY < 20 || iconY > 26) throw new Exception($"the window buttons' pictures are at {iconY:0.#} px from the top");
                if (Math.Abs(iconY - title) > 2.5) throw new Exception($"window buttons at {iconY:0.#} px, the title at {title:0.#} px - not on one line");
                if (W - icon.Right < 14 || W - icon.Right > 24) throw new Exception($"the close picture is {W - icon.Right:0.#} px from the right edge");
                return $"corner alpha {corner}, shade {inside}; buttons {iconY:0.#} px down (title {title:0.#}), close picture {W - icon.Right:0.#} px from the edge, button reaches the corner";
            }
            finally { ov.Close(); }
        });

        Check("windows: the window buttons sit on the line of the toolbar they are in (viewer, photo editor)", () =>
        {
            static T? Find<T>(DependencyObject d) where T : DependencyObject
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is T t) return t;
                    if (Find<T>(c) is { } deeper) return deeper;
                }
                return null;
            }
            var report = new List<string>();
            void Look(string name, Window win, FrameworkElement beside, double w, double h)
            {
                var root = (FrameworkElement)win.Content;
                win.Content = null;
                try
                {
                    root.Measure(new Size(w, h));
                    root.Arrange(new Rect(0, 0, w, h));
                    root.UpdateLayout();
                    var buttons = Find<CaptionButtons>(root) ?? throw new Exception($"{name}: no window buttons");
                    Rect Box(FrameworkElement e) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));
                    var close = (Button)buttons.Children[^1];
                    var all = Box(close);
                    if (all.Top > 0.5 || all.Right < w - 0.5) throw new Exception($"{name}: the close button must still reach the corner (it ends at {all.Right:0},{all.Top:0})");
                    var icon = Box((FrameworkElement)close.Content);
                    var other = Box(beside);
                    double y = icon.Top + icon.Height / 2, line = other.Top + other.Height / 2;
                    if (Math.Abs(y - line) > 1.5) throw new Exception($"{name}: window buttons at {y:0.#} px, the toolbar's buttons at {line:0.#} px");
                    if (w - icon.Right < 14 || w - icon.Right > 24) throw new Exception($"{name}: the close picture is {w - icon.Right:0.#} px from the right edge");
                    report.Add($"{name}: {y:0.#} px (toolbar {line:0.#} px)");
                }
                finally { win.Close(); }
            }
            var viewer = new ImageViewerWindow();
            Look("viewer", viewer, viewer.CopyBtn, 1100, 760);
            var doc = new MarkupDocument(MakeFakeUi(400, 240).ToBitmap());
            var editor = new EditorWindow(doc);
            Look("photo editor", editor, editor.DoneBtn, 1180, 760);
            return string.Join(", ", report);
        });

        Check("edit: captions drawn like TikTok (outline, background, plain)", () =>
        {
            var dv = new DrawingVisual();
            double W = 1080, H = 640;
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x6E, 0xA5), Color.FromRgb(0xC0, 0x6C, 0x84), 20), null, new Rect(0, 0, W, H));
                var items = new[]
                {
                    new TextOverlay { Text = "Biały napis z czarnym obrysem 😂", Style = CaptionStyle.Stroke, Size = 0.07 },
                    new TextOverlay { Text = "Tło za tekstem\nw dwóch liniach", Style = CaptionStyle.Fill, Color = Colors.White, Size = 0.07 },
                    new TextOverlay { Text = "Kolorowe tło", Style = CaptionStyle.Fill, Color = Captions.Palette[3], Size = 0.07 },
                    new TextOverlay { Text = "Bez niczego 🔥", Style = CaptionStyle.None, Size = 0.07 },
                };
                double y = 90;
                foreach (var t in items)
                {
                    var l = Captions.Arrange(t, W, H);
                    if (l.Bounds.Width < 50 || l.Bounds.Height < 20) throw new Exception($"empty layout for '{t.Text}'");
                    Captions.Draw(dc, l, new Point(W / 2, y + l.Bounds.Height / 2));
                    y += l.Bounds.Height + 30;
                }
            }
            Save(RenderVisual(dv, (int)W, (int)H), Path.Combine(dir, "edit-captions.png"));
            var face = Captions.Face.FontFamily.Source;
            if (!face.Contains("TikTok")) throw new Exception("caption font is not TikTok Sans: " + face);
            return face;
        });

        Check("editor live: under the video, cut + caption + banner + 9:16, the freeze plays on time", () => EditorLive(dir, mp4, banner));
    }

    private static string EditorLive(string dir, string mp4, string banner)
    {
        var s = App.Instance!.Settings;
        bool wasMuted = s.PlayerMuted;
        string? oldBanner = s.BannerPath;
        s.PlayerMuted = true;
        s.BannerPath = banner;
        var pw = new PlayerWindow();
        void Shot(string name)
        {
            Pump(TimeSpan.FromMilliseconds(450), () => false);
            Native.GetWindowRect(pw.Handle, out var wr);
            Save(ScreenCapturer.CaptureBitmap(new Int32Rect(wr.Left, wr.Top, wr.Width, wr.Height)), Path.Combine(dir, name));
        }
        try
        {
            pw.OpenFile(mp4, edit: true);
            Pump(TimeSpan.FromSeconds(12), () => pw.Editor.IsActive && pw.Editor.ActualHeight > 50);
            if (!pw.Editor.IsActive) throw new Exception("the editor did not open");
            var ed = pw.Editor;
            Pump(TimeSpan.FromMilliseconds(800), () => false);
            Shot("editor-live-1-open.png");
            ed.TestSeek(2);
            ed.TestSplit();
            ed.TestSeek(4);
            ed.TestSplit();
            ed.TestDeleteSegment(1);
            if (ed.Project.Segments.Count != 2) throw new Exception($"{ed.Project.Segments.Count} pieces after a cut");
            Near(ed.Project.EditedLength, 8, "after cutting 2-4 out");
            ed.TestSeek(1);
            var adding = ed.TestAddBanner();
            Pump(TimeSpan.FromSeconds(10), () => adding.IsCompleted);
            var bo = ed.Project.Overlays.OfType<BannerOverlay>().SingleOrDefault() ?? throw new Exception("no banner added");
            Shot("editor-live-2-banner.png");
            ed.TestAddText("Test 😂 LinearFrame");
            ed.TestSelect(ed.Project.Overlays.OfType<TextOverlay>().First());
            Shot("editor-live-3-caption.png");
            ed.TestDone();
            ed.TestOpenEmoji();
            Pump(TimeSpan.FromSeconds(30), () => ed.TestEmojiReady);
            Shot("editor-live-4-emoji.png");
            ed.TestPickEmoji("1f525");
            Pump(TimeSpan.FromSeconds(5), () => AppleEmoji.Image("1f525") != null);
            Shot("editor-live-5-emoji-added.png");
            ed.TestDone();
            ed.TestFormat(CanvasFormat.Vertical);
            Pump(TimeSpan.FromMilliseconds(600), () => false);
            Shot("editor-live-6-format.png");
            ed.TestDone();
            var p = ed.Project;
            var hold = p.Holds.Single();
            double hf = p.EToF(hold.E);
            var (b0, b1) = p.BannerSpan(bo);
            Near(b0, 1, "the banner starts at the playhead", 0.05);

            ed.TestSeek(0.5);
            ed.TogglePlay();
            var clock = Stopwatch.StartNew();
            double freezeSeen = -1, freezeLeft = -1;
            bool pausedInFreeze = false, shot = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(14) && ed.IsPlaying)
            {
                Pump(TimeSpan.FromMilliseconds(20), () => false);
                if (ed.InFreeze && freezeSeen < 0) freezeSeen = clock.Elapsed.TotalSeconds;
                if (ed.InFreeze && pw.IsPaused) pausedInFreeze = true;
                if (ed.InFreeze && !shot && clock.Elapsed.TotalSeconds - freezeSeen > 1.0)
                {
                    shot = true;
                    Native.GetWindowRect(pw.Handle, out var wr);
                    Save(ScreenCapturer.CaptureBitmap(new Int32Rect(wr.Left, wr.Top, wr.Width, wr.Height)), Path.Combine(dir, "editor-live-7-freeze.png"));
                }
                if (freezeSeen >= 0 && freezeLeft < 0 && !ed.InFreeze) freezeLeft = clock.Elapsed.TotalSeconds;
                if (freezeLeft > 0 && clock.Elapsed.TotalSeconds - freezeLeft > 1) break;
            }
            if (ed.IsPlaying) ed.TogglePlay();
            if (freezeSeen < 0) throw new Exception("the preview never froze");
            if (!pausedInFreeze) throw new Exception("the video kept playing during the freeze");
            if (freezeLeft < 0) throw new Exception("the freeze never ended");
            double len = freezeLeft - freezeSeen;
            if (Math.Abs(len - hold.Length) > 0.3) throw new Exception($"freeze lasted {len:0.00}s, expected {hold.Length:0.00}s");
            double reachedAt = freezeSeen, expectedAt = hf - 0.5;
            Near(reachedAt, expectedAt, "freeze came at", 0.35);
            ed.TestDiscard();
            Pump(TimeSpan.FromSeconds(2), () => !pw.Editor.IsActive);
            if (pw.Editor.IsActive) throw new Exception("the editor did not close");
            return $"froze after {freezeSeen:0.00}s (expected {expectedAt:0.00}) for {len:0.00}s (expected {hold.Length:0.00}); panel {ed.ActualHeight:0} px";
        }
        finally
        {
            pw.Close();
            s.PlayerMuted = wasMuted;
            s.BannerPath = oldBanner;
            s.Save();
        }
    }

    private static void RunFfmpeg(string args)
    {
        var psi = new ProcessStartInfo(Ffmpeg.Exe!, "-hide_banner -loglevel error " + args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception(err);
    }

    private static string DecodeErrors(string file)
    {
        var psi = new ProcessStartInfo(Ffmpeg.Exe!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-i", file, "-f", "null", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd().Trim();
        p.WaitForExit();
        return err;
    }

    private static double ProbeDuration(string file)
    {
        var probe = Path.Combine(Path.GetDirectoryName(Ffmpeg.Exe!)!, "ffprobe.exe");
        if (!File.Exists(probe)) probe = "ffprobe";
        var psi = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var s = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return double.Parse(s, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The timeline asks for one seek at a time and waits for the player to say the picture is there
    /// (<see cref="ScrubSeeker"/>). That report must come for every seek, exact or to a key frame, also while paused.
    /// No window and no sound: mpv decodes into nothing.
    /// </summary>
    private static unsafe string MpvSeekLanding(string file)
    {
        if (!MpvNative.TryLoad(out var err)) throw new Exception(err);
        var ctx = MpvNative.mpv_create();
        void Opt(string n, string v)
        {
            var nb = Encoding.UTF8.GetBytes(n + "\0");
            var vb = Encoding.UTF8.GetBytes(v + "\0");
            fixed (byte* a = nb) fixed (byte* b = vb) MpvNative.mpv_set_option_string(ctx, a, b);
        }
        Opt("vo", "null");
        Opt("ao", "null");
        Opt("idle", "yes");
        Opt("keep-open", "yes");
        Opt("pause", "yes");
        Opt("hr-seek-framedrop", "yes");
        if (MpvNative.mpv_initialize(ctx) < 0) throw new Exception("init failed");
        try
        {
            void Cmd(bool wait, params string[] args)
            {
                var ptrs = args.Select(Marshal.StringToCoTaskMemUTF8).Append(IntPtr.Zero).ToArray();
                fixed (IntPtr* p = ptrs)
                {
                    if (wait) MpvNative.mpv_command(ctx, p);
                    else MpvNative.mpv_command_async(ctx, 0, p);
                }
                foreach (var q in ptrs) if (q != IntPtr.Zero) Marshal.FreeCoTaskMem(q);
            }
            bool WaitFor(MpvEventId id, int ms)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    var ev = MpvNative.mpv_wait_event(ctx, 0.25);
                    if (ev->EventId == id) return true;
                    if (ev->EventId == MpvEventId.EndFile) throw new Exception("the file ended");
                }
                return false;
            }
            double Pos()
            {
                double v = -1;
                var n = Encoding.UTF8.GetBytes("time-pos\0");
                fixed (byte* p = n) MpvNative.mpv_get_property(ctx, p, MpvFormat.Double, &v);
                return v;
            }
            Cmd(true, "loadfile", file);
            if (!WaitFor(MpvEventId.FileLoaded, 8000)) throw new Exception("the file did not load");
            if (!WaitFor(MpvEventId.PlaybackRestart, 8000)) throw new Exception("the first picture was never reported");
            var exact = new List<double>();
            var key = new List<double>();
            var rnd = new Random(3);
            for (int i = 0; i < 24; i++)
            {
                double t = Math.Round(0.2 + rnd.NextDouble() * 9.3, 3);
                bool ex = i % 2 == 0;
                var sw = Stopwatch.StartNew();
                Cmd(false, "seek", t.ToString("0.###", CultureInfo.InvariantCulture), ex ? "absolute+exact" : "absolute+keyframes");
                if (!WaitFor(MpvEventId.PlaybackRestart, 4000)) throw new Exception($"seek {i + 1} ({(ex ? "exact" : "key frame")}) to {t:0.000} s was never reported");
                (ex ? exact : key).Add(sw.Elapsed.TotalMilliseconds);
                double at = Pos();
                // exact: the frame of that moment (30 a second); a key frame: one of the neighbours (one a second here)
                if (Math.Abs(at - t) > (ex ? 0.04 : 1.1)) throw new Exception($"seek {i + 1} ({(ex ? "exact" : "key frame")}) to {t:0.000} s ended at {at:0.000} s");
            }
            return $"12 exact seeks: {exact.Average():0} ms each (slowest {exact.Max():0}), 12 key-frame seeks: {key.Average():0} ms each (slowest {key.Max():0}) - every one reported";
        }
        finally { MpvNative.mpv_terminate_destroy(ctx); }
    }

    private static unsafe string MpvHeadless(string file)
    {
        if (!MpvNative.TryLoad(out var err)) throw new Exception(err);
        ulong ver = MpvNative.mpv_client_api_version();
        var ctx = MpvNative.mpv_create();
        void Opt(string n, string v)
        {
            var nb = Encoding.UTF8.GetBytes(n + "\0");
            var vb = Encoding.UTF8.GetBytes(v + "\0");
            fixed (byte* a = nb) fixed (byte* b = vb) MpvNative.mpv_set_option_string(ctx, a, b);
        }
        Opt("vo", "null");
        Opt("ao", "null");
        Opt("idle", "yes");
        Opt("keep-open", "yes");
        Opt("pause", "yes");
        if (MpvNative.mpv_initialize(ctx) < 0) throw new Exception("init failed");
        try
        {
            var args = new[] { "loadfile", file };
            var ptrs = args.Select(Marshal.StringToCoTaskMemUTF8).Append(IntPtr.Zero).ToArray();
            fixed (IntPtr* p = ptrs) MpvNative.mpv_command(ctx, p);
            foreach (var q in ptrs) if (q != IntPtr.Zero) Marshal.FreeCoTaskMem(q);
            var sw = Stopwatch.StartNew();
            bool loaded = false;
            while (sw.ElapsedMilliseconds < 8000)
            {
                var ev = MpvNative.mpv_wait_event(ctx, 0.5);
                if (ev->EventId == MpvEventId.FileLoaded) { loaded = true; break; }
                if (ev->EventId == MpvEventId.EndFile) throw new Exception("end-file before load");
            }
            if (!loaded) throw new Exception("timeout");
            double dur;
            var name = Encoding.UTF8.GetBytes("duration\0");
            fixed (byte* n = name) MpvNative.mpv_get_property(ctx, n, MpvFormat.Double, &dur);
            IntPtr s;
            var tl = Encoding.UTF8.GetBytes("track-list\0");
            fixed (byte* n = tl) s = MpvNative.mpv_get_property_string(ctx, n);
            string tracks = Marshal.PtrToStringUTF8(s) ?? "";
            MpvNative.mpv_free(s);
            int vids = tracks.Split("\"type\":\"video\"").Length - 1, auds = tracks.Split("\"type\":\"audio\"").Length - 1;
            return $"api {ver >> 16}.{ver & 0xFFFF}, duration {dur:0.00}s, {vids} video / {auds} audio tracks";
        }
        finally { MpvNative.mpv_terminate_destroy(ctx); }
    }

    // ------------------------------------------------------------------ UI snapshots

    private static void RenderUi(string dir)
    {
        Directory.CreateDirectory(dir);
        void Snap(FrameworkElement el, int w, int h, string file, Brush bg)
        {
            el.Measure(new Size(w, h));
            el.Arrange(new Rect(0, 0, w, h));
            el.UpdateLayout();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Render(el);
            rtb.Freeze();
            Save(rtb, Path.Combine(dir, file));
        }

        // the question box: over the (dimmed) video editor, over a light and a dark app window, and a one-button message
        foreach (var (title, text, action, cancel, icon, danger, dark, bg, file) in new (string, string, string, string?, string, bool, bool, Color, string)[]
                 {
                     (L.T("Edit.Discard"), L.T("Edit.DiscardText"), L.T("Common.Discard"), L.T("Common.Cancel"), "trash", true, true, Color.FromRgb(0x0C, 0x0C, 0x0D), "alert-video.png"),
                     (L.T("Editor.Unsaved"), L.T("Editor.UnsavedText"), L.T("Common.Discard"), L.T("Common.Cancel"), "trash", true, false, Color.FromRgb(0xAC, 0xAC, 0xAF), "alert-light.png"),
                     (L.T("Editor.Unsaved"), L.T("Editor.UnsavedText"), L.T("Common.Discard"), L.T("Common.Cancel"), "trash", true, true, Color.FromRgb(0x10, 0x10, 0x11), "alert-dark.png"),
                     (L.T("Edit.SaveFailed"), "ffmpeg: Conversion failed (exit code 1)", L.T("Common.Ok"), null, "warning", false, true, Color.FromRgb(0x0C, 0x0C, 0x0D), "alert-message.png"),
                 })
        {
            var card = AlertWindow.Preview(title, text, action, cancel, icon, danger, dark);
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Snap(card, (int)Math.Ceiling(card.DesiredSize.Width), (int)Math.Ceiling(card.DesiredSize.Height), file, new SolidColorBrush(bg));
        }
        if (Environment.GetEnvironmentVariable("LF_RENDER_ONLY") == "alert") return;

        // main window pages
        var main = new MainWindow();
        var mainRoot = (FrameworkElement)main.Content;
        main.Content = null;
        Snap(mainRoot, 1000, 660, "main-screens.png", (Brush)Application.Current.FindResource("Brush.ContentBg"));
        main.Navigate("settings");
        Snap(mainRoot, 1000, 900, "main-settings.png", (Brush)Application.Current.FindResource("Brush.ContentBg"));
        main.Navigate("player");
        Snap(mainRoot, 1000, 660, "main-player.png", (Brush)Application.Current.FindResource("Brush.ContentBg"));
        main.Close();

        // editor with sample markup
        var ui = MakeFakeUi(720, 420);
        var doc = new MarkupDocument(ui.ToBitmap());
        double u = doc.BaseUnit * 1.8;
        doc.Items.Add(new LineAnnotation { A = new Point(120, 360), B = new Point(300, 200), Arrow = true, Width = u, Color = Color.FromRgb(0xFF, 0x3B, 0x30) });
        doc.Items.Add(new ShapeAnnotation { Rect = new Rect(290, 40, 190, 70), Kind = ShapeKind.Rect, Width = u * 0.7, Color = Color.FromRgb(0x34, 0xC7, 0x59) });
        doc.Items.Add(new CounterAnnotation { Center = new Point(500, 40), Number = 1, Width = u, Color = Color.FromRgb(0xFF, 0x3B, 0x30) });
        var ed = new EditorWindow(doc);
        ed.View.SetZoom(1.0);
        var edRoot = (FrameworkElement)ed.Content;
        ed.Content = null;
        Snap(edRoot, 1180, 640, "editor.png", (Brush)Application.Current.FindResource("Brush.CanvasBg"));
        ed.Close();

        // the picture editor with a caption chosen: the caption tool (the video editor's) floating over the picture
        {
            var capDoc = new MarkupDocument(ui.ToBitmap());
            var cap = new CaptionAnnotation { Center = new Point(360, 300), Text = "Kliknij tutaj 👆", Style = CaptionStyle.Fill, Color = Captions.Palette[4], Size = 0.075, CanvasWidth = capDoc.Width, CanvasHeight = capDoc.Height };
            cap.WrapEm = Captions.WrapEmFor(cap.Size, capDoc.Width, capDoc.Height);
            capDoc.Items.Add(cap);
            var capEd = new EditorWindow(capDoc);
            capEd.View.SetZoom(1.0);
            capEd.View.Select(cap);
            var capRoot = (FrameworkElement)capEd.Content;
            capEd.Content = null;
            Snap(capRoot, 1180, 640, "editor-caption.png", (Brush)Application.Current.FindResource("Brush.CanvasBg"));
            capEd.Close();
        }
        if (Environment.GetEnvironmentVariable("LF_RENDER_ONLY") == "editor") return;

        // player overlay controls on a gradient "video"
        var ov = new PlayerOverlay(null!);
        var ovRoot = (FrameworkElement)ov.Content;
        ov.Content = null;
        var gradient = new LinearGradientBrush(Color.FromRgb(0x20, 0x50, 0x80), Color.FromRgb(0x80, 0x30, 0x50), 30);
        Snap(ovRoot, 960, 540, "player-overlay.png", gradient);
        // a phone video (9:16) opens 420 wide: the pill fits it
        foreach (var (w, h, file) in new[] { (420, 746, "player-overlay-phone.png"), (480, 760, "player-overlay-480.png") })
        {
            var ovN = new PlayerOverlay(null!);
            var ovNRoot = (FrameworkElement)ovN.Content;
            ovN.Content = null;
            ovN.SetModes(audio: false, edit: false, fullscreen: false, maximized: false, canEdit: true);
            ovN.SetTitle("Don't mind the ones that don't matter.mp4");
            ovN.UpdatePlayback(51, 96, true, 70, false);
            Snap(ovNRoot, w, h, file, gradient);
            ovN.Close();
        }

        // video editor panel in its four states (an edit drawn in - no player behind it)
        {
            var still = new RenderTargetBitmap(160, 90, 96, 96, PixelFormats.Pbgra32);
            var sv = new DrawingVisual();
            using (var dc = sv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2E, 0x5E, 0x9E), Color.FromRgb(0xD0, 0x7A, 0x4C), 25), null, new Rect(0, 0, 160, 90));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xE0, 0x80)), null, new Point(110, 40), 22, 22);
            }
            still.Render(sv);
            still.Freeze();
            var proj = new EditProject(new EditClip { Path = "sample.mp4", Duration = 24, Width = 1920, Height = 1080, Fps = 30 });
            proj.Split(8);
            proj.Split(11);
            proj.Delete(1);
            var caption = new TextOverlay { Text = "Siema 😂", Start = 1, End = 12, Size = 0.05 };
            var fire = new EmojiOverlay { Unified = "1f525", Start = 3, End = 9, Size = 0.12 };
            var bannerItem = new BannerOverlay { Asset = new BannerInfo(@"C:\banner\Brand banner.mov", 1, DateTime.Now, 4, 1920, 1080, 60, new Int32Rect(40, 176, 1844, 724), true), At = 15 };
            proj.Overlays.AddRange(new EditOverlay[] { caption, fire, bannerItem });
            var song = FakeSong();
            foreach (var (sel, format, file) in new (EditOverlay?, bool, string)[]
                     { (null, false, "editor-video-tools.png"), (caption, false, "editor-video-caption.png"), (bannerItem, false, "editor-video-banner.png"), (null, true, "editor-video-format.png") })
            {
                var vid = new VideoEditor();
                vid.TestStatic(proj, sel, format, 6.4);
                for (int k = 0; k < 16; k++) vid.Timeline.AddThumbnail(0, k * 1.5 + 0.75, still);
                vid.Timeline.SetWaveform(0, song);
                vid.Measure(new Size(960, double.PositiveInfinity));
                Snap(vid, 960, (int)Math.Ceiling(vid.DesiredSize.Height), file, Brushes.Black);
            }

            // the start and the end trimmed off: still on the strip, dimmed
            {
                var trimmed = proj.Clone();
                trimmed.Trim(0, true, 1.5);
                trimmed.Trim(trimmed.Segments.Count - 1, false, 20.5);
                trimmed.Changed();
                var vid = new VideoEditor();
                vid.TestStatic(trimmed, null, false, 4);
                for (int k = 0; k < 16; k++) vid.Timeline.AddThumbnail(0, k * 1.5 + 0.75, still);
                vid.Timeline.SetWaveform(0, song);
                vid.Measure(new Size(960, double.PositiveInfinity));
                Snap(vid, 960, (int)Math.Ceiling(vid.DesiredSize.Height), "editor-video-trimmed.png", Brushes.Black);
            }

            // the emoji gallery inside the caption tool: categories, and a search
            var picker = new EmojiPicker();
            Wait(picker.LoadAsync().ContinueWith(_ => 0), 60);
            Snap(picker, 404, 396, "editor-video-emoji.png", Brushes.Black);
            picker.TestSearch("serce");
            Snap(picker, 404, 396, "editor-video-emoji-search.png", Brushes.Black);
        }
        ov.Close();

        // audio: cover, title and the waveform with the control pill on top - at the default size and a big one
        float[] FakeSong()
        {
            var r = new Random(7);
            return Enumerable.Range(0, 1200).Select(i =>
            {
                double env = 0.35 + 0.65 * Math.Pow(Math.Sin(i / 1200.0 * Math.PI), 0.6) * (0.75 + 0.25 * Math.Sin(i / 37.0));
                return (float)Math.Clamp(env * (0.55 + 0.45 * r.NextDouble()), 0, 1);
            }).ToArray();
        }
        foreach (var (w, h, file) in new[] { (680, 500, "player-audio.png"), (1280, 800, "player-audio-big.png"), (440, 270, "player-audio-small.png") })
        {
            var pw = new PlayerWindow();
            var pwRoot = (Grid)pw.Content;
            pw.Content = null;
            pw.AudioView.Visibility = Visibility.Visible;
            pw.AudioTitle.Text = "Kołysanka dla Księżyca";
            pw.AudioArtist.Text = "Nocne Niebo";
            pw.Wave.SetPeaks(FakeSong());
            pw.Wave.BeginAnimation(WaveformView.RevealProperty, null);
            pw.Wave.Reveal = 1;
            pw.Wave.Duration = 214;
            pw.Wave.Position = 78;
            pw.Wave.Hover = 0.62;
            var ovA = new PlayerOverlay(null!);
            var ovARoot = (FrameworkElement)ovA.Content;
            ovA.Content = null;
            ovA.SetModes(audio: true, edit: false, fullscreen: false, maximized: false, canEdit: true);
            ovA.UpdatePlayback(78, 214, false, 70, false);
            var stack = new Grid();
            stack.Children.Add(pwRoot);
            stack.Children.Add(ovARoot);
            stack.Measure(new Size(w, h));
            stack.Arrange(new Rect(0, 0, w, h));
            pw.UpdateAudioLayout(force: true);
            Snap(stack, w, h, file, Brushes.Black);
            ovA.Close();
            pw.Close();
        }

        // picture viewer with a "photo"
        var viewer = new ImageViewerWindow();
        var vRoot = (FrameworkElement)viewer.Content;
        viewer.Content = null;
        var photoPx = MakeFakeUi(1600, 1000);
        viewer.TitleText.Text = "IMG_2041.jpg";
        viewer.InfoText.Text = "4032 × 3024";
        viewer.NavButtons.Visibility = Visibility.Visible;
        viewer.ZoomText.Text = "38%";
        viewer.View.SetPicture(photoPx.ToBitmap(), 1600, 1000, false);
        Snap(vRoot, 1100, 760, "viewer.png", (Brush)Application.Current.FindResource("Brush.CanvasBg"));
        viewer.Close();

        // trim panel
        var trim = new TrimBar { Height = 58, Width = 900 };
        trim.Reset(120);
        trim.SetRange(18, 74);
        trim.SetPosition(40);
        trim.SetThumbnailCount(10);
        var frame = MakeFakeUi(160, 90).ToBitmap();
        for (int i = 0; i < 10; i++) trim.SetThumbnail(i, frame);
        var panel = new Border { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)), Padding = new Thickness(20), Child = trim };
        Snap(panel, 940, 100, "trimbar.png", Brushes.Black);

        // floating thumbnail after a capture
        var toastMon = new MonitorInfo(IntPtr.Zero, new Int32Rect(0, 0, 1920, 1080), new Int32Rect(0, 0, 1920, 1040), 1.0, true);
        var toastWin = (Window)Activator.CreateInstance(typeof(CaptureToast), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
            new object?[] { MakeFakeUi(640, 360).ToBitmap(), null, toastMon, "HD 4×", true }, null)!;
        var toastRoot = (FrameworkElement)toastWin.Content;
        toastWin.Content = null;
        Snap(toastRoot, 300, 220, "toast.png", new SolidColorBrush(Color.FromRgb(0x5A, 0x6E, 0x8C)));
        toastWin.Close();
        // ...and one that could not be rendered in HD (says why), from a small selection
        var plainWin = (Window)Activator.CreateInstance(typeof(CaptureToast), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
            new object?[] { MakeFakeUi(200, 240).ToBitmap(), null, toastMon, HdCapture.Explain(HdFailure.Desktop), false }, null)!;
        var plainRoot = (FrameworkElement)plainWin.Content;
        plainWin.Content = null;
        Snap(plainRoot, 300, 220, "toast-1x.png", new SolidColorBrush(Color.FromRgb(0x5A, 0x6E, 0x8C)));
        plainWin.Close();

        // capture overlay (synthetic screen, simulated selection)
        var screen = MakeFakeUi(1280, 720);
        var (session, overlay) = CaptureSession.CreateForTest(App.Instance!.Settings, screen, hd: true);
        var ovlRoot = (FrameworkElement)overlay.Content;
        ovlRoot.Measure(new Size(1280, 720));
        ovlRoot.Arrange(new Rect(0, 0, 1280, 720));
        ovlRoot.UpdateLayout();
        overlay.SelectForTest(new Int32Rect(16, 16, 470, 150));
        ovlRoot.UpdateLayout();
        Snap(ovlRoot, 1280, 720, "capture-overlay.png", Brushes.Black);
        overlay.Close();
        File.WriteAllText(Path.Combine(dir, "done.txt"), "ok");
    }
}
