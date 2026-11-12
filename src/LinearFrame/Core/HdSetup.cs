using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace LinearFrame.Core;

/// <summary>
/// One-time set-up of the virtual screen that HD captures render on: the open-source "Virtual Display Driver"
/// (MIT, signed; github.com/VirtualDrivers/Virtual-Display-Driver). The package (130 KB) is downloaded from its
/// GitHub release, checked against a pinned SHA-256 and installed by an elevated copy of LinearFrame - one Windows
/// confirmation. After that everything (showing / hiding the screen, its resolution, its scaling) works without admin rights.
/// </summary>
public static class HdSetup
{
    public const string DriverVersion = "25.7.23";
    public const string DriverPage = "https://github.com/VirtualDrivers/Virtual-Display-Driver";
    private const string DriverUrl = DriverPage + "/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip";
    private const string DriverSha256 = "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a";
    private const string HardwareId = @"Root\MttVDD";
    private const string ConfigDir = @"C:\VirtualDisplayDriver";
    private const string Marker = "LinearFrame.txt";
    private const int Cancelled = 1223; // ERROR_CANCELLED - "No" in the Windows prompt

    public sealed record Outcome(bool Ok, string? Error = null);

    /// <summary>LinearFrame installed the driver itself (so it may also remove it).</summary>
    public static bool OwnsDriver
    {
        get { try { return File.Exists(Path.Combine(ConfigDir, Marker)); } catch { return false; } }
    }

    // ------------------------------------------------------------------ from the app (not elevated)

    public static async Task<Outcome> InstallAsync(Action<string>? status = null)
    {
        try
        {
            if (RuntimeInformation.OSArchitecture != Architecture.X64) return new(false, L.T("Hd.Err.Arch"));
            if (VirtualScreen.Find() != null) return new(true); // already there (e.g. set up by another tool)

            status?.Invoke(L.T("Hd.Status.Downloading"));
            string inf = await FetchDriverAsync();
            status?.Invoke(L.T("Hd.Status.Installing"));
            int code = await RunElevatedAsync("install", inf);
            if (code == Cancelled) return new(false, L.T("Hd.Err.Cancelled"));
            if (code != 0) return new(false, L.F("Hd.Err.Install", Describe(code)));

            // the screen shows up a moment after the driver has started; Windows puts it on the desktop - take it off again
            for (int i = 0; i < 60; i++)
            {
                var v = VirtualScreen.Find();
                if (v != null)
                {
                    if (v.Attached) VirtualScreen.Park(force: true);
                    Log.Info("HD setup: virtual screen ready");
                    return new(true);
                }
                await Task.Delay(250);
            }
            return new(false, L.T("Hd.Err.NoScreen"));
        }
        catch (Exception ex)
        {
            Log.Error("HD setup failed", ex);
            return new(false, ex.Message);
        }
    }

    public static async Task<Outcome> RemoveAsync()
    {
        try
        {
            VirtualScreen.Park(force: true);
            int code = await RunElevatedAsync("remove", "-");
            if (code == Cancelled) return new(false, L.T("Hd.Err.Cancelled"));
            return code == 0 ? new(true) : new(false, L.F("Hd.Err.Install", Describe(code)));
        }
        catch (Exception ex)
        {
            Log.Error("HD removal failed", ex);
            return new(false, ex.Message);
        }
    }

    private static string DriverDir => Path.Combine(AppPaths.LocalDir, "hd-driver", DriverVersion);
    private static string SetupLog => Path.Combine(AppPaths.LocalDir, "hd-setup.log");

    private static string? FindFile(string dir, string name) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault() : null;

