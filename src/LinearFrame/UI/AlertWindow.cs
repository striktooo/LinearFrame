using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using LinearFrame.Core;

namespace LinearFrame.UI;

/// <summary>
/// LinearFrame's own question box instead of Windows' grey MessageBox: a rounded card in the app's look (an icon in a
/// tinted circle, a bold question, a line of explanation, two wide buttons - cancel on the left, the action on the right,
/// like on a Mac) that pops up in the middle of its window while the window is dimmed behind it.
/// Enter = the action, Esc = cancel. A click next to the card only nudges it.
/// </summary>
public sealed class AlertWindow : Window
{
    private sealed record Palette(Color Card, Color Edge, Color Title, Color Text,
        Color Button, Color ButtonHover, Color ButtonPressed,
        Color Red, Color RedHover, Color RedPressed,
        Color Blue, Color BlueHover, Color BluePressed,
        byte Shade, double Shadow, byte Tint);

    private static readonly Palette DarkLook = new(
        Card: Color.FromRgb(0x2C, 0x2C, 0x2E), Edge: Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF),
        Title: Color.FromRgb(0xF5, 0xF5, 0xF7), Text: Color.FromRgb(0xA1, 0xA1, 0xA6),
        Button: Color.FromRgb(0x3A, 0x3A, 0x3C), ButtonHover: Color.FromRgb(0x46, 0x46, 0x49), ButtonPressed: Color.FromRgb(0x52, 0x52, 0x55),
        Red: Color.FromRgb(0xFF, 0x45, 0x3A), RedHover: Color.FromRgb(0xFF, 0x5D, 0x53), RedPressed: Color.FromRgb(0xE0, 0x3B, 0x31),
        Blue: Color.FromRgb(0x0A, 0x84, 0xFF), BlueHover: Color.FromRgb(0x33, 0x95, 0xFF), BluePressed: Color.FromRgb(0x00, 0x71, 0xDC),
        Shade: 0x70, Shadow: 0.55, Tint: 0x30);

    private static readonly Palette LightLook = new(
        Card: Colors.White, Edge: Color.FromArgb(0x12, 0x00, 0x00, 0x00),
        Title: Color.FromRgb(0x1D, 0x1D, 0x1F), Text: Color.FromRgb(0x6E, 0x6E, 0x73),
        Button: Color.FromRgb(0xEC, 0xEC, 0xF0), ButtonHover: Color.FromRgb(0xE2, 0xE2, 0xE7), ButtonPressed: Color.FromRgb(0xD6, 0xD6, 0xDB),
        Red: Color.FromRgb(0xFF, 0x3B, 0x30), RedHover: Color.FromRgb(0xFF, 0x52, 0x48), RedPressed: Color.FromRgb(0xE0, 0x33, 0x29),
        Blue: Color.FromRgb(0x00, 0x7A, 0xFF), BlueHover: Color.FromRgb(0x1A, 0x88, 0xFF), BluePressed: Color.FromRgb(0x00, 0x6A, 0xE0),
        Shade: 0x48, Shadow: 0.22, Tint: 0x1E);

    private readonly Window? _over;
    private readonly Border _shade;
    private readonly Border _card;
    private readonly ScaleTransform _scale = new(1, 1);
    private bool _result, _leaving;

    /// <summary>
    /// A question; true = the action button. <paramref name="danger"/> makes it red (throwing something away).
    /// <paramref name="dark"/> null = the app's theme (the player is always dark).
    /// </summary>
    public static bool Ask(Window? owner, string title, string message, string action, string? cancel = null,
        string icon = "trash", bool danger = true, bool? dark = null)
    {
        try
        {
            var w = new AlertWindow(owner, title, message, action, cancel ?? L.T("Common.Cancel"), icon, danger, dark ?? Theme.IsDark);
            w.ShowDialog();
            return w._result;
        }
        catch (Exception ex)
        {
            // never lose the question itself
            Log.Error("Alert failed", ex);
            return MessageBox.Show(title + (message.Length > 0 ? "\n\n" + message : ""), "LinearFrame",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
    }

    /// <summary>A message with one OK button (e.g. why saving failed).</summary>
    public static void Tell(Window? owner, string title, string message, string icon = "warning", bool? dark = null)
    {
        try
        {
            new AlertWindow(owner, title, message, L.T("Common.Ok"), null, icon, danger: false, dark ?? Theme.IsDark).ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error("Alert failed", ex);
            MessageBox.Show(title + (message.Length > 0 ? "\n\n" + message : ""), "LinearFrame", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>The card: icon, question, explanation and the buttons (cancel on the left, the action on the right).</summary>
    private static Border BuildCard(Palette p, string title, string message, string action, string? cancel, string icon, bool danger,
        Transform transform, out Pill main, out Pill? no)
    {
        Color tint = danger ? p.Red : p.Blue;
        var badge = new Border
        {
            Width = 50,
            Height = 50,
            CornerRadius = new CornerRadius(25),
            Background = Solid(Color.FromArgb(p.Tint, tint.R, tint.G, tint.B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            Child = new IconView
            {
                Icon = icon,
                Width = 25,
                Height = 25,
                StrokeWidth = 1.9,
                Foreground = Solid(tint),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var head = new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Display"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(p.Title),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var body = new TextBlock
        {
            Text = message,
            FontSize = 13,
            LineHeight = 18.5,
            Foreground = Solid(p.Text),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 6, 4, 0),
            Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible,
        };

        main = danger
            ? new Pill(action, p.Red, p.RedHover, p.RedPressed, Colors.White, bold: true)
            : new Pill(action, p.Blue, p.BlueHover, p.BluePressed, Colors.White, bold: true);
        no = null;
        var row = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        if (cancel != null)
        {
            no = new Pill(cancel, p.Button, p.ButtonHover, p.ButtonPressed, p.Title, bold: false);
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(main, 2);
            row.Children.Add(no);
        }
        row.Children.Add(main);

        var stack = new StackPanel();
        stack.Children.Add(badge);
        stack.Children.Add(head);
        stack.Children.Add(body);
        stack.Children.Add(row);
        var card = new Border
        {
            Width = 320,
            Background = Solid(p.Card),
            BorderBrush = Solid(p.Edge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22, 24, 22, 20),
            Margin = new Thickness(36), // room for the shadow
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 44, ShadowDepth = 10, Direction = 270, Opacity = p.Shadow, Color = Colors.Black },
            RenderTransform = transform,
            RenderTransformOrigin = new Point(0.5, 0.5),
            UseLayoutRounding = true,
        };
        TextElement.SetFontFamily(card, (FontFamily)Application.Current.FindResource("Font.UI"));
        return card;
    }

    private AlertWindow(Window? owner, string title, string message, string action, string? cancel, string icon, bool danger, bool dark)
    {
        var p = dark ? DarkLook : LightLook;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "LinearFrame";
        UseLayoutRounding = true;
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        _over = owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized
                && new WindowInteropHelper(owner).Handle != IntPtr.Zero ? owner : null;

        _card = BuildCard(p, title, message, action, cancel, icon, danger, _scale, out var main, out var no);
        _card.Opacity = 0;
        main.Click += () => Dismiss(true);
        if (no != null) no.Click += () => Dismiss(false);
        _shade = new Border { Background = Solid(Color.FromArgb(p.Shade, 0, 0, 0)), Opacity = 0 };
        _shade.MouseLeftButtonDown += (_, _) => Bump();

        var root = new Grid();
        root.Children.Add(_shade);
        root.Children.Add(_card);
        Content = root;

        if (_over != null)
        {
            Owner = _over;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // a first guess; Place() puts it exactly over the window (in pixels, any monitor)
            Left = _over.Left;
            Top = _over.Top;
            Width = Math.Max(1, _over.ActualWidth);
            Height = Math.Max(1, _over.ActualHeight);
            DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Place));
        }
        else
        {
            _shade.Visibility = Visibility.Collapsed;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
        }
        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) =>
        {
            Place();
            Focus();
            Appear();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Dismiss(false); e.Handled = true; }
            else if (e.Key == Key.Enter) { Dismiss(true); e.Handled = true; }
        };
    }

    /// <summary>Covers the owner exactly (dimming it), or - when it is too small for the card - just sits in its middle.</summary>
    private void Place()
    {
        if (_over == null) return;
        var me = new WindowInteropHelper(this).Handle;
        var owner = new WindowInteropHelper(_over).Handle;
        if (me == IntPtr.Zero || owner == IntPtr.Zero) return;
        if (Native.DwmGetWindowAttribute(owner, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) != 0
            && !Native.GetWindowRect(owner, out r)) return;
        bool zoomed = Native.IsZoomed(owner), whole = false;
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (Native.GetMonitorInfo(Native.MonitorFromWindow(owner, Native.MONITOR_DEFAULTTONEAREST), ref mi))
        {
            // a maximised window reaches a little past the screen; the shade must not spill onto the taskbar or the next monitor
            var m = zoomed ? mi.rcWork : mi.rcMonitor;
            r = new Native.RECT(Math.Max(r.Left, m.Left), Math.Max(r.Top, m.Top), Math.Min(r.Right, m.Right), Math.Min(r.Bottom, m.Bottom));
            whole = r.Left == mi.rcMonitor.Left && r.Top == mi.rcMonitor.Top && r.Right == mi.rcMonitor.Right && r.Bottom == mi.rcMonitor.Bottom;
        }
        if (r.Width <= 0 || r.Height <= 0) return;

        double k = VisualTreeHelper.GetDpi(_over).DpiScaleX;
        _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var need = _card.DesiredSize;
        if (r.Width < need.Width * k || r.Height < need.Height * k)
        {
            // a tiny window: the card alone, centred over it
            _shade.Visibility = Visibility.Collapsed;
            int w = (int)Math.Ceiling(need.Width * k), h = (int)Math.Ceiling(need.Height * k);
            Native.SetWindowPos(me, IntPtr.Zero, r.Left + (r.Width - w) / 2, r.Top + (r.Height - h) / 2, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return;
        }
        _shade.Visibility = Visibility.Visible;
        Native.SetWindowPos(me, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        // Windows 11 rounds a normal window's corners: the shade over it follows them
        bool round = Environment.OSVersion.Version.Build >= 22000 && !zoomed && !whole;
        _shade.CornerRadius = new CornerRadius(round ? 8 : 0);
    }

    private void Appear()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _shade.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        var settle = new DoubleAnimation(1.07, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
    }

    /// <summary>A click beside the card: it is still waiting for an answer.</summary>
    private void Bump()
    {
        if (_leaving) return;
        var bump = new DoubleAnimation(1, 1.03, TimeSpan.FromMilliseconds(85)) { AutoReverse = true, EasingFunction = new SineEase() };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, bump);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, bump);
    }

    private void Dismiss(bool result)
    {
        if (_leaving) return;
        _leaving = true;
        _result = result;
        var d = TimeSpan.FromMilliseconds(120);
        var fade = new DoubleAnimation(0, d);
        fade.Completed += (_, _) =>
        {
            try { DialogResult = result; }
            catch (InvalidOperationException) { Close(); }
        };
        _shade.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        _card.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.97, d);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>The card itself, as the window shows it - for the UI snapshots (--render-ui).</summary>
    internal static FrameworkElement Preview(string title, string message, string action, string? cancel, string icon, bool danger, bool dark) =>
        BuildCard(dark ? DarkLook : LightLook, title, message, action, cancel, icon, danger, Transform.Identity, out _, out _);

    /// <summary>A wide, rounded button (hover / pressed shades), clicked with the mouse; Enter / Esc are handled by the window.</summary>
    private sealed class Pill : Border
    {
        private readonly Brush _normal, _hover, _pressed;
        private bool _down;
        public event Action? Click;

        public Pill(string text, Color normal, Color hover, Color pressed, Color fore, bool bold)
        {
            _normal = Solid(normal);
            _hover = Solid(hover);
            _pressed = Solid(pressed);
            Background = _normal;
            CornerRadius = new CornerRadius(9);
            Height = 34;
            Cursor = Cursors.Hand;
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13.5,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = Solid(fore),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(10, 0, 10, 1),
            };
            MouseEnter += (_, _) => Background = _down ? _pressed : _hover;
            MouseLeave += (_, _) => Background = _normal;
            MouseLeftButtonDown += (_, e) =>
            {
                _down = true;
                Background = _pressed;
                CaptureMouse();
                e.Handled = true;
            };
            MouseLeftButtonUp += (_, e) =>
            {
                bool was = _down;
                _down = false;
                // while captured IsMouseOver is always true: is the pointer really still on the button?
                var at = e.GetPosition(this);
                bool inside = at.X >= 0 && at.Y >= 0 && at.X <= ActualWidth && at.Y <= ActualHeight;
                ReleaseMouseCapture();
                Background = inside ? _hover : _normal;
                e.Handled = true;
                if (was && inside) Click?.Invoke();
            };
            LostMouseCapture += (_, _) =>
            {
                _down = false;
                Background = IsMouseOver ? _hover : _normal;
            };
        }
    }
}
