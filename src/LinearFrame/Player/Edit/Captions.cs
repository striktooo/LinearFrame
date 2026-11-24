using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// What a caption is - the video editor's captions and the picture editor's are the same thing, drawn by the same code.
/// </summary>
public interface ICaption
{
    string Text { get; }
    CaptionStyle Style { get; }
    Color Color { get; }
    /// <summary>Font size as a part of the canvas height.</summary>
    double Size { get; }
    /// <summary>
    /// How wide its lines may get, in font sizes - set while the text is typed (lines break at the canvas's caption
    /// width), kept when only the size changes: enlarging a caption scales it, it never breaks a line in two.
    /// 0 = the canvas's caption width at the current size.
    /// </summary>
    double WrapEm { get; }
    /// <summary>Parts of the text in a colour of their own (the rest has <see cref="Color"/>).</summary>
    IReadOnlyList<ColorSpan> Spans { get; }
    /// <summary>An imported font (<see cref="FontBook"/>); null = TikTok Sans.</summary>
    string? Font { get; }
}

/// <summary>A stretch of a caption's text (UTF-16 positions) in its own colour.</summary>
public readonly record struct ColorSpan(int Start, int Length, Color Color)
{
    public int End => Start + Length;
}

/// <summary>Colour spans are never changed in place: painting or typing gives a new list (undo keeps the old one).</summary>
public static class ColorSpans
{
    public static Color? At(IReadOnlyList<ColorSpan>? spans, int index)
    {
        if (spans == null) return null;
        for (int i = 0; i < spans.Count; i++)
            if (index >= spans[i].Start && index < spans[i].End) return spans[i].Color;
        return null;
    }

    /// <summary>The spans with <paramref name="start"/>..+<paramref name="length"/> painted (what was there before makes way).</summary>
    public static IReadOnlyList<ColorSpan> Paint(IReadOnlyList<ColorSpan> spans, int start, int length, Color color)
    {
        if (length <= 0) return spans;
        int end = start + length;
        var list = new List<ColorSpan>();
        foreach (var s in spans)
        {
            if (s.End <= start || s.Start >= end) { list.Add(s); continue; }
            if (s.Start < start) list.Add(new ColorSpan(s.Start, start - s.Start, s.Color));
            if (s.End > end) list.Add(new ColorSpan(end, s.End - end, s.Color));
        }
        list.Add(new ColorSpan(start, length, color));
        return Tidy(list);
    }

    /// <summary>
    /// The spans after the text changed (typed, deleted, pasted, an emoji put in): what is before the change stays, what
    /// is after it moves along, a coloured word typed into grows, and text put right before or after one is plain.
    /// </summary>
    public static IReadOnlyList<ColorSpan> Rebase(IReadOnlyList<ColorSpan> spans, string oldText, string newText)
    {
        if (spans.Count == 0 || oldText == newText) return spans;
        int max = Math.Min(oldText.Length, newText.Length), p = 0;
        while (p < max && oldText[p] == newText[p]) p++;
        int s = 0;
        while (s < max - p && oldText[oldText.Length - 1 - s] == newText[newText.Length - 1 - s]) s++;
        int r0 = p, r1 = oldText.Length - s, added = newText.Length - p - s, delta = added - (r1 - r0);
        var list = new List<ColorSpan>();
        foreach (var sp in spans)
        {
            int a = sp.Start < r0 ? sp.Start : sp.Start >= r1 ? sp.Start + delta : r0 + added;
            int b = sp.End <= r0 ? sp.End : sp.End >= r1 ? sp.End + delta : r0;
            a = Math.Clamp(a, 0, newText.Length);
            b = Math.Clamp(b, 0, newText.Length);
            if (b > a) list.Add(new ColorSpan(a, b - a, sp.Color));
        }
        return Tidy(list);
    }

    private static IReadOnlyList<ColorSpan> Tidy(List<ColorSpan> list)
    {
        list.Sort((x, y) => x.Start.CompareTo(y.Start));
        for (int i = list.Count - 1; i > 0; i--)
            if (list[i - 1].End == list[i].Start && list[i - 1].Color == list[i].Color)
            {
                list[i - 1] = new ColorSpan(list[i - 1].Start, list[i - 1].Length + list[i].Length, list[i].Color);
                list.RemoveAt(i);
            }
        return list.Count == 0 ? Array.Empty<ColorSpan>() : list;
    }

