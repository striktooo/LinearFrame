using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Core;
using LinearFrame.Imaging;

namespace LinearFrame.Capture;

/// <summary>A physical monitor (all values in physical pixels).</summary>
public sealed record MonitorInfo(IntPtr Handle, Int32Rect Bounds, Int32Rect WorkArea, double Scale, bool Primary)
{
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr d) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref info))
            {
                double scale = 1.0;
                if (Native.GetDpiForMonitor(h, 0, out uint dpi, out _) == 0 && dpi > 0) scale = dpi / 96.0;
                var b = info.rcMonitor;
                var w = info.rcWork;
                list.Add(new MonitorInfo(h, new Int32Rect(b.Left, b.Top, b.Width, b.Height),
                    new Int32Rect(w.Left, w.Top, w.Width, w.Height), scale, (info.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static Int32Rect VirtualBounds(IReadOnlyList<MonitorInfo> monitors)
    {
        int x0 = monitors.Min(m => m.Bounds.X), y0 = monitors.Min(m => m.Bounds.Y);
        int x1 = monitors.Max(m => m.Bounds.X + m.Bounds.Width), y1 = monitors.Max(m => m.Bounds.Y + m.Bounds.Height);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    public bool Contains(int x, int y) => x >= Bounds.X && y >= Bounds.Y && x < Bounds.X + Bounds.Width && y < Bounds.Y + Bounds.Height;
}

/// <summary>GDI screen capture of the composed desktop (fast, works for every window type DWM can compose).</summary>
public static class ScreenCapturer
{
    public static PixelBuffer Capture(Int32Rect r)
    {
        var sw = Stopwatch.StartNew();
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = r.Width,
            biHeight = -r.Height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr hbmp = Native.CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        var pb = new PixelBuffer(r.Width, r.Height);
        try
        {
            IntPtr old = Native.SelectObject(mem, hbmp);
            Native.BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, Native.SRCCOPY | Native.CAPTUREBLT);
            Native.SelectObject(mem, old);
            Marshal.Copy(bits, pb.Data, 0, pb.Data.Length);
        }
        finally
        {
            Native.DeleteObject(hbmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        pb.ForceOpaque();
        Log.Info($"Captured {r.Width}x{r.Height} in {sw.ElapsedMilliseconds} ms");
        return pb;
    }
}

/// <summary>Visible top-level windows in z-order (front first), used for "click a window to capture it".</summary>
public static class WindowFinder
{
    public static List<Int32Rect> VisibleWindows()
    {
        var list = new List<Int32Rect>();
        uint ownPid = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true; // click-through overlays (game bars, GPU overlays)
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if ((ex & Native.WS_EX_LAYERED) != 0 &&
                    Native.GetLayeredWindowAttributes(h, out uint _key, out byte alpha, out uint flags) && (flags & 2) != 0 && alpha < 16)
                    return true;

                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == ownPid && Native.WindowText(h).StartsWith("LinearFrame.")) return true;

                string cls = Native.ClassName(h);
                if (cls is "Progman" or "WorkerW") return true; // desktop -> handled as "whole monitor"

                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) != 0)
                    Native.GetWindowRect(h, out r);
                if (r.Width < 6 || r.Height < 6) return true;
                list.Add(new Int32Rect(r.Left, r.Top, r.Width, r.Height));
            }
            catch { /* ignore odd windows */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
