using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinearFrame.Core;
using LinearFrame.Editor;
using LinearFrame.Imaging;
using LinearFrame.UI;

namespace LinearFrame.Capture;

/// <summary>
/// The floating thumbnail in the corner after a capture (like macOS): click to edit, drag it into any app, or let it go.
/// </summary>
public sealed class CaptureToast : Window
{
    private static CaptureToast? _current;

    private readonly BitmapSource _image;
    private readonly Image _picture;
    private readonly string? _path;
    private readonly MonitorInfo _monitor;
    private readonly DispatcherTimer _timer;
    private readonly TranslateTransform _slide = new();
    private readonly Grid _hoverLayer;
    private readonly Grid _inner;
    private readonly double _width;
    private Point _down;
    private bool _pressed, _closing;
    private IntPtr _hwnd;

    /// <param name="badge">small label on the preview: "HD 4×", or why the capture stayed a normal one</param>
    /// <param name="badgeGood">true = accent colour (HD worked), false = neutral note</param>
    /// <param name="preview">a small copy to draw (<see cref="Preview"/>); without one the capture itself is drawn</param>
    public static void Show(BitmapSource image, string? path, MonitorInfo monitor, string? badge = null, bool badgeGood = false, BitmapSource? preview = null)
    {
        _current?.Dismiss(fast: true);
        _current = new CaptureToast(image, path, monitor, badge, badgeGood);
        if (preview != null) _current._picture.Source = preview;
        _current.Show();
    }

