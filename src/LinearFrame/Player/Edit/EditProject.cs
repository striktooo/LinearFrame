using System.Windows;
using System.Windows.Media;

namespace LinearFrame.Player.Edit;

/// <summary>One input file of the edit (the opened video plus any clips joined after it).</summary>
public sealed class EditClip
{
    public required string Path { get; init; }
    public double Duration { get; init; }
    /// <summary>Picture size as it is shown (the phone's rotation applied).</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; } = 30;
    public bool HasVideo { get; init; } = true;
    public bool HasAudio { get; init; } = true;
    /// <summary>Where this clip starts on the joined source line ("S" time).</summary>
    public double Offset { get; set; }

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>A kept piece of a clip, in that clip's own seconds.</summary>
public sealed record EditSegment(int Clip, double In, double Out)
{
    public double Length => Out - In;
}

public enum CaptionStyle { Stroke, Fill, None }

/// <summary>Something drawn on the video for a while (text, emoji, banner). Times are on the joined source line, so a
/// caption stays with "its" moment when something before it is cut out.</summary>
public abstract class EditOverlay
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>Centre on the canvas, 0..1.</summary>
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    /// <summary>Text: font size; emoji and pictures: height - all as a part of the canvas height. Banner: width as a part of the canvas width.</summary>
    public double Size { get; set; }
    /// <summary>Mirrored left to right.</summary>
    public bool Flip { get; set; }

    /// <summary>The same item in another state of the edit (undo keeps these): it stays who it is.</summary>
    public abstract EditOverlay Clone();

    /// <summary>A second item that starts out looking the same.</summary>
    public EditOverlay Duplicate()
    {
        var copy = Clone();
        copy.Id = Guid.NewGuid();
        return copy;
    }
}

public sealed class TextOverlay : EditOverlay, ICaption
{
    public string Text { get; set; } = "";
    public CaptionStyle Style { get; set; } = CaptionStyle.Stroke;
    public Color Color { get; set; } = Colors.White;
    /// <inheritdoc/>
    public double WrapEm { get; set; }
    /// <summary>Parts of the text in a colour of their own. Never changed in place - a change is a new list (undo keeps the old one).</summary>
    public IReadOnlyList<ColorSpan> Spans { get; set; } = Array.Empty<ColorSpan>();
    /// <inheritdoc/>
    public string? Font { get; set; }

    public override EditOverlay Clone() => (TextOverlay)MemberwiseClone();
}

/// <summary>A picture laid on the video (a logo, a meme, a photo): moved, resized, mirrored like a caption.</summary>
public sealed class ImageOverlay : EditOverlay
{
    public required string Path { get; init; }
    /// <summary>The picture's size as it is shown (the camera's rotation applied).</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    public double Aspect => Width / (double)Math.Max(1, Height);

    public override EditOverlay Clone() => (ImageOverlay)MemberwiseClone();
}

/// <summary>
/// Where a video sits: a frame on the canvas (a window - nothing of the video shows outside it) and the video in that
/// frame. The edit's own video is one of these (<see cref="CanvasSettings"/>, the frame is the whole canvas until it is
/// changed), every further <see cref="VideoLayer"/> another.
/// </summary>
public interface IVideoPlacement
{
    /// <summary>The frame, as parts of the canvas.</summary>
    double FX { get; set; }
    double FY { get; set; }
    double FW { get; set; }
    double FH { get; set; }
    /// <summary>1 = the whole video fits the frame; more = zoomed in.</summary>
    double Scale { get; set; }
    /// <summary>Centre of the video in the frame, 0..1.</summary>
    double CX { get; set; }
    double CY { get; set; }
    bool Flip { get; set; }
}

/// <summary>
/// The same video once more, in a frame of its own with its own zoom: a stream's camera on the upper half and the game
/// on the lower one are the video and one layer. Drawn above the video and under everything else, for its stretch of time.
/// </summary>
public sealed class VideoLayer : EditOverlay, IVideoPlacement
{
    public double FX { get; set; }
    public double FY { get; set; }
    public double FW { get; set; } = 1;
    public double FH { get; set; } = 1;
    public double Scale { get; set; } = 1;
    public double CX { get; set; } = 0.5;
    public double CY { get; set; } = 0.5;

