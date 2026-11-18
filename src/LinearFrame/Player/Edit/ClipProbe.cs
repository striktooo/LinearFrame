using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>What a media file holds (length, picture size as shown, frame rate, sound) - read by libmpv without playing it.</summary>
internal static unsafe class ClipProbe
{
    /// <summary>The file already open in the player.</summary>
    public static EditClip FromPlayer(MpvPlayer mpv, string path) =>
        Parse(path, mpv.GetDouble("duration") ?? mpv.Duration, mpv.GetString("track-list"), mpv.GetDouble("container-fps"));

    public static Task<EditClip?> ProbeAsync(string path) => Task.Run(() => Probe(path));

    private static EditClip? Probe(string path)
    {
        if (!MpvNative.TryLoad(out _)) return null;
        var ctx = MpvNative.mpv_create();
        if (ctx == IntPtr.Zero) return null;
        try
        {
            void Opt(string n, string v)
            {
                fixed (byte* a = Z(n)) fixed (byte* b = Z(v)) MpvNative.mpv_set_option_string(ctx, a, b);
            }
            Opt("vo", "null");
            Opt("ao", "null");
            Opt("idle", "yes");
            Opt("pause", "yes");
            Opt("load-scripts", "no");
            Opt("ytdl", "no");
            Opt("sub-auto", "no");
            if (MpvNative.mpv_initialize(ctx) < 0) return null;
            var argv = new[] { "loadfile", path };
            var ptrs = argv.Select(Marshal.StringToCoTaskMemUTF8).Append(IntPtr.Zero).ToArray();
            try { fixed (IntPtr* p = ptrs) MpvNative.mpv_command(ctx, p); }
            finally { foreach (var q in ptrs) if (q != IntPtr.Zero) Marshal.FreeCoTaskMem(q); }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                var ev = MpvNative.mpv_wait_event(ctx, 0.5);
                if (ev->EventId == MpvEventId.EndFile) return null;
                if (ev->EventId != MpvEventId.FileLoaded) continue;
                double dur = 0;
                fixed (byte* n = Z("duration")) MpvNative.mpv_get_property(ctx, n, MpvFormat.Double, &dur);
                double fps = 0;
                fixed (byte* n = Z("container-fps")) MpvNative.mpv_get_property(ctx, n, MpvFormat.Double, &fps);
                string? tracks = null;
                fixed (byte* n = Z("track-list"))
                {
                    var s = MpvNative.mpv_get_property_string(ctx, n);
                    if (s != IntPtr.Zero) { tracks = Marshal.PtrToStringUTF8(s); MpvNative.mpv_free(s); }
                }
                return Parse(path, dur, tracks, fps > 0 ? fps : null);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Probe {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
        finally { MpvNative.mpv_terminate_destroy(ctx); }
    }

    private static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    private static EditClip Parse(string path, double duration, string? tracksJson, double? containerFps)
    {
        int w = 0, h = 0, rot = 0;
        double fps = containerFps ?? 0;
        bool video = false, audio = false;
        try
        {
            if (!string.IsNullOrEmpty(tracksJson))
            {
                using var doc = JsonDocument.Parse(tracksJson);
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    string type = t.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                    bool art = t.TryGetProperty("albumart", out var a) && a.ValueKind == JsonValueKind.True;
                    if (type == "audio") audio = true;
                    if (type != "video" || art || video) continue;
                    video = true;
                    if (t.TryGetProperty("demux-w", out var dw)) w = dw.GetInt32();
                    if (t.TryGetProperty("demux-h", out var dh)) h = dh.GetInt32();
                    if (t.TryGetProperty("demux-rotation", out var dr) && dr.TryGetInt32(out int r)) rot = ((r % 360) + 360) % 360;
                    if (t.TryGetProperty("demux-fps", out var df) && df.TryGetDouble(out double f) && f > 0) fps = f;
                }
            }
        }
        catch (Exception ex) { Log.Warn("track-list: " + ex.Message); }
        if (rot is 90 or 270) (w, h) = (h, w);
        if (fps <= 0 || !double.IsFinite(fps)) fps = 30;
        return new EditClip
        {
            Path = path,
            Duration = duration > 0 ? duration : 0,
            Width = w,
            Height = h,
            Fps = fps,
            HasVideo = video && w > 0 && h > 0,
            HasAudio = audio,
        };
    }

    public static string Describe(EditClip c) => c.HasVideo
        ? $"{c.Width}x{c.Height} {c.Fps.ToString("0.##", CultureInfo.InvariantCulture)} fps {c.Duration:0.00}s{(c.HasAudio ? " +audio" : "")}"
        : $"audio {c.Duration:0.00}s";
}
