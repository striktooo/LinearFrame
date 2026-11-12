using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using LinearFrame.Core;

namespace LinearFrame.Player;

internal enum MpvFormat { None = 0, String = 1, OsdString = 2, Flag = 3, Int64 = 4, Double = 5, Node = 6 }

internal enum MpvEventId
{
    None = 0, Shutdown = 1, LogMessage = 2, GetPropertyReply = 3, SetPropertyReply = 4, CommandReply = 5,
    StartFile = 6, EndFile = 7, FileLoaded = 8, Idle = 11, Tick = 14, ClientMessage = 16, VideoReconfig = 17,
    AudioReconfig = 18, Seek = 20, PlaybackRestart = 21, PropertyChange = 22, QueueOverflow = 24, Hook = 25,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public int Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

/// <summary>Raw libmpv client API (libmpv-2.dll from the engine folder).</summary>
internal static unsafe class MpvNative
{
    private const string Lib = "libmpv-2.dll";
    private static bool _resolverSet;
    private static IntPtr _handle;

    public static bool TryLoad(out string? error)
    {
        error = null;
        if (_handle != IntPtr.Zero) return true;
        var path = AppPaths.FindEngineFile(Lib);
        if (path == null) { error = "libmpv-2.dll not found"; return false; }
        try
        {
            _handle = NativeLibrary.Load(path);
            if (!_resolverSet)
            {
                NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
                    name == Lib ? _handle : IntPtr.Zero);
                _resolverSet = true;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error("libmpv load failed", ex);
            return false;
        }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern ulong mpv_client_api_version();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_option_string(IntPtr ctx, byte* name, byte* data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command(IntPtr ctx, IntPtr* args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command_async(IntPtr ctx, ulong userdata, IntPtr* args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property_string(IntPtr ctx, byte* name, byte* data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte* name, MpvFormat format, void* data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_get_property_string(IntPtr ctx, byte* name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_observe_property(IntPtr ctx, ulong userdata, byte* name, MpvFormat format);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern MpvEvent* mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_wakeup(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_free(IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_error_string(int error);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_request_log_messages(IntPtr ctx, byte* minLevel);
}

public sealed record MpvTrack(long Id, string Type, string? Title, string? Lang, string? Codec, bool Selected, bool AlbumArt, bool External);

/// <summary>
/// One libmpv instance rendering into a native child window. Events are read on a background thread and
/// coalesced into a single UI notification, so a playing video costs the UI thread almost nothing.
/// </summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    private const ulong IdTime = 1, IdDuration = 2, IdPause = 3, IdVolume = 4, IdMute = 5, IdEof = 6, IdSpeed = 7, IdSeeking = 8, IdTracks = 9, IdIdle = 10;

    private IntPtr _ctx;
    private readonly Thread _thread;
    private readonly Dispatcher _ui;
    private volatile bool _quit;
    private int _notifyPending;
    private long _lastTimeNotify;
    private volatile bool _wantTime = true;

    /// <summary>Minimum time between position notifications (shorter while editing so cuts can be skipped precisely).</summary>
    public int TimeIntervalMs { get; set; } = 120;

    /// <summary>
    /// When false, playback position changes don't wake the UI thread at all (controls hidden = zero UI work while watching).
    /// </summary>
    public bool WantTimeUpdates
    {
        get => _wantTime;
        set { _wantTime = value; if (value) RequestNotify(); }
    }

    public double TimePos { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; }
    public double Volume { get; private set; } = 100;
    public bool Muted { get; private set; }
    public bool EofReached { get; private set; }
    public double Speed { get; private set; } = 1;
    public bool Seeking { get; private set; }
    public bool IdleActive { get; private set; } = true;

    public event Action? StateChanged;
    public event Action? FileLoaded;
    public event Action<int, int>? EndFile;
    public event Action? VideoReconfig;
    public event Action? TracksChanged;
    public event Action? Idle;

    public MpvPlayer(IntPtr wid, Dispatcher ui, double volume, bool muted)
    {
        _ui = ui;
        _ctx = MpvNative.mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");

        void Opt(string name, string value)
        {
            int r = SetOption(name, value);
            if (r < 0) Log.Info($"mpv option {name}={value} -> {r}");
        }

        Opt("wid", wid.ToInt64().ToString());
        Opt("hwdec", "auto-safe");
        Opt("keep-open", "yes");
        Opt("idle", "yes");
        Opt("force-window", "yes");
        Opt("osc", "no");
        Opt("osd-level", "0");
        Opt("input-default-bindings", "no");
        Opt("input-builtin-bindings", "no");
        Opt("input-vo-keyboard", "no");
        Opt("input-cursor", "no");
        Opt("input-media-keys", "no");
        Opt("cursor-autohide", "no");
        Opt("ytdl", "no");
        Opt("load-scripts", "no");
        Opt("load-stats-overlay", "no");
        Opt("load-console", "no");
        Opt("load-osd-console", "no");
        Opt("load-auto-profiles", "no");
        Opt("load-select", "no");
        Opt("load-positioning", "no");
        Opt("load-commands", "no");
        Opt("load-context-menu", "no");
        Opt("drag-and-drop", "no");
        Opt("audio-client-name", "LinearFrame");
        Opt("sub-auto", "fuzzy");
        Opt("audio-file-auto", "no");
        Opt("audio-display", "no");
        Opt("background-color", "#000000");
        Opt("volume", Math.Clamp(volume, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Opt("mute", muted ? "yes" : "no");
        Opt("screenshot-format", "png");
        Opt("screenshot-tag-colorspace", "yes");
        Opt("hr-seek-framedrop", "yes");
        Opt("demuxer-max-back-bytes", "64MiB");
        // developer override: LF_MPV_OPTS="vo=gpu;hwdec=no"
        foreach (var kv in (Environment.GetEnvironmentVariable("LF_MPV_OPTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = kv.IndexOf('=');
            if (eq > 0) Opt(kv[..eq], kv[(eq + 1)..]);
        }

        int init = MpvNative.mpv_initialize(_ctx);
        if (init < 0) throw new InvalidOperationException("mpv_initialize failed: " + ErrorString(init));

        fixed (byte* lvl = Utf8("warn")) MpvNative.mpv_request_log_messages(_ctx, lvl);

        Observe(IdTime, "time-pos", MpvFormat.Double);
        Observe(IdDuration, "duration", MpvFormat.Double);
        Observe(IdPause, "pause", MpvFormat.Flag);
        Observe(IdVolume, "volume", MpvFormat.Double);
        Observe(IdMute, "mute", MpvFormat.Flag);
        Observe(IdEof, "eof-reached", MpvFormat.Flag);
        Observe(IdSpeed, "speed", MpvFormat.Double);
        Observe(IdSeeking, "seeking", MpvFormat.Flag);
        Observe(IdTracks, "track-list/count", MpvFormat.Int64);
        Observe(IdIdle, "idle-active", MpvFormat.Flag);

        _thread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _thread.Start();
    }

    private static byte[] Utf8(string s)
    {
        var b = new byte[System.Text.Encoding.UTF8.GetByteCount(s) + 1];
        System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    private static string ErrorString(int code) => Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(code)) ?? code.ToString();

    private int SetOption(string name, string value)
    {
        fixed (byte* n = Utf8(name)) fixed (byte* v = Utf8(value)) return MpvNative.mpv_set_option_string(_ctx, n, v);
    }

    private void Observe(ulong id, string name, MpvFormat fmt)
    {
        fixed (byte* n = Utf8(name)) MpvNative.mpv_observe_property(_ctx, id, n, fmt);
    }

    // ------------------------------------------------------------------ event loop (background thread)

    private void EventLoop()
    {
        while (!_quit)
        {
            MpvEvent* ev = MpvNative.mpv_wait_event(_ctx, -1);
            if (ev == null) continue;
            switch (ev->EventId)
            {
                case MpvEventId.None:
                    continue;
                case MpvEventId.Shutdown:
                    return;
                case MpvEventId.PropertyChange:
                    HandleProperty(ev->ReplyUserdata, (MpvEventProperty*)ev->Data);
                    break;
                case MpvEventId.FileLoaded:
                    Post(() => FileLoaded?.Invoke());
                    break;
                case MpvEventId.EndFile:
                {
                    var ef = (MpvEventEndFile*)ev->Data;
                    int reason = ef->Reason, error = ef->Error;
                    Post(() => EndFile?.Invoke(reason, error));
                    break;
                }
                case MpvEventId.VideoReconfig:
                    Post(() => VideoReconfig?.Invoke());
                    break;
                case MpvEventId.LogMessage:
                {
                    var lm = (MpvEventLogMessage*)ev->Data;
                    var text = Marshal.PtrToStringUTF8(lm->Text)?.TrimEnd();
                    var prefix = Marshal.PtrToStringUTF8(lm->Prefix);
                    if (!string.IsNullOrEmpty(text)) Log.Warn($"mpv[{prefix}] {text}");
                    break;
                }
            }
        }
    }

    private void HandleProperty(ulong id, MpvEventProperty* p)
    {
        bool has = p->Format != MpvFormat.None && p->Data != IntPtr.Zero;
        switch (id)
        {
            case IdTime:
                TimePos = has ? *(double*)p->Data : 0;
                // position ticks every frame: deliver at most ~8×/s and only when someone looks at it
                if (!_wantTime) return;
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastTimeNotify) < TimeIntervalMs) return;
                Interlocked.Exchange(ref _lastTimeNotify, now);
                break;
            case IdDuration: Duration = has ? *(double*)p->Data : 0; break;
            case IdPause: Paused = has && *(int*)p->Data != 0; break;
            case IdVolume: if (has) Volume = *(double*)p->Data; break;
            case IdMute: Muted = has && *(int*)p->Data != 0; break;
            case IdEof: EofReached = has && *(int*)p->Data != 0; break;
            case IdSpeed: if (has) Speed = *(double*)p->Data; break;
            case IdSeeking: Seeking = has && *(int*)p->Data != 0; break;
            case IdTracks: Post(() => TracksChanged?.Invoke()); return;
            case IdIdle:
                IdleActive = has && *(int*)p->Data != 0;
                if (IdleActive) Post(() => Idle?.Invoke());
                break;
        }
        RequestNotify();
    }

    private void RequestNotify()
    {
        if (Interlocked.Exchange(ref _notifyPending, 1) == 1) return;
        _ui.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _notifyPending, 0);
            if (!_quit) StateChanged?.Invoke();
        }));
    }

    private void Post(Action a) => _ui.BeginInvoke(DispatcherPriority.Normal, new Action(() => { if (!_quit) a(); }));

    // ------------------------------------------------------------------ API (any thread)

    public int Command(params string[] args) => RunCommand(args, async: false);
    public int CommandAsync(params string[] args) => RunCommand(args, async: true);

    private int RunCommand(string[] args, bool async)
    {
        if (_ctx == IntPtr.Zero) return -1;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            fixed (IntPtr* p = ptrs)
                return async ? MpvNative.mpv_command_async(_ctx, 0, p) : MpvNative.mpv_command(_ctx, p);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++) if (ptrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(ptrs[i]);
        }
    }

    public int SetProperty(string name, string value)
    {
        if (_ctx == IntPtr.Zero) return -1;
        fixed (byte* n = Utf8(name)) fixed (byte* v = Utf8(value)) return MpvNative.mpv_set_property_string(_ctx, n, v);
    }

    public string? GetString(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        IntPtr r;
        fixed (byte* n = Utf8(name)) r = MpvNative.mpv_get_property_string(_ctx, n);
        if (r == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(r); }
        finally { MpvNative.mpv_free(r); }
    }

    public double? GetDouble(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        double v;
        fixed (byte* n = Utf8(name)) return MpvNative.mpv_get_property(_ctx, n, MpvFormat.Double, &v) >= 0 ? v : null;
    }

    public long? GetLong(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        long v;
        fixed (byte* n = Utf8(name)) return MpvNative.mpv_get_property(_ctx, n, MpvFormat.Int64, &v) >= 0 ? v : null;
    }

    public List<MpvTrack> Tracks()
    {
        var list = new List<MpvTrack>();
        var json = GetString("track-list");
        if (string.IsNullOrEmpty(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                string? S(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool B(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                long id = t.TryGetProperty("id", out var idv) && idv.TryGetInt64(out var l) ? l : 0;
                list.Add(new MpvTrack(id, S("type") ?? "", S("title"), S("lang"), S("codec"), B("selected"), B("albumart"), B("external")));
            }
        }
        catch (Exception ex) { Log.Error("track-list parse failed", ex); }
        return list;
    }

    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        _quit = true;
        MpvNative.mpv_wakeup(_ctx);
        _thread.Join(1500);
        var ctx = _ctx;
        _ctx = IntPtr.Zero;
        MpvNative.mpv_terminate_destroy(ctx);
    }
}