    public override EditOverlay Clone() => (VideoLayer)MemberwiseClone();
}

/// <summary>What a video layer shows where: a rectangle of the clip's picture (pixels) scaled into a rectangle of the canvas.</summary>
public readonly record struct LayerCut(Rect Source, Rect Target, bool Flip);

public sealed class EmojiOverlay : EditOverlay
{
    /// <summary>Unicode code points as emoji-data names them, e.g. "1f602".</summary>
    public string Unified { get; set; } = "";

    public override EditOverlay Clone() => (EmojiOverlay)MemberwiseClone();
}

/// <summary>Brand banner (an animation with transparency): it comes in on the moving video, the video freezes
/// <see cref="Lead"/> s later and moves again <see cref="Tail"/> s before the banner ends.</summary>
public sealed class BannerOverlay : EditOverlay
{
    public required BannerInfo Asset { get; init; }
    /// <summary>The moment (joined source line) the video stops on.</summary>
    public double At { get; set; }
    public double Lead { get; set; } = 0.7;
    public double Tail { get; set; } = 0.4;

    public override EditOverlay Clone() => (BannerOverlay)MemberwiseClone();
}

/// <summary>A banner file, analysed once: how long it is and where on its transparent canvas the banner really is.</summary>
public sealed record BannerInfo(string Path, long Length, DateTime Stamp, double Duration, int Width, int Height, double Fps, Int32Rect Box, bool HasAudio);

public enum CanvasFormat { Original, Vertical, Square, Portrait, Wide }
public enum CanvasBackground { Black, Blur }

/// <summary>The finished video's frame: aspect ratio, how big the video sits in it and the resolution.</summary>
public sealed class CanvasSettings : IVideoPlacement
{
    public CanvasFormat Format { get; set; } = CanvasFormat.Original;
    /// <summary>1 = the whole video fits its frame; more = zoomed in (cropped).</summary>
    public double Scale { get; set; } = 1;
    /// <summary>Centre of the video in its frame, 0..1.</summary>
    public double CX { get; set; } = 0.5;
    public double CY { get; set; } = 0.5;
    /// <summary>The video's frame on the canvas - the whole canvas, or a part of it once another layer shares the picture.</summary>
    public double FX { get; set; }
    public double FY { get; set; }
    public double FW { get; set; } = 1;
    public double FH { get; set; } = 1;
    public bool Flip { get; set; }
    /// <summary>How the video sat while it had the canvas to itself - it gets that back when its last layer goes.</summary>
    public (double Scale, double CX, double CY)? Alone { get; set; }
    public CanvasBackground Background { get; set; } = CanvasBackground.Blur;
    /// <summary>Shorter side in pixels; 0 = like the source.</summary>
    public int ShortSide { get; set; }

    public CanvasSettings Clone() => (CanvasSettings)MemberwiseClone();

    public bool WholeFrame => Math.Abs(FX) < 1e-6 && Math.Abs(FY) < 1e-6 && Math.Abs(FW - 1) < 1e-6 && Math.Abs(FH - 1) < 1e-6;

    public bool IsUntouched => Format == CanvasFormat.Original && Math.Abs(Scale - 1) < 1e-6 && Math.Abs(CX - 0.5) < 1e-6
                               && Math.Abs(CY - 0.5) < 1e-6 && ShortSide == 0 && WholeFrame && !Flip;

    public static double? Aspect(CanvasFormat f) => f switch
    {
        CanvasFormat.Vertical => 9.0 / 16,
        CanvasFormat.Square => 1.0,
        CanvasFormat.Portrait => 4.0 / 5,
        CanvasFormat.Wide => 16.0 / 9,
        _ => null,
    };
}

/// <summary>A freeze of the video while a banner plays: at <see cref="E"/> (edited time) for <see cref="Length"/> s.</summary>
public readonly record struct Hold(double E, double Length, double Lead, double Tail, BannerOverlay Banner);

