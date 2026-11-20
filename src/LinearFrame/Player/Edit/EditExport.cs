using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Core;
using LinearFrame.Imaging;

namespace LinearFrame.Player.Edit;

/// <summary>
/// Turns an edit into a file. Only cuts → the streams are copied bit for bit (seconds, no quality loss). Anything drawn,
/// joined or reframed → one FFmpeg pass: every clip is decoded once, pieces are cut on exact frames, banner freezes are
/// frozen frames, captions/emoji are pictures rendered by the same code as the preview, and the GPU encoder does the rest.
/// </summary>
internal static class EditExport
{
    private static Task<string>? _encoder;

    /// <summary>The fastest H.264 encoder this PC really has (NVIDIA, AMD, Intel) or the CPU one - tested once.</summary>
    public static Task<string> EncoderAsync() => _encoder ??= Task.Run(async () =>
    {
        foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=0.2",
                "-c:v", enc, "-f", "null", "-" };
            var (code, _) = await Run(args, null, 0, CancellationToken.None, 15000);
            if (code == 0) return enc;
        }
        return "libx264";
    });

    public static string EncoderLabel(string enc) => enc switch
    {
        "h264_nvenc" => "NVENC",
        "h264_amf" => "AMD",
        "h264_qsv" => "Intel",
        _ => "CPU",
    };

    /// <summary>
    /// Can this edit be saved without re-encoding? Only cuts, and every later piece starts on a keyframe. With B-frames
    /// (<paramref name="reordered"/>) stream-copied pieces don't join frame-exactly - every join would show a few frames
    /// of the removed part and stall for a moment - so several pieces are rendered then (quick on the GPU, frame-exact).
    /// </summary>
    public static bool CanCopy(EditProject p, double[]? keyframes, bool reordered)
    {
        if (!p.CutsOnly || p.Holds.Count > 0) return false;
        if (p.Segments.Count == 1) return true;
        if (p.IsAudioOnly) return true;
        if (reordered || keyframes == null || keyframes.Length == 0) return false;
        for (int i = 1; i < p.Segments.Count; i++)
        {
            int k = Array.BinarySearch(keyframes, p.Segments[i].In - 0.0005);
            if (k < 0) k = ~k;
            if (k >= keyframes.Length || Math.Abs(keyframes[k] - p.Segments[i].In) > 0.002) return false;
        }
        return true;
    }

    public static async Task<(bool Ok, string Error)> SaveAsync(EditProject p, string output, double[]? keyframes, bool reordered, IProgress<double>? progress, CancellationToken ct)
    {
        if (CanCopy(p, keyframes, reordered))
        {
            var clip = p.Clips[0];
            if (p.Segments.Count == 1) return await Ffmpeg.Trim(clip.Path, output, p.Segments[0].In, p.Segments[0].Out, progress, ct);
            return await Ffmpeg.CopyPieces(clip.Path, output, p.Segments.Select(s => (s.In, s.Out)).ToList(), keyframes, progress, ct);
        }
        return await RenderAsync(p, output, progress, ct);
    }

    // ------------------------------------------------------------------ the full render

    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    /// <summary>Output frame rate: the first video's own, at most 60.</summary>
    public static double OutputFps(EditProject p)
    {
        double f = p.FirstVideo.Fps;
        if (!double.IsFinite(f) || f < 10) f = 30;
        return Math.Min(60, f);
    }

    private static string FpsText(double f) =>
        Math.Abs(f - 30000.0 / 1001) < 0.01 ? "30000/1001" :
        Math.Abs(f - 60000.0 / 1001) < 0.01 ? "60000/1001" :
        Math.Abs(f - 24000.0 / 1001) < 0.01 ? "24000/1001" : N(Math.Round(f, 3));

    public static async Task<(bool Ok, string Error)> RenderAsync(EditProject p, string output, IProgress<double>? progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "LinearFrame", "export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var (graph, inputs, duration, hasAudio) = await BuildAsync(p, temp);
            var enc = await EncoderAsync();
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
            args.AddRange(inputs);
            var graphFile = Path.Combine(temp, "graph.txt");
            await File.WriteAllTextAsync(graphFile, graph, ct);
            args.AddRange(new[] { "-/filter_complex", graphFile, "-map", "[vout]" });
            if (hasAudio) args.AddRange(new[] { "-map", "[aout]" });
            args.AddRange(enc switch
            {
                "h264_nvenc" => new[] { "-c:v", enc, "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", "0", "-spatial-aq", "1", "-bf", "3" },
                "h264_amf" => new[] { "-c:v", enc, "-quality", "quality", "-rc", "cqp", "-qp_i", "18", "-qp_p", "20", "-qp_b", "22" },
                "h264_qsv" => new[] { "-c:v", enc, "-preset", "slower", "-global_quality", "20" },
                _ => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "19" },
            });
            args.AddRange(new[] { "-pix_fmt", "yuv420p", "-profile:v", "high" });
            if (hasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
            args.AddRange(new[] { "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output });
            Log.Info($"Edit render: {p.Segments.Count} pieces, {p.Overlays.Count} overlays, {p.Holds.Count} freezes, {duration:0.0} s, {enc}");
            var sw = Stopwatch.StartNew();
            var (code, err) = await Run(args, progress, duration, ct, 0);
            Log.Info($"Edit render finished in {sw.ElapsedMilliseconds} ms (exit {code})");
            if (code != 0)
            {
                try { if (File.Exists(output)) File.Delete(output); } catch { /* ignore */ }
                return (false, err.Length > 400 ? err[^400..] : err);
            }
            return (true, "");
        }
        catch (OperationCanceledException) { return (false, "cancelled"); }
        catch (Exception ex)
        {
            Log.Error("Edit render failed", ex);
            return (false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* temp */ }
        }
    }

    /// <summary>The filter graph and inputs (public for the self-test).</summary>
    internal static async Task<(string Graph, List<string> Inputs, double Duration, bool HasAudio)> BuildAsync(EditProject p, string temp)
    {
        var (W, H) = p.CanvasSize();
        double fps = OutputFps(p);
        string fpsText = FpsText(fps);
        var pieces = p.Pieces();
        double total = pieces.Sum(x => x.Length);
        var inputs = new List<string>();
        var g = new StringBuilder();

        for (int k = 0; k < p.Clips.Count; k++) inputs.AddRange(new[] { "-i", p.Clips[k].Path });
        int next = p.Clips.Count;

        bool anyAudio = p.Clips.Any(c => c.HasAudio) || p.Overlays.OfType<BannerOverlay>().Any(b => b.Asset.HasAudio);

        // ---- each clip decoded once, fanned out to its pieces (a freeze reads its one frame through its own input, so
        //      nothing has to wait in memory while the frozen frame is repeated)
        var vLabels = new Dictionary<int, Queue<string>>();
        var aLabels = new Dictionary<int, Queue<string>>();
        for (int k = 0; k < p.Clips.Count; k++)
        {
            var clip = p.Clips[k];
            int n = pieces.Count(x => x.Clip == k && !x.IsHold);
            if (n == 0) continue;
            if (clip.HasVideo)
            {
                var names = Enumerable.Range(0, n).Select(i => $"c{k}v{i}").ToList();
                g.Append(CultureInfo.InvariantCulture, $"[{k}:v]setpts=PTS-STARTPTS,fps={fpsText},format=yuv420p");
                g.Append(n > 1 ? $",split={n}{string.Concat(names.Select(x => $"[{x}]"))};\n" : $"[{names[0]}];\n");
                vLabels[k] = new Queue<string>(names);
            }
            if (clip.HasAudio && anyAudio)
            {
                var names = Enumerable.Range(0, n).Select(i => $"c{k}a{i}").ToList();
                g.Append(CultureInfo.InvariantCulture, $"[{k}:a]asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo");
                g.Append(n > 1 ? $",asplit={n}{string.Concat(names.Select(x => $"[{x}]"))};\n" : $"[{names[0]}];\n");
                aLabels[k] = new Queue<string>(names);
            }
        }

        // ---- the pieces, in order, each framed onto the canvas
        var concat = new StringBuilder();
        for (int i = 0; i < pieces.Count; i++)
        {
            var pc = pieces[i];
            var clip = p.Clips[pc.Clip];
            int frames;
            if (clip.HasVideo && pc.IsHold)
            {
                int last = Math.Max(0, (int)Math.Floor(clip.Duration * fps) - 1);
                int at = Math.Min((int)Math.Round(pc.SrcIn * fps), last);
                frames = Math.Max(1, (int)Math.Round(pc.Length * fps));
                int k = next++;
                inputs.AddRange(new[] { "-ss", N(at / fps), "-i", clip.Path });
                g.Append(CultureInfo.InvariantCulture, $"[{k}:v]setpts=PTS-STARTPTS,fps={fpsText},format=yuv420p,trim=end_frame=1,setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop={frames - 1}[t{i}];\n");
                Frame(p, clip, W, H, $"t{i}", $"p{i}v", i, g);
            }
            else if (vLabels.TryGetValue(pc.Clip, out var vq))
            {
                int a = (int)Math.Round(pc.SrcIn * fps), b = Math.Max(a + 1, (int)Math.Round(pc.SrcOut * fps));
                frames = b - a;
                g.Append(CultureInfo.InvariantCulture, $"[{vq.Dequeue()}]trim=start_frame={a}:end_frame={b},setpts=PTS-STARTPTS[t{i}];\n");
                Frame(p, clip, W, H, $"t{i}", $"p{i}v", i, g);
            }
            else
            {
                // a clip without picture (audio joined to a video): black for its length
                frames = Math.Max(1, (int)Math.Round(pc.Length * fps));
                g.Append(CultureInfo.InvariantCulture, $"color=c=black:s={W}x{H}:r={fpsText}:d={N(frames / fps)},format=yuv420p,setsar=1[p{i}v];\n");
            }
            double len = frames / fps;
            if (anyAudio)
            {
                if (!pc.IsHold && aLabels.TryGetValue(pc.Clip, out var aq))
                {
                    long s0 = (long)Math.Round(Math.Round(pc.SrcIn * fps) / fps * 48000);
                    long s1 = s0 + (long)Math.Round(len * 48000);
                    g.Append(CultureInfo.InvariantCulture, $"[{aq.Dequeue()}]atrim=start_sample={s0}:end_sample={s1},asetpts=PTS-STARTPTS,apad=whole_len={s1 - s0}[p{i}a];\n");
                }
                else g.Append(CultureInfo.InvariantCulture, $"aevalsrc=exprs=0|0:c=stereo:s=48000:d={N(len)}[p{i}a];\n"); // a freeze is silent; the banner brings its own sound
                concat.Append(CultureInfo.InvariantCulture, $"[p{i}v][p{i}a]");
            }
            else concat.Append(CultureInfo.InvariantCulture, $"[p{i}v]");
        }
        g.Append(concat).Append($"concat=n={pieces.Count}:v=1:a={(anyAudio ? 1 : 0)}[base]" + (anyAudio ? "[aorig]" : "") + ";\n");

        // ---- banners (under the captions), each shifted to its moment
        string cur = "base";
        int step = 0;
        var bannerAudio = new List<string>();
        foreach (var b in p.Overlays.OfType<BannerOverlay>())
        {
            var (f0, f1) = p.BannerSpan(b);
            if (f1 - f0 < 0.05) continue;
            var box = b.Asset.Box;
            int bw = Even(b.Size * W), bh = Even(bw * box.Height / (double)Math.Max(1, box.Width));
            int x = (int)Math.Round(b.X * W - bw / 2.0), y = (int)Math.Round(b.Y * H - bh / 2.0);
            int k = next++;
            inputs.AddRange(new[] { "-i", b.Asset.Path });
            g.Append(CultureInfo.InvariantCulture, $"[{k}:v]setpts=PTS-STARTPTS,crop={box.Width}:{box.Height}:{box.X}:{box.Y},scale={bw}:{bh}:flags=lanczos,format=yuva420p,setpts=PTS+{N(f0)}/TB[bn{step}];\n");
            g.Append(CultureInfo.InvariantCulture, $"[{cur}][bn{step}]overlay=x={x}:y={y}:eof_action=pass:format=auto[ob{step}];\n");
            cur = $"ob{step}";
            if (b.Asset.HasAudio && anyAudio)
            {
                g.Append(CultureInfo.InvariantCulture, $"[{k}:a]asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=delays={(long)Math.Round(f0 * 1000)}:all=1[ba{step}];\n");
                bannerAudio.Add($"[ba{step}]");
            }
            step++;
        }

        // ---- captions and emoji: pictures from the same drawing code as the preview
        int pic = 0;
        foreach (var o in p.Overlays)
        {
            if (o is BannerOverlay) continue;
            var (f0, f1) = p.Span(o);
            if (f1 - f0 < 0.02) continue;
            BitmapSource? img;
            Rect bounds;
            if (o is TextOverlay t)
            {
                if (string.IsNullOrWhiteSpace(t.Text)) continue;
                await PreloadEmoji(t.Text);
                img = Captions.Render(t, W, H, out bounds);
            }
            else if (o is EmojiOverlay e)
            {
                var src = await AppleEmoji.ImageAsync(e.Unified);
                if (src == null) continue;
                int s = Even(e.Size * H);
                img = ImageOps.Resize(PixelBuffer.FromBitmap(src), s, s).ToBitmap(); // Lanczos from Apple's 64 px picture
                bounds = new Rect(-s / 2.0, -s / 2.0, s, s);
            }
            else continue;
            var file = Path.Combine(temp, $"overlay{pic}.png");
            await File.WriteAllBytesAsync(file, ImageIO.EncodePng(img));
            int k = next++;
            inputs.AddRange(new[] { "-i", file });
            int x = (int)Math.Round(o.X * W + bounds.X), y = (int)Math.Round(o.Y * H + bounds.Y);
            g.Append(CultureInfo.InvariantCulture, $"[{cur}][{k}:v]overlay=x={x}:y={y}:format=auto:enable='between(t,{N(f0)},{N(f1 - 0.5 / fps)})'[oc{pic}];\n");
            cur = $"oc{pic}";
            pic++;
        }
        g.Append(CultureInfo.InvariantCulture, $"[{cur}]format=yuv420p,setsar=1[vout]");

        if (anyAudio)
        {
            g.Append(";\n");
            if (bannerAudio.Count == 0) g.Append("[aorig]alimiter=limit=0.97:level=false[aout]");
            else g.Append(CultureInfo.InvariantCulture, $"[aorig]{string.Concat(bannerAudio)}amix=inputs={1 + bannerAudio.Count}:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95:level=false[aout]");
        }
        return (g.ToString(), inputs, total, anyAudio);
    }

    /// <summary>A clip's picture onto the canvas: zoomed, moved, and on black or on a blurred copy of itself.</summary>
    private static void Frame(EditProject p, EditClip clip, int W, int H, string input, string output, int i, StringBuilder g)
    {
        var r = p.VideoRect(clip, W, H);
        int vw = Even(r.Width), vh = Even(r.Height);
        int x = (int)Math.Round(r.X), y = (int)Math.Round(r.Y);
        bool covers = x <= 0 && y <= 0 && x + vw >= W && y + vh >= H;
        bool inside = x >= 0 && y >= 0 && x + vw <= W && y + vh <= H;
        if (covers)
        {
            if (vw == W && vh == H) g.Append(CultureInfo.InvariantCulture, $"[{input}]scale={W}:{H}:flags=bicubic,setsar=1[{output}];\n");
            else g.Append(CultureInfo.InvariantCulture, $"[{input}]scale={vw}:{vh}:flags=bicubic,crop={W}:{H}:{-x}:{-y},setsar=1[{output}];\n");
        }
        else if (inside && p.Canvas.Background == CanvasBackground.Black)
            g.Append(CultureInfo.InvariantCulture, $"[{input}]scale={vw}:{vh}:flags=bicubic,pad={W}:{H}:{x}:{y}:black,setsar=1[{output}];\n");
        else
        {
            g.Append(CultureInfo.InvariantCulture, $"[{input}]split[f{i}a][f{i}b];\n");
            if (p.Canvas.Background == CanvasBackground.Blur)
            {
                // blur a small copy (cheap) and blow it up: the same look as a big blur, a fraction of the work
                int sw = Even(W / 8.0), sh = Even(H / 8.0);
                g.Append(CultureInfo.InvariantCulture, $"[f{i}a]scale={sw}:{sh}:force_original_aspect_ratio=increase:flags=bilinear,crop={sw}:{sh},gblur=sigma=5,scale={W}:{H}:flags=bilinear,eq=brightness=-0.06[f{i}g];\n");
            }
            else g.Append(CultureInfo.InvariantCulture, $"[f{i}a]scale={W}:{H},drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill[f{i}g];\n");
            g.Append(CultureInfo.InvariantCulture, $"[f{i}b]scale={vw}:{vh}:flags=bicubic[f{i}s];\n");
            g.Append(CultureInfo.InvariantCulture, $"[f{i}g][f{i}s]overlay=x={x}:y={y}:format=auto,setsar=1[{output}];\n");
        }
    }

    private static async Task PreloadEmoji(string text)
    {
        if (!Captions.SplitRuns(text).Any(r => r.Emoji)) return;
        await AppleEmoji.CatalogAsync();
        foreach (var (emoji, run) in Captions.SplitRuns(text))
            if (emoji)
                foreach (var cl in AppleEmoji.Clusters(run))
                {
                    if (AppleEmoji.ImageForText(cl) != null) continue;
                    var cat = AppleEmoji.Catalog;
                    var code = AppleEmoji.Code(cl);
                    var entry = cat?.FirstOrDefault(e => e.Unified == code || e.NonQualified == code || e.Unified.Replace("-fe0f", "") == code.Replace("-fe0f", ""));
                    if (entry != null) await AppleEmoji.ImageAsync(entry.Unified);
                }
    }

    // ------------------------------------------------------------------ running ffmpeg

    private static async Task<(int Code, string Error)> Run(List<string> args, IProgress<double>? progress, double duration, CancellationToken ct, int timeoutMs)
    {
        var psi = new ProcessStartInfo(Ffmpeg.Exe!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        var err = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    if (err.Length < 8000) err.AppendLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* killed: nothing more to read */ }
        });
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0) limit.CancelAfter(timeoutMs);
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(limit.Token)) != null)
            {
                if (progress != null && duration > 0 && (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                    && long.TryParse(line.AsSpan(line.IndexOf('=') + 1), out long us))
                    progress.Report(Math.Clamp(us / 1_000_000.0 / duration, 0, 1));
            }
            await proc.WaitForExitAsync(limit.Token);
            await errTask;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* gone */ }
            if (ct.IsCancellationRequested) throw;
            return (-1, "timeout");
        }
        return (proc.ExitCode, err.ToString().Trim());
    }
}
