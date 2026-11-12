using System.Runtime.InteropServices;
using System.Windows;

namespace LinearFrame.Core;

/// <summary>One active monitor as Windows sees it right now.</summary>
public sealed record DisplayState(
    string Device, string Name, string Adapter,
    int X, int Y, int Width, int Height, int Frequency, bool Primary,
    int NativeWidth, int NativeHeight, int ScalePercent,
    int DoubleFrequency) // refresh rate of the 2× desktop mode (DSR / VSR), 0 = the driver offers none
{
    /// <summary>Desktop rendered at 2× the panel's own resolution (DSR 4.00x / VSR) - i.e. Retina mode is on here.</summary>
    public bool IsDoubled => NativeWidth > 0 && Width >= NativeWidth * 2 && Height >= NativeHeight * 2;

    /// <summary>How many real pixels a screenshot gets per "normal" pixel: 1× at 100 %, 2× at 200 %.</summary>
    public double Detail => ScalePercent / 100.0;

    /// <summary>Retina mode can be switched on for this monitor.</summary>
    public bool CanDouble => !IsDoubled && DoubleFrequency > 0 && ScalePercent < 200;

    public string Rect => $"{Width}×{Height}";
}

public sealed record RetinaStatus(IReadOnlyList<DisplayState> Displays)
{
    public bool IsOn => Displays.Any(d => d.IsDoubled);
    public bool CanToggle => IsOn || Displays.Any(d => d.CanDouble);
    /// <summary>Every screen already renders at 2× or more (Retina on, or real HiDPI monitors).</summary>
    public bool AllSharp => Displays.Count > 0 && Displays.All(d => d.ScalePercent >= 200);
    public string Vendor
    {
        get
        {
            string a = string.Join(" ", Displays.Select(d => d.Adapter));
            if (a.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "nvidia";
            if (a.Contains("AMD", StringComparison.OrdinalIgnoreCase) || a.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "amd";
            if (a.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "intel";
            return "other";
        }
    }
}

/// <summary>
/// "Retina mode" - exactly what a Mac does: the desktop is rendered at twice the resolution (1920×1080 → 3840×2160)
/// with 200 % scaling, so everything keeps its size on screen, but every app draws its text, icons and vectors with
/// 4× as many real pixels. The graphics card (NVIDIA DSR / AMD VSR) scales the picture down for the monitor.
/// Screenshots then simply contain the real high-resolution rendering - no upscaling, no AI.
/// </summary>
public static class Retina
{
    private static readonly int[] DpiSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    // ------------------------------------------------------------------ query

    public static RetinaStatus Query()
    {
        try { return new RetinaStatus(QueryDisplays()); }
        catch (Exception ex)
        {
            Log.Error("Display query failed", ex);
            return new RetinaStatus(Array.Empty<DisplayState>());
        }
    }

    private sealed record PathIds(LUID Adapter, uint SourceId, LUID TargetAdapter, uint TargetId);

    private static Dictionary<string, PathIds> ActivePaths()
    {
        var result = new Dictionary<string, PathIds>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
        var paths = new DISPLAYCONFIG_PATH_INFO[np];
        var modes = new DISPLAYCONFIG_MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;
        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            src.header.type = 1; // GET_SOURCE_NAME
            src.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            src.header.adapterId = p.sourceInfo.adapterId;
            src.header.id = p.sourceInfo.id;
            if (DisplayConfigGetDeviceInfo(ref src) != 0 || string.IsNullOrEmpty(src.viewGdiDeviceName)) continue;
            result.TryAdd(src.viewGdiDeviceName, new PathIds(p.sourceInfo.adapterId, p.sourceInfo.id, p.targetInfo.adapterId, p.targetInfo.id));
        }
        return result;
    }

    private static List<DisplayState> QueryDisplays()
    {
        var paths = ActivePaths();
        var list = new List<DisplayState>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() })
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var cur = NewDevMode();
            if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;

            int nativeW = (int)cur.dmPelsWidth, nativeH = (int)cur.dmPelsHeight;
            string name = "";
            int scale = 0;
            if (paths.TryGetValue(dd.DeviceName, out var ids))
            {
                var pref = new DISPLAYCONFIG_TARGET_PREFERRED_MODE();
                pref.header.type = 3; // GET_TARGET_PREFERRED_MODE
                pref.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_PREFERRED_MODE>();
                pref.header.adapterId = ids.TargetAdapter;
                pref.header.id = ids.TargetId;
                if (DisplayConfigGetDeviceInfo(ref pref) == 0 && pref.width > 0) { nativeW = (int)pref.width; nativeH = (int)pref.height; }

                var tn = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tn.header.type = 2; // GET_TARGET_NAME
                tn.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                tn.header.adapterId = ids.TargetAdapter;
                tn.header.id = ids.TargetId;
                if (DisplayConfigGetDeviceInfo(ref tn) == 0) name = tn.monitorFriendlyDeviceName ?? "";

                scale = GetScale(ids).Current;
            }
            if (scale <= 0) scale = ScaleFromMonitor(cur.dmPositionX, cur.dmPositionY);

            // The 2× desktop mode the driver offers (DSR / VSR): today's refresh rate if possible, else the closest lower one.
            // (Listing the modes takes ~40 ms per monitor, so callers on the UI thread use App.RetinaState, refreshed in the background.)
            int doubleHz = 0;
            if (nativeW > 0 && cur.dmPelsWidth < nativeW * 2)
            {
                int curHz = (int)cur.dmDisplayFrequency, below = 0, any = 0;
                var m = NewDevMode();
                for (int k = 0; EnumDisplaySettings(dd.DeviceName, k, ref m); k++)
                {
                    if (m.dmPelsWidth != nativeW * 2 || m.dmPelsHeight != nativeH * 2 || m.dmBitsPerPel < 24) continue;
                    int hz = (int)m.dmDisplayFrequency;
                    if (hz == curHz) { doubleHz = hz; break; }
                    if (hz < curHz) below = Math.Max(below, hz);
                    any = Math.Max(any, hz);
                }
                if (doubleHz == 0) doubleHz = below > 0 ? below : any;
            }

            list.Add(new DisplayState(
                dd.DeviceName, string.IsNullOrWhiteSpace(name) ? dd.DeviceName.Replace(@"\\.\", "") : name, dd.DeviceString,
                cur.dmPositionX, cur.dmPositionY, (int)cur.dmPelsWidth, (int)cur.dmPelsHeight, (int)cur.dmDisplayFrequency,
                (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                nativeW, nativeH, scale, doubleHz));
        }
        return list;
    }

    private static (int Current, int Recommended, int Max, int MinRel, int MaxRel) GetScale(PathIds ids)
    {
        var g = new DPI_GET();
        g.header.type = -3; // GET_DPI_SCALE (undocumented, used by Windows' own Settings app)
        g.header.size = Marshal.SizeOf<DPI_GET>();
        g.header.adapterId = ids.Adapter;
        g.header.id = ids.SourceId;
        if (DisplayConfigGetDeviceInfo(ref g) != 0) return (0, 0, 0, 0, 0);
        int baseIdx = Math.Abs(g.minScaleRel);
        int Step(int rel) => DpiSteps[Math.Clamp(baseIdx + rel, 0, DpiSteps.Length - 1)];
        return (Step(g.curScaleRel), Step(0), Step(g.maxScaleRel), g.minScaleRel, g.maxScaleRel);
    }

    private static int ScaleFromMonitor(int x, int y)
    {
        var mon = Native.MonitorFromPoint(new Native.POINT(x + 1, y + 1), Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0 ? (int)Math.Round(dpi * 100 / 96.0) : 100;
    }

    private static bool SetScale(string device, int percent)
    {
        if (!ActivePaths().TryGetValue(device, out var ids)) return false;
        var (cur, _, _, minRel, maxRel) = GetScale(ids);
        if (cur == percent) return true;
        int target = Array.IndexOf(DpiSteps, percent);
        if (target < 0) target = Array.FindLastIndex(DpiSteps, v => v <= percent);
        if (target < 0) return false;
        int rel = Math.Clamp(target - Math.Abs(minRel), minRel, maxRel);
        var s = new DPI_SET();
        s.header.type = -4; // SET_DPI_SCALE
        s.header.size = Marshal.SizeOf<DPI_SET>();
        s.header.adapterId = ids.Adapter;
        s.header.id = ids.SourceId;
        s.scaleRel = rel;
        int r = DisplayConfigSetDeviceInfo(ref s);
        Log.Info($"Scale {device} → {percent} % (rel {rel}): {r}");
        return r == 0;
    }

    // ------------------------------------------------------------------ switching

    public sealed record Result(bool Ok, string? Error = null);

    private sealed record Change(DisplayState Display, int Width, int Height, int Frequency, int ScalePercent);

    /// <summary>Retina mode on for every monitor that supports it. Blocking (a mode change takes a second or two).</summary>
    public static Result Enable(AppSettings s)
    {
        var displays = QueryDisplays();
        if (!displays.Any(d => d.CanDouble)) return new Result(false, L.T("Retina.NoMode"));
        s.RetinaRestore = displays.Select(d => new SavedDisplay
        {
            Device = d.Device, Width = d.Width, Height = d.Height, Frequency = d.Frequency, X = d.X, Y = d.Y, ScalePercent = d.ScalePercent,
        }).ToList();
        var changes = displays.Select(d => d.CanDouble
            ? new Change(d, d.NativeWidth * 2, d.NativeHeight * 2, d.DoubleFrequency, Math.Min(d.ScalePercent * 2, 500))
            : new Change(d, d.Width, d.Height, d.Frequency, d.ScalePercent)).ToList();
        var r = Apply(changes);
        if (!r.Ok) s.RetinaRestore = null;
        return r;
    }

    /// <summary>Back to the monitors' normal resolution and scaling.</summary>
    public static Result Disable(AppSettings s)
    {
        var displays = QueryDisplays();
        var saved = s.RetinaRestore;
        var changes = new List<Change>();
        foreach (var d in displays)
        {
            var old = saved?.FirstOrDefault(x => string.Equals(x.Device, d.Device, StringComparison.OrdinalIgnoreCase));
            if (d.IsDoubled)
            {
                bool useSaved = old != null && old.Width * 2 <= d.Width && old.Width > 0;
                changes.Add(new Change(d,
                    useSaved ? old!.Width : d.NativeWidth, useSaved ? old!.Height : d.NativeHeight,
                    useSaved ? old!.Frequency : d.Frequency,
                    useSaved ? old!.ScalePercent : Math.Max(100, d.ScalePercent / 2)));
            }
            else changes.Add(new Change(d, d.Width, d.Height, d.Frequency, d.ScalePercent));
        }
        var r = changes.Any(c => c.Width != c.Display.Width) ? Apply(changes) : new Result(true);
        if (r.Ok) s.RetinaRestore = null;
        return r;
    }

    private static Result Apply(List<Change> changes)
    {
        int primary = changes.FindIndex(c => c.Display.Primary);
        if (primary < 0) primary = 0;
        var oldRects = changes.Select(c => new Int32Rect(c.Display.X, c.Display.Y, c.Display.Width, c.Display.Height)).ToArray();
        var sizes = changes.Select(c => (c.Width, c.Height)).ToArray();
        var pos = Relayout(oldRects, sizes, primary);
        if (pos == null) return new Result(false, L.T("Retina.Layout"));

        // 1) every mode is checked first (nothing changes if the driver refuses one)
        var modes = new List<(string Device, DEVMODE Mode, DEVMODE Original)>();
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            var orig = NewDevMode();
            if (!EnumDisplaySettings(c.Display.Device, ENUM_CURRENT_SETTINGS, ref orig)) return new Result(false, L.T("Retina.Failed"));
            orig.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION;
            var dm = orig;
            dm.dmPelsWidth = (uint)c.Width;
            dm.dmPelsHeight = (uint)c.Height;
            dm.dmDisplayFrequency = (uint)c.Frequency;
            dm.dmPositionX = pos[i].X;
            dm.dmPositionY = pos[i].Y;
            if (c.Width != c.Display.Width)
            {
                var test = dm;
                test.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                int t = ChangeDisplaySettingsEx(c.Display.Device, ref test, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
                if (t != 0)
                {
                    Log.Warn($"Mode test {c.Display.Device} {c.Width}x{c.Height}@{c.Frequency}: {t}");
                    return new Result(false, L.T("Retina.Failed"));
                }
            }
            modes.Add((c.Display.Device, dm, orig));
        }

        // 2) all monitors at once, in one go (one flicker instead of three)
        bool staged = true;
        foreach (var (device, mode, _) in modes)
        {
            var dm = mode;
            int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (r != 0) { Log.Warn($"Mode stage {device}: {r}"); staged = false; break; }
        }
        if (!staged)
        {
            // put the stored (not yet applied) settings back, so nothing changes now or after a restart
            foreach (var (device, _, original) in modes)
            {
                var dm = original;
                ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            }
            ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            return new Result(false, L.T("Retina.Failed"));
        }
        int applied = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.Info($"Display modes applied: {applied} ({string.Join(", ", changes.Select((c, i) => $"{c.Display.Device} {c.Width}x{c.Height}@{c.Frequency} at {pos[i].X},{pos[i].Y}"))})");
        if (applied != 0) return new Result(false, L.T("Retina.Failed"));

        // 3) scaling, so everything keeps its size (Windows picks the new resolution's default otherwise)
        Thread.Sleep(400);
        foreach (var c in changes)
        {
            if (c.ScalePercent == c.Display.ScalePercent && c.Width == c.Display.Width) continue;
            for (int attempt = 0; attempt < 3 && !SetScale(c.Display.Device, c.ScalePercent); attempt++) Thread.Sleep(300);
        }
        return new Result(true);
    }

    /// <summary>
    /// New monitor positions after some of them changed size, keeping the arrangement: monitors that touched still touch
    /// on the same side, the primary one stays at 0,0. Returns null for layouts that can't be kept (then nothing is changed).
    /// </summary>
    internal static (int X, int Y)[]? Relayout(Int32Rect[] old, (int W, int H)[] size, int primary)
    {
        int n = old.Length;
        var pos = new (int X, int Y)?[n];
        pos[primary] = (old[primary].X, old[primary].Y);
        var queue = new Queue<int>();
        queue.Enqueue(primary);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            var a = old[i];
            var (ax, ay) = pos[i]!.Value;
            var (aw, ah) = size[i];
            double kx = (double)aw / a.Width, ky = (double)ah / a.Height;
            for (int j = 0; j < n; j++)
            {
                if (pos[j] != null) continue;
                var b = old[j];
                var (bw, bh) = size[j];
                bool vOverlap = b.Y < a.Y + a.Height && a.Y < b.Y + b.Height;
                bool hOverlap = b.X < a.X + a.Width && a.X < b.X + b.Width;
                int nx, ny;
                if (b.X == a.X + a.Width && vOverlap) { nx = ax + aw; ny = ay + (int)Math.Round((b.Y - a.Y) * ky); }
                else if (b.X + b.Width == a.X && vOverlap) { nx = ax - bw; ny = ay + (int)Math.Round((b.Y - a.Y) * ky); }
                else if (b.Y == a.Y + a.Height && hOverlap) { ny = ay + ah; nx = ax + (int)Math.Round((b.X - a.X) * kx); }
                else if (b.Y + b.Height == a.Y && hOverlap) { ny = ay - bh; nx = ax + (int)Math.Round((b.X - a.X) * kx); }
                else continue;
                // still sharing an edge (a monitor next to a now twice as tall one stays reachable)
                if (b.X == a.X + a.Width || b.X + b.Width == a.X) ny = Math.Clamp(ny, ay - bh + 1, ay + ah - 1);
                else nx = Math.Clamp(nx, ax - bw + 1, ax + aw - 1);
                pos[j] = (nx, ny);
                queue.Enqueue(j);
            }
        }
        if (pos.Any(p => p == null)) return null;
        var result = pos.Select(p => p!.Value).ToArray();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                bool overlapX = result[i].X < result[j].X + size[j].W && result[j].X < result[i].X + size[i].W;
                bool overlapY = result[i].Y < result[j].Y + size[j].H && result[j].Y < result[i].Y + size[i].H;
                if (overlapX && overlapY) return null;
            }
        return result;
    }

    /// <summary>Opens the place where the graphics driver's super-resolution switch lives.</summary>
    public static void OpenDriverSettings(string vendor)
    {
        try
        {
            if (vendor == "nvidia")
            {
                ShellUtil.Open(@"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel");
                return;
            }
            if (vendor == "amd")
            {
                var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"AMD\CNext\CNext\RadeonSoftware.exe");
                if (File.Exists(exe)) { ShellUtil.Open(exe); return; }
            }
            ShellUtil.Open("ms-settings:display");
        }
        catch (Exception ex) { Log.Error("Open driver settings failed", ex); }
    }

    // ------------------------------------------------------------------ interop

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1, DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const uint DM_POSITION = 0x20, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2, CDS_NORESET = 0x10000000;

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx;
        public int outputTechnology; public int rotation; public int scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public int scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { public int infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public int size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags; public int outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate; public DISPLAYCONFIG_RATIONAL hSyncFreq; public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public uint activeCx, activeCy, totalCx, totalCy; public uint videoStandard; public int scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint width; public uint height; public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_GET { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public int minScaleRel; public int curScaleRel; public int maxScaleRel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_SET { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public int scaleRel; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_PREFERRED_MODE info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DPI_GET info);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DPI_SET info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);
}