/// <summary>A stretch of the finished video: a piece of a clip, or a frozen frame (<see cref="Hold"/> ≥ 0).</summary>
public readonly record struct Piece(int Segment, int Clip, int Hold, double F0, double F1, double SrcIn, double SrcOut)
{
    public bool IsHold => Hold >= 0;
    public double Length => F1 - F0;
}

/// <summary>
/// The whole edit. Three time lines: S = the clips joined end to end (overlays are anchored here), E = after the cuts
/// (what mpv plays as an EDL), F = the finished video (E plus the banner freezes).
/// </summary>
public sealed class EditProject
{
    public const double MinHold = 0.5;
    public const double MinSegment = 0.1;

    public List<EditClip> Clips { get; private set; } = new();
    public List<EditSegment> Segments { get; private set; } = new();
    public List<EditOverlay> Overlays { get; private set; } = new();
    public CanvasSettings Canvas { get; private set; } = new();

    private List<Hold>? _holds;

    public EditProject(EditClip first)
    {
        AddClip(first);
    }

    private EditProject() { }

    public EditProject Clone() => new()
    {
        Clips = Clips.ToList(),
        Segments = Segments.ToList(),
        Overlays = Overlays.Select(o => o.Clone()).ToList(),
        Canvas = Canvas.Clone(),
    };

    /// <summary>Call after any change to segments or banners (cached hold list).</summary>
    public void Changed() => _holds = null;

    public void AddClip(EditClip clip)
    {
        clip.Offset = Clips.Count == 0 ? 0 : Clips[^1].Offset + Clips[^1].Duration;
        Clips.Add(clip);
        Segments.Add(new EditSegment(Clips.Count - 1, 0, clip.Duration));
        Changed();
    }

    public EditClip FirstVideo => Clips.FirstOrDefault(c => c.HasVideo) ?? Clips[0];
    public bool IsAudioOnly => Clips.All(c => !c.HasVideo);
    public double SourceLength => Clips.Sum(c => c.Duration);
    public double EditedLength => Segments.Sum(s => s.Length);
    public double FinalLength => EditedLength + Holds.Sum(h => h.Length);

    // ------------------------------------------------------------------ S <-> E

    public double SegmentStartE(int index)
    {
        double e = 0;
        for (int i = 0; i < index && i < Segments.Count; i++) e += Segments[i].Length;
        return e;
    }

    /// <summary>Joined-source second → edited second. A moment that was cut out lands where the video continues.</summary>
    public double SToE(double s)
    {
        double e = 0;
        foreach (var seg in Segments)
        {
            double s0 = Clips[seg.Clip].Offset + seg.In, s1 = s0 + seg.Length;
            if (s < s0) return e;
            if (s <= s1) return e + (s - s0);
            e += seg.Length;
        }
        return e;
    }

    /// <summary>Edited second → (segment, joined-source second).</summary>
    public (int Segment, double S) EToS(double e)
    {
        double acc = 0;
        for (int i = 0; i < Segments.Count; i++)
        {
            var seg = Segments[i];
            if (e < acc + seg.Length || i == Segments.Count - 1)
            {
                double into = Math.Clamp(e - acc, 0, seg.Length);
                return (i, Clips[seg.Clip].Offset + seg.In + into);
            }
            acc += seg.Length;
        }
        return (0, 0);
    }

    /// <summary>Edited second → (clip, that clip's own second).</summary>
    public (int Clip, double T) EToClip(double e)
    {
        var (i, _) = EToS(e);
        var seg = Segments[i];
        double into = Math.Clamp(e - SegmentStartE(i), 0, seg.Length);
        return (seg.Clip, seg.In + into);
    }

    // ------------------------------------------------------------------ banner freezes, E <-> F

    public IReadOnlyList<Hold> Holds => _holds ??= ComputeHolds();

