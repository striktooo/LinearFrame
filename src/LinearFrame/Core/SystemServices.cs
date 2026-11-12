using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LinearFrame.Core;

/// <summary>Hidden top-level window that receives hotkeys, tray callbacks, IPC and system broadcasts.</summary>
internal sealed class MessageWindow : IDisposable
{
    public const string Title = "LinearFrame.MessageWindow";
    private readonly HwndSource _source;
    private readonly List<Func<int, IntPtr, IntPtr, bool>> _handlers = new();

    public IntPtr Handle => _source.Handle;

    public MessageWindow()
    {
        var p = new HwndSourceParameters(Title)
        {
            WindowStyle = unchecked((int)Native.WS_POPUP),
            ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
    }

    public void AddHandler(Func<int, IntPtr, IntPtr, bool> handler) => _handlers.Add(handler);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        foreach (var h in _handlers)
        {
            try
            {
                if (h(msg, wParam, lParam)) { handled = true; return new IntPtr(1); }
            }
            catch (Exception ex) { Log.Error("Message handler failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>Notification-area icon built on Shell_NotifyIcon (no WinForms dependency).</summary>
internal sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = Native.WM_APP + 1;
    public static readonly int TaskbarCreated = (int)Native.RegisterWindowMessage("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private string _tip;
    private bool _added;

    public event Action? Click;
    public event Action<int, int>? ContextMenu;
    public event Action? BalloonClick;

    public TrayIcon(IntPtr hwnd, IntPtr icon, string tip)
    {
        _hwnd = hwnd;
        _icon = icon;
        _tip = tip;
    }

    private Native.NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip.Length > 127 ? _tip[..127] : _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    public void Show()
    {
        var d = Data();
        d.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref d);
        if (!_added)
        {
            // Probably already there (e.g. after an explorer hiccup) - try to update instead.
            _added = Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
        }
        d.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref d);
        Log.Info($"Tray icon added: {_added}");
    }

    public bool IsVisible => _added;

    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        var d = Data();
        d.uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP;
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
    }

    public void SetIcon(IntPtr icon)
    {
        var old = _icon;
        _icon = icon;
        if (_added)
        {
            var d = Data();
            d.uFlags = Native.NIF_ICON;
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
        }
        if (old != IntPtr.Zero && old != icon) Native.DestroyIcon(old);
    }

    public void ShowInfo(string title, string text)
    {
        if (!_added) return;
        var d = Data();
        d.uFlags = Native.NIF_INFO;
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = Native.NIIF_USER | Native.NIIF_LARGE_ICON;
        d.hBalloonIcon = _icon;
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
    }

    public bool Handle(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TaskbarCreated)
        {
            _added = false;
            Show();
            return false;
        }
        if (msg != CallbackMessage) return false;

        int evt = Native.LoWord(lParam);
        switch (evt)
        {
            case Native.NIN_SELECT:
            case Native.NIN_KEYSELECT:
                Click?.Invoke();
                break;
            case Native.WM_CONTEXTMENU:
                ContextMenu?.Invoke(Native.LoWord(wParam), Native.HiWord(wParam));
                break;
            case Native.NIN_BALLOONUSERCLICK:
                BalloonClick?.Invoke();
                break;
        }
        return true;
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data();
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref d);
            _added = false;
        }
        if (_icon != IntPtr.Zero) { Native.DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}

/// <summary>
/// System-wide shortcuts. Keys (combinations or a single key like End) go through RegisterHotKey - zero CPU while idle.
/// Mouse buttons need a low-level mouse hook, which only exists while a mouse button is actually bound.
/// </summary>
internal sealed class HotkeyManager
{
    private const uint MOD_NOREPEAT = 0x4000;
    public const int MouseShortcutMessage = Native.WM_APP + 7;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly List<Action> _mouseActions = new();
    private readonly List<(HotMouse Button, uint Mods)> _mouseBindings = new();
    private MouseShortcutHook? _hook;
    private int _nextId = 0x100;

    public HotkeyManager(IntPtr hwnd) { _hwnd = hwnd; }

    public bool Register(HotkeyGesture gesture, Action action)
    {
        if (gesture.IsEmpty) return true;
        if (gesture.IsMouse)
        {
            _mouseBindings.Add((gesture.Mouse, gesture.Win32Modifiers));
            _mouseActions.Add(action);
            _hook ??= new MouseShortcutHook(_hwnd, MouseShortcutMessage);
            _hook.SetBindings(_mouseBindings.ToArray());
            return _hook.IsActive;
        }
        int id = _nextId++;
        bool ok = Native.RegisterHotKey(_hwnd, id, gesture.Win32Modifiers | MOD_NOREPEAT, gesture.VirtualKey);
        if (ok) _actions[id] = action;
        else Log.Warn($"RegisterHotKey failed for {gesture} (error {Marshal.GetLastWin32Error()})");
        return ok;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _mouseBindings.Clear();
        _mouseActions.Clear();
        _hook?.Dispose();
        _hook = null;
    }

