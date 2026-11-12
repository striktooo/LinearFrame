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
                var task = Task.Run(() => res = HdCapture.RenderOn(entries, src, sel, frozen, 1, (_, _) => target, sameScaleOk: true, Stopwatch.StartNew()));
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

        Check("update: the small package is taken only when the installed engine matches", () =>
        {
            string app = Path.Combine(dir, "upd-app");
            Directory.CreateDirectory(Path.Combine(app, "engine"));
            File.WriteAllBytes(Path.Combine(app, "engine", "a.bin"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(app, "engine", "b.bin"), new byte[] { 9, 9 });
            static string H(params byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
            string? Ask(string list) => Task.Run(() => Updater.SmallPackageHashAsync(list, app, default)).GetAwaiter().GetResult();
            string zip = H(7);
            string list = $"{zip}  LinearFrame-9.9.9-update.zip\r\n{H(1, 2, 3, 4)}  engine/a.bin\r\n{H(9, 9)}  engine/b.bin\r\n";
            if (Ask(list) != zip) throw new Exception("matching engine not accepted");
            if (Ask(list + $"{H(5)}  engine/missing.dll\n") != null) throw new Exception("a missing engine file must mean: full package");
            if (Ask($"{zip}  x.zip\n{H(1)}  ../outside.bin\n") != null) throw new Exception("paths outside the engine folder must be refused");
            if (Ask("not a checksum list") != null) throw new Exception("garbage accepted");
            File.WriteAllBytes(Path.Combine(app, "engine", "b.bin"), new byte[] { 9, 8 });
            if (Ask(list) != null) throw new Exception("a changed engine must mean: full package");
            return "matching engine, changed engine, missing file, outside path, garbage: ok";
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
            Memory.TrimNow(releasePages: false);
            long before = PrivateMb(), peak = 0;
            for (int i = 0; i < 4; i++)
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

        TestMedia(dir);
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
