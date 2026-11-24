using System.Globalization;
using System.Text;
using System.Windows;

namespace LinearFrame.Player.Edit;

/// <summary>
/// The picture of an edit as an FFmpeg filter graph: the canvas (black, or a blurred copy of the video) with the video
/// and its further layers on it - each one a part of the clip's picture cut out, mirrored if asked, scaled into its place.
/// One and the same graph makes the finished file (<see cref="EditExport"/>) and, at the preview's size, the picture the
/// player shows while layers are being arranged - what is seen is what is saved.
/// </summary>
internal static class EditGraph
{
    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    /// <summary>A layer and, when it is not there all the time, when it is (a filter "enable" expression).</summary>
    internal readonly record struct Part(IVideoPlacement Layer, string? Enable);

    /// <summary>One layer's cut as pixels: what to take from the clip, how big to make it, what of that to keep, where it goes.</summary>
    internal readonly record struct Placed(int CropX, int CropY, int CropW, int CropH, int ScaleW, int ScaleH, int TakeX, int TakeY, int W, int H, int X, int Y, bool Flip)
    {
        public bool WholeCanvas(int cw, int ch) => X == 0 && Y == 0 && W == cw && H == ch;
    }

    /// <summary>
    /// The numbers for a layer on a canvas of <paramref name="W"/> × <paramref name="H"/> pixels. The part of the clip is
    /// cut out first (on even pixels, a little more than needed), scaled, and then trimmed to the exact place - so a
    /// camera picture zoomed in twenty times costs no more than the small piece of the frame it is.
    /// </summary>
    internal static Placed? Place(EditClip clip, IVideoPlacement layer, int W, int H)
    {
        if (EditProject.Cut(clip, layer, W, H) is not { } cut) return null;
        int vw = Math.Max(2, clip.Width), vh = Math.Max(2, clip.Height);
        int x = (int)Math.Round(cut.Target.X), y = (int)Math.Round(cut.Target.Y);
        int r = Math.Min(W, (int)Math.Round(cut.Target.Right)), b = Math.Min(H, (int)Math.Round(cut.Target.Bottom));
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        int w = r - x, h = b - y;
        if (w < 2 || h < 2) return null;
        double kx = w / cut.Source.Width, ky = h / cut.Source.Height; // canvas pixels per clip pixel
        int cx0 = Math.Clamp((int)Math.Floor(cut.Source.X / 2) * 2, 0, vw - 2), cy0 = Math.Clamp((int)Math.Floor(cut.Source.Y / 2) * 2, 0, vh - 2);
        int cx1 = Math.Clamp((int)Math.Ceiling(cut.Source.Right / 2) * 2, cx0 + 2, vw + (vw & 1)), cy1 = Math.Clamp((int)Math.Ceiling(cut.Source.Bottom / 2) * 2, cy0 + 2, vh + (vh & 1));
        cx1 = Math.Min(cx1, vw);
        cy1 = Math.Min(cy1, vh);
        int cw = Math.Max(1, cx1 - cx0), ch = Math.Max(1, cy1 - cy0);
        int sw = Math.Max(w, (int)Math.Round(cw * kx)), sh = Math.Max(h, (int)Math.Round(ch * ky));
        int tx = Math.Clamp((int)Math.Round((cut.Source.X - cx0) * kx), 0, sw - w), ty = Math.Clamp((int)Math.Round((cut.Source.Y - cy0) * ky), 0, sh - h);
        if (cut.Flip) tx = sw - w - tx; // the piece is mirrored after it is cut out: what was spare on the right is on the left
        return new Placed(cx0, cy0, cw, ch, sw, sh, tx, ty, w, h, x, y, cut.Flip);
    }

    private static string Chain(Placed c, EditClip clip, string scaler)
    {
        var sb = new StringBuilder();
        if (c.CropX != 0 || c.CropY != 0 || c.CropW != clip.Width || c.CropH != clip.Height)
            sb.Append(CultureInfo.InvariantCulture, $"crop={c.CropW}:{c.CropH}:{c.CropX}:{c.CropY}:exact=1,");
        if (c.Flip) sb.Append("hflip,");
        sb.Append(CultureInfo.InvariantCulture, $"scale={c.ScaleW}:{c.ScaleH}:flags={scaler}");
        if (c.ScaleW != c.W || c.ScaleH != c.H) sb.Append(CultureInfo.InvariantCulture, $",crop={c.W}:{c.H}:{c.TakeX}:{c.TakeY}:exact=1");
        return sb.ToString();
    }