    private List<Hold> ComputeHolds()
    {
        var list = new List<Hold>();
        double total = EditedLength;
        foreach (var b in Overlays.OfType<BannerOverlay>().OrderBy(b => b.At))
        {
            double e = Math.Clamp(SToE(b.At), 0, total);
            double d = b.Asset.Duration;
            // the banner never runs off the video: near an edge it has less moving video around the freeze
            double lead = Math.Max(0, b.Lead), tail = Math.Max(0, b.Tail);
            double room = Math.Max(0, d - MinHold);
            if (lead + tail > room && lead + tail > 0)
            {
                double k = room / (lead + tail);
                lead *= k;
                tail *= k;
            }
            lead = Math.Min(lead, e);
            tail = Math.Min(tail, total - e);
            list.Add(new Hold(e, Math.Max(0, d - lead - tail), lead, tail, b));
        }
        return list;
    }

    /// <summary>Edited → finished second. <paramref name="afterHold"/>: a freeze right at <paramref name="e"/> counts as passed.</summary>
    public double EToF(double e, bool afterHold = false)
    {
        double f = e;
        foreach (var h in Holds)
            if (h.E < e - 1e-9 || afterHold && Math.Abs(h.E - e) <= 1e-9) f += h.Length;
        return f;
    }

    /// <summary>Finished → edited second; inside a freeze also says which one and how far into it.</summary>
    public (double E, int Hold, double Into) FToE(double f)
    {
        double offset = 0;
        var holds = Holds;
        for (int i = 0; i < holds.Count; i++)
        {
            double start = holds[i].E + offset;
            if (f < start) return (f - offset, -1, 0);
            if (f < start + holds[i].Length) return (holds[i].E, i, f - start);
            offset += holds[i].Length;
        }
        return (Math.Min(f - offset, EditedLength), -1, 0);
    }

    /// <summary>When a banner is on screen in the finished video (it starts <see cref="Hold.Lead"/> s before its freeze).</summary>
    public (double F0, double F1) BannerSpan(BannerOverlay b)
    {
        foreach (var h in Holds)
        {
            if (!ReferenceEquals(h.Banner, b)) continue;
            double hf = EToF(h.E);
            return (hf - h.Lead, hf + h.Length + h.Tail);
        }
        return (0, 0);
    }

    /// <summary>When an overlay is on screen in the finished video.</summary>
    public (double F0, double F1) Span(EditOverlay o) => o is BannerOverlay b
        ? BannerSpan(b)
        : (EToF(SToE(o.Start)), EToF(SToE(o.End), afterHold: true));

    public bool VisibleAt(EditOverlay o, double f)
    {
        var (a, b) = Span(o);
        if (b - a <= 1e-3) return false;
        // standing on the very end shows the last frame, and the saved video has the overlay on it (EditExport)
        bool atEnd = f >= FinalLength - 1e-6 && b >= FinalLength - 1e-6;
        return f >= a - 1e-6 && (f < b - 1e-6 || atEnd);
    }

    /// <summary>The finished video as stretches of clips and frozen frames, in order.</summary>
    public List<Piece> Pieces()
    {
        var list = new List<Piece>();
        var holds = Holds;
        int h = 0;
        double e = 0, f = 0;
        for (int i = 0; i < Segments.Count; i++)
        {
            var seg = Segments[i];
            double e1 = e + seg.Length, src = seg.In;
            bool last = i == Segments.Count - 1;
            while (h < holds.Count && (holds[h].E < e1 - 1e-9 || last))
            {
                double at = seg.In + Math.Clamp(holds[h].E - e, src - seg.In, seg.Length);
                if (at - src > 1e-6) { list.Add(new Piece(i, seg.Clip, -1, f, f + (at - src), src, at)); f += at - src; }
                list.Add(new Piece(i, seg.Clip, h, f, f + holds[h].Length, at, at));
                f += holds[h].Length;
                src = at;
                h++;
            }
            if (seg.Out - src > 1e-6) { list.Add(new Piece(i, seg.Clip, -1, f, f + (seg.Out - src), src, seg.Out)); f += seg.Out - src; }
            e = e1;
        }
        return list;
    }

    // ------------------------------------------------------------------ cutting

    /// <summary>Splits the piece under the edited second <paramref name="e"/> in two. False on an edge.</summary>
    public bool Split(double e)
    {
        var (i, _) = EToS(e);
        var seg = Segments[i];
        double at = seg.In + (e - SegmentStartE(i));
        if (at - seg.In < MinSegment || seg.Out - at < MinSegment) return false;
        Segments[i] = seg with { Out = at };
        Segments.Insert(i + 1, seg with { In = at });
        Changed();
        return true;
    }