    public bool Handle(int msg, IntPtr wParam)
    {
        if (msg == MouseShortcutMessage)
        {
            int i = wParam.ToInt32();
            if (i >= 0 && i < _mouseActions.Count) _mouseActions[i]();
            return true;
        }
        if (msg != Native.WM_HOTKEY) return false;
        if (_actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            return true;
        }
        return false;
    }
}

/// <summary>
/// Low-level mouse hook for the side buttons (Mouse 4 / Mouse 5). It runs on its own small thread, so the mouse never
/// waits for the UI thread, and the callback does nothing but compare two numbers for every other mouse event.
/// The bound click is swallowed (down and up), so e.g. a browser doesn't also go "Forward".
/// </summary>
internal sealed class MouseShortcutHook : IDisposable
{
    private readonly IntPtr _notify;
    private readonly int _message;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Native.HookProc? _proc; // must stay referenced while the hook exists
    private IntPtr _hook;
    private uint _threadId;
    private volatile (HotMouse Button, uint Mods)[] _bindings = Array.Empty<(HotMouse, uint)>();
    private HotMouse _swallowUp; // hook thread only

    public bool IsActive => _hook != IntPtr.Zero;

    public MouseShortcutHook(IntPtr notify, int message)
    {
        _notify = notify;
        _message = message;
        _thread = new Thread(Run) { IsBackground = true, Name = "LinearFrame mouse shortcuts", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(3000);
    }

    public void SetBindings((HotMouse Button, uint Mods)[] bindings) => _bindings = bindings;

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _proc = Proc;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn($"Mouse hook failed (error {Marshal.GetLastWin32Error()})");
        else Log.Info("Mouse shortcut hook on");
        _ready.Set();
        if (_hook == IntPtr.Zero) return;
        while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Log.Info("Mouse shortcut hook off");
    }

    private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            if (msg is Native.WM_XBUTTONDOWN or Native.WM_XBUTTONUP or Native.WM_MBUTTONDOWN or Native.WM_MBUTTONUP)
            {
                var button = msg is Native.WM_MBUTTONDOWN or Native.WM_MBUTTONUP ? HotMouse.Middle
                    : ((Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF) == 1 ? HotMouse.XButton1 : HotMouse.XButton2;
                if (msg is Native.WM_XBUTTONUP or Native.WM_MBUTTONUP)
                {
                    if (_swallowUp == button) { _swallowUp = HotMouse.None; return new IntPtr(1); }
                }
                else
                {
                    var bindings = _bindings;
                    uint mods = CurrentModifiers();
                    for (int i = 0; i < bindings.Length; i++)
                    {
                        if (bindings[i].Button != button || bindings[i].Mods != mods) continue;
                        _swallowUp = button;
                        Native.PostMessage(_notify, _message, new IntPtr(i), IntPtr.Zero);
                        return new IntPtr(1);
                    }
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static uint CurrentModifiers()
    {
        uint m = 0;
        if (Down(0x12)) m |= 0x1;                 // Alt
        if (Down(0x11)) m |= 0x2;                 // Ctrl
        if (Down(0x10)) m |= 0x4;                 // Shift
        if (Down(0x5B) || Down(0x5C)) m |= 0x8;   // Win
        return m;
        static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1500);
        _ready.Dispose();
    }
}

/// <summary>"Start with Windows" through the per-user Run key (no admin rights needed).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue(AppPaths.AppName, $"\"{AppPaths.ExePath}\" --tray");
            else key.DeleteValue(AppPaths.AppName, false);
        }
        catch (Exception ex) { Log.Error("Autostart change failed", ex); }
    }
}