    /// <summary>
    /// A copy of the capture no bigger than the preview can show (any thread). Drawing the preview from the capture
    /// itself made the graphics card take the whole picture - an HD capture is 80 MB of it, for a thumbnail 236 wide.
    /// </summary>
    public static BitmapSource Preview(BitmapSource image)
    {
        try
        {
            // the preview is at most 236 × 160 units; three times that covers every screen scaling
            double k = Math.Max(720.0 / image.PixelWidth, 480.0 / image.PixelHeight);
            if (k >= 0.75) return image;
            var small = new CachedBitmap(new TransformedBitmap(image, new ScaleTransform(k, k)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            small.Freeze();
            return small;
        }
        catch (Exception ex)
        {
            Log.Warn("Preview copy: " + ex.Message);
            return image;
        }
    }

    /// <summary>Looks for a QR code in the screenshot just taken (off the UI thread); a link shows up on the preview.</summary>
    public static async void FindCode(BitmapSource image)
    {
        var toast = _current;
        if (toast == null || !ReferenceEquals(toast._image, image)) return;
        List<QrReader.Code> codes;
        try { codes = await Task.Run(() => QrReader.Read(PixelBuffer.FromBitmap(image))); }
        catch { return; }
        if (codes.Count == 0 || _current != toast || toast._closing) return;
        toast.ShowCode(codes[0]);
    }

    private void ShowCode(QrReader.Code code)
    {
        string? link = code.Link;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new IconView { Icon = link != null ? "link" : "copy", Width = 13, Height = 13, Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock
        {
            Text = link != null ? Viewer.LiveText.Short(link) : L.T("Live.QrCopy"),
            Foreground = Brushes.White,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(60, _width - 60),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var pill = new Button
        {
            Content = content,
            ToolTip = code.Text,
            Padding = new Thickness(9, 4, 10, 4),
            Margin = new Thickness(6, 6, 40, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.Hand,
        };
        pill.SetResourceReference(StyleProperty, "Hud.Primary");
        pill.Click += (_, _) =>
        {
            if (link != null) ShellUtil.Open(link);
            else if (ImageIO.CopyText(code.Text)) Hud.Show(L.T("Live.Copied"), "copy");
            Dismiss();
        };
        _inner.Children.Add(pill);
        // the link needs a moment to be read
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(12);
        _timer.Start();
    }

    private CaptureToast(BitmapSource image, string? path, MonitorInfo monitor, string? badge, bool badgeGood)
    {
        _image = image;
        _path = path;
        _monitor = monitor;
        Title = "LinearFrame.Toast";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        double aspect = image.PixelWidth / (double)Math.Max(1, image.PixelHeight);
        double w = 236, h = w / aspect;
        if (h > 160) { h = 160; w = h * aspect; }
        w = Math.Max(w, 96); h = Math.Max(h, 64);

        var img = new Image { Source = image, Stretch = Stretch.UniformToFill, Width = w, Height = h };
        _picture = img;
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        _hoverLayer = new Grid { Opacity = 0, Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)) };
        var close = new Button { Content = new IconView { Icon = "close", Width = 13, Height = 13, StrokeWidth = 2.4 }, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) };
        close.SetResourceReference(StyleProperty, "Hud.IconButton");
        close.Background = new SolidColorBrush(Color.FromArgb(0x99, 0x1F, 0x1F, 0x22));
        close.Click += (_, _) => Dismiss();
        var folder = new Button { Content = new IconView { Icon = "folder", Width = 15, Height = 15 }, Width = 28, Height = 26, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6), ToolTip = L.T("Screens.Reveal") };
        folder.SetResourceReference(StyleProperty, "Hud.IconButton");
        folder.Background = new SolidColorBrush(Color.FromArgb(0x99, 0x1F, 0x1F, 0x22));
        folder.Click += (_, _) => { if (_path != null) ShellUtil.Reveal(_path); Dismiss(); };
        folder.Visibility = _path != null ? Visibility.Visible : Visibility.Collapsed;
        var edit = new TextBlock
        {
            Text = L.T("Screens.Edit"),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var textBtn = new Button { Content = new IconView { Icon = "textscan", Width = 15, Height = 15 }, Width = 28, Height = 26, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6), ToolTip = L.T("Capture.CopyTextTip") };
        textBtn.SetResourceReference(StyleProperty, "Hud.IconButton");
        textBtn.Background = new SolidColorBrush(Color.FromArgb(0x99, 0x1F, 0x1F, 0x22));
        textBtn.Click += async (_, _) =>
        {
            // the text in the screenshot to the clipboard (instead of the picture)
            var text = await Task.Run(() => TextRecognizer.Recognize(PixelBuffer.FromBitmap(_image)));
            if (text == null || text.IsEmpty) { Hud.Show(L.T(text == null ? "Capture.NoOcr" : "Capture.NoText"), "info"); return; }
            if (ImageIO.CopyText(text.Text)) Hud.Show(L.T("Live.Copied"), "copy");
            Dismiss();
        };
        _hoverLayer.Children.Add(edit);
        _hoverLayer.Children.Add(close);
        _hoverLayer.Children.Add(folder);
        _hoverLayer.Children.Add(textBtn);

        var inner = new Grid();
        _inner = inner;
        _width = w;
        inner.Children.Add(img);
        if (!string.IsNullOrEmpty(badge))
        {
            // a failed HD attempt gets a wider preview, so the reason fits in one or two lines
            if (!badgeGood && w < 190) { w = 190; img.Width = w; }
            inner.Children.Add(new Border
            {
                Background = new SolidColorBrush(badgeGood ? Color.FromArgb(0xE6, 0x0A, 0x84, 0xFF) : Color.FromArgb(0xE0, 0x1F, 0x1F, 0x22)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 3),
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                MaxWidth = w - 12,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = badge,
                    Foreground = Brushes.White,
                    FontSize = badgeGood ? 11.5 : 11,
                    FontWeight = badgeGood ? FontWeights.Bold : FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
                },
            });
        }
        inner.Children.Add(_hoverLayer);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)),
            Background = Brushes.Black,
            Child = inner,
            Margin = new Thickness(18),
            Cursor = Cursors.Hand,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.4, Direction = 270 },
            RenderTransform = _slide,
        };
        inner.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 9, 9);
        Content = card;
        Opacity = 0;

        card.MouseEnter += (_, _) => { _timer!.Stop(); _hoverLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120))); };
        card.MouseLeave += (_, _) => { if (!_closing) _timer!.Start(); _hoverLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160))); };
        card.MouseLeftButtonDown += (_, e) => { _pressed = true; _down = e.GetPosition(this); card.CaptureMouse(); };
        card.MouseMove += (_, e) =>
        {
            if (!_pressed || _path == null) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _down.X) > 6 || Math.Abs(p.Y - _down.Y) > 6)
            {
                _pressed = false;
                card.ReleaseMouseCapture();
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, new[] { _path });
                data.SetImage(_image);
                var result = DragDrop.DoDragDrop(card, data, DragDropEffects.Copy | DragDropEffects.Move);
                if (result != DragDropEffects.None) Dismiss();
            }
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            card.ReleaseMouseCapture();
            if (!_pressed) return;
            _pressed = false;
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            OpenEditor();
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _timer.Tick += (_, _) => Dismiss();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            if (AppPaths.Ghost) Native.SetCloak(_hwnd, true); // a test copy that must not be seen
        };
        Loaded += (_, _) => PlaceAndAnimate();
        Closed += (_, _) => { if (_current == this) _current = null; Memory.TrimSoon(); };
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void PlaceAndAnimate()
    {
        // bottom-right corner of the monitor the capture came from (physical pixels)
        double k = WindowHelper.DpiScale(this);
        int wpx = (int)Math.Ceiling(ActualWidth * k), hpx = (int)Math.Ceiling(ActualHeight * k);
        var wa = _monitor.WorkArea;
        int x = wa.X + wa.Width - wpx - (int)(4 * k);
        int y = wa.Y + wa.Height - hpx - (int)(4 * k);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, wpx, hpx, Native.SWP_NOACTIVATE);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(70, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        _timer.Start();
    }

    private void OpenEditor()
    {
        try
        {
            var doc = _path != null && File.Exists(_path) ? MarkupDocument.FromFile(_path) : new MarkupDocument(_image);
            var ed = new EditorWindow(doc);
            ed.Show();
            ed.Activate();
        }
        catch (Exception ex) { Log.Error("Open editor from toast failed", ex); }
        Dismiss(fast: true);
    }

    private void Dismiss(bool fast = false)
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        if (fast) { Close(); return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var a = new DoubleAnimation(0, 80, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        _slide.BeginAnimation(TranslateTransform.XProperty, a);
        var f = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        f.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, f);
    }
}