    public static string Key(IReadOnlyList<ColorSpan>? spans) =>
        spans == null || spans.Count == 0 ? "" : string.Join(';', spans.Select(s => $"{s.Start},{s.Length},{s.Color}"));
}

/// <summary>
/// Captions the way TikTok's text editor draws them: TikTok Sans "Classic" (weight 600), centred lines balanced in length,
/// a black outline, or a background plate per line whose joins curve inwards, emoji as Apple images. The same code draws
/// the video editor's preview, the finished video and the picture editor's captions, so what you see is what you get.
/// </summary>
public static class Captions
{
    private const double EmojiBox = 1.12, EmojiAdvance = 1.20;
    private static Typeface? _face;
    private static GlyphTypeface? _glyphs;

    /// <summary>TikTok Sans SemiBold, embedded in the app (SIL Open Font License).</summary>
    public static Typeface Face
    {
        get
        {
            if (_face != null) return _face;
            foreach (var name in new[] { "./Assets/Fonts/#TikTok Sans", "./Assets/Fonts/#TikTok Sans 36pt SemiBold" })
            {
                try
                {
                    var family = new FontFamily(new Uri("pack://application:,,,/"), name);
                    var tf = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                    // only the embedded file counts (WPF would quietly fall back to a system font)
                    if (tf.TryGetGlyphTypeface(out var gt) && gt.FamilyNames.Values.Concat(gt.Win32FamilyNames.Values).Any(n => n.StartsWith("TikTok", StringComparison.OrdinalIgnoreCase)))
                    {
                        _glyphs = gt;
                        return _face = tf;
                    }
                }
                catch (Exception ex) { Log.Warn($"Caption font {name}: {ex.Message}"); }
            }
            _face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            _face.TryGetGlyphTypeface(out _glyphs);
            return _face;
        }
    }

    /// <summary>The face a caption is drawn with: its imported font, or TikTok Sans.</summary>
    private static (Typeface Face, GlyphTypeface? Glyphs) FaceOf(ICaption t)
    {
        if (t.Font != null && FontBook.Find(t.Font) is { } f) return (f.Face, f.Glyphs);
        var face = Face;
        return (face, _glyphs);
    }

    private static (double Ascent, double Descent) Metrics(double size, GlyphTypeface? glyphs)
    {
        double a = (glyphs?.Baseline ?? 0.93) * size;
        double h = (glyphs?.Height ?? 1.2) * size;
        return (a, Math.Max(0, h - a));
    }

    /// <summary>Everything a caption's look depends on - one text to compare (a layout is kept as long as this stays).</summary>
    public static string LookKey(ICaption t) =>
        FormattableString.Invariant($"{t.Text}|{t.Style}|{t.Color}|{t.Size:0.00000}|{t.WrapEm:0.0000}|{t.Font}|{ColorSpans.Key(t.Spans)}");

    // ------------------------------------------------------------------ layout

    internal sealed record Run(bool Emoji, string Text, double Width, Color? Color = null);
    internal sealed record Line(List<Run> Runs, double Width);

    /// <summary>A caption laid out at a given canvas size. Coordinates are canvas pixels, relative to the caption's centre.</summary>
    public sealed class Layout
    {
        internal List<Line> Lines = new();
        public double Size, Ascent, Advance, Stroke, PadX, PadY;
        public Rect Bounds;
        internal ICaption Source = null!;
        internal double PixelsPerDip = 1;
        internal Typeface Face = null!;
    }

    public static double MaxWidth(double cw, double ch) => cw * (cw <= ch ? 0.80 : 0.66);

    /// <summary>A new caption's size on a canvas (a part of its height): readable, a bit bigger on wide pictures.</summary>
    public static double DefaultSize(double cw, double ch) => 0.036 * (cw > ch ? 1.45 : 1);

    /// <summary>The line width (in font sizes) that fills the canvas's caption width at <paramref name="size"/> - <see cref="ICaption.WrapEm"/> while typing.</summary>
    public static double WrapEmFor(double size, double cw, double ch) => MaxWidth(cw, ch) / Math.Max(4, size * ch);

