using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Media.Imaging;
using LinearFrame.Core;

namespace LinearFrame.Player;

/// <summary>ffmpeg.exe helpers: lossless trimming (stream copy), filmstrip frames, waveforms and cover art.</summary>
internal static class Ffmpeg
{
    private static string? _path;

    public static string? Exe
    {
        get
        {
            if (_path != null && File.Exists(_path)) return _path;
            _path = AppPaths.FindEngineFile("ffmpeg.exe") ?? FindOnPath("ffmpeg.exe");
            return _path;
        }
    }

    public static bool Available => Exe != null;

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { /* bad PATH entry */ }
        }
        return null;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static ProcessStartInfo Start(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Exe!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static async Task<byte[]?> RunForBytes(List<string> args, CancellationToken ct, int timeoutMs = 15000)
    {
        if (!Available) return null;
        using var p = Process.Start(Start(args));
        if (p == null) return null;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* exited already */ }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        var errTask = p.StandardError.ReadToEndAsync(timeout.Token);
        using var ms = new MemoryStream();
        try
        {
            await p.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* gone */ }
            return null;
        }
        return p.ExitCode == 0 && ms.Length > 0 ? ms.ToArray() : null;
    }

    private static BitmapSource? Decode(byte[]? bytes)
    {
        if (bytes == null) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            // copy into a plain bitmap: decoder frames stay tied to the thread that created them
            return Imaging.PixelBuffer.FromBitmap(dec.Frames[0]).ToBitmap();
        }
        catch { return null; }
    }

    /// <summary>One video frame near <paramref name="t"/> (keyframe-fast), scaled to the given height.</summary>
    public static async Task<BitmapSource?> Frame(string file, double t, int height, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", F(Math.Max(0, t)), "-i", file,
            "-frames:v", "1", "-an", "-sn", "-dn",
            "-vf", $"scale=-2:{height}:flags=bilinear",
            "-f", "image2pipe", "-c:v", "bmp", "pipe:1",
        };
        return Decode(await RunForBytes(args, ct, 10000));
    }

    public static async Task<BitmapSource?> CoverArt(string file, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-i", file,
            "-map", "0:v:0?", "-frames:v", "1", "-an", "-vf", "scale='min(600,iw)':-2",
            "-f", "image2pipe", "-c:v", "png", "pipe:1",
        };
        return Decode(await RunForBytes(args, ct, 8000));
    }

    /// <summary>Peak envelope of the audio, <paramref name="bins"/> values in 0..1.</summary>
    public static async Task<float[]?> Waveform(string file, int bins, double duration, CancellationToken ct)
    {
        int rate = duration > 1800 ? 200 : duration > 600 ? 800 : 2000;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-i", file,
            "-vn", "-sn", "-ac", "1", "-ar", rate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le", "-c:a", "pcm_s16le", "pipe:1",
        };
        var bytes = await RunForBytes(args, ct, 60000);
        if (bytes == null || bytes.Length < 4) return null;
        int n = bytes.Length / 2;
        var peaks = new float[bins];
        for (int b = 0; b < bins; b++)
        {
            int s0 = (int)((long)n * b / bins), s1 = (int)((long)n * (b + 1) / bins);
            int max = 0;
            for (int i = s0; i < s1; i++)
            {
                int v = Math.Abs((short)(bytes[i * 2] | bytes[i * 2 + 1] << 8));
                if (v > max) max = v;
            }
            peaks[b] = max / 32768f;
        }
        float top = peaks.Max();
        if (top > 0) for (int i = 0; i < bins; i++) peaks[i] = MathF.Sqrt(peaks[i] / top);
        return peaks;
    }

    /// <summary>
    /// Lossless trim: no re-encoding, every stream is copied bit for bit. The cut starts at the keyframe before
    /// <paramref name="start"/>; MP4/MOV get an edit list so players still begin exactly at the chosen frame.
    /// </summary>
    public static async Task<(bool ok, string error)> Trim(string input, string output, double start, double end, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Available) return (false, "ffmpeg.exe not found");
        double dur = Math.Max(0.05, end - start);
        var ext = Path.GetExtension(input).ToLowerInvariant();

        List<string> Build(bool withSubs)
        {
            var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
            if (start > 0.001) a.AddRange(new[] { "-ss", F(start) });
            a.AddRange(new[] { "-i", input, "-t", F(dur), "-map", "0:v?", "-map", "0:a?" });
            if (withSubs) a.AddRange(new[] { "-map", "0:s?" });
            if (withSubs && ext is ".mkv" or ".mka" or ".mk3d") a.AddRange(new[] { "-map", "0:t?" });
            a.AddRange(new[] { "-c", "copy", "-map_metadata", "0", "-map_chapters", "0" });
            if (!MediaFormats.IsMp4Family(input)) a.AddRange(new[] { "-avoid_negative_ts", "make_zero" });
            else a.AddRange(new[] { "-movflags", "+use_metadata_tags" });
            if (ext == ".mp3") a.AddRange(new[] { "-id3v2_version", "3", "-write_xing", "1" });
            a.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
            return a;
        }

        var (ok, err) = await RunTrim(Build(withSubs: true), dur, progress, ct);
        if (!ok && !ct.IsCancellationRequested)
        {
            Log.Warn($"Trim with subtitles failed ({err}), retrying without");
            (ok, err) = await RunTrim(Build(withSubs: false), dur, progress, ct);
        }
        if (!ok) { try { if (File.Exists(output)) File.Delete(output); } catch { /* ignore */ } }
        return (ok, err);
    }

    /// <summary>
    /// Keyframe timestamps (seconds) of the first video stream. Reads packets from the container without decoding
    /// (the framecrc muxer marks every non-keyframe with "F=..."), so it is about as fast as reading the file.
    /// </summary>
    public static async Task<double[]?> Keyframes(string file, CancellationToken ct) => (await KeyframeScan(file, ct)).Keyframes;

    /// <summary>
    /// Keyframe times of the first video track, and whether its frames are stored out of display order (B-frames) -
    /// stream-copied pieces of such a video don't join frame-exactly.
    /// </summary>
    public static async Task<(double[]? Keyframes, bool Reordered)> KeyframeScan(string file, CancellationToken ct)
    {
        if (!Available) return (null, true);
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", file, "-map", "0:v:0", "-c", "copy", "-f", "framecrc", "pipe:1" };
        using var p = Process.Start(Start(args));
        if (p == null) return (null, true);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* exited already */ }
        var errTask = p.StandardError.ReadToEndAsync(ct);
        var list = new List<double>();
        double tbNum = 1, tbDen = 1000;
        long maxPts = long.MinValue;
        bool reordered = false;
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (line.StartsWith("#tb 0:"))
                {
                    var frac = line[6..].Trim().Split('/');
                    if (frac.Length == 2 && double.TryParse(frac[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
                        double.TryParse(frac[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                    { tbNum = n; tbDen = d; }
                    continue;
                }
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split(',');
                if (parts.Length < 6) continue;
                bool key = true;
                if (parts.Length >= 7)
                {
                    var f = parts[6].Trim();
                    if (f.StartsWith("F=0x") && int.TryParse(f[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int flags))
                        key = (flags & 1) != 0 && (flags & 4) == 0;
                }
                if (!long.TryParse(parts[2].Trim(), out long pts)) continue;
                if (pts < maxPts) reordered = true;
                maxPts = Math.Max(maxPts, pts);
                if (key) list.Add(pts * tbNum / tbDen);
            }
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* gone */ }
            return (null, true);
        }
        if (list.Count == 0) return (null, reordered);
        list.Sort();
        return (list.ToArray(), reordered);
    }

    /// <summary>
    /// Removes [<paramref name="a"/>, <paramref name="b"/>] and joins what is left - again without re-encoding.
    /// Both halves are stream-copied into temporary files next to the output (each one gets clean timestamps,
    /// B-frame tails included) and then concatenated. <paramref name="b"/> should sit on a keyframe.
    /// </summary>
    public static async Task<(bool ok, string error)> CutOut(string input, string output, double a, double b, double duration, IProgress<double>? progress, CancellationToken ct, double[]? keyframes = null)
    {
        if (!Available) return (false, "ffmpeg.exe not found");
        if (a <= 0.05) return await Trim(input, output, b, duration, progress, ct);
        if (b >= duration - 0.05) return await Trim(input, output, 0, a, progress, ct);

        var ext = Path.GetExtension(input).ToLowerInvariant();
        var dir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var part1 = Path.Combine(dir, $".lf-part1-{tag}{ext}");
        var part2 = Path.Combine(dir, $".lf-part2-{tag}{ext}");
        var list = Path.Combine(dir, $".lf-list-{tag}.ffconcat");
        double len1 = a, len2 = Math.Max(0.05, duration - b), total = len1 + len2;

        // Where to seek so the second half starts exactly on the keyframe at b: aim between b and the next keyframe
        // (seeking just past a keyframe can land one keyframe earlier in some containers, e.g. Matroska).
        double seek2 = b + 0.001;
        if (keyframes is { Length: > 0 })
        {
            int i = Array.BinarySearch(keyframes, b - 0.0005);
            if (i < 0) i = ~i;
            if (i < keyframes.Length && Math.Abs(keyframes[i] - b) < 0.01)
            {
                double next = i + 1 < keyframes.Length ? keyframes[i + 1] : b + 1.0;
                seek2 = b + Math.Clamp((next - b) / 2, 0.001, 0.5);
            }
        }

        List<string> Piece(string outFile, double from, double? len, bool withSubs)
        {
            var x = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
            if (from > 0.001) x.AddRange(new[] { "-ss", F(from) });
            x.AddRange(new[] { "-i", input });
            if (len is double l) x.AddRange(new[] { "-t", F(l) });
            x.AddRange(new[] { "-map", "0:v?", "-map", "0:a?" });
            if (withSubs) x.AddRange(new[] { "-map", "0:s?" });
            x.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero", "-progress", "pipe:1", "-nostats", outFile });
            return x;
        }

        List<string> Join(bool withSubs)
        {
            var x = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                "-f", "concat", "-safe", "0", "-i", list,
                "-t", "0", "-i", input,                       // second input only lends its metadata
                "-map", "0:v?", "-map", "0:a?" };
            if (withSubs) x.AddRange(new[] { "-map", "0:s?" });
            x.AddRange(new[] { "-c", "copy", "-map_metadata", "1", "-avoid_negative_ts", "make_zero" });
            if (MediaFormats.IsMp4Family(input)) x.AddRange(new[] { "-movflags", "+use_metadata_tags" });
            if (ext == ".mp3") x.AddRange(new[] { "-id3v2_version", "3", "-write_xing", "1" });
            x.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
            return x;
        }

        IProgress<double> Slice(double from, double span) => new Progress<double>(v => progress?.Report(from + v * span));

        async Task<(bool, string)> Attempt(bool withSubs)
        {
            var r = await RunTrim(Piece(part1, 0, len1, withSubs), len1, Slice(0, 0.45 * len1 / total), ct);
            if (!r.Item1) return r;
            // +1 ms so the seek lands on the keyframe at b itself, not the one before
            r = await RunTrim(Piece(part2, seek2, null, withSubs), len2, Slice(0.45 * len1 / total, 0.45 * len2 / total), ct);
            if (!r.Item1) return r;
            static string Esc(string p) => p.Replace('\\', '/').Replace("'", "'\\''");
            File.WriteAllText(list, $"ffconcat version 1.0\nfile '{Esc(part1)}'\nfile '{Esc(part2)}'\n");
            return await RunTrim(Join(withSubs), total, Slice(0.45, 0.55), ct);
        }

        try
        {
            var (ok, err) = await Attempt(withSubs: true);
            if (!ok && !ct.IsCancellationRequested)
            {
                Log.Warn($"Cut with subtitles failed ({err}), retrying without");
                (ok, err) = await Attempt(withSubs: false);
            }
            if (!ok) { try { if (File.Exists(output)) File.Delete(output); } catch { /* ignore */ } }
            return (ok, err);
        }
        finally
        {
            foreach (var f in new[] { part1, part2, list }) { try { if (File.Exists(f)) File.Delete(f); } catch { /* temp */ } }
        }
    }

    /// <summary>
    /// Keeps several pieces of one file and joins them - without re-encoding (the general case of <see cref="CutOut"/>).
    /// FFmpeg's concat list reads the pieces straight from the file (in and out points), so every piece lasts exactly as
    /// long as it should and no temporary files are needed. Every piece after the first must start on a keyframe, and the
    /// video must not have B-frames (<see cref="KeyframeScan"/>) - then the joins are exact to the frame.
    /// </summary>
    public static async Task<(bool ok, string error)> CopyPieces(string input, string output, List<(double In, double Out)> pieces, double[]? keyframes, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Available) return (false, "ffmpeg.exe not found");
        if (pieces.Count == 1) return await Trim(input, output, pieces[0].In, pieces[0].Out, progress, ct);
        var ext = Path.GetExtension(input).ToLowerInvariant();
        var dir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var list = Path.Combine(dir, $".lf-list-{Guid.NewGuid().ToString("N")[..8]}.ffconcat");
        double total = pieces.Sum(p => p.Out - p.In);
        static string T(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        static string Esc(string p) => p.Replace('\\', '/').Replace("'", "'\\''");

        var sb = new StringBuilder("ffconcat version 1.0\n");
        string src = Esc(Path.GetFullPath(input));
        foreach (var (a, b) in pieces)
        {
            sb.Append($"file '{src}'\n");
            if (a > 0.0005) sb.Append($"inpoint {T(a)}\n");
            sb.Append($"outpoint {T(b)}\n");
        }

        List<string> Join(bool withSubs)
        {
            var x = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                "-f", "concat", "-safe", "0", "-i", list, "-t", "0", "-i", input, "-map", "0:v?", "-map", "0:a?" };
            if (withSubs) x.AddRange(new[] { "-map", "0:s?" });
            x.AddRange(new[] { "-c", "copy", "-map_metadata", "1" });
            // MP4/MOV: an edit list hides what comes before the first piece's start (it may begin between keyframes)
            if (!MediaFormats.IsMp4Family(input)) x.AddRange(new[] { "-avoid_negative_ts", "make_zero" });
            else x.AddRange(new[] { "-movflags", "+use_metadata_tags" });
            if (ext == ".mp3") x.AddRange(new[] { "-id3v2_version", "3", "-write_xing", "1" });
            x.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
            return x;
        }

        try
        {
            await File.WriteAllTextAsync(list, sb.ToString(), ct);
            var (ok, err) = await RunTrim(Join(withSubs: true), total, progress, ct);
            if (!ok && !ct.IsCancellationRequested)
            {
                Log.Warn($"Piece copy with subtitles failed ({err}), retrying without");
                (ok, err) = await RunTrim(Join(withSubs: false), total, progress, ct);
            }
            if (!ok) { try { if (File.Exists(output)) File.Delete(output); } catch { /* ignore */ } }
            return (ok, err);
        }
        finally
        {
            try { if (File.Exists(list)) File.Delete(list); } catch { /* temp */ }
        }
    }

    private static async Task<(bool, string)> RunTrim(List<string> args, double dur, IProgress<double>? progress, CancellationToken ct)
    {
        using var p = Process.Start(Start(args));
        if (p == null) return (false, "ffmpeg did not start");
        var err = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
                if (err.Length < 4000) err.AppendLine(line);
        });
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                {
                    if (long.TryParse(line.AsSpan(line.IndexOf('=') + 1), out long us) && dur > 0)
                        progress?.Report(Math.Clamp(us / 1_000_000.0 / dur, 0, 1));
                }
            }
            await p.WaitForExitAsync(ct);
            await errTask;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* gone */ }
            return (false, "cancelled");
        }
        string e = err.ToString().Trim();
        if (p.ExitCode != 0) Log.Warn("ffmpeg trim failed: " + e);
        return (p.ExitCode == 0, e.Length > 300 ? e[^300..] : e);
    }
}