/// <summary>
/// Start menu entry, so LinearFrame can be found by typing its name. Created once; if the folder is moved later, the
/// entry is pointed at the new place. If you delete it, it stays deleted.
/// </summary>
internal static class StartMenuShortcut
{
    public static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppPaths.AppName + ".lnk");

    public static void Ensure(AppSettings s)
    {
        try
        {
            string link = LinkPath;
            if (!s.StartMenuShortcutDone)
            {
                Create(link);
                s.StartMenuShortcutDone = true;
                s.Save();
            }
            else if (File.Exists(link) && !string.Equals(ReadTarget(link), AppPaths.ExePath, StringComparison.OrdinalIgnoreCase))
            {
                Create(link);
            }
        }
        catch (Exception ex) { Log.Error("Start menu shortcut failed", ex); }
    }

    public static void Create(string link)
    {
        var sl = (IShellLinkW)new ShellLink();
        try
        {
            sl.SetPath(AppPaths.ExePath);
            sl.SetWorkingDirectory(Path.GetDirectoryName(AppPaths.ExePath) ?? "");
            sl.SetDescription(L.T("App.Tagline"));
            sl.SetIconLocation(AppPaths.ExePath, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)sl).Save(link, true);
            Log.Info($"Start menu shortcut written: {link}");
        }
        finally { Marshal.FinalReleaseComObject(sl); }
    }

    public static string? ReadTarget(string link)
    {
        var sl = (IShellLinkW)new ShellLink();
        try
        {
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)sl).Load(link, 0);
            var sb = new StringBuilder(1024);
            sl.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        finally { Marshal.FinalReleaseComObject(sl); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

/// <summary>Win32 popup menu - native look (rounded, dark mode aware) and zero WPF overhead.</summary>
internal sealed class NativeMenu : IDisposable
{
    private readonly IntPtr _handle;
    private readonly Dictionary<int, Action> _actions;
    private readonly List<NativeMenu> _children = new();
    private readonly int[] _counter;

    public NativeMenu() : this(new Dictionary<int, Action>(), new[] { 1 }) { }

    private NativeMenu(Dictionary<int, Action> actions, int[] counter)
    {
        _handle = Native.CreatePopupMenu();
        _actions = actions;
        _counter = counter;
    }

    public NativeMenu Add(string text, Action action, bool isChecked = false, bool enabled = true, bool isDefault = false)
    {
        int id = _counter[0]++;
        _actions[id] = action;
        uint flags = Native.MF_STRING | (isChecked ? Native.MF_CHECKED : 0) | (enabled ? 0 : Native.MF_GRAYED);
        Native.AppendMenu(_handle, flags, (UIntPtr)id, text);
        if (isDefault) Native.SetMenuDefaultItem(_handle, (uint)id, 0);
        return this;
    }

    public NativeMenu Separator()
    {
        Native.AppendMenu(_handle, Native.MF_SEPARATOR, UIntPtr.Zero, null);
        return this;
    }

    public NativeMenu Sub(string text, Action<NativeMenu> build, bool enabled = true)
    {
        var child = new NativeMenu(_actions, _counter);
        build(child);
        _children.Add(child);
        Native.AppendMenu(_handle, Native.MF_POPUP | (enabled ? 0 : Native.MF_GRAYED), (UIntPtr)(ulong)child._handle.ToInt64(), text);
        return this;
    }

    /// <summary>Shows the menu at screen pixel coordinates and runs the chosen action afterwards.</summary>
    public void Show(IntPtr owner, int x, int y, bool forceDark = false, bool alignBottom = false)
    {
        Native.TrySetMenuMode(forceDark ? 2 : 1, owner);
        Native.SetForegroundWindow(owner);
        uint flags = Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY | (alignBottom ? Native.TPM_BOTTOMALIGN : 0);
        int cmd = Native.TrackPopupMenuEx(_handle, flags, x, y, owner, IntPtr.Zero);
        Native.PostMessage(owner, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        if (forceDark) Native.TrySetMenuMode(1, owner);
        if (cmd > 0 && _actions.TryGetValue(cmd, out var action))
            Dispatcher.CurrentDispatcher.BeginInvoke(action, DispatcherPriority.Input);
        Dispose();
    }

    /// <summary>Shows the menu right under (or above) a WPF element.</summary>
    public void ShowFor(FrameworkElement anchor, bool forceDark = false, bool above = false)
    {
        var window = Window.GetWindow(anchor);
        var hwnd = window != null ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        var p = anchor.PointToScreen(above ? new Point(0, 0) : new Point(0, anchor.ActualHeight + 4));
        Show(hwnd, (int)p.X, (int)p.Y - (above ? 4 : 0), forceDark, alignBottom: above);
    }

    public void Dispose()
    {
        // DestroyMenu also destroys attached submenus.
        Native.DestroyMenu(_handle);
    }
}

internal static class ShellUtil
{
    public static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Log.Error("Reveal failed", ex); }
    }

    public static void Open(string pathOrUrl)
    {
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open failed", ex); }
    }

    /// <summary>Moves a file to the Recycle Bin (silently). Returns false if the file is still there.</summary>
    public static bool MoveToRecycleBin(string path)
    {
        try
        {
            var op = new Native.SHFILEOPSTRUCT
            {
                wFunc = Native.FO_DELETE,
                pFrom = path + "\0",
                fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI),
            };
            int r = Native.SHFileOperation(ref op);
            return r == 0 && !File.Exists(path);
        }
        catch (Exception ex)
        {
            Log.Error("Recycle failed", ex);
            return false;
        }
    }
}

