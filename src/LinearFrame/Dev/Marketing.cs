using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using LinearFrame.Capture;
using LinearFrame.Core;
using LinearFrame.Editor;
using LinearFrame.Imaging;
using LinearFrame.UI;

namespace LinearFrame.Dev;

/// <summary>
/// Builds the README images from the real UI code (rendered off-screen, so no personal desktop content ever ends up in them):
///   LinearFrame.exe --marketing outDir           -> hero, editor, extend, library, settings
///   LinearFrame.exe --compose in.png out.png     -> frames a live window capture (rounded corners, shadow, wallpaper)
/// </summary>
internal static class Marketing
{
    private static readonly Typeface Semibold = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static Brush Wallpaper(double w, double h)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x1F, 0x4D), 0.0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x43, 0x38, 0xCA), 0.38));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x8B, 0x5C, 0xF6), 0.68));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xEC, 0x6F, 0xB0), 1.0));
        g.Freeze();
        return g;
    }

    private static BitmapSource Render(Visual v, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource Snap(FrameworkElement el, int w, int h, Brush bg)
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
        return rtb;
    }

    /// <summary>Puts a window image on the wallpaper with rounded corners, a hairline border and a soft shadow.</summary>
    public static BitmapSource Frame(BitmapSource window, int pad = 70, double radius = 10, Brush? background = null)
    {
        int w = window.PixelWidth + pad * 2, h = window.PixelHeight + pad * 2;
        var root = new ContainerVisual();
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(background ?? Wallpaper(w, h), null, new Rect(0, 0, w, h));
        root.Children.Add(bg);

        var win = new DrawingVisual
        {
            Effect = new DropShadowEffect { BlurRadius = 46, ShadowDepth = 14, Direction = 270, Opacity = 0.5, RenderingBias = RenderingBias.Quality },
        };
        var rect = new Rect(pad, pad, window.PixelWidth, window.PixelHeight);
        using (var dc = win.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(rect, radius, radius));
            dc.DrawImage(window, rect);
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), 1), rect, radius, radius);
        }
        root.Children.Add(win);
        return Render(root, w, h);
    }

    public static void Compose(string input, string output)
    {
        var img = ImageIO.LoadFull(input) ?? throw new InvalidOperationException("cannot read " + input);
        Save(Frame(img), output);
    }

    private static void Save(BitmapSource bmp, string path) => File.WriteAllBytes(path, ImageIO.EncodePng(bmp));

    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = Application.Current;

        // ---------- settings, light & dark
        foreach (bool dark in new[] { false, true })
        {
            Theme.Init(app, dark);
            var w = new MainWindow();
            Neutral(w);
            w.Navigate("settings");
            var root = (FrameworkElement)w.Content;
            w.Content = null;
            var bmp = Snap(root, 1000, 900, (Brush)app.FindResource("Brush.ContentBg"));
            Save(Frame(bmp), Path.Combine(dir, dark ? "settings-dark.png" : "settings-light.png"));
            w.Close();
        }

        // ---------- library (screenshots page) with sample thumbnails
        Theme.Init(app, true);
        var samples = SampleShots();
        var lib = new MainWindow();
        Neutral(lib);
        lib.ShowShotsForTest(samples);
        var libRoot = (FrameworkElement)lib.Content;
        lib.Content = null;
        var libBmp = Snap(libRoot, 1060, 700, (Brush)app.FindResource("Brush.ContentBg"));
        Save(Frame(libBmp), Path.Combine(dir, "library.png"));
        lib.Close();

        // a light settings render doubles as "the app being captured" in the hero shot and the editor shot
        Theme.Init(app, false);
        var subject = new MainWindow();
        Neutral(subject);
        subject.Navigate("settings");
        var subjectRoot = (FrameworkElement)subject.Content;
        subject.Content = null;
        var subjectBmp = Snap(subjectRoot, 1000, 760, (Brush)app.FindResource("Brush.ContentBg"));
        subject.Close();

        // ---------- hero: a desktop with that window, the capture overlay on top, a selection and the toolbar
        const int DW = 1600, DH = 1000;
        var desk = new DrawingVisual();
        int wx = 300, wy = 120;
        using (var dc = desk.RenderOpen())
        {
            dc.DrawRectangle(Wallpaper(DW, DH), null, new Rect(0, 0, DW, DH));
        }
        var deskRoot = new ContainerVisual();
        deskRoot.Children.Add(desk);
        var winVisual = new DrawingVisual { Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.45 } };
        using (var dc = winVisual.RenderOpen())
        {
            var r = new Rect(wx, wy, subjectBmp.PixelWidth, subjectBmp.PixelHeight);
            dc.PushClip(new RectangleGeometry(r, 9, 9));
            dc.DrawImage(subjectBmp, r);
            dc.Pop();
        }
        deskRoot.Children.Add(winVisual);
        var desktop = PixelBuffer.FromBitmap(Render(deskRoot, DW, DH));
        var (session, overlay) = CaptureSession.CreateForTest(App.Instance!.Settings, desktop);
        session.SetSize(ExportSize.UHD4K);
        var ovRoot = (FrameworkElement)overlay.Content;
        ovRoot.Measure(new Size(DW, DH));
        ovRoot.Arrange(new Rect(0, 0, DW, DH));
        ovRoot.UpdateLayout();
        // select the "Keyboard shortcuts" card of the window
        overlay.SelectForTest(new Int32Rect(wx + 250, wy + 120, 720, 115));
        ovRoot.UpdateLayout();
        var hero = Snap(ovRoot, DW, DH, Brushes.Black);
        overlay.Close();
        Save(Frame(hero, pad: 40, radius: 12, background: new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x12))), Path.Combine(dir, "hero.png"));

        // ---------- editor with a marked-up crop of that window
        Theme.Init(app, true);
        var crop = PixelBuffer.FromBitmap(subjectBmp).Crop(new Int32Rect(228 + 20, 40, 720, 480));
        var doc = new MarkupDocument(crop.ToBitmap());
        double u = doc.BaseUnit * 1.8;
        var red = Color.FromRgb(0xFF, 0x3B, 0x30);
        doc.Items.Add(new StrokeAnnotation { Highlighter = true, Width = u, Color = Color.FromRgb(0xFF, 0xCC, 0x00), Points = { new Point(10, 33), new Point(148, 33) } });
        doc.Items.Add(new ShapeAnnotation { Kind = ShapeKind.Rect, Width = u * 0.8, Color = red, Rect = new Rect(506, 90, 194, 50) });
        doc.Items.Add(new LineAnnotation { Arrow = true, Width = u, Color = red, A = new Point(345, 157), B = new Point(494, 124) });
        doc.Items.Add(new CounterAnnotation { Number = 1, Width = u, Color = red, Center = new Point(700, 90) });
        doc.Items.Add(new CounterAnnotation { Number = 2, Width = u, Color = red, Center = new Point(615, 281) });
        doc.Items.Add(new TextAnnotation { Text = L.IsPolish ? "Twój skrót" : "Your shortcut", Width = u * 0.95, Color = red, Position = new Point(196, 142) });
        doc.Items.Add(new StrokeAnnotation { Highlighter = true, Width = u, Color = Color.FromRgb(0xFF, 0xCC, 0x00), Points = { new Point(40, 286), new Point(252, 286) } });
        var ed = new EditorWindow(doc);
        ed.View.SetZoom(1.0);
        var edRoot = (FrameworkElement)ed.Content;
        ed.Content = null;
        var edBmp = Snap(edRoot, 1180, 680, (Brush)app.FindResource("Brush.CanvasBg"));
        Save(Frame(edBmp), Path.Combine(dir, "editor.png"));
        ed.Close();

        // ---------- crop & extend: before / after
        var small = PixelBuffer.FromBitmap(subjectBmp).Crop(new Int32Rect(228 + 32 + 6, 134, 690, 92)); // the two shortcut rows
        var before = small.ToBitmap();
        var ext = new MarkupDocument(before);
        var content = ext.Frame;
        double margin = Math.Round(Math.Min(content.Width, content.Height) * 0.9);
        var box = new Rect(-margin, -margin, content.Width + margin * 2, content.Height + margin * 2);
        double k = 16.0 / 9, bw = box.Width, bh = box.Height;
        if (bw / bh < k) bw = bh * k; else bh = bw / k;
        ext.Frame = new Int32Rect((int)Math.Round(content.Width / 2.0 - bw / 2), (int)Math.Round(content.Height / 2.0 - bh / 2), (int)Math.Round(bw), (int)Math.Round(bh));
        ext.Items.Add(new LineAnnotation { Arrow = true, Width = 5, Color = red, A = new Point(-62, 132), B = new Point(10, 68) });
        var after = Exporter.Render(ext, ExportSize.UHD4K);
        Save(after, Path.Combine(dir, "extend-4k-full.png"));
        Save(BeforeAfter(before, after), Path.Combine(dir, "extend.png"));
    }

    /// <summary>Nothing from the machine the images are made on: sample folder, sample monitor, sample second shortcut.</summary>
    private static void Neutral(MainWindow w)
    {
        w.FolderText.Text = L.IsPolish ? @"Obrazy\LinearFrame" : @"Pictures\LinearFrame";
        w.RegionHotkey2Box.Gesture = new HotkeyGesture(System.Windows.Input.ModifierKeys.None, HotMouse.XButton2);
        w.ShowRetinaForTest(new RetinaStatus(new[]
        {
            new DisplayState(@"\\.\DISPLAY1", L.IsPolish ? "Monitor 24\"" : "24\" monitor", "NVIDIA GeForce", 0, 0, 3840, 2160, 144, true, 1920, 1080, 200, 0),
        }));
    }

    private static BitmapSource BeforeAfter(BitmapSource before, BitmapSource after)
    {
        const int W = 1600, H = 620;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A)), null, new Rect(0, 0, W, H));
            void Label(string t, double x, double y, double size, Brush b)
            {
                var ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Semibold, size, b, 1.0);
                dc.DrawText(ft, new Point(x - ft.Width / 2, y));
            }
            // before (actual pixels)
            var bRect = new Rect(70, (H - before.PixelHeight) / 2.0, before.PixelWidth, before.PixelHeight);
            dc.DrawImage(before, bRect);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), 1), bRect);
            Label($"{before.PixelWidth} × {before.PixelHeight}", bRect.X + bRect.Width / 2, bRect.Bottom + 18, 18, Brushes.White);
            Label("Zrzut / capture", bRect.X + bRect.Width / 2, bRect.Top - 40, 16, new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6)));
            // arrow
            var arrow = AnnotationRenderer.ArrowGeometry(new Point(640, H / 2.0), new Point(740, H / 2.0), 7);
            dc.DrawGeometry(Brushes.White, null, arrow);
            // after (scaled down 4K)
            double aw = 780, ah = aw * after.PixelHeight / after.PixelWidth;
            var aRect = new Rect(780, (H - ah) / 2, aw, ah);
            var group = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality);
            using (var g = group.Open()) g.DrawImage(after, aRect);
            dc.DrawDrawing(group);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), 1), aRect);
            Label($"{after.PixelWidth} × {after.PixelHeight}  (4K, 16:9)", aRect.X + aRect.Width / 2, aRect.Bottom + 18, 18, Brushes.White);
            Label("Kadr rozszerzony, tło dobrane automatycznie / auto-filled background", aRect.X + aRect.Width / 2, aRect.Top - 40, 16, new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6)));
        }
        return Render(dv, W, H);
    }

    private static List<(string Name, DateTime Date, BitmapSource Image)> SampleShots()
    {
        var list = new List<(string, DateTime, BitmapSource)>();
        var palette = new[]
        {
            (Color.FromRgb(0x1F, 0x2B, 0x6E), Color.FromRgb(0x7B, 0x61, 0xFF)),
            (Color.FromRgb(0x0F, 0x76, 0x6E), Color.FromRgb(0x34, 0xD3, 0x99)),
            (Color.FromRgb(0x7C, 0x2D, 0x12), Color.FromRgb(0xFB, 0x92, 0x3C)),
            (Color.FromRgb(0x83, 0x18, 0x43), Color.FromRgb(0xF4, 0x72, 0xB6)),
            (Color.FromRgb(0x1E, 0x3A, 0x8A), Color.FromRgb(0x38, 0xBD, 0xF8)),
            (Color.FromRgb(0x36, 0x36, 0x3C), Color.FromRgb(0x8E, 0x8E, 0x93)),
        };
        var t = new DateTime(2026, 11, 12, 14, 32, 5);
        for (int i = 0; i < palette.Length; i++)
        {
            var (c1, c2) = palette[i];
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(c1, c2, 35), null, new Rect(0, 0, 400, 256));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), null, new Rect(30, 34, 210, 22), 6, 6);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), null, new Rect(30, 70, 300, 14), 5, 5);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), null, new Rect(30, 94, 260, 14), 5, 5);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), null, new Rect(30, 190, 120, 34), 9, 9);
            }
            var name = string.Format(L.T("Capture.FileName"), t.AddMinutes(-i * 17));
            list.Add((name, t.AddMinutes(-i * 17), Render(dv, 400, 256)));
        }
        return list;
    }
}
