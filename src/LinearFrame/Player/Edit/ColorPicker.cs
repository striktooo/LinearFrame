using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// Every colour in one field: the hue runs across, towards the top a colour fades to white, towards the bottom to black;
/// a strip on the left holds the greys. Press and drag - the colour under the mouse is reported all the time
/// (<see cref="Changing"/>) and once more when the button comes up (<see cref="Chosen"/>).
/// </summary>
public sealed class ColorField : FrameworkElement
{
    private const double GreyWidth = 18, Gap = 8, Radius = 8;
    private static BitmapSource? _spectrum, _greys;
    private Color? _current;
    private bool _held;

    public event Action<Color>? Changing;
    public event Action<Color>? Chosen;

    public ColorField()
    {
        Focusable = false; // the caption's text keeps the keyboard (and its selection) while a colour is picked
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
    }

    /// <summary>The colour marked in the field (when it is one the field holds).</summary>
    public Color? Current
    {
        get => _current;
        set { _current = value; InvalidateVisual(); }
    }

    // ------------------------------------------------------------------ colours

    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }

    public static (double Hue, double Saturation, double Value) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d < 1e-9 ? 0 : max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h, max < 1e-9 ? 0 : d / max, max);
    }

    /// <summary>
    /// The colour at a point of the spectrum (0..1 across and down): the hue across; from the top white turns into the
    /// full colour by the middle, from the middle the full colour turns into black.
    /// </summary>
    public static Color SpectrumAt(double x, double y)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        double hue = Math.Min(359.999, x * 360);
        return y <= 0.5 ? FromHsv(hue, y * 2, 1) : FromHsv(hue, 1, 2 - 2 * y);
    }

    /// <summary>The grey at a height of the strip: white at the top, black at the bottom.</summary>
    public static Color GreyAt(double y)
    {
        byte v = (byte)Math.Round(255 * (1 - Math.Clamp(y, 0, 1)));
        return Color.FromRgb(v, v, v);
    }

    /// <summary>Where the field has a colour: in the strip of greys (X = -1) or in the spectrum; null when it has no such colour.</summary>
    public static Point? Find(Color c)
    {
        var (h, s, v) = ToHsv(c);
        if (s < 0.02) return new Point(-1, 1 - v);
        if (v > 0.985) return new Point(h / 360, s / 2);
        if (s > 0.985) return new Point(h / 360, 1 - v / 2);
        return null;
    }

    private static BitmapSource Spectrum()
    {
        if (_spectrum != null) return _spectrum;
        const int w = 360, h = 180;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = SpectrumAt(x / (double)(w - 1), y / (double)(h - 1));
                int i = (y * w + x) * 4;
                px[i] = c.B;
                px[i + 1] = c.G;
                px[i + 2] = c.R;
                px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return _spectrum = bmp;
    }

    private static BitmapSource Greys()
    {
        if (_greys != null) return _greys;
        const int h = 180;
        var px = new byte[h * 4];
        for (int y = 0; y < h; y++)
        {
            var c = GreyAt(y / (double)(h - 1));
            px[y * 4] = px[y * 4 + 1] = px[y * 4 + 2] = c.R;
            px[y * 4 + 3] = 255;
        }
        var bmp = BitmapSource.Create(1, h, 96, 96, PixelFormats.Bgra32, null, px, 4);
        bmp.Freeze();
        return _greys = bmp;
    }

    // ------------------------------------------------------------------ layout, drawing

    private Rect GreyRect => new(0, 0, GreyWidth, ActualHeight);
    private Rect SpectrumRect => new(GreyWidth + Gap, 0, Math.Max(1, ActualWidth - GreyWidth - Gap), ActualHeight);

    /// <summary>The colour under a point of this control (anywhere - a drag may leave the field: it sticks to its edge).</summary>
    internal Color ColorAt(Point p)
    {
        double y = ActualHeight <= 1 ? 0 : p.Y / ActualHeight;
        if (p.X < GreyWidth + Gap / 2) return GreyAt(y);
        var s = SpectrumRect;
        return SpectrumAt((p.X - s.X) / s.Width, y);
    }

    /// <summary>Where a colour sits in this control - for the mark, and for tests.</summary>
    internal Point? PointOf(Color c)
    {
        if (Find(c) is not { } at) return null;
        if (at.X < 0) return new Point(GreyWidth / 2, at.Y * ActualHeight);
        var s = SpectrumRect;
        return new Point(s.X + at.X * s.Width, at.Y * ActualHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 4 || ActualHeight < 4) return;
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), 1);
        edge.Freeze();
        foreach (var (rect, bmp) in new[] { (GreyRect, Greys()), (SpectrumRect, Spectrum()) })
        {
            dc.PushClip(new RectangleGeometry(rect, Radius, Radius));
            dc.DrawImage(bmp, rect);
            dc.Pop();
            dc.DrawRoundedRectangle(null, edge, new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1), Radius, Radius);
        }
        if (_current is Color cur && PointOf(cur) is { } at)
        {
            at = new Point(Math.Clamp(at.X, 6, ActualWidth - 6), Math.Clamp(at.Y, 6, ActualHeight - 6));
            var fill = new SolidColorBrush(cur);
            fill.Freeze();
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 3.5), at, 6.5, 6.5);
            dc.DrawEllipse(fill, new Pen(Brushes.White, 2), at, 6.5, 6.5);
        }
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        e.Handled = true;
        PressAt(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_held) MoveTo(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_held) return;
        ReleaseAt(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // the mouse was taken away mid-drag: the colour last shown is the choice
        if (_held && _current is Color c) { _held = false; Chosen?.Invoke(c); }
    }

    internal void PressAt(Point p)
    {
        _held = true;
        MoveTo(p);
    }

    internal void MoveTo(Point p)
    {
        if (!_held) return;
        var c = ColorAt(p);
        _current = c;
        InvalidateVisual();
        Changing?.Invoke(c);
    }

    internal void ReleaseAt(Point p)
    {
        if (!_held) return;
        var c = ColorAt(p);
        _held = false;
        _current = c;
        InvalidateVisual();
        Chosen?.Invoke(c);
    }
}