    /// <summary>Downloads the driver package (once), verifies it and unpacks it. Returns the path of its INF.</summary>
    private static async Task<string> FetchDriverAsync()
    {
        string dir = DriverDir;
        if (FindFile(dir, "MttVDD.inf") is string ready && FindFile(dir, "MttVDD.dll") != null) return ready;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LinearFrame");
        byte[] zip = await http.GetByteArrayAsync(DriverUrl);
        string hash = Convert.ToHexString(SHA256.HashData(zip));
        if (!hash.Equals(DriverSha256, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"HD setup: driver package hash {hash} does not match");
            throw new InvalidOperationException(L.T("Hd.Err.Checksum"));
        }

        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            foreach (var e in archive.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue; // folder
                string target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never outside our folder
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, true);
            }
        }
        Log.Info($"HD setup: driver {DriverVersion} downloaded and verified ({zip.Length} bytes)");
        return FindFile(dir, "MttVDD.inf") ?? throw new FileNotFoundException("MttVDD.inf is missing from the driver package");
    }

    private static async Task<int> RunElevatedAsync(string verb, string argument)
    {
        var psi = new ProcessStartInfo(AppPaths.ExePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--hd-setup");
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(argument);
        psi.ArgumentList.Add(SetupLog);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return 1;
            await p.WaitForExitAsync();
            Log.Info($"HD setup: elevated '{verb}' finished with {p.ExitCode}");
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return Cancelled;
        }
    }

    private static string Describe(int code) => unchecked((uint)code) switch
    {
        0xE0000242 or 0xE0000243 or 0xE0000241 or 0x800B0109 or 0x800B0100 => L.T("Hd.Err.Declined"),
        5 => L.T("Hd.Err.Access"),
        _ => $"0x{code:X8}",
    };

    // ------------------------------------------------------------------ elevated: LinearFrame.exe --hd-setup <install|remove> <inf|-> <log>

    /// <summary>Entry point of the elevated copy. Returns the process exit code (0 = done).</summary>
    public static int RunElevated(string[] args)
    {
        string verb = args.Length > 1 ? args[1] : "";
        string argument = args.Length > 2 ? args[2] : "";
        string? logPath = args.Length > 3 ? args[3] : null;
        var log = new StringBuilder();
        void Say(string s) => log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {s}");
        int code;
        try
        {
            code = verb switch
            {
                "install" => Install(argument, Say),
                "remove" => Remove(Say),
                _ => 87, // ERROR_INVALID_PARAMETER
            };
        }
        catch (Win32Exception ex)
        {
            Say($"FAILED {ex.NativeErrorCode} (0x{ex.NativeErrorCode:X8}): {ex.Message}");
            code = ex.NativeErrorCode != 0 ? ex.NativeErrorCode : 1;
        }
        catch (Exception ex)
        {
            Say("FAILED " + ex);
            code = 1;
        }
        Say($"exit {code}");
        try { if (logPath != null) File.AppendAllText(logPath, log.ToString()); } catch { /* the log is a nicety */ }
        return code;
    }

    private static int Install(string inf, Action<string> say)
    {
        if (!File.Exists(inf)) { say("INF not found: " + inf); return 2; }
        say($"install {inf}");

        // 1) the driver's settings: one screen, our resolutions (kept if somebody already has their own set-up)
        Directory.CreateDirectory(ConfigDir);
        string xml = Path.Combine(ConfigDir, "vdd_settings.xml");
        if (!File.Exists(xml))
        {
            string? shipped = FindFile(Path.GetDirectoryName(Path.GetDirectoryName(inf)!) ?? "", "vdd_settings.xml")
                              ?? FindFile(Path.GetDirectoryName(inf)!, "vdd_settings.xml");
            File.WriteAllText(xml, SettingsXml(shipped != null ? File.ReadAllText(shipped) : null), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(ConfigDir, Marker),
                "This folder was created by LinearFrame for its HD captures (virtual screen). Settings → HD captures removes it again.\r\n");
            say("settings written");
        }
        else say("existing settings kept");

        // 2) the device node (once) and the driver for it
        bool existed = ForEachDevice(presentOnly: false, null) > 0;
        if (!existed) { CreateRootDevice(inf); say("device node created"); }
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, INSTALLFLAG_FORCE, out bool reboot))
        {
            int err = Marshal.GetLastWin32Error();
            say($"driver install failed: {err} (0x{err:X8})");
            if (!existed) ForEachDevice(presentOnly: false, Uninstall); // don't leave a half-made device behind
            return err != 0 ? err : 1;
        }
        say($"driver installed (reboot needed: {reboot})");

        // 3) Windows extends the desktop onto a new screen straight away - take it off again
        for (int i = 0; i < 40; i++)
        {
            var v = VirtualScreen.Find();
            if (v != null)
            {
                say($"virtual screen {v.Device} found, attached: {v.Attached}");
                if (v.Attached) VirtualScreen.Park(force: true);
                break;
            }
            Thread.Sleep(250);
        }
        return 0;
    }

    private static int Remove(Action<string> say)
    {
        int n = ForEachDevice(presentOnly: false, Uninstall);
        say($"devices removed: {n}");
        try
        {
            string inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            foreach (var f in Directory.EnumerateFiles(inf, "oem*.inf"))
            {
                string text;
                try { text = File.ReadAllText(f); } catch { continue; }
                if (!text.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)) continue;
                bool ok = SetupUninstallOEMInf(Path.GetFileName(f), SUOI_FORCEDELETE, IntPtr.Zero);
                say($"driver package {Path.GetFileName(f)} removed: {ok}");
            }
        }
        catch (Exception ex) { say("driver package clean-up: " + ex.Message); }
        try
        {
            if (File.Exists(Path.Combine(ConfigDir, Marker))) { Directory.Delete(ConfigDir, true); say("settings folder removed"); }
        }
        catch (Exception ex) { say("settings folder: " + ex.Message); }
        return 0;
    }

    /// <summary>The driver's settings file: its own (shipped) file with our list of resolutions, or a minimal one.</summary>
    internal static string SettingsXml(string? shipped)
    {
        var modes = new List<(int Width, int Height, int Frequency)> { (1920, 1080, 60) };
        modes.AddRange(VirtualScreen.KnownModes);
        XElement Resolution((int Width, int Height, int Frequency) m) => new("resolution",
            new XElement("width", m.Width), new XElement("height", m.Height), new XElement("refresh_rate", m.Frequency));
        try
        {
            if (!string.IsNullOrWhiteSpace(shipped))
            {
                var doc = XDocument.Parse(shipped);
                var root = doc.Root!;
                var monitors = root.Element("monitors") ?? Added(root, "monitors");
                (monitors.Element("count") ?? Added(monitors, "count")).Value = "1";
                var global = root.Element("global") ?? Added(root, "global");
                global.Elements("g_refresh_rate").Remove();
                global.Add(new XElement("g_refresh_rate", 60));
                var res = root.Element("resolutions") ?? Added(root, "resolutions");
                res.RemoveAll();
                foreach (var m in modes) res.Add(Resolution(m));
                return "<?xml version='1.0' encoding='utf-8'?>\r\n" + doc.Root;
            }
        }
        catch (Exception ex) { Log.Warn("HD setup: shipped settings unusable - " + ex.Message); }

        var minimal = new XElement("vdd_settings",
            new XElement("monitors", new XElement("count", 1)),
            new XElement("gpu", new XElement("friendlyname", "default")),
            new XElement("global", new XElement("g_refresh_rate", 60)),
            new XElement("resolutions", modes.Select(Resolution)),
            new XElement("logging", new XElement("SendLogsThroughPipe", "false"), new XElement("logging", "false"), new XElement("debuglogging", "false")),
            new XElement("colour", new XElement("SDR10bit", "false"), new XElement("HDRPlus", "false"), new XElement("ColourFormat", "RGB")),
            new XElement("cursor", new XElement("HardwareCursor", "true"), new XElement("CursorMaxX", 128), new XElement("CursorMaxY", 128),
                new XElement("AlphaCursorSupport", "true"), new XElement("XorCursorSupportLevel", 2)),
            new XElement("edid", new XElement("CustomEdid", "false"), new XElement("PreventSpoof", "false"), new XElement("EdidCeaOverride", "false")));
        return "<?xml version='1.0' encoding='utf-8'?>\r\n" + minimal;

        static XElement Added(XElement parent, string name) { var e = new XElement(name); parent.Add(e); return e; }
    }

    // ------------------------------------------------------------------ SetupAPI (what "devcon install" does)

    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly IntPtr InvalidHandle = new(-1);
    private const int DICD_GENERATE_ID = 1, SPDRP_HARDWAREID = 1, DIF_REGISTERDEVICE = 0x19, DIGCF_PRESENT = 2;
    private const int INSTALLFLAG_FORCE = 1, SUOI_FORCEDELETE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    /// <summary>Is the driver's device there? (works without admin rights)</summary>
    public static bool DevicePresent() { try { return ForEachDevice(presentOnly: true, null) > 0; } catch { return false; } }

    private static int ForEachDevice(bool presentOnly, Action<IntPtr, SP_DEVINFO_DATA>? action)
    {
        var cls = DisplayClass;
        IntPtr set = SetupDiGetClassDevs(ref cls, null, IntPtr.Zero, presentOnly ? DIGCF_PRESENT : 0);
        if (set == InvalidHandle) return 0;
        int found = 0;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buffer = new byte[4096];
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, out _, buffer, buffer.Length, out int size)) continue;
                string ids = Encoding.Unicode.GetString(buffer, 0, Math.Clamp(size, 0, buffer.Length));
                if (!ids.Split('\0').Any(s => s.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) continue;
                found++;
                action?.Invoke(set, data);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return found;
    }

    private static void Uninstall(IntPtr set, SP_DEVINFO_DATA data) => DiUninstallDevice(IntPtr.Zero, set, ref data, 0, out _);

    private static void CreateRootDevice(string inf)
    {
        var className = new StringBuilder(64);
        if (!SetupDiGetINFClass(inf, out Guid cls, className, className.Capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr set = SetupDiCreateDeviceInfoList(ref cls, IntPtr.Zero);
        if (set == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfo(set, className.ToString(), ref cls, null, IntPtr.Zero, DICD_GENERATE_ID, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] id = Encoding.Unicode.GetBytes(HardwareId + "\0\0"); // REG_MULTI_SZ
            if (!SetupDiSetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, id, id.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Self-test: reads the device class out of an INF (no admin rights needed) - proves the interop is declared right.</summary>
    internal static (Guid Class, string Name) InfClass(string inf)
    {
        var name = new StringBuilder(64);
        if (!SetupDiGetINFClass(inf, out Guid cls, name, name.Capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (cls, name.ToString());
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetINFClass(string infName, out Guid classGuid, StringBuilder className, int classNameSize, out int requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfo(IntPtr set, string deviceName, ref Guid classGuid, string? description, IntPtr hwndParent, int flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property, byte[] buffer, int size);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property, out int type, byte[] buffer, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupUninstallOEMInf(string infFileName, int flags, IntPtr reserved);
    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath, int installFlags, out bool rebootRequired);
    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiUninstallDevice(IntPtr hwndParent, IntPtr set, ref SP_DEVINFO_DATA data, int flags, out bool needReboot);
}