/// <summary>Registers the app in "Open with" for media files (per user, no admin).</summary>
internal static class FileAssociations
{
    private const string ProgId = "LinearFrame.Media";

    public static void Register(IEnumerable<string> extensions)
    {
        try
        {
            string exe = AppPaths.ExePath;
            string cmd = $"\"{exe}\" \"%1\"";
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue("", "LinearFrame media");
                prog.SetValue("FriendlyTypeName", "LinearFrame media");
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
                using (var open = prog.CreateSubKey(@"shell\open\command")) open.SetValue("", cmd);
            }

            using var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{Path.GetFileName(exe)}");
            app.SetValue("FriendlyAppName", "LinearFrame");
            using (var open = app.CreateSubKey(@"shell\open\command")) open.SetValue("", cmd);
            using var supported = app.CreateSubKey("SupportedTypes");

            using var caps = Registry.CurrentUser.CreateSubKey(@"Software\LinearFrame\Capabilities");
            caps.SetValue("ApplicationName", "LinearFrame");
            caps.SetValue("ApplicationDescription", L.T("App.Tagline"));
            using var capsAssoc = caps.CreateSubKey("FileAssociations");

            foreach (var ext in extensions)
            {
                using (var e = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
                    e.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                supported.SetValue(ext, "");
                capsAssoc.SetValue(ext, ProgId);
            }

            using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                reg.SetValue("LinearFrame", @"Software\LinearFrame\Capabilities");

            // "Trim with LinearFrame" in the classic context menu of video files (independent of the default app)
            foreach (var ext in extensions.Where(e => Player.MediaFormats.Video.Contains(e)))
            {
                using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\LinearFrame.Trim");
                verb.SetValue("MUIVerb", L.T("Player.TrimVerb"));
                verb.SetValue("Icon", $"\"{exe}\",0");
                using var c = verb.CreateSubKey("command");
                c.SetValue("", $"\"{exe}\" --trim \"%1\"");
            }

            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("File association failed", ex); }
    }

    public static void OpenDefaultAppsSettings()
    {
        ShellUtil.Open("ms-settings:defaultapps?registeredAppUser=LinearFrame");
    }
}

/// <summary>Single instance + forwarding command lines through WM_COPYDATA.</summary>
internal static class Ipc
{
    public const int CopyDataId = 0x4C46; // 'LF'

    public static bool TrySendToRunningInstance(IReadOnlyList<string> args)
    {
        var hwnd = Native.FindWindow(null, MessageWindow.Title);
        if (hwnd == IntPtr.Zero) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        Native.AllowSetForegroundWindow((int)pid);

        var list = new List<string>();
        foreach (var a in args)
            list.Add(!a.StartsWith("--") && !a.StartsWith("/") || File.Exists(a) ? SafeFullPath(a) : a);
        string payload = list.Count == 0 ? "--show" : string.Join("\n", list);

        byte[] bytes = Encoding.Unicode.GetBytes(payload + "\0");
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var cds = new Native.COPYDATASTRUCT
            {
                dwData = new IntPtr(CopyDataId),
                cbData = bytes.Length,
                lpData = handle.AddrOfPinnedObject(),
            };
            Native.SendMessage(hwnd, Native.WM_COPYDATA, IntPtr.Zero, ref cds);
            return true;
        }
        finally { handle.Free(); }
    }

    public static string[]? ReadCopyData(IntPtr lParam)
    {
        var cds = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(lParam);
        if (cds.dwData.ToInt64() != CopyDataId || cds.cbData <= 0) return null;
        string s = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2).TrimEnd('\0');
        return s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }
}

/// <summary>Gives memory back to Windows after windows are closed (the app idles in the tray).</summary>
internal static class Memory
{
    private static DispatcherTimer? _timer;

    public static void TrimSoon(int delayMs = 2500)
    {
        if (Application.Current == null) return;
        _timer ??= new DispatcherTimer(DispatcherPriority.ApplicationIdle, Application.Current.Dispatcher);
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        // Only trim when the app is really idle in the tray - never while something is on screen.
        bool busy = Capture.CaptureSession.Current != null ||
                    Application.Current.Windows.Cast<Window>().Any(w => w.IsVisible && !w.Title.StartsWith("LinearFrame.Hud"));
        if (busy) return;
        TrimNow();
    }

    public static void TrimNow()
    {
        try
        {
            long before = Environment.WorkingSet;
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            bool ok = Native.SetProcessWorkingSetSizeEx(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1), 0);
            Log.Info($"Trim: ok={ok} ws {before / 1048576} MB -> {Environment.WorkingSet / 1048576} MB, gc heap {GC.GetTotalMemory(false) / 1048576} MB");
        }
        catch (Exception ex) { Log.Error("Trim failed", ex); }
    }
}
