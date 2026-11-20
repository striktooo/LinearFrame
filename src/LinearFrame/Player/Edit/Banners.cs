using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// Banner files (e.g. a ProRes 4444 .mov with transparency): analysed once - length, frame rate, sound, and where the
/// banner itself sits on its transparent canvas. The live preview decodes it with <see cref="BannerStream"/>.
/// </summary>
internal static partial class Banners
{
    private sealed record Cached(string Path, long Length, long Stamp, double Duration, int Width, int Height, double Fps, int X, int Y, int W, int H, bool Audio);

    private static string IndexFile => Path.Combine(AppPaths.LocalDir, "banners.json");

    public static async Task<BannerInfo?> AnalyzeAsync(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return null;
        var cache = Load();
        var hit = cache.FirstOrDefault(c => string.Equals(c.Path, fi.FullName, StringComparison.OrdinalIgnoreCase) && c.Length == fi.Length && c.Stamp == fi.LastWriteTimeUtc.Ticks);
        if (hit != null) return ToInfo(hit);

        var clip = await ClipProbe.ProbeAsync(fi.FullName);
        if (clip == null || !clip.HasVideo || clip.Duration <= 0.1) return null;
        var sw = Stopwatch.StartNew();
        var box = await ContentBox(fi.FullName, clip.Width, clip.Height) ?? new Int32Rect(0, 0, clip.Width, clip.Height);
        Log.Info($"Banner {fi.Name}: {ClipProbe.Describe(clip)}, picture at {box.X},{box.Y} {box.Width}x{box.Height} (found in {sw.ElapsedMilliseconds} ms)");
        var entry = new Cached(fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks, clip.Duration, clip.Width, clip.Height, clip.Fps, box.X, box.Y, box.Width, box.Height, clip.HasAudio);
        cache.RemoveAll(c => string.Equals(c.Path, fi.FullName, StringComparison.OrdinalIgnoreCase));
        cache.Add(entry);
        Save(cache.TakeLast(20).ToList());
        return ToInfo(entry);
    }

    private static BannerInfo ToInfo(Cached c) => new(c.Path, c.Length, new DateTime(c.Stamp, DateTimeKind.Utc), c.Duration, c.Width, c.Height, c.Fps,
        new Int32Rect(c.X, c.Y, c.W, c.H), c.Audio);

    private static List<Cached> Load()
    {
        try { return File.Exists(IndexFile) ? JsonSerializer.Deserialize<List<Cached>>(File.ReadAllText(IndexFile)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void Save(List<Cached> list)
    {
        try { File.WriteAllText(IndexFile, JsonSerializer.Serialize(list)); } catch { /* cache only */ }
    }

    [GeneratedRegex(@"x1:(-?\d+) x2:(-?\d+) y1:(-?\d+) y2:(-?\d+)")]
    private static partial Regex BoxLine();

    /// <summary>Union of the non-transparent area over all frames (FFmpeg's bbox on the alpha plane, at a quarter size).</summary>
    private static async Task<Int32Rect?> ContentBox(string path, int w, int h)
    {
        const int probeW = 480;
        double k = w / (double)probeW;
        var psi = new ProcessStartInfo(Ffmpeg.Exe!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-i", path, "-an", "-vf", $"alphaextract,scale={probeW}:-1:flags=area,bbox=min_val=24", "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        p.StandardOutput.ReadToEndAsync().Forget();
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = -1, y2 = -1;
        string? line;
        while ((line = await p.StandardError.ReadLineAsync()) != null)
        {
            var m = BoxLine().Match(line);
            if (!m.Success) continue;
            x1 = Math.Min(x1, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            x2 = Math.Max(x2, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            y1 = Math.Min(y1, int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            y2 = Math.Max(y2, int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
        }
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 || x2 < x1 || y2 < y1) return null; // no alpha channel: the whole frame is the banner
        // back to full size, a few pixels of air around it, even numbers for the encoder
        int X = Math.Max(0, (int)Math.Floor(x1 * k) - 4) & ~1, Y = Math.Max(0, (int)Math.Floor(y1 * k) - 4) & ~1;
        int R = Math.Min(w, (int)Math.Ceiling((x2 + 1) * k) + 4), B = Math.Min(h, (int)Math.Ceiling((y2 + 1) * k) + 4);
        int W = Math.Max(2, (R - X) & ~1), H = Math.Max(2, (B - Y) & ~1);
        return new Int32Rect(X, Y, Math.Min(W, w - X), Math.Min(H, h - Y));
    }

    /// <summary>UpdateLayeredWindow wants premultiplied alpha.</summary>
    internal static void Premultiply(byte[] px)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            if (a == 0) { px[i] = px[i + 1] = px[i + 2] = 0; continue; }
            px[i] = (byte)((px[i] * a + 127) / 255);
            px[i + 1] = (byte)((px[i + 1] * a + 127) / 255);
            px[i + 2] = (byte)((px[i + 2] * a + 127) / 255);
        }
    }

    /// <summary>The banner's sound as a WAV for the preview (WPF's player can't read ProRes containers).</summary>
    public static async Task<string?> AudioAsync(BannerInfo b)
    {
        if (!b.HasAudio) return null;
        var dir = Path.Combine(Path.GetTempPath(), "LinearFrame");
        Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{b.Path}|{b.Length}|{b.Stamp.Ticks}")))[..12];
        var wav = Path.Combine(dir, $"banner-{key}.wav");
        if (File.Exists(wav)) return wav;
        var psi = new ProcessStartInfo(Ffmpeg.Exe!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", b.Path, "-vn", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", wav })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        p.StandardError.ReadToEndAsync().Forget();
        await p.WaitForExitAsync();
        return p.ExitCode == 0 && File.Exists(wav) ? wav : null;
    }
}