    /// <summary>
    /// A clip's picture onto the canvas with every layer. <paramref name="input"/> / <paramref name="output"/> are pad
    /// names; empty = the graph's own open input / output (a player's filter has exactly one of each).
    /// <paramref name="tag"/> keeps the names of the pads in between apart from other pieces'.
    /// </summary>
    public static void Compose(EditProject p, EditClip clip, int W, int H, string input, string output, string tag, StringBuilder g,
                               IReadOnlyList<Part> parts, bool preview, int fullW = 0, int fullH = 0)
    {
        string scaler = preview ? "bilinear" : "bicubic";
        // the finished video's size: the blurred background is made from a copy an eighth of THAT size, so the preview
        // (drawn smaller) is blurred exactly as much as the file
        if (fullW <= 0 || fullH <= 0) (fullW, fullH) = (W, H);
        var placed = new List<(Placed C, string? Enable)>();
        foreach (var part in parts)
            if (Place(clip, part.Layer, W, H) is { } c) placed.Add((c, part.Enable));

        bool blur = p.Canvas.Background == CanvasBackground.Blur;
        // the first layer that is always there and takes the whole canvas is the picture's ground; nothing shows under it
        bool ground = placed.Count > 0 && placed[0].Enable == null && placed[0].C.WholeCanvas(W, H);
        // on black, a first layer that is always there carries the canvas itself (padded out)
        bool padded = !ground && !blur && placed.Count > 0 && placed[0].Enable == null;
        bool backdrop = !ground && !padded;

        int branches = placed.Count + (backdrop ? 1 : 0);
        string In(string name) => name.Length == 0 ? "" : $"[{name}]";
        var src = new Queue<string>();
        if (branches <= 1) src.Enqueue(input);
        else
        {
            var names = Enumerable.Range(0, branches).Select(i => $"{tag}s{i}").ToList();
            g.Append(In(input)).Append(CultureInfo.InvariantCulture, $"split={branches}").Append(string.Concat(names.Select(n => $"[{n}]"))).Append(";\n");
            foreach (var n in names) src.Enqueue(n);
        }

        string cur;
        int next = 0;
        if (backdrop)
        {
            cur = $"{tag}bg";
            g.Append(In(src.Dequeue()));
            if (blur)
            {
                // blur a small copy (cheap) and blow it up: the same look as a big blur, a fraction of the work
                int sw = Even(fullW / 8.0), sh = Even(fullH / 8.0);
                // In the player three passes of a box blur stand in for the Gaussian one (they come out the same to the
                // eye): the player's own gblur crashes on a picture this small - found with a 90x160 one; FFmpeg's does not.
                string blurred = preview ? $"boxblur={Math.Clamp(Math.Min(sw, sh) / 4, 1, 4)}:3" : "gblur=sigma=5";
                g.Append(CultureInfo.InvariantCulture, $"scale={sw}:{sh}:force_original_aspect_ratio=increase:flags=bilinear,crop={sw}:{sh},{blurred},scale={W}:{H}:flags=bilinear,eq=brightness=-0.06");
            }
            else g.Append(CultureInfo.InvariantCulture, $"crop=16:16:0:0,scale={W}:{H}:flags=neighbor,drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill");
            g.Append($"[{cur}];\n");
        }
        else
        {
            var first = placed[0].C;
            cur = $"{tag}c0";
            g.Append(In(src.Dequeue())).Append(Chain(first, clip, scaler));
            if (padded) g.Append(CultureInfo.InvariantCulture, $",pad={W}:{H}:{first.X}:{first.Y}:black");
            g.Append($"[{cur}];\n");
            next = 1;
        }
        for (int i = next; i < placed.Count; i++)
        {
            var (c, enable) = placed[i];
            g.Append(In(src.Dequeue())).Append(Chain(c, clip, scaler)).Append($"[{tag}l{i}];\n");
            g.Append(CultureInfo.InvariantCulture, $"[{cur}][{tag}l{i}]overlay=x={c.X}:y={c.Y}:format=auto");
            if (enable != null) g.Append(":enable='").Append(enable).Append('\'');
            cur = $"{tag}c{i + 1}";
            g.Append($"[{cur}];\n");
        }
        g.Append($"[{cur}]setsar=1");
        if (output.Length > 0) g.Append($"[{output}];\n");
    }

    /// <summary>The video and the layers that are on screen during a stretch of the finished video (F seconds), for the file.</summary>
    public static List<Part> PartsFor(EditProject p, double f0, double f1, double fps)
    {
        var list = new List<Part> { new(p.Canvas, null) };
        foreach (var l in p.Overlays.OfType<VideoLayer>())
        {
            var (a, b) = p.Span(l);
            if (b - a < 0.02 || b <= f0 + 1e-6 || a >= f1 - 1e-6) continue;
            bool whole = a <= f0 + 1e-6 && b >= f1 - 1e-6;
            // inside the piece its own clock runs from zero; the last frame of the layer is the one before its end
            list.Add(new Part(l, whole ? null : $"between(t,{N(Math.Max(0, a - f0))},{N(Math.Min(f1, b) - f0 - 0.5 / fps)})"));
        }
        return list;
    }

    /// <summary>
    /// The filter for the player's preview: one picture in (the clip as the player decodes it), the canvas out, at
    /// <paramref name="W"/> × <paramref name="H"/> pixels. Layers that are only there for a while switch themselves on
    /// and off by the player's own clock (the edit's time, cuts taken out). <paramref name="rotation"/>: a phone video
    /// stored lying on its side is stood up first (the player does not turn a filtered picture).
    /// </summary>
    public static string Preview(EditProject p, EditClip clip, int W, int H, int rotation)
    {
        var parts = new List<Part> { new(p.Canvas, null) };
        double total = p.EditedLength;
        foreach (var l in p.Overlays.OfType<VideoLayer>())
        {
            double a = p.SToE(l.Start), b = p.SToE(l.End);
            if (b - a < 0.02) continue;
            parts.Add(new Part(l, a <= 0.001 && b >= total - 0.001 ? null : $"between(t,{N(a)},{N(b - 0.001)})"));
        }
        var g = new StringBuilder();
        // one plain picture format for everything that follows (the player hands over whatever the decoder gave it)
        g.Append("format=yuv420p");
        string turn = rotation switch { 90 => "transpose=clock", 180 => "hflip,vflip", 270 => "transpose=cclock", _ => "" };
        if (turn.Length > 0) g.Append(',').Append(turn);
        g.Append("[up];\n");
        var (fullW, fullH) = p.CanvasSize();
        Compose(p, clip, W, H, "up", "", "v", g, parts, preview: true, fullW, fullH);
        return g.ToString();
    }
}