    /// <summary>Removes a piece (the last one can't go).</summary>
    public bool Delete(int index)
    {
        if (Segments.Count <= 1 || index < 0 || index >= Segments.Count) return false;
        Segments.RemoveAt(index);
        Changed();
        return true;
    }

    /// <summary>Moves the start or end of a piece to a clip second (kept inside the clip and away from its neighbours).</summary>
    public void Trim(int index, bool start, double t)
    {
        var seg = Segments[index];
        var clip = Clips[seg.Clip];
        // neighbours from the same clip must not overlap in the source (nothing would be shown twice)
        double lo = 0, hi = clip.Duration;
        if (index > 0 && Segments[index - 1].Clip == seg.Clip && Segments[index - 1].Out <= seg.In + 1e-9) lo = Segments[index - 1].Out;
        if (index + 1 < Segments.Count && Segments[index + 1].Clip == seg.Clip && Segments[index + 1].In >= seg.Out - 1e-9) hi = Segments[index + 1].In;
        Segments[index] = start
            ? seg with { In = Math.Clamp(t, lo, seg.Out - MinSegment) }
            : seg with { Out = Math.Clamp(t, seg.In + MinSegment, hi) };
        Changed();
    }

    // ------------------------------------------------------------------ canvas

    /// <summary>Output size in pixels (even numbers - H.264 wants them).</summary>
    public (int W, int H) CanvasSize()
    {
        var v = FirstVideo;
        int vw = Math.Max(2, v.Width), vh = Math.Max(2, v.Height);
        double? aspect = CanvasSettings.Aspect(Canvas.Format);
        double w, h;
        if (aspect is not double a)
        {
            w = vw;
            h = vh;
            if (Canvas.ShortSide > 0)
            {
                double k = Canvas.ShortSide / (double)Math.Min(vw, vh);
                w *= k;
                h *= k;
            }
        }
        else
        {
            int s = Canvas.ShortSide > 0 ? Canvas.ShortSide : Math.Min(1080, Math.Max(Math.Min(vw, vh), 720));
            if (a < 1) { w = s; h = s / a; } else { h = s; w = s * a; }
        }
        static int Even(double x) => Math.Max(2, (int)Math.Round(x / 2) * 2);
        return (Even(w), Even(h));
    }

    /// <summary>Where a clip's picture lands on a canvas of <paramref name="cw"/> × <paramref name="ch"/> (the edit's own video).</summary>
    public Rect VideoRect(EditClip clip, double cw, double ch) => VideoRect(clip, Canvas, cw, ch);

    /// <summary>The zoom at which the video just covers the whole canvas (no bars).</summary>
    public double FillScale(EditClip clip, double cw, double ch) => FillScale(clip, Canvas, cw, ch);

    // ------------------------------------------------------------------ video layers

    /// <summary>The largest zoom of a video in its frame - a small camera picture in the corner of a stream can fill a frame.</summary>
    public const double MaxZoom = 24, MinZoom = 0.3;
    /// <summary>A frame's smallest side, as a part of the canvas.</summary>
    public const double MinFrame = 0.06;

    /// <summary>The video and its further layers, bottom first.</summary>
    public IEnumerable<IVideoPlacement> VideoLayers()
    {
        yield return Canvas;
        foreach (var l in Overlays.OfType<VideoLayer>()) yield return l;
    }

    /// <summary>The picture is put together from more than the plain video: layers, or a mirrored video.</summary>
    public bool IsComposite => Canvas.Flip || Overlays.Any(o => o is VideoLayer);

    public static Rect FrameRect(IVideoPlacement l, double cw, double ch) => new(l.FX * cw, l.FY * ch, Math.Max(0, l.FW) * cw, Math.Max(0, l.FH) * ch);

