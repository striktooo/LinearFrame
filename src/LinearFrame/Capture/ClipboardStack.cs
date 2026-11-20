using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using LinearFrame.Core;
using LinearFrame.Imaging;
using LinearFrame.UI;

namespace LinearFrame.Capture;

/// <summary>One Ctrl+C capture on its way to the clipboard: either on its own, or - when C comes a second time - added.</summary>
internal sealed class ClipTicket
{
    public uint SeqAtCommit;
    public List<string> PrevFiles = new();
    public uint PrevSeq;
    public bool Stack;
    public bool Delivered;
    public BitmapSource? Image;
    public byte[]? Png;
    public string? File;
}

/// <summary>
/// Several screenshots in the clipboard at once. A normal capture replaces what was there (as always); Ctrl+C+C - C pressed
/// twice with Ctrl held - adds the capture to the ones already there: the clipboard then holds them as files (like
/// copying them in Explorer), so one Ctrl+V pastes all of them into a chat, a mail or a folder. Up to 10, oldest drop out.
/// Anything copied elsewhere in between starts a new set.
/// </summary>
internal static class ClipboardStack
{
    public const int Max = 10;
    private static List<string> _files = new();
    private static uint _ourSeq;

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    /// <summary>Ctrl+C was pressed in the capture overlay: from now a second C (Ctrl still held) stacks this capture.</summary>
    public static ClipTicket Begin()
    {
        var t = new ClipTicket { SeqAtCommit = GetClipboardSequenceNumber(), PrevFiles = _files.ToList(), PrevSeq = _ourSeq };
        var ui = Application.Current.Dispatcher;
        SecondPress.Watch(() => ui.BeginInvoke(new Action(() => OnSecond(t))));
        return t;
    }

    // for the self-test: a ticket without the keyboard watch, and the second C "pressed"
    internal static ClipTicket BeginForTest() => new() { SeqAtCommit = GetClipboardSequenceNumber(), PrevFiles = _files.ToList(), PrevSeq = _ourSeq };
    internal static void SecondForTest(ClipTicket t) => OnSecond(t);

    /// <summary>The capture is ready (encoded, saved): onto the clipboard, alone or stacked.</summary>
    public static void Delivered(ClipTicket t, BitmapSource image, byte[] png, string? file)
    {
        t.Image = image;
        t.Png = png;
        t.File = file;
        t.Delivered = true;
        if (t.Stack) WriteStack(t);
        else WriteSingle(image, png, file);
    }

    /// <summary>A normal copy (Enter, the toolbar button): the clipboard holds just this one of ours now.</summary>
    public static void Single(string? file)
    {
        _files = file != null ? new List<string> { file } : new List<string>();
        _ourSeq = GetClipboardSequenceNumber();
    }

    private static void WriteSingle(BitmapSource image, byte[] png, string? file)
    {
        ImageIO.CopyToClipboard(image, png);
        Single(file);
    }

    private static void OnSecond(ClipTicket t)
    {
        if (t.Stack) return;
        t.Stack = true;
        if (t.Delivered) WriteStack(t); // otherwise the delivery stacks it when it's ready
    }

