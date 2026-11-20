using System.Runtime.InteropServices;
using System.Windows;

namespace LinearFrame.Core;

/// <summary>
/// The off-screen canvas for HD captures: a software-only display (open-source "Virtual Display Driver") that is put on
/// the desktop for the moment of a capture and taken off again right after. While it's off, Windows doesn't use it at
/// all - no extra desktop area, no memory, nothing the mouse could wander into. The real monitors are never touched.
/// </summary>
public static class VirtualScreen
{
    public sealed record Armed(string Device, Int32Rect Bounds, Int32Rect WorkArea, int ScalePercent, uint Dpi);

    /// <summary>The resolutions LinearFrame configures the virtual screen with (2×, 3× and 4× of Full HD).</summary>
    public static readonly (int Width, int Height, int Frequency)[] KnownModes =
    {
        (3840, 2160, 60), (5760, 3240, 60), (7680, 4320, 60),
    };

    private static readonly object Gate = new();
    private static string? _device;      // \\.\DISPLAYn of the virtual screen, once known
    private static bool _attachedByUs;
    private static Timer? _park;
    private static long _attachedAt; // when it last went onto the desktop (Environment.TickCount64)

    /// <summary>True for the virtual screen's device name (so the capture overlay never treats it as a monitor).</summary>
    public static bool IsVirtualDevice(string? device) =>
        device != null && _device != null && string.Equals(device.TrimEnd('\0'), _device, StringComparison.OrdinalIgnoreCase);

    /// <summary>Looks the virtual screen up (≈1 ms); null when the driver isn't installed.</summary>
    public static DisplayState? Find()
    {
        var v = Displays.Query().FirstOrDefault(d => d.IsVirtual);
        _device = v?.Device;
        return v;
    }