    /// <summary>Where a clip's whole picture is on the canvas for a layer (it may stick out of the layer's frame).</summary>
    public static Rect VideoRect(EditClip clip, IVideoPlacement l, double cw, double ch)
    {
        var f = FrameRect(l, cw, ch);
        double vw = Math.Max(1, clip.Width), vh = Math.Max(1, clip.Height);
        double fit = Math.Min(f.Width / vw, f.Height / vh);
        double s = fit * l.Scale;
        double w = vw * s, h = vh * s;
        return new Rect(f.X + l.CX * f.Width - w / 2, f.Y + l.CY * f.Height - h / 2, w, h);
    }

    /// <summary>The zoom at which the video just covers the layer's frame.</summary>
    public static double FillScale(EditClip clip, IVideoPlacement l, double cw, double ch)
    {
        var f = FrameRect(l, cw, ch);
        double vw = Math.Max(1, clip.Width), vh = Math.Max(1, clip.Height);
        double a = f.Width / vw, b = f.Height / vh;
        return Math.Min(a, b) <= 0 ? 1 : Math.Max(a, b) / Math.Min(a, b);
    }

    /// <summary>
    /// What of the clip a layer shows and where on the canvas: the part of the video inside the layer's frame and the
    /// canvas. Null when nothing of it is on the canvas.
    /// </summary>
    public static LayerCut? Cut(EditClip clip, IVideoPlacement l, double cw, double ch)
    {
        var frame = Rect.Intersect(FrameRect(l, cw, ch), new Rect(0, 0, cw, ch));
        if (frame.IsEmpty || frame.Width < 1 || frame.Height < 1) return null;
        var v = VideoRect(clip, l, cw, ch);
        var vis = Rect.Intersect(v, frame);
        if (vis.IsEmpty || vis.Width < 1 || vis.Height < 1 || v.Width < 1e-6 || v.Height < 1e-6) return null;
        double vw = Math.Max(1, clip.Width), vh = Math.Max(1, clip.Height);
        double u0 = (vis.X - v.X) / v.Width, u1 = (vis.Right - v.X) / v.Width;
        double t0 = (vis.Y - v.Y) / v.Height, t1 = (vis.Bottom - v.Y) / v.Height;
        if (l.Flip) (u0, u1) = (1 - u1, 1 - u0); // mirrored: the frame's left side shows the picture's right side
        return new LayerCut(new Rect(u0 * vw, t0 * vh, Math.Max(1e-6, (u1 - u0) * vw), Math.Max(1e-6, (t1 - t0) * vh)), vis, l.Flip);
    }

    /// <summary>
    /// A layer's frame changes (an edge was dragged) while the video stays exactly where it is on the canvas: more or
    /// less of it shows, nothing jumps.
    /// </summary>
    public static void SetFrame(EditClip clip, IVideoPlacement l, Rect frame, double cw, double ch)
    {
        var v = VideoRect(clip, l, cw, ch);
        l.FX = frame.X;
        l.FY = frame.Y;
        l.FW = Math.Max(MinFrame, frame.Width);
        l.FH = Math.Max(MinFrame, frame.Height);
        var f = FrameRect(l, cw, ch);
        double vw = Math.Max(1, clip.Width), vh = Math.Max(1, clip.Height);
        double fit = Math.Min(f.Width / vw, f.Height / vh);
        if (fit <= 0 || f.Width <= 0 || f.Height <= 0) return;
        l.Scale = Math.Clamp(v.Width / (vw * fit), MinZoom / 4, MaxZoom * 4);
        l.CX = (v.X + v.Width / 2 - f.X) / f.Width;
        l.CY = (v.Y + v.Height / 2 - f.Y) / f.Height;
    }

    /// <summary>
    /// A new layer of the same video. The first one shares the canvas with the video: halves, one above the other on an
    /// upright canvas (a stream's camera over its game), side by side on a wide one - each filled. Later ones start as a
    /// box in the middle.
    /// </summary>
    public VideoLayer AddLayer()
    {
        var (cw, ch) = CanvasSize();
        var clip = FirstVideo;
        var layer = new VideoLayer { Start = 0, End = SourceLength };
        if (Canvas.WholeFrame && !Overlays.Any(o => o is VideoLayer))
        {
            Canvas.Alone = (Canvas.Scale, Canvas.CX, Canvas.CY);
            Halve(layer, upright: ch >= cw);
            Canvas.Scale = FillScale(clip, Canvas, cw, ch);
            Canvas.CX = Canvas.CY = 0.5;
        }
        else
        {
            layer.FX = layer.FY = 0.25;
            layer.FW = layer.FH = 0.5;
        }
        layer.Scale = FillScale(clip, layer, cw, ch);
        Overlays.Add(layer);
        Changed();
        return layer;
    }