    private static void WriteStack(ClipTicket t)
    {
        try
        {
            string file = t.File != null && System.IO.File.Exists(t.File) ? t.File : TempCopy(t.Png!);
            // the set goes on if, when C was pressed, the clipboard still held our last copy
            bool continuing = t.SeqAtCommit == t.PrevSeq && t.PrevFiles.Count > 0;
            var list = continuing ? t.PrevFiles.Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(f)).ToList() : new List<string>();
            list.Add(file);
            if (list.Count > Max) list.RemoveRange(0, list.Count - Max);
            bool ok = list.Count == 1 ? ImageIO.CopyToClipboard(t.Image!, t.Png) : ImageIO.CopyFilesToClipboard(list);
            _files = list;
            _ourSeq = GetClipboardSequenceNumber();
            Log.Info($"Clipboard set: {list.Count} screenshot(s){(ok ? "" : " - failed")}");
            Hud.Show(Count(list.Count), "copy", hint: ClearHint());
        }
        catch (Exception ex) { Log.Error("Clipboard set", ex); }
    }

    /// <summary>"Ctrl + Shift + Backspace czyści schowek" under the count, while that shortcut is set.</summary>
    private static string? ClearHint()
    {
        var g = App.Instance?.Settings.ClearClipboardHotkey;
        return g == null || g.IsEmpty ? null : L.F("Clipboard.ClearHint", g);
    }

    /// <summary>The "Clear the clipboard" shortcut: empties it - the Ctrl+C+C set with it - and says so.</summary>
    public static void Clear()
    {
        int ours = _ourSeq == GetClipboardSequenceNumber() ? _files.Count : 0;
        _files = new List<string>();
        _ourSeq = 0;
        bool ok = ImageIO.ClearClipboard();
        Log.Info($"Clipboard cleared by the shortcut ({ours} screenshot(s) of ours in it){(ok ? "" : " - failed")}");
        Hud.Show(L.T(ok ? "Clipboard.Cleared" : "Clipboard.ClearFailed"), ok ? "trash" : "warning");
    }

    /// <summary>"3 zrzuty w schowku" / "3 screenshots in the clipboard".</summary>
    public static string Count(int n) => L.IsPolish
        ? $"{n} {(n == 1 ? "zrzut" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "zrzuty" : "zrzutów")} w schowku"
        : $"{n} screenshot{(n == 1 ? "" : "s")} in the clipboard";

    /// <summary>Screenshots aren't saved to a folder (switched off): a copy for the clipboard set lives in the temp folder.</summary>
    private static string TempCopy(byte[] png)
    {
        var dir = Path.Combine(Path.GetTempPath(), "LinearFrame", "clipboard");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "*.png"))
            try { if (DateTime.Now - System.IO.File.GetLastWriteTime(old) > TimeSpan.FromDays(2)) System.IO.File.Delete(old); } catch { /* in use */ }
        var path = ImageIO.UniquePath(dir, L.F("Capture.FileName", DateTime.Now), ".png");
        System.IO.File.WriteAllBytes(path, png);
        return path;
    }
}

/// <summary>
/// Watches for one more press of C (with Ctrl held) for a moment after Ctrl+C - with a keyboard hook on a thread of its
/// own, since the capture overlay is gone by then and the key would otherwise reach the app behind it (which would copy
/// its own selection over ours). The second C is swallowed.
/// </summary>
internal static class SecondPress
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_TIMER = 0x0113;
    private const int VK_C = 0x43, VK_CONTROL = 0x11;

    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    public static void Watch(Action onSecond)
    {
        uint window = Math.Clamp(Native.GetDoubleClickTime(), 350u, 600u);
        var thread = new Thread(() =>
        {
            bool released = (Native.GetAsyncKeyState(VK_C) & 0x8000) == 0, fired = false, swallowUp = false;
            Native.HookProc proc = (code, wParam, lParam) =>
            {
                if (code >= 0 && Marshal.ReadInt32(lParam) == VK_C)
                {
                    int msg = (int)wParam;
                    if (msg is WM_KEYUP or WM_SYSKEYUP)
                    {
                        released = true;
                        if (swallowUp) { swallowUp = false; return new IntPtr(1); }
                    }
                    else if (msg is WM_KEYDOWN or WM_SYSKEYDOWN && released && !fired && (Native.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
                    {
                        fired = true;
                        swallowUp = true;
                        try { onSecond(); } catch { /* the UI thread gets it */ }
                        return new IntPtr(1);
                    }
                }
                return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            };
            var hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) return;
            var timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, window, IntPtr.Zero);
            int ticks = 0;
            while (Native.GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                // the window is over - unless the second C is still held (its release is swallowed too, up to 1 s more)
                if (m.message == WM_TIMER && (!swallowUp || ++ticks * window > 1000)) break;
            }
            KillTimer(IntPtr.Zero, timer);
            Native.UnhookWindowsHookEx(hook);
            GC.KeepAlive(proc);
        }) { IsBackground = true, Name = "Ctrl+C+C", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }
}
