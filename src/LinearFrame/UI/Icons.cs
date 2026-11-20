using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LinearFrame.UI;

/// <summary>Line icons drawn on a 24×24 grid (SF Symbols-like). Stroke part + optional fill part.</summary>
public static class Icons
{
    public sealed record Def(Geometry? Stroke, Geometry? Fill);

    private static readonly Dictionary<string, Def> Map = new(StringComparer.OrdinalIgnoreCase);

    static Icons()
    {
        Add("select", "M6.5,3.5 L6.5,18.5 L10.4,14.8 L13.2,20.8 L15.9,19.6 L13.1,13.7 L18.5,13.7 Z");
        Add("pen", "M15.2,4.6 A2.2,2.2 0 0 1 18.3,4.6 L19.4,5.7 A2.2,2.2 0 0 1 19.4,8.8 L8.6,19.6 L4,20.6 L5,16 Z M13.6,6.2 L17.8,10.4");
        Add("highlighter", "M14.5,4.5 L19.5,9.5 L11.5,17.5 L6.5,17.5 L6.5,12.5 Z M9.5,9.5 L14.5,14.5 M3.5,21 H20.5");
        Add("arrow", "M5,19 L18.5,5.5 M9.5,5.5 H18.5 V14.5");
        Add("line", "M5,19 L19,5");
        Add("rect", "M6,6 H18 A2,2 0 0 1 20,8 V16 A2,2 0 0 1 18,18 H6 A2,2 0 0 1 4,16 V8 A2,2 0 0 1 6,6 Z");
        Add("ellipse", "M4,12 A8,6.5 0 1 0 20,12 A8,6.5 0 1 0 4,12 Z");
        Add("text", "M5.5,7 V5 H18.5 V7 M12,5 V19 M9,19 H15");
        Add("counter", "M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M10.6,9.4 L12.6,8 V16");
        Add("pixelate", "M6,4 H18 A2,2 0 0 1 20,6 V18 A2,2 0 0 1 18,20 H6 A2,2 0 0 1 4,18 V6 A2,2 0 0 1 6,4 Z",
            "M8,8 H12 V12 H8 Z M12,12 H16 V16 H12 Z");
        Add("crop", "M7,3 V17 H21 M3,7 H17 V21");
        Add("undo", "M9,5.5 L4.5,10 L9,14.5 M4.5,10 H14.5 A5,5 0 0 1 14.5,20 H11");
        Add("redo", "M15,5.5 L19.5,10 L15,14.5 M19.5,10 H9.5 A5,5 0 0 0 9.5,20 H13");
        Add("copy", "M11,9 H18 A2,2 0 0 1 20,11 V18 A2,2 0 0 1 18,20 H11 A2,2 0 0 1 9,18 V11 A2,2 0 0 1 11,9 Z M15,9 V6 A2,2 0 0 0 13,4 H6 A2,2 0 0 0 4,6 V13 A2,2 0 0 0 6,15 H9");
        Add("save", "M12,4 V14.5 M7.5,10 L12,14.5 L16.5,10 M5,15.5 V18 A2,2 0 0 0 7,20 H17 A2,2 0 0 0 19,18 V15.5");
        Add("more", null, "M4.3,12 A1.7,1.7 0 1 0 7.7,12 A1.7,1.7 0 1 0 4.3,12 Z M10.3,12 A1.7,1.7 0 1 0 13.7,12 A1.7,1.7 0 1 0 10.3,12 Z M16.3,12 A1.7,1.7 0 1 0 19.7,12 A1.7,1.7 0 1 0 16.3,12 Z");
        Add("close", "M6.5,6.5 L17.5,17.5 M17.5,6.5 L6.5,17.5");
        Add("minimize", "M6.5,12 H17.5");
        Add("maximize", "M7.5,6.5 H16.5 A1,1 0 0 1 17.5,7.5 V16.5 A1,1 0 0 1 16.5,17.5 H7.5 A1,1 0 0 1 6.5,16.5 V7.5 A1,1 0 0 1 7.5,6.5 Z");
        Add("restore", "M7.5,9 H14 A1,1 0 0 1 15,10 V16.5 A1,1 0 0 1 14,17.5 H7.5 A1,1 0 0 1 6.5,16.5 V10 A1,1 0 0 1 7.5,9 Z M9.5,6.5 H16 A1.5,1.5 0 0 1 17.5,8 V14.5");
        Add("play", "M8,5 L19,12 L8,19 Z", "M8,5 L19,12 L8,19 Z");
        Add("pause", "M7,5.5 H9.5 V18.5 H7 Z M14.5,5.5 H17 V18.5 H14.5 Z", "M7,5.5 H9.5 V18.5 H7 Z M14.5,5.5 H17 V18.5 H14.5 Z");
        Add("back", "M11.5,7 L6.5,12 L11.5,17 M18,7 L13,12 L18,17");
        Add("forward", "M6,7 L11,12 L6,17 M12.5,7 L17.5,12 L12.5,17");
        Add("volume", "M4,9.5 H7 L11.5,5.5 V18.5 L7,14.5 H4 Z M15,9 A4.2,4.2 0 0 1 15,15 M17.6,6.4 A7.8,7.8 0 0 1 17.6,17.6");
        Add("mute", "M4,9.5 H7 L11.5,5.5 V18.5 L7,14.5 H4 Z M15.5,9.5 L20.5,14.5 M20.5,9.5 L15.5,14.5");
        Add("fullscreen", "M4,9 V4 H9 M15,4 H20 V9 M20,15 V20 H15 M9,20 H4 V15");
        Add("unfullscreen", "M9,4 V9 H4 M15,4 V9 H20 M20,15 H15 V20 M4,15 H9 V20");
        Add("scissors", "M4,6.5 A2.5,2.5 0 1 0 9,6.5 A2.5,2.5 0 1 0 4,6.5 Z M4,17.5 A2.5,2.5 0 1 0 9,17.5 A2.5,2.5 0 1 0 4,17.5 Z M8.6,8 L20,17 M8.6,16 L20,7");
        Add("folder", "M3.5,7.5 A2,2 0 0 1 5.5,5.5 H9.5 L11.5,7.5 H18.5 A2,2 0 0 1 20.5,9.5 V16.5 A2,2 0 0 1 18.5,18.5 H5.5 A2,2 0 0 1 3.5,16.5 Z");
        Add("screenshot", "M4,8.5 V6 A2,2 0 0 1 6,4 H8.5 M15.5,4 H18 A2,2 0 0 1 20,6 V8.5 M20,15.5 V18 A2,2 0 0 1 18,20 H15.5 M8.5,20 H6 A2,2 0 0 1 4,18 V15.5 M12,9.5 V14.5 M9.5,12 H14.5");
        Add("film", "M6,4.5 H18 A2,2 0 0 1 20,6.5 V17.5 A2,2 0 0 1 18,19.5 H6 A2,2 0 0 1 4,17.5 V6.5 A2,2 0 0 1 6,4.5 Z", "M10,9 L15,12 L10,15 Z");
        Add("settings", "M4,7.5 H10 M14,7.5 H20 M4,16.5 H6.5 M10.5,16.5 H20 M10,7.5 A2,2 0 1 0 14,7.5 A2,2 0 1 0 10,7.5 Z M6.5,16.5 A2,2 0 1 0 10.5,16.5 A2,2 0 1 0 6.5,16.5 Z");
        Add("check", "M5,12.5 L10,17 L19,7.5");
        Add("trash", "M5,7 H19 M9.5,7 V5.5 A1,1 0 0 1 10.5,4.5 H13.5 A1,1 0 0 1 14.5,5.5 V7 M7,7 L7.8,18.2 A2,2 0 0 0 9.8,20 H14.2 A2,2 0 0 0 16.2,18.2 L17,7");
        Add("eyedropper", "M5,19 L14.5,9.5 M12.5,7.5 L16.5,11.5 M14.5,9.5 L18,6 A1.5,1.5 0 0 0 18,4 A1.5,1.5 0 0 0 16,4 L12.5,7.5");
        Add("transparent", "M5,4 H19 A1,1 0 0 1 20,5 V19 A1,1 0 0 1 19,20 H5 A1,1 0 0 1 4,19 V5 A1,1 0 0 1 5,4 Z",
            "M4,4 H12 V12 H4 Z M12,12 H20 V20 H12 Z");
        Add("chevron-down", "M7,10 L12,15 L17,10");
        Add("chevron-left", "M14.5,6 L8.5,12 L14.5,18");
        Add("chevron-right", "M9.5,6 L15.5,12 L9.5,18");
        Add("image", "M6,5 H18 A2,2 0 0 1 20,7 V17 A2,2 0 0 1 18,19 H6 A2,2 0 0 1 4,17 V7 A2,2 0 0 1 6,5 Z M4.5,16 L9,11.5 L13,15.5 L15.5,13 L19.5,17",
            "M14.5,8.8 A1.5,1.5 0 1 0 17.5,8.8 A1.5,1.5 0 1 0 14.5,8.8 Z");
        Add("music", "M9,17.5 V6 L19,4 V15.5 M9,9 L19,7",
            "M4,17.5 A2.5,2.5 0 1 0 9,17.5 A2.5,2.5 0 1 0 4,17.5 Z M14,15.5 A2.5,2.5 0 1 0 19,15.5 A2.5,2.5 0 1 0 14,15.5 Z");
        Add("size", "M14,4 H20 V10 M20,4 L13.5,10.5 M10,20 H4 V14 M4,20 L10.5,13.5");
        Add("pin", "M9,4 H15 M10,4 V9.5 L7,13 H17 L14,9.5 V4 M12,13 V20");
        Add("plus", "M12,5 V19 M5,12 H19");
        Add("minus", "M5,12 H19");
        Add("link", "M10,14 L14,10 M8.5,11.5 L6.5,13.5 A3.2,3.2 0 0 0 11,18 L13,16 M15.5,12.5 L17.5,10.5 A3.2,3.2 0 0 0 13,6 L11,8");
        Add("warning", "M12,4.3 L20.8,19.3 H3.2 Z M12,10 V14.2", "M10.95,16.65 A1.05,1.05 0 1 0 13.05,16.65 A1.05,1.05 0 1 0 10.95,16.65 Z");
        Add("info", "M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M12,11 V16.5", "M10.9,7.8 A1.1,1.1 0 1 0 13.1,7.8 A1.1,1.1 0 1 0 10.9,7.8 Z");
        Add("keyboard", "M5,7 H19 A2,2 0 0 1 21,9 V16 A2,2 0 0 1 19,18 H5 A2,2 0 0 1 3,16 V9 A2,2 0 0 1 5,7 Z M7,11 H7.01 M10,11 H10.01 M13,11 H13.01 M16,11 H16.01 M8,14.5 H16");
        Add("cutout", "M3.5,8 H8.5 V16 H3.5 Z M15.5,8 H20.5 V16 H15.5 Z M10.5,6 L13.5,18 M13.5,6 L10.5,18");
        Add("display", "M5,4.5 H19 A2,2 0 0 1 21,6.5 V14.5 A2,2 0 0 1 19,16.5 H5 A2,2 0 0 1 3,14.5 V6.5 A2,2 0 0 1 5,4.5 Z M8.5,20 H15.5 M12,16.5 V20");
        Add("sparkle", "M12,3.5 L13.7,10.3 L20.5,12 L13.7,13.7 L12,20.5 L10.3,13.7 L3.5,12 L10.3,10.3 Z",
            "M12,3.5 L13.7,10.3 L20.5,12 L13.7,13.7 L12,20.5 L10.3,13.7 L3.5,12 L10.3,10.3 Z");
        Add("mouse", "M12,3.5 A6,6 0 0 1 18,9.5 V14.5 A6,6 0 0 1 6,14.5 V9.5 A6,6 0 0 1 12,3.5 Z M12,7 V10.5");
        Add("upload", "M12,20 V9.5 M7.5,14 L12,9.5 L16.5,14 M5,8.5 V6 A2,2 0 0 1 7,4 H17 A2,2 0 0 1 19,6 V8.5");
        // text in a viewfinder ("copy the text in the picture")
        Add("textscan", "M4,8.5 V6 A2,2 0 0 1 6,4 H8.5 M15.5,4 H18 A2,2 0 0 1 20,6 V8.5 M20,15.5 V18 A2,2 0 0 1 18,20 H15.5 M8.5,20 H6 A2,2 0 0 1 4,18 V15.5 M8,9 H16 M8,12 H16 M8,15 H13");
        Add("search", "M4.5,10.5 A6,6 0 1 0 16.5,10.5 A6,6 0 1 0 4.5,10.5 Z M15,15 L20,20");
        Add("clock", "M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M12,7 V12 L15.5,14");
        Add("smile", "M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M8.3,14.3 A4.4,4.4 0 0 0 15.7,14.3",
            "M8.4,9.7 A1.2,1.2 0 1 0 10.8,9.7 A1.2,1.2 0 1 0 8.4,9.7 Z M13.2,9.7 A1.2,1.2 0 1 0 15.6,9.7 A1.2,1.2 0 1 0 13.2,9.7 Z");
        Add("banner", "M5,6.5 H19 A2,2 0 0 1 21,8.5 V15.5 A2,2 0 0 1 19,17.5 H5 A2,2 0 0 1 3,15.5 V8.5 A2,2 0 0 1 5,6.5 Z",
            "M12,8.6 L12.95,11.05 L15.4,12 L12.95,12.95 L12,15.4 L11.05,12.95 L8.6,12 L11.05,11.05 Z");
        Add("aspect", "M5,5 H19 A2,2 0 0 1 21,7 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 V7 A2,2 0 0 1 5,5 Z M7,11 V8.5 H9.5 M17,13 V15.5 H14.5");
    }

    private static void Add(string name, string? stroke, string? fill = null)
    {
        Geometry? s = stroke != null ? Geometry.Parse(stroke) : null;
        Geometry? f = fill != null ? Geometry.Parse(fill) : null;
        s?.Freeze();
        f?.Freeze();
        Map[name] = new Def(s, f);
    }

    public static Def? Get(string? name) => name != null && Map.TryGetValue(name, out var d) ? d : null;

    public static IEnumerable<string> Names => Map.Keys;
}

/// <summary>Renders an icon from <see cref="Icons"/> using the inherited Foreground.</summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(IconView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconView),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(1.9, FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public IconView()
    {
        Width = 18;
        Height = 18;
        SnapsToDevicePixels = false;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var def = Icons.Get(Icon);
        if (def == null) return;
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double s = size / 24.0;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        var brush = Foreground;
        if (def.Fill != null) dc.DrawGeometry(brush, null, def.Fill);
        if (def.Stroke != null)
        {
            var pen = new Pen(brush, StrokeWidth)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, pen, def.Stroke);
        }
        dc.Pop();
        dc.Pop();
    }
}