    /// <summary>
    /// Puts the virtual screen on the desktop with at least the given size and the given scaling.
    /// Returns what was actually achieved (Windows may cap the scaling), or null if it can't be done.
    /// <paramref name="settle"/>: wait until its work area is final (the taskbar Windows adds to it a second later) -
    /// needed before a maximized window goes over; otherwise the work area it will have is worked out instead.
    /// </summary>
    public static Armed? Arm(int minWidth, int minHeight, int scalePercent, bool settle = false)
    {
        lock (Gate)
        {
            _park?.Dispose();
            _park = null;
            var all = Displays.Query();
            var v = all.FirstOrDefault(d => d.IsVirtual);
            _device = v?.Device;
            var real = all.Where(d => d.Attached && !d.IsVirtual).ToList();
            if (v == null || real.Count == 0) return null;

            var modes = Displays.Modes(v.Device);
            if (modes.Count == 0) modes = KnownModes.ToList(); // a detached display may not list its modes
            var fit = modes
                .Where(m => m.Width >= minWidth && m.Height >= minHeight)
                .OrderBy(m => (long)m.Width * m.Height).ThenBy(m => Math.Abs(m.Frequency - 60))
                .FirstOrDefault();
            if (fit.Width == 0) { Log.Warn($"Virtual screen has no mode for {minWidth}x{minHeight}"); return null; }

            bool fresh = !(v.Attached && v.Width == fit.Width && v.Height == fit.Height);
            if (fresh)
            {
                // Up and to the left of the left-most monitor, touching it in a single pixel: Windows wants every screen
                // to touch another one, and that corner is where the mouse practically never goes.
                var left = real.OrderBy(d => d.X).ThenBy(d => d.Y).First();
                if (!v.Attached) _attachedByUs = true;
                if (!Displays.Attach(v.Device, fit.Width, fit.Height, fit.Frequency, left.X - fit.Width, left.Y - fit.Height + 1)) return null;
                _attachedAt = Environment.TickCount64;
            }

            // scaling (the display needs a moment to become an active path)
            int reached = 0;
            for (int i = 0; i < 30; i++)
            {
                var (cur, max) = Displays.Scale(v.Device);
                if (cur == 0) { Thread.Sleep(40); continue; }
                int want = Math.Min(scalePercent, max);
                reached = cur;
                if (cur == want) break;
                Displays.SetScale(v.Device, want);
                Thread.Sleep(50);
            }

            var now = Displays.Query().FirstOrDefault(d => d.IsVirtual && d.Attached);
            if (now == null || now.Width == 0) return null;
            var mon = Native.MonitorFromPoint(new Native.POINT(now.X + now.Width / 2, now.Y + now.Height / 2), Native.MONITOR_DEFAULTTONEAREST);
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(mon, ref info) || !IsVirtualDevice(info.szDevice)) return null;
            uint dpi = 96;
            for (int i = 0; i < 20; i++)
            {
                if (Native.GetDpiForMonitor(mon, 0, out dpi, out _) == 0 && dpi == (uint)Math.Round(reached * 96 / 100.0)) break;
                Thread.Sleep(30);
            }
            // "Show my taskbar on all displays": Windows puts a taskbar on the fresh screen about a second later, and its
            // work area shrinks. A maximized window moved over before that was maximized again by Windows - often back
            // on its own screen, the HD capture failed ("selection outside the virtual screen"). So for a maximized
            // window the work area has to be final first; for any other the coming taskbar is only kept clear of.
            bool BarThere() => info.rcWork.Height != info.rcMonitor.Height || info.rcWork.Width != info.rcMonitor.Width;
            bool barComing = !BarThere() && Environment.TickCount64 - _attachedAt < 4000 && TaskbarOnAllDisplays();
            if (barComing && settle)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 1500 && !BarThere())
                {
                    Thread.Sleep(40);
                    Native.GetMonitorInfo(mon, ref info);
                }
                Log.Info($"Virtual screen: taskbar {(BarThere() ? "there" : "not seen")} after {clock.ElapsedMilliseconds} ms");
                barComing = !BarThere();
            }
            var b = info.rcMonitor; var w = barComing ? ExpectedWorkArea(info.rcMonitor, dpi) : info.rcWork;
            return new Armed(now.Device, new Int32Rect(b.Left, b.Top, b.Width, b.Height), new Int32Rect(w.Left, w.Top, w.Width, w.Height),
                (int)Math.Round(dpi * 100 / 96.0), dpi);
        }
    }

    /// <summary>The work area a screen will have once its taskbar is there: the main screen's taskbar edge and thickness, scaled.</summary>
    private static Native.RECT ExpectedWorkArea(Native.RECT monitor, uint dpi)
    {
        var primary = Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTONEAREST);
        var pi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(primary, ref pi) || Native.GetDpiForMonitor(primary, 0, out uint pdpi, out _) != 0 || pdpi == 0) return monitor;
        double s = dpi / (double)pdpi;
        var m = pi.rcMonitor; var w = pi.rcWork;
        return new Native.RECT(monitor.Left + (int)Math.Round((w.Left - m.Left) * s), monitor.Top + (int)Math.Round((w.Top - m.Top) * s),
            monitor.Right - (int)Math.Round((m.Right - w.Right) * s), monitor.Bottom - (int)Math.Round((m.Bottom - w.Bottom) * s));
    }

    /// <summary>Windows' "Show my taskbar on all displays" (on unless turned off).</summary>
    private static bool TaskbarOnAllDisplays()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("MMTaskbarEnabled") is not int on || on != 0;
        }
        catch { return true; }
    }

    /// <summary>Takes the virtual screen off the desktop a little later (so a second capture right away is instant).</summary>
    public static void ParkSoon(int milliseconds = 12000)
    {
        lock (Gate)
        {
            _park?.Dispose();
            _park = new Timer(_ => Park(), null, milliseconds, Timeout.Infinite);
        }
    }

    /// <summary>Takes the virtual screen off the desktop now (only if LinearFrame put it there, or owns the driver).</summary>
    public static void Park(bool force = false)
    {
        lock (Gate)
        {
            _park?.Dispose();
            _park = null;
            try
            {
                var v = Displays.Query().FirstOrDefault(d => d.IsVirtual);
                _device = v?.Device;
                if (v is { Attached: true } && (_attachedByUs || force)) Displays.Detach(v.Device);
                _attachedByUs = false;
            }
            catch (Exception ex) { Log.Error("Virtual screen park failed", ex); }
        }
    }
}
