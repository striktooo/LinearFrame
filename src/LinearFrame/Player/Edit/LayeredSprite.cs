using System.Runtime.InteropServices;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// A click-through, see-through native window that shows ready-made premultiplied BGRA frames with UpdateLayeredWindow.
/// The banner preview plays through it: a frame is one memory copy and one call - no WPF layout, no redraw of anything else.
/// </summary>
internal sealed class LayeredSprite : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SZ { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLEND { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT dst, ref SZ size, IntPtr hdcSrc, ref PT src, int key, ref BLEND blend, int flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string proc);

    private const int ULW_ALPHA = 2;
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
    private const string ClassName = "LinearFrame.Banner";
    private static bool _registered;

    // a plain Win32 window: WPF must not draw into it (a window WPF renders can't take UpdateLayeredWindow)
    private IntPtr _hwnd;
    private bool _warned;
    private IntPtr _dc, _bmp, _old, _bits;
    private int _w, _h;
    private bool _visible;
    private byte[]? _last;
    private int _lx = int.MinValue, _ly = int.MinValue;

    public LayeredSprite(IntPtr owner)
    {
        if (!_registered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = GetProcAddress(Native.GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = Native.GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            _registered = true;
        }
        const int ex = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
        // owned by the player: it stays above the player and goes with it (minimise, other windows on top)
        _hwnd = CreateWindowEx(ex, ClassName, ClassName, Native.WS_POPUP, -32000, -32000, 1, 1, owner, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) Log.Warn($"Banner window not created ({Marshal.GetLastWin32Error()})");
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Shows a frame at screen position (x, y) in physical pixels, just below the window <paramref name="below"/>.</summary>
    public void Show(byte[] frame, int w, int h, int x, int y, IntPtr below)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (ReferenceEquals(frame, _last) && x == _lx && y == _ly && _visible) return; // nothing changed
        if (w != _w || h != _h) Alloc(w, h);
        if (_bits == IntPtr.Zero) return;
        Marshal.Copy(frame, 0, _bits, Math.Min(frame.Length, w * h * 4));
        var dst = new PT { X = x, Y = y };
        var size = new SZ { CX = w, CY = h };
        var src = new PT();
        var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
        if (!UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _dc, ref src, 0, ref blend, ULW_ALPHA) && !_warned)
        {
            _warned = true;
            Log.Warn($"UpdateLayeredWindow failed ({Marshal.GetLastWin32Error()})");
        }
        _last = frame;
        _lx = x;
        _ly = y;
        if (!_visible)
        {
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            _visible = true;
            if (below != IntPtr.Zero) Native.SetWindowPos(_hwnd, below, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public void Hide()
    {
        if (!_visible || _hwnd == IntPtr.Zero) return;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        _visible = false;
        _last = null;
    }

    private void Alloc(int w, int h)
    {
        Free();
        var bmi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try
        {
            _bmp = Native.CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            _dc = Native.CreateCompatibleDC(screen);
        }
        finally { Native.ReleaseDC(IntPtr.Zero, screen); }
        if (_bmp == IntPtr.Zero || _dc == IntPtr.Zero) { Free(); return; }
        _old = Native.SelectObject(_dc, _bmp);
        _w = w;
        _h = h;
    }

    private void Free()
    {
        if (_dc != IntPtr.Zero)
        {
            if (_old != IntPtr.Zero) Native.SelectObject(_dc, _old);
            Native.DeleteDC(_dc);
        }
        if (_bmp != IntPtr.Zero) Native.DeleteObject(_bmp);
        _dc = _bmp = _old = _bits = IntPtr.Zero;
        _w = _h = 0;
    }

    public void Dispose()
    {
        Hide();
        Free();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