    /// <summary>The layer and the video share the canvas in halves: one above the other, or side by side.</summary>
    private void Halve(VideoLayer layer, bool upright)
    {
        (layer.FX, layer.FY, layer.FW, layer.FH) = (0, 0, upright ? 1 : 0.5, upright ? 0.5 : 1);
        (Canvas.FX, Canvas.FY, Canvas.FW, Canvas.FH) = (upright ? 0 : 0.5, upright ? 0.5 : 0, upright ? 1 : 0.5, upright ? 0.5 : 1);
    }

    /// <summary>
    /// The canvas got another shape. A layout that is still the two halves it started as follows the shape (halves side
    /// by side on a wide canvas, one above the other on an upright one); frames set by hand keep their part of the
    /// canvas. Every frame is filled again.
    /// </summary>
    public void Reframe()
    {
        var (cw, ch) = CanvasSize();
        var layers = Overlays.OfType<VideoLayer>().ToList();
        if (layers.Count == 1)
        {
            var l = layers[0];
            static bool Is(IVideoPlacement p, double x, double y, double w, double h) =>
                Math.Abs(p.FX - x) < 1e-6 && Math.Abs(p.FY - y) < 1e-6 && Math.Abs(p.FW - w) < 1e-6 && Math.Abs(p.FH - h) < 1e-6;
            bool stacked = Is(l, 0, 0, 1, 0.5) && Is(Canvas, 0, 0.5, 1, 0.5), beside = Is(l, 0, 0, 0.5, 1) && Is(Canvas, 0.5, 0, 0.5, 1);
            if (stacked || beside) Halve(l, upright: ch >= cw);
        }
        foreach (var p in VideoLayers())
        {
            p.Scale = FillScale(FirstVideo, p, cw, ch);
            p.CX = p.CY = 0.5;
        }
        Canvas.Alone = null;
        Changed();
    }

    /// <summary>Takes an item out. When it was the last video layer, the video has the canvas to itself again.</summary>
    public void Remove(EditOverlay o)
    {
        Overlays.Remove(o);
        if (o is VideoLayer && !Overlays.Any(x => x is VideoLayer))
        {
            Canvas.FX = Canvas.FY = 0;
            Canvas.FW = Canvas.FH = 1;
            // as it sat before it shared the canvas - or, when the canvas has changed shape since, simply the whole video
            (Canvas.Scale, Canvas.CX, Canvas.CY) = Canvas.Alone ?? (1, 0.5, 0.5);
            Canvas.Alone = null;
        }
        Changed();
    }

    /// <summary>
    /// Keeps a layer's video where it can be seen: its centre may go as far as leaves a strip of it in the frame - so a
    /// video zoomed in twenty times can still be pushed all the way to any of its corners.
    /// </summary>
    public static void KeepInFrame(EditClip clip, IVideoPlacement l, double cw, double ch)
    {
        var f = FrameRect(l, cw, ch);
        var v = VideoRect(clip, l, cw, ch);
        if (f.Width <= 0 || f.Height <= 0) return;
        double hx = v.Width / f.Width / 2, hy = v.Height / f.Height / 2;
        l.CX = Math.Clamp(l.CX, 0.1 - hx, 0.9 + hx);
        l.CY = Math.Clamp(l.CY, 0.1 - hy, 0.9 + hy);
    }

    // ------------------------------------------------------------------ export kind

    /// <summary>Only cut: the result can be copied bit for bit (when every later piece starts on a keyframe).</summary>
    public bool CutsOnly => Clips.Count == 1 && Overlays.Count == 0 && Canvas.IsUntouched;
}
