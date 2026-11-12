using System.Runtime.InteropServices;
using System.Windows;

namespace LinearFrame.Core;

/// <summary>One display output as Windows sees it right now (attached to the desktop or not).</summary>
public sealed record DisplayState(
    string Device, string Adapter, string AdapterId, string MonitorId,
    int X, int Y, int Width, int Height, int Frequency, bool Primary, bool Attached, int ScalePercent)
{
    public Int32Rect Bounds => new(X, Y, Width, Height);

    /// <summary>The software-only screen of the open-source "Virtual Display Driver" (used for HD captures).</summary>
    public bool IsVirtual =>
        Adapter.Equals("Virtual Display Driver", StringComparison.OrdinalIgnoreCase)
        || AdapterId.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
        || MonitorId.Contains("MTT1337", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Display plumbing: which screens exist, attaching/detaching one to the desktop, its resolution and its scaling.
/// Everything here works without administrator rights.
/// </summary>
public static class Displays
{
    private static readonly int[] DpiSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    // ------------------------------------------------------------------ query

    public static List<DisplayState> Query()
    {
        var list = new List<DisplayState>();
        try
        {
            var paths = ActivePaths();
            var dd = NewDevice();
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd = NewDevice())
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
                bool attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;

                string monitorId = "";
                var mon = NewDevice();
                if (EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0)) monitorId = mon.DeviceID ?? "";

                int x = 0, y = 0, w = 0, h = 0, hz = 0, scale = 0;
                if (attached)
                {
                    var cur = NewDevMode();
                    if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur))
                    {
                        x = cur.dmPositionX; y = cur.dmPositionY;
                        w = (int)cur.dmPelsWidth; h = (int)cur.dmPelsHeight; hz = (int)cur.dmDisplayFrequency;
                    }
                    if (paths.TryGetValue(dd.DeviceName, out var ids)) scale = GetScale(ids).Current;
                    if (scale <= 0) scale = ScaleFromMonitor(x, y);
                }
                list.Add(new DisplayState(dd.DeviceName, dd.DeviceString ?? "", dd.DeviceID ?? "", monitorId,
                    x, y, w, h, hz, (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0, attached, scale));
            }
        }
        catch (Exception ex) { Log.Error("Display query failed", ex); }
        return list;
    }

    /// <summary>Resolutions a display offers (also for a detached one), largest first.</summary>
    public static List<(int Width, int Height, int Frequency)> Modes(string device)
    {
        var set = new HashSet<(int, int, int)>();
        var m = NewDevMode();
        for (int k = 0; EnumDisplaySettings(device, k, ref m); k++)
            if (m.dmBitsPerPel >= 24) set.Add(((int)m.dmPelsWidth, (int)m.dmPelsHeight, (int)m.dmDisplayFrequency));
        return set.OrderByDescending(t => (long)t.Item1 * t.Item2).ThenByDescending(t => t.Item3).ToList();
    }

    // ------------------------------------------------------------------ attach / detach / mode

    /// <summary>Puts a display on the desktop at the given place and resolution (or changes those if it's already there).</summary>
    public static bool Attach(string device, int width, int height, int frequency, int x, int y)
    {
        var dm = NewDevMode();
        dm.dmPelsWidth = (uint)width;
        dm.dmPelsHeight = (uint)height;
        dm.dmBitsPerPel = 32;
        dm.dmDisplayFrequency = (uint)frequency;
        dm.dmPositionX = x;
        dm.dmPositionY = y;
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | (frequency > 0 ? DM_DISPLAYFREQUENCY : 0);
        int staged = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        int applied = staged == 0 ? ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) : staged;
        Log.Info($"Display attach {device} {width}x{height}@{frequency} at {x},{y}: staged {staged}, applied {applied}");
        return staged == 0 && applied == 0;
    }

    /// <summary>Takes a display off the desktop (it stays connected, Windows just doesn't use it).</summary>
    public static bool Detach(string device)
    {
        var dm = NewDevMode();
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        int staged = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        int applied = staged == 0 ? ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) : staged;
        Log.Info($"Display detach {device}: staged {staged}, applied {applied}");
        return staged == 0 && applied == 0;
    }

    // ------------------------------------------------------------------ scaling

    /// <summary>Current / highest scaling Windows allows for an attached display at its current resolution.</summary>
    public static (int Current, int Max) Scale(string device)
    {
        if (!ActivePaths().TryGetValue(device, out var ids)) return (0, 0);
        var s = GetScale(ids);
        return (s.Current, s.Max);
    }

    public static bool SetScale(string device, int percent)
    {
        if (!ActivePaths().TryGetValue(device, out var ids)) return false;
        var (cur, _, _, minRel, maxRel) = GetScale(ids);
        if (cur == percent) return true;
        int target = Array.IndexOf(DpiSteps, percent);
        if (target < 0) target = Array.FindLastIndex(DpiSteps, v => v <= percent);
        if (target < 0) return false;
        int rel = Math.Clamp(target - Math.Abs(minRel), minRel, maxRel);
        var s = new DPI_SET();
        s.header.type = -4; // SET_DPI_SCALE - what the Windows Settings app itself calls
        s.header.size = Marshal.SizeOf<DPI_SET>();
        s.header.adapterId = ids.Adapter;
        s.header.id = ids.SourceId;
        s.scaleRel = rel;
        int r = DisplayConfigSetDeviceInfo(ref s);
        Log.Info($"Display scale {device} → {percent} % (rel {rel}): {r}");
        return r == 0;
    }

    /// <summary>Highest scale step that is ≤ the wanted one.</summary>
    public static int StepAtMost(int percent) => DpiSteps.LastOrDefault(v => v <= percent, 100);
    public static bool IsStep(int percent) => Array.IndexOf(DpiSteps, percent) >= 0;

    private sealed record PathIds(LUID Adapter, uint SourceId);

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
            result.TryAdd(src.viewGdiDeviceName, new PathIds(p.sourceInfo.adapterId, p.sourceInfo.id));
        }
        return result;
    }

    private static (int Current, int Recommended, int Max, int MinRel, int MaxRel) GetScale(PathIds ids)
    {
        var g = new DPI_GET();
        g.header.type = -3; // GET_DPI_SCALE
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

    // ------------------------------------------------------------------ interop

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1, DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4, DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;
    private const uint DM_POSITION = 0x20, DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x1, CDS_NORESET = 0x10000000;

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
    private static DISPLAY_DEVICE NewDevice() => new() { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

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
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DPI_GET info);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DPI_SET info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);
}