    // lines are broken at this size and then drawn at the caption's own: the breaks never depend on the size
    private const double WrapSize = 100;

    public static Layout Arrange(ICaption t, double cw, double ch, double pixelsPerDip = 1)
    {
        double size = Math.Max(4, t.Size * ch);
        bool plate = t.Style == CaptionStyle.Fill;
        var (face, glyphs) = FaceOf(t);
        var (ascent, descent) = Metrics(size, glyphs);
        var l = new Layout
        {
            Source = t,
            Size = size,
            Ascent = ascent,
            Advance = (ascent + descent) * (plate ? 1.0 : 1.04),
            Stroke = t.Style == CaptionStyle.Stroke ? Math.Round(0.10 * size * 4) / 4 : 0,
            PadX = plate ? 0.30 * size : 0,
            PadY = plate ? 0.08 * size : 0,
            PixelsPerDip = pixelsPerDip,
            Face = face,
        };
        double em = t.WrapEm > 0 ? t.WrapEm : WrapEmFor(t.Size, cw, ch);
        double inner = Math.Max(1, (em - 2 * (plate ? 0.30 : 0) - 2 * (t.Style == CaptionStyle.Stroke ? 0.10 : 0)) * WrapSize);
        string raw = string.IsNullOrEmpty(t.Text) ? " " : t.Text;
        var spans = t.Spans is { Count: > 0 } && !string.IsNullOrEmpty(t.Text) ? t.Spans : null;
        // every word remembers where it is in the text: a part of the text in its own colour is found again after wrapping
        var words = new List<(int At, string Text)>();
        for (int pos = 0; ;)
        {
            int nl = raw.IndexOf('\n', pos);
            int end = nl < 0 ? raw.Length : nl;
            int stop = end > pos && raw[end - 1] == '\r' ? end - 1 : end;
            words.Clear();
            for (int i = pos; i < stop;)
            {
                while (i < stop && raw[i] == ' ') i++;
                int from = i;
                while (i < stop && raw[i] != ' ') i++;
                if (i > from) words.Add((from, raw.Substring(from, i - from)));
            }
            if (words.Count == 0) l.Lines.Add(new Line(new List<Run>(), 0));
            else
            {
                var texts = words.Select(x => x.Text).ToArray();
                foreach (var (first, count) in Wrap(texts, WrapSize, inner, pixelsPerDip, face))
                    l.Lines.Add(MakeLine(words, first, count, size, pixelsPerDip, face, spans));
            }
            if (nl < 0) break;
            pos = nl + 1;
        }
        double w = l.Lines.Max(x => x.Width) + 2 * l.PadX + 2 * l.Stroke;
        double h = l.Advance * l.Lines.Count + 2 * l.PadY + 2 * l.Stroke;
        l.Bounds = new Rect(-w / 2, -h / 2, Math.Max(1, w), Math.Max(1, h));
        return l;
    }

    private static Line MakeLine(List<(int At, string Text)> words, int first, int count, double size, double ppd, Typeface face, IReadOnlyList<ColorSpan>? spans)
    {
        // the line as it is drawn (words with one space between them) and, for every character, where it is in the text
        var sb = new StringBuilder();
        var src = new List<int>();
        for (int k = first; k < first + count; k++)
        {
            if (k > first) { sb.Append(' '); src.Add(words[k].At - 1); }
            sb.Append(words[k].Text);
            for (int c = 0; c < words[k].Text.Length; c++) src.Add(words[k].At + c);
        }
        var runs = new List<Run>();
        int at = 0;
        foreach (var (emoji, piece) in SplitRuns(sb.ToString()))
        {
            if (emoji)
                foreach (var cluster in AppleEmoji.Clusters(piece)) runs.Add(new Run(true, cluster, EmojiAdvance * size));
            else if (spans == null) runs.Add(new Run(false, piece, Measure(piece, size, ppd, face)));
            else
            {
                // one run per colour
                int from = 0;
                Color? cur = ColorSpans.At(spans, src[at]);
                for (int c = 1; c <= piece.Length; c++)
                {
                    Color? col = c < piece.Length ? ColorSpans.At(spans, src[at + c]) : null;
                    if (c < piece.Length && (col == cur || char.IsLowSurrogate(piece[c]))) continue;
                    string part = piece.Substring(from, c - from);
                    runs.Add(new Run(false, part, Measure(part, size, ppd, face), cur));
                    from = c;
                    cur = col;
                }
            }
            at += piece.Length;
        }
        return new Line(runs, runs.Sum(r => r.Width));
    }

