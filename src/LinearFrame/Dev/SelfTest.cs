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
using LinearFrame.UI;

namespace LinearFrame.Dev;

/// <summary>
/// Developer checks that run without touching the desktop:
///   LinearFrame.exe --make-icon path.ico
///   LinearFrame.exe --selftest outDir
///   LinearFrame.exe --render-ui outDir
/// </summary>
internal static class SelfTest
{
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

    private static void Check(string name, Func<string> test)
    {
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
            doc.Items.Add(new TextAnnotation { Position = new Point(300, 250), Text = "Kliknij tutaj", Width = u * 0.9, Color = Colors.White });
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

        TestMedia(dir);
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

    private static PixelBuffer MakeFakeUi(int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)), null, new Rect(0, 0, w, h));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x38, 0x3A, 0x40)), null, new Rect(24, 24, 240, 120), 10, 10);
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            dc.DrawText(new FormattedText("Ustawienia konta", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 18, Brushes.White, 1.0), new Point(40, 40));
            dc.DrawText(new FormattedText("Powiadomienia e-mail", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, new SolidColorBrush(Color.FromRgb(0xB0, 0xB3, 0xB8)), 1.0), new Point(40, 96));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2)), null, new Rect(300, 50, 160, 40), 8, 8);
            dc.DrawText(new FormattedText("Zapisz zmiany", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 14, Brushes.White, 1.0), new Point(332, 60));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3B, 0xA5, 0x5D)), null, new Point(80, 210), 26, 26);
        }
        var rtb = RenderVisual(dv, w, h);
        return PixelBuffer.FromBitmap(rtb);
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
            return $"max {w.Max():0.00}";
        });

        Check("libmpv headless playback", () => MpvHeadless(mp4));
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

        // player overlay controls on a gradient "video"
        var ov = new PlayerOverlay(null!);
        var ovRoot = (FrameworkElement)ov.Content;
        ov.Content = null;
        var gradient = new LinearGradientBrush(Color.FromRgb(0x20, 0x50, 0x80), Color.FromRgb(0x80, 0x30, 0x50), 30);
        Snap(ovRoot, 960, 540, "player-overlay.png", gradient);
        ov.Close();

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
            new object?[] { MakeFakeUi(640, 360).ToBitmap(), null, toastMon }, null)!;
        var toastRoot = (FrameworkElement)toastWin.Content;
        toastWin.Content = null;
        Snap(toastRoot, 300, 220, "toast.png", new SolidColorBrush(Color.FromRgb(0x5A, 0x6E, 0x8C)));
        toastWin.Close();

        // capture overlay (synthetic screen, simulated selection)
        var screen = MakeFakeUi(1280, 720);
        var (session, overlay) = CaptureSession.CreateForTest(App.Instance!.Settings, screen);
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
