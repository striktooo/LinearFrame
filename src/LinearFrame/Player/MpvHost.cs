using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LinearFrame.Player;

/// <summary>Native child window that mpv renders into (its own D3D11 swap chain = smooth, GPU-only playback).</summary>
public sealed class MpvHost : HwndHost
{
    private const string ClassName = "LinearFrameVideoSurface";
    private static bool _registered;
    private IntPtr _hwnd;

    public IntPtr VideoHandle => _hwnd;

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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string proc);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr inst, IntPtr id);

    private static void EnsureClass()
    {
        if (_registered) return;
        var user32 = GetModuleHandle("user32.dll");
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = GetProcAddress(user32, "DefWindowProcW"),
            hInstance = GetModuleHandle(null),
            hbrBackground = Core.Native.CreateSolidBrush(0x000000),
            hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        _registered = true;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClass();
        const uint style = Core.Native.WS_CHILD | Core.Native.WS_VISIBLE | Core.Native.WS_CLIPSIBLINGS | Core.Native.WS_CLIPCHILDREN;
        _hwnd = CreateWindowEx(0, ClassName, "", style, 0, 0, 16, 16, hwndParent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }
}