    private static FormattedText Text(string s, double size, double ppd, Typeface face, Brush? brush = null) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush ?? Brushes.White, null, TextFormattingMode.Ideal, ppd);

    private static double Measure(string s, double size, double ppd, Typeface face) => s.Length == 0 ? 0 : Text(s, size, ppd, face).WidthIncludingTrailingWhitespace;

    private static double MeasureMixed(string s, double size, double ppd, Typeface face)
    {
        double w = 0;
        foreach (var (emoji, piece) in SplitRuns(s))
            w += emoji ? AppleEmoji.Clusters(piece).Count() * EmojiAdvance * size : Measure(piece, size, ppd, face);
        return w;
    }

    /// <summary>Lines as stretches of words (first word, how many).</summary>
    private static List<(int First, int Count)> Greedy(string[] words, double size, double maxW, double ppd, Typeface face)
    {
        var lines = new List<(int, int)>();
        string cur = "";
        int first = 0;
        for (int i = 0; i < words.Length; i++)
        {
            string cand = cur.Length == 0 ? words[i] : cur + " " + words[i];
            if (cur.Length > 0 && MeasureMixed(cand, size, ppd, face) > maxW) { lines.Add((first, i - first)); first = i; cur = words[i]; }
            else cur = cand;
        }
        if (cur.Length > 0) lines.Add((first, words.Length - first));
        return lines;
    }

    /// <summary>Wrapping like TikTok: as few lines as fit, with lengths evened out (no long line over a stub).</summary>
    private static List<(int First, int Count)> Wrap(string[] words, double size, double maxW, double ppd, Typeface face)
    {
        var lines = Greedy(words, size, maxW, ppd, face);
        if (lines.Count <= 1) return lines;
        double lo = MeasureMixed(string.Join(' ', words), size, ppd, face) / lines.Count, hi = maxW;
        var best = lines;
        for (int i = 0; i < 12; i++)
        {
            double mid = (lo + hi) / 2;
            var cand = Greedy(words, size, mid, ppd, face);
            if (cand.Count <= lines.Count) { best = cand; hi = mid; } else lo = mid;
        }
        return best;
    }

    /// <summary>Text and emoji runs (emoji = pictographs, flags, keycaps and their joiners).</summary>
    internal static IEnumerable<(bool Emoji, string Text)> SplitRuns(string s)
    {
        var runes = s.EnumerateRunes().ToArray();
        var sb = new StringBuilder();
        bool? mode = null;
        for (int i = 0; i < runes.Length; i++)
        {
            int cp = runes[i].Value;
            int next = i + 1 < runes.Length ? runes[i + 1].Value : 0, next2 = i + 2 < runes.Length ? runes[i + 2].Value : 0;
            bool joiner = cp is 0x200D or 0xFE0F or 0x20E3 || cp is >= 0x1F3FB and <= 0x1F3FF || cp is >= 0xE0020 and <= 0xE007F;
            // "1️⃣" = digit + FE0F + 20E3; "©️" and friends are emoji only with the FE0F selector
            bool keycap = (cp is >= '0' and <= '9' || cp is '#' or '*') && (next == 0x20E3 || next == 0xFE0F && next2 == 0x20E3);
            bool e = IsEmojiRune(cp) || keycap || next == 0xFE0F && cp > 0x7F || mode == true && joiner;
            if (mode != null && mode != e) { yield return (mode.Value, sb.ToString()); sb.Clear(); }
            mode = e;
            sb.Append(runes[i].ToString());
        }
        if (sb.Length > 0 && mode != null) yield return (mode.Value, sb.ToString());
    }

    internal static bool IsEmojiRune(int cp) =>
        cp is >= 0x1F000 and <= 0x1FAFF || cp is >= 0x2600 and <= 0x27BF || cp is >= 0x2B00 and <= 0x2BFF ||
        cp is >= 0x2300 and <= 0x23FF || cp is 0x3030 or 0x303D or 0x3297 or 0x3299;

    // ------------------------------------------------------------------ drawing

    /// <summary>Draws a laid-out caption with its centre at <paramref name="center"/>.</summary>
    public static void Draw(DrawingContext dc, Layout l, Point center)
    {
        var t = l.Source;
        int n = l.Lines.Count;
        double blockTop = center.Y + l.Bounds.Top;
        double y0 = blockTop + l.Stroke + l.PadY;
        double plateLeft = center.X + l.Bounds.Left + l.Stroke;
        double plateW = l.Bounds.Width - 2 * l.Stroke;

        Color textColor = t.Color;
        if (t.Style == CaptionStyle.Fill)
        {
            var rects = new List<Rect>();
            for (int i = 0; i < n; i++)
            {
                double pw = l.Lines[i].Width + 2 * l.PadX;
                if (l.Lines[i].Runs.Count == 0) pw = 0;
                double x1 = plateLeft + (plateW - pw) / 2;
                double top = i == 0 ? y0 - l.PadY : y0 + i * l.Advance - l.PadY;
                double bot = i == n - 1 ? y0 + n * l.Advance + l.PadY : y0 + (i + 1) * l.Advance + l.PadY;
                rects.Add(new Rect(x1, top, Math.Max(0, pw), Math.Max(0, bot - top)));
            }
            var plate = Plates(rects, Math.Min(0.26 * l.Size, l.Advance / 2));
            var plateBrush = new SolidColorBrush(t.Color);
            plateBrush.Freeze();
            dc.DrawGeometry(plateBrush, null, plate);
            // on a plate the text takes the contrasting colour, like in TikTok
            double lum = 0.299 * t.Color.R + 0.587 * t.Color.G + 0.114 * t.Color.B;
            textColor = lum > 160 ? Colors.Black : Colors.White;
        }

        // all letters together get the outline; then each colour is filled (most captions have just the one)
        var glyphs = new GeometryGroup { FillRule = FillRule.Nonzero };
        var plain = new GeometryGroup { FillRule = FillRule.Nonzero };
        Dictionary<Color, GeometryGroup>? tinted = null;
        var emojis = new List<(string Cluster, Rect Box)>();
        for (int i = 0; i < n; i++)
        {
            var line = l.Lines[i];
            double x = plateLeft + (plateW - line.Width) / 2;
            double baseline = y0 + i * l.Advance + l.Ascent;
            foreach (var run in line.Runs)
            {
                if (run.Emoji)
                {
                    double box = EmojiBox * l.Size;
                    emojis.Add((run.Text, new Rect(x + (run.Width - box) / 2, baseline - 0.8 * box, box, box)));
                }
                else
                {
                    var ft = Text(run.Text, l.Size, l.PixelsPerDip, l.Face);
                    var geo = ft.BuildGeometry(new Point(x, baseline - ft.Baseline));
                    glyphs.Children.Add(geo);
                    if (run.Color is Color own && own != textColor)
                    {
                        tinted ??= new();
                        if (!tinted.TryGetValue(own, out var group)) tinted[own] = group = new GeometryGroup { FillRule = FillRule.Nonzero };
                        group.Children.Add(geo);
                    }
                    else plain.Children.Add(geo);
                }
                x += run.Width;
            }
        }
        glyphs.Freeze();
        if (l.Stroke > 0)
        {
            var pen = new Pen(Brushes.Black, 2 * l.Stroke) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, glyphs);
        }
        var fill = new SolidColorBrush(textColor);
        fill.Freeze();
        plain.Freeze();
        dc.DrawGeometry(fill, null, plain);
        if (tinted != null)
            foreach (var (color, group) in tinted)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                group.Freeze();
                dc.DrawGeometry(brush, null, group);
            }
        foreach (var (cluster, box) in emojis)
        {
            var img = AppleEmoji.ImageForText(cluster);
            if (img != null) { dc.DrawImage(img, box); continue; }
            // not downloaded (yet) or no Apple picture for it: Windows' own emoji, so nothing is missing
            var ft = new FormattedText(cluster, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Emoji"), box.Height * 0.85, fill, null, TextFormattingMode.Ideal, l.PixelsPerDip);
            dc.DrawText(ft, new Point(box.X + (box.Width - ft.Width) / 2, box.Y + (box.Height - ft.Height) / 2));
        }
    }

    /// <summary>
    /// TikTok's background: one rounded plate per line; where a wider line meets a narrower one the corner of the wider
    /// line is rounded and the step curves into the narrower one.
    /// </summary>
    private static Geometry Plates(List<Rect> rects, double r)
    {
        Geometry g = Geometry.Empty;
        foreach (var rc in rects.Where(x => x.Width > 0)) g = Geometry.Combine(g, new RectangleGeometry(rc), GeometryCombineMode.Union, null);

        void Convex(double cx, double cy, double rc, int sx, int sy)
        {
            if (rc < 0.5) return;
            var sq = new RectangleGeometry(new Rect(new Point(cx, cy), new Point(cx + sx * rc, cy + sy * rc)));
            g = Geometry.Combine(g, sq, GeometryCombineMode.Exclude, null);
            g = Geometry.Combine(g, new EllipseGeometry(new Point(cx + sx * rc, cy + sy * rc), rc, rc), GeometryCombineMode.Union, null);
        }

        void Fillet(double cx, double cy, double rf, int sx, int sy)
        {
            if (rf < 0.5) return;
            var sq = new RectangleGeometry(new Rect(new Point(cx, cy), new Point(cx + sx * rf, cy + sy * rf)));
            g = Geometry.Combine(g, sq, GeometryCombineMode.Union, null);
            g = Geometry.Combine(g, new EllipseGeometry(new Point(cx + sx * rf, cy + sy * rf), rf, rf), GeometryCombineMode.Exclude, null);
        }

        const double eps = 1.0;
        var rs = rects.Where(x => x.Width > 0).ToList();
        for (int i = 0; i < rs.Count; i++)
        {
            var c = rs[i];
            Rect? above = i > 0 ? rs[i - 1] : null, below = i < rs.Count - 1 ? rs[i + 1] : null;
            if (above is not Rect a)
            {
                Convex(c.Left, c.Top, r, 1, 1);
                Convex(c.Right, c.Top, r, -1, 1);
            }
            else
            {
                double dl = a.Left - c.Left;
                if (dl > eps) { double k = Math.Min(r, dl / 2); Convex(c.Left, c.Top, k, 1, 1); Fillet(a.Left, c.Top, k, -1, -1); }
                double dr = c.Right - a.Right;
                if (dr > eps) { double k = Math.Min(r, dr / 2); Convex(c.Right, c.Top, k, -1, 1); Fillet(a.Right, c.Top, k, 1, -1); }
            }
            if (below is not Rect b)
            {
                Convex(c.Left, c.Bottom, r, 1, -1);
                Convex(c.Right, c.Bottom, r, -1, -1);
            }
            else
            {
                double dl = b.Left - c.Left;
                if (dl > eps) { double k = Math.Min(r, dl / 2); Convex(c.Left, c.Bottom, k, 1, -1); Fillet(b.Left, c.Bottom, k, -1, 1); }
                double dr = c.Right - b.Right;
                if (dr > eps) { double k = Math.Min(r, dr / 2); Convex(c.Right, c.Bottom, k, -1, -1); Fillet(b.Right, c.Bottom, k, 1, 1); }
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>A caption as a picture for the finished video (canvas pixels; the picture's centre is the caption's centre).</summary>
    public static BitmapSource Render(ICaption t, double cw, double ch, out Rect bounds)
    {
        var l = Arrange(t, cw, ch);
        bounds = l.Bounds;
        int w = Math.Max(2, (int)Math.Ceiling(l.Bounds.Width) + 4), h = Math.Max(2, (int)Math.Ceiling(l.Bounds.Height) + 4);
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) Draw(dc, l, new Point(w / 2.0, h / 2.0));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        bounds = new Rect(-w / 2.0, -h / 2.0, w, h);
        return rtb;
    }
}