/// <summary>
/// The caption tool's full palette: the field with every colour and, under it, the colours used lately (kept in the
/// settings, for the video editor's captions and the picture editor's alike). It stays open while colours are tried -
/// the caption follows each - and is put away by a click next to it.
/// </summary>
public sealed class ColorPickerPanel : Border
{
    public const int MaxRecent = 8;

    private readonly ColorField _field = new() { Width = 272, Height = 150 };
    private readonly TextBlock _label;
    private readonly StackPanel _recents = new() { Orientation = Orientation.Horizontal };
    // the colour this opening was left on: it joins the recent ones when the palette goes away
    private Color? _chosen;

    /// <summary>A colour under the mouse (false) or the one chosen (true).</summary>
    public event Action<Color, bool>? Picked;

    public ColorPickerPanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.5 };
        _label = new TextBlock
        {
            Text = L.T("Edit.RecentColors"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6)),
            FontSize = 12,
            Margin = new Thickness(2, 11, 0, 5),
        };
        var stack = new StackPanel();
        stack.Children.Add(_field);
        stack.Children.Add(_label);
        stack.Children.Add(_recents);
        Child = stack;
        _field.Changing += c => Picked?.Invoke(c, false);
        _field.Chosen += c =>
        {
            _chosen = c;
            Picked?.Invoke(c, true);
        };
        ShowRecent();
    }

    internal ColorField Field => _field;

    /// <summary>Opens on the colour the caption has now.</summary>
    public void Open(Color current)
    {
        _chosen = null;
        _field.Current = current;
        ShowRecent();
    }

    /// <summary>
    /// The palette went away: the colour it was left on is the newest of the recent ones - not every colour tried on
    /// the way to it. White and black have their own dots next to the wheel and are not kept.
    /// </summary>
    public void Close()
    {
        if (_chosen is not Color c) return;
        _chosen = null;
        if (c != Colors.White && c != Colors.Black) Remember(c);
    }

    private void ShowRecent()
    {
        _recents.Children.Clear();
        var list = Recent();
        _label.Visibility = _recents.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var c in list)
        {
            var color = c;
            var dot = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 8, 0),
                Cursor = Cursors.Hand,
                ToolTip = Hex(color),
            };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                _chosen = color;
                _field.Current = color;
                Picked?.Invoke(color, true);
            };
            _recents.Children.Add(dot);
        }
    }

    // ------------------------------------------------------------------ the colours used lately

    // without the app around (a picture of the panel being drawn): kept here for the life of the process
    private static readonly List<string> _own = new();

    /// <summary>For tests and pictures of the panel: a list of their own instead of the user's settings.</summary>
    internal static List<string>? StoreOverride;

    private static List<string> Store => StoreOverride ?? App.Instance?.Settings.RecentColors ?? _own;

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static List<Color> Recent()
    {
        var list = new List<Color>();
        foreach (var s in Store)
        {
            if (s.Length == 7 && s[0] == '#' && int.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                list.Add(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v));
            if (list.Count == MaxRecent) break;
        }
        return list;
    }

    /// <summary>A colour was chosen: it is the first of the recent ones now (each only once, the oldest drop off).</summary>
    public static void Remember(Color c)
    {
        var store = Store;
        string hex = Hex(c);
        store.RemoveAll(x => string.Equals(x, hex, StringComparison.OrdinalIgnoreCase));
        store.Insert(0, hex);
        if (store.Count > MaxRecent) store.RemoveRange(MaxRecent, store.Count - MaxRecent);
        if (StoreOverride != null) return;
        try { App.Instance?.Settings.Save(); }
        catch (Exception ex) { Log.Warn("Recent colours not saved: " + ex.Message); }
    }

    // ------------------------------------------------------------------ the button that opens the palette

    private static BitmapSource? _wheel;

    /// <summary>A small wheel of all hues - the face of the button that opens the palette.</summary>
    public static BitmapSource Wheel()
    {
        if (_wheel != null) return _wheel;
        const int n = 64;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double dx = x - (n - 1) / 2.0, dy = y - (n - 1) / 2.0;
                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 450) % 360; // red at the top, clockwise
                double far = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / (n / 2.0));
                var c = ColorField.FromHsv(hue, 0.35 + 0.65 * far, 1);
                int i = (y * n + x) * 4;
                px[i] = c.B;
                px[i + 1] = c.G;
                px[i + 2] = c.R;
                px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4);
        bmp.Freeze();
        return _wheel = bmp;
    }
}
