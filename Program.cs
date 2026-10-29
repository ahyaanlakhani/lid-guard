// LidGuard - full-screen password cover that keeps the PC running with the lid closed.
// Lives in the tray; a global shortcut (Ctrl+L by default) locks the screen.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, KeyBlock.Proc cb, IntPtr mod, uint tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid setting, int flags);
    [DllImport("user32.dll")] public static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, int[] data, uint ini);
    public static readonly Guid LidSwitchState = new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
    public static readonly Guid ConsoleDisplayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x312;
    public static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff, HWND_TOPMOST = (IntPtr)(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    public static readonly uint MyPid = (uint)Process.GetCurrentProcess().Id;

    public static bool OwnsForeground()
    {
        uint pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        return pid == MyPid;
    }

    // Windows refuses SetForegroundWindow from background apps; sharing input state
    // with the current foreground thread for a moment lets the cover take focus.
    public static void ForceForeground(IntPtr h)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == h) return;
        uint pid;
        uint fgThread = GetWindowThreadProcessId(fg, out pid);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // watchdog: a helper process that locks Windows if LidGuard dies while the screen is locked
        int workWd = Array.IndexOf(args, "--work-watchdog");
        if (workWd >= 0 && workWd + 1 < args.Length) { SecureWorkSession.Watch(int.Parse(args[workWd + 1])); return; }
        int wd = Array.IndexOf(args, "--watchdog");
        if (wd >= 0 && wd + 1 < args.Length) { Guard.Watch(int.Parse(args[wd + 1])); return; }
        bool recover = Array.IndexOf(args, "--recover") >= 0;
        bool background = Array.IndexOf(args, "--background") >= 0;
        bool first;
        using (var mutex = new Mutex(true, "LidGuard.SingleInstance", out first))
        {
            if (!first)
            {
                if (recover) { Environment.ExitCode = 2; return; }
                // already running: ask that instance to show its window
                if (!background) Native.PostMessage(Native.HWND_BROADCAST, App.ShowMsg, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Power.RecoverIfStale();
            Guard.Release();
            if (recover)
            {
                Environment.ExitCode = RecoveryPending() ? 1 : 0;
                return;
            }
            // a previous run died while locked: put the taskbar and the rest back
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { Power.RestoreNow(); Guard.Release(); };
            SystemEvents.SessionEnding += (s, e) => { Power.RestoreNow(); Guard.Release(); };
            Application.Run(new App(background));
        }
    }
    static bool RecoveryPending()
    {
        return File.Exists(Path.Combine(Config.Dir, "restore.txt")) || Guard.Engaged || File.Exists(Path.Combine(Config.Dir, "dnd.flag"));
    }
}

// ---------- settings + password + secret key ----------
static class Config
{
    // LIDGUARD_HOME overrides the data folder (portable use, testing)
    public static readonly string Dir = Environment.GetEnvironmentVariable("LIDGUARD_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LidGuard");
    static readonly string FilePath = Path.Combine(Dir, "config.txt");
    static string salt = "", hash = "", keySalt = "", keyHash = "";
    public static bool Dark = true;
    public static string Hotkey = "Ctrl+L";

    // lock screen personalisation
    public static string Name = "", Note = "Back soon", City = "", Video = "", VideoSections = "", Wallpaper = "", VideoLoop = "", VideoLoopKey = "";   // VideoLoop: the baked seamless clip   // Wallpaper: "builtin" or "video"
    public static string WallStyle = "Classic";   // which built-in wallpaper: see Wallpaper.Styles
    public static string WallpaperMode { get { return Wallpaper.Length > 0 ? Wallpaper : Video.Length > 0 ? "video" : "builtin"; } }
      // VideoSections: "3.4-7.01;15.2-16.41" plays just those parts, in order
    public static double Lat, Lon, VideoStart, VideoEnd, VideoSpeed = 1;   // loop only this part of the video (seconds; end 0 = to the end)

    static Config()
    {
        // Location stays unset until the user chooses a weather city.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string k = line.Substring(0, i), v = line.Substring(i + 1);
                if (k == "salt") salt = v; else if (k == "hash") hash = v; else if (k == "dark") Dark = v == "1";
                else if (k == "ksalt") keySalt = v; else if (k == "khash") keyHash = v;
                else if (k == "hotkey") Hotkey = v;
                else if (k == "name") Name = v; else if (k == "note") Note = v; else if (k == "city") City = v; else if (k == "video") Video = v; else if (k == "videosections") VideoSections = v; else if (k == "wallpaper") Wallpaper = v == "journey" ? "builtin" : v; else if (k == "wallstyle") WallStyle = v; else if (k == "videoloop") VideoLoop = v; else if (k == "videoloopkey") VideoLoopKey = v;
                else if (k == "lat") double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out Lat);
                else if (k == "lon") double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out Lon);
                else if (k == "videostart") double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out VideoStart);
                else if (k == "videoend") double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out VideoEnd);
                else if (k == "videospeed") { double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out VideoSpeed); if (VideoSpeed < 0.25 || VideoSpeed > 2) VideoSpeed = 1; }
            }
        }
        catch { }
    }

    public static bool HasPassword { get { return hash.Length > 0; } }
    public static bool HasKey { get { return keyHash.Length > 0; } }
    public static bool HasPlace { get { return City.Length > 0 && (Lat != 0 || Lon != 0); } }

    public static void Save()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(FilePath, new[] { "salt=" + salt, "hash=" + hash, "dark=" + (Dark ? "1" : "0"), "ksalt=" + keySalt, "khash=" + keyHash, "hotkey=" + Hotkey,
            "name=" + Name, "note=" + Note, "city=" + City, "lat=" + Lat.ToString(inv), "lon=" + Lon.ToString(inv), "video=" + Video, "videostart=" + VideoStart.ToString(inv), "videoend=" + VideoEnd.ToString(inv), "videosections=" + VideoSections, "videospeed=" + VideoSpeed.ToString(inv), "wallpaper=" + Wallpaper, "wallstyle=" + WallStyle, "videoloop=" + VideoLoop, "videoloopkey=" + VideoLoopKey });
    }

    static byte[] Derive(string input, byte[] s)
    {
        using (var d = new Rfc2898DeriveBytes(input, s, 100000)) return d.GetBytes(32);
    }

    static void Store(string input, out string s64, out string h64)
    {
        var s = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(s);
        s64 = Convert.ToBase64String(s);
        h64 = Convert.ToBase64String(Derive(input, s));
    }

    static bool Check(string input, string s, string h)
    {
        if (h.Length == 0) return false;
        var expected = Convert.FromBase64String(h);
        var actual = Derive(input, Convert.FromBase64String(s));
        int diff = 0;
        for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ actual[i];
        return diff == 0;
    }

    public static void SetPassword(string pw) { Store(pw, out salt, out hash); Save(); }
    public static bool Verify(string pw) { return Check(pw, salt, hash); }

    // the user picks their own key; only a salted hash is stored (case-sensitive)
    public static void SetKey(string key) { Store(key, out keySalt, out keyHash); Save(); }
    public static bool VerifyKey(string key) { return Check(key, keySalt, keyHash); }
}

// keeps the baked seamless loop of the chosen video in step with the settings
static class Loop
{
    static int building;

    static string CurrentKey()
    {
        return Config.Video.Length > 0 ? LoopBuilder.Key(Config.Video, Config.VideoSections, Config.VideoSpeed) : "";
    }

    public static bool Ready
    {
        get
        {
            return Config.VideoLoop.Length > 0 && System.IO.File.Exists(Config.VideoLoop)
                && Config.VideoLoopKey.Length > 0 && Config.VideoLoopKey == CurrentKey();
        }
    }

    // (re)build in the background when the video, its sections or the speed changed
    public static void EnsureAsync()
    {
        if (Config.WallpaperMode != "video" || Config.Video.Length == 0 || !System.IO.File.Exists(Config.Video)) return;
        if (VideoWallpaper.ParseSections(Config.VideoSections).Count == 0 && Config.VideoSpeed == 1) return;   // nothing to bake
        if (Ready || Interlocked.Exchange(ref building, 1) == 1) return;
        string key = CurrentKey(), video = Config.Video, sections = Config.VideoSections;
        double speed = Config.VideoSpeed;
        var parts = VideoWallpaper.ParseSections(sections);
        if (parts.Count == 0) parts.Add(new[] { Config.VideoStart, Config.VideoEnd });
        var thread = new Thread(() =>
        {
            try
            {
                string dest = Path.Combine(Config.Dir, "wallpaper-loop.mp4"), temp = dest + ".building.mp4";
                LoopBuilder.Build(video, parts, speed, temp);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(temp, dest);
                lock (typeof(Config)) { Config.VideoLoop = dest; Config.VideoLoopKey = key; Config.Save(); }
            }
            catch { }
            finally { building = 0; }
        }) { IsBackground = true, Name = "LidGuard loop builder", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
}

static class Hotkeys
{
    public static readonly string[] Names = { "Ctrl+L", "Ctrl+Alt+L", "Ctrl+Shift+L", "Off" };
    public const uint KeyL = 0x4C;

    public static bool Modifiers(string name, out uint mods)
    {
        mods = 0;
        if (name == "Ctrl+L") mods = Native.MOD_CONTROL;
        else if (name == "Ctrl+Alt+L") mods = Native.MOD_CONTROL | Native.MOD_ALT;
        else if (name == "Ctrl+Shift+L") mods = Native.MOD_CONTROL | Native.MOD_SHIFT;
        return mods != 0;
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("LidGuard") != null; }
    }

    public static void Set(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("LidGuard", "\"" + Application.ExecutablePath + "\" --background");
            else k.DeleteValue("LidGuard", false);
        }
    }
}

static class AppIcon
{
    static Icon Load(Size? size)
    {
        var st = typeof(AppIcon).Assembly.GetManifestResourceStream("LidGuard.ico");
        if (st == null) return SystemIcons.Shield;
        using (st) return size.HasValue ? new Icon(st, size.Value) : new Icon(st);
    }

    public static Icon Full { get { return Load(null); } }
    public static Icon Small { get { return Load(SystemInformation.SmallIconSize); } }
}

// ---------- power: keep running with the lid closed, restore afterwards ----------
static class Power
{
    static readonly string StateFile = Path.Combine(Config.Dir, "restore.txt");
    static readonly object gate = new object(), run = new object();
    static System.Threading.Tasks.Task queue = System.Threading.Tasks.Task.FromResult(0);

    // power changes run in order on a worker thread so the UI (and the keyboard hook) never stalls
    // lid setting first: closing the lid right after locking must already be safe
    public static void EnterWorkModeAsync()
    {
        lock (gate) queue = queue.ContinueWith(t =>
        {
            try { EnterWorkMode(); Dnd.On(); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not enable closed-lid work mode. Your laptop may still sleep when closed.\n" + ex.Message,
                    "LidGuard power settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
    }
    public static void RestoreAsync() { lock (gate) queue = queue.ContinueWith(t => Restore()); }

    // finish anything queued, then make sure the user's settings are back (exit, sign-out)
    public static void RestoreNow()
    {
        try { queue.Wait(5000); } catch { }
        Restore();
    }

    internal static bool HasPending { get { return File.Exists(StateFile); } }
    internal static void EnterSecureWorkMode()
    {
        lock (run) PowerRecovery.Enter(StateFile, Backend);
    }

    internal static IPowerBackend Backend = new WindowsPowerBackend();
    static void EnterWorkMode()
    {
        lock (run) PowerRecovery.Enter(StateFile, Backend);
    }

    static void Restore()
    {
        Dnd.Off();
        lock (run)
        {
            try { PowerRecovery.Restore(StateFile, Backend); }
            catch (Exception ex) { Trace.TraceError("Power restore failed: " + ex.Message); }
        }
    }

    // if a previous run died while locked, put the user's power settings back
    public static void RecoverIfStale() { RestoreNow(); }
}

// ---------- Do Not Disturb while locked (Windows presentation mode silences notifications) ----------
static class Dnd
{
    // the flag lets a later run turn it back off if LidGuard died while locked
    static readonly string Flag = Path.Combine(Config.Dir, "dnd.flag");

    static void Run(string arg)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PresentationSettings.exe"), arg) { UseShellExecute = false, CreateNoWindow = true };
            // PresentationSettings may stay running; don't wait on it
            using (Process.Start(psi)) { }
        }
        catch { }
    }

    public static void On()
    {
        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(Flag, "1");
        Run("/start");
    }

    public static void Off()
    {
        if (!File.Exists(Flag)) return;
        Run("/stop");
        try { File.Delete(Flag); } catch { }
    }
}

// ---------- block app-switching shortcuts while locked ----------
static class KeyBlock
{
    public delegate IntPtr Proc(int n, IntPtr w, IntPtr l);
    static readonly Proc proc = Callback;   // keep a reference so it is not collected
    static IntPtr hook = IntPtr.Zero;
    static Thread thread;
    static uint threadId;

    [StructLayout(LayoutKind.Sequential)]
    struct KBD { public uint vk, scan, flags, time; public IntPtr extra; }

    // the hook lives on its own thread: Windows silently drops a keyboard hook that answers
    // too slowly, and the UI thread is busy painting the video
    public static void Install()
    {
        if (thread != null) return;
        var ready = new ManualResetEvent(false);
        thread = new Thread(() =>
        {
            threadId = Native.GetCurrentThreadId();
            hook = Native.SetWindowsHookEx(13, proc, Native.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName), 0);
            ready.Set();
            Native.MSG m;
            while (Native.GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
            Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "LidGuard keyboard guard", Priority = ThreadPriority.Highest };
        thread.Start();
        ready.WaitOne(2000);
    }

    public static void Remove()
    {
        if (thread == null) return;
        Native.PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);   // WM_QUIT
        thread.Join(2000);
        thread = null;
    }

    static IntPtr Callback(int n, IntPtr w, IntPtr l)
    {
        if (n >= 0)
        {
            // keys only ever go to the cover; anything aimed at a window behind it is dropped
            if (!Native.OwnsForeground()) return (IntPtr)1;
            var k = (KBD)Marshal.PtrToStructure(l, typeof(KBD));
            bool alt = (k.flags & 0x20) != 0;
            bool ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
            uint vk = k.vk;
            if (vk == 0x5B || vk == 0x5C || vk == 0x5D) return (IntPtr)1;            // Win keys, Menu key
            if (alt && (vk == 0x09 || vk == 0x1B || vk == 0x73)) return (IntPtr)1;     // Alt+Tab, Alt+Esc, Alt+F4
            if (ctrl && vk == 0x1B) return (IntPtr)1;                                  // Ctrl+Esc, Ctrl+Shift+Esc
            if (vk == 0x2C) return (IntPtr)1;                                          // Print Screen (opens the Snipping Tool)
            if ((vk >= 0xA6 && vk <= 0xAC) || (vk >= 0xB4 && vk <= 0xB7)) return (IntPtr)1;   // browser, mail, media-player and app keys
        }
        return Native.CallNextHookEx(hook, n, w, l);
    }
}

// ---------- guardrails while locked: everything outside the cover stays out of reach ----------
// hides the taskbar, turns off Task Manager (the one useful item behind Ctrl+Alt+Del) and the
// Sticky/Filter/Toggle Keys shortcuts, whose pop-ups link into Settings. Everything is put back
// on unlock; guard.txt holds the originals so a later run or the watchdog can restore them.
static class Guard
{
    static readonly string StateFile = Path.Combine(Config.Dir, "guard.txt");
    const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    static readonly object gate = new object();
    // SPI get/set pairs and struct sizes for STICKYKEYS, FILTERKEYS, TOGGLEKEYS; bit 0x4 is "shortcut active" in all three
    static readonly uint[][] Shortcuts = { new uint[] { 0x3A, 0x3B, 8 }, new uint[] { 0x32, 0x33, 24 }, new uint[] { 0x34, 0x35, 8 } };
    static Process watchdog;

    public static bool Engaged { get { return File.Exists(StateFile); } }

    public static void Engage()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(StateFile))
                {
                    var saved = new List<string>();
                    using (var k = Registry.CurrentUser.OpenSubKey(PolicyKey))
                    {
                        object v = k == null ? null : k.GetValue("DisableTaskMgr");
                        saved.Add(v == null ? "" : v.ToString());
                    }
                    foreach (var s in Shortcuts) saved.Add(ShortcutFlags(s).ToString());
                    Directory.CreateDirectory(Config.Dir);
                    File.WriteAllText(StateFile, string.Join(",", saved));
                }
            }
            catch { }
            // the policy key is admin-only on most PCs; then Task Manager is handled by the focus guard instead
            try { using (var k = Registry.CurrentUser.CreateSubKey(PolicyKey)) k.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord); } catch { }
            foreach (var s in Shortcuts) { try { SetShortcutFlags(s, ShortcutFlags(s) & ~4); } catch { } }
            HideTaskbars();
        }
        StartWatchdog();
    }

    public static void Release()
    {
        StopWatchdog();
        lock (gate)
        {
            if (!File.Exists(StateFile)) return;
            ShowTaskbars();
            try
            {
                var p = File.ReadAllText(StateFile).Trim().Split(',');
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(PolicyKey, true))
                        if (k != null)
                        {
                            if (p[0].Length == 0) k.DeleteValue("DisableTaskMgr", false);
                            else k.SetValue("DisableTaskMgr", int.Parse(p[0]), RegistryValueKind.DWord);
                        }
                }
                catch { return; }
                for (int i = 0; i < Shortcuts.Length && i + 1 < p.Length; i++)
                    SetShortcutFlags(Shortcuts[i], (ShortcutFlags(Shortcuts[i]) & ~4) | (int.Parse(p[i + 1]) & 4));
                File.Delete(StateFile);
            }
            catch { }
        }
    }

    static int ShortcutFlags(uint[] s)
    {
        var data = new int[s[2] / 4];
        data[0] = (int)s[2];
        return Native.SystemParametersInfo(s[0], s[2], data, 0) ? data[1] : 0;
    }

    // not saved to the profile (last argument 0), so a restart brings the shortcuts back regardless
    static void SetShortcutFlags(uint[] s, int flags)
    {
        var data = new int[s[2] / 4];
        data[0] = (int)s[2];
        if (!Native.SystemParametersInfo(s[0], s[2], data, 0)) throw new InvalidOperationException("Could not read accessibility settings.");
        data[1] = flags;
        if (!Native.SystemParametersInfo(s[1], s[2], data, 0)) throw new InvalidOperationException("Could not restore accessibility settings.");
    }

    // the taskbar on every screen; Explorer can bring it back, so the lock re-hides it regularly
    public static void HideTaskbars() { Taskbars(0); }
    static void ShowTaskbars() { Taskbars(8); }   // SW_SHOWNA: show without taking focus

    static void Taskbars(int cmd)
    {
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            for (IntPtr h = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, cls, null); h != IntPtr.Zero; h = Native.FindWindowEx(IntPtr.Zero, h, cls, null))
                Native.ShowWindow(h, cmd);
    }

    static void StartWatchdog()
    {
        try
        {
            if (watchdog != null && !watchdog.HasExited) return;
            watchdog = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--watchdog " + Native.MyPid) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { watchdog = null; }
    }

    static void StopWatchdog()
    {
        try { if (watchdog != null && !watchdog.HasExited) watchdog.Kill(); } catch { }
        watchdog = null;
    }

    // runs in the watchdog process: if LidGuard ends while still locked (crash, killed),
    // lock Windows itself so the desktop never sits open, then put the user's settings back
    public static void Watch(int pid)
    {
        try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(); } catch { }
        if (!Engaged) return;
        Native.LockWorkStation();
        Power.RestoreNow();
        Release();
    }
}

// ---------- the tray app: owns the hotkey, the main window and the lock ----------
class App : ApplicationContext
{
    public static readonly uint ShowMsg = Native.RegisterWindowMessage("LidGuard.ShowWindow");
    readonly NotifyIcon tray;
    readonly MessageWindow msgWin;
    MainForm main;
    LockSession session;
    readonly SecureWorkSession work;
    public bool Quitting;
    public bool HotkeyOk = true;

    public App(bool background)
    {
        msgWin = new MessageWindow(this);
        work = new SecureWorkSession(Balloon);
        var menu = MenuRenderer.NewMenu();
        var miLock = menu.Items.Add("Privacy cover", null, (a, e) => Lock());
        menu.Items.Add("Lock Windows securely", null, (a, e) => SecureLock());
        var miWork = menu.Items.Add("Secure closed-lid session...", null, (a, e) => StartWork());
        var miEndWork = menu.Items.Add("End work session", null, (a, e) => work.End("Session ended."));
        var miOpen = menu.Items.Add("Open LidGuard", null, (a, e) => ShowMain());
        menu.Items.Add(new ToolStripSeparator());
        var miExit = menu.Items.Add("Exit", null, (a, e) => Quit());
        menu.Opening += (a, e) =>
        {
            miLock.Enabled = session == null && !work.Active;
            miOpen.Enabled = miExit.Enabled = session == null;
            miWork.Enabled = session == null && !work.Active;
            miEndWork.Enabled = work.Active;
            miEndWork.Text = "End session (" + work.Status + ")";
        };
        tray = new NotifyIcon { Icon = AppIcon.Small, Text = "LidGuard", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (a, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };

        ApplyHotkey(false);
        // have the wallpaper and weather ready before the first lock, so the cover appears instantly
        var size = Screen.PrimaryScreen.Bounds.Size;
        ThreadPool.QueueUserWorkItem(_ => { try { Wallpaper.Get(size); } catch { } });
        Loop.EnsureAsync();
        Weather.Refresh();
        if (!background || !Config.HasPassword) ShowMain();
    }

    public void Balloon(string text) { tray.ShowBalloonTip(3000, "LidGuard", text, ToolTipIcon.Info); }

    public bool ApplyHotkey(bool interactive)
    {
        msgWin.ClearHotkey();
        uint mods;
        HotkeyOk = true;
        if (!Hotkeys.Modifiers(Config.Hotkey, out mods)) { tray.Text = "LidGuard"; return true; }
        if (msgWin.SetHotkey(mods, Hotkeys.KeyL)) { tray.Text = "LidGuard - " + Config.Hotkey + " to lock"; return true; }
        HotkeyOk = false;
        tray.Text = "LidGuard";
        string msg = Config.Hotkey + " is already taken by another app. Pick a different shortcut in LidGuard.";
        if (interactive) MessageBox.Show(msg, "LidGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else Balloon(msg);
        return false;
    }

    public bool WorkActive { get { return work.Active; } }
    public string WorkStatus { get { return work.Status; } }
    public void StartWork()
    {
        if (session != null) return;
        if (work.Active) { work.End("Session ended."); return; }
        foreach (Form f in Application.OpenForms) if (f.Modal) { f.Activate(); return; }
        using (var dialog = new WorkSessionDialog())
        {
            if (dialog.ShowDialog(main) != DialogResult.OK) return;
            try { work.Start(dialog.Options); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Could not start session", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    public void SecureLock()
    {
        if (!Native.LockWorkStation())
            Balloon("Windows could not lock this session. Try Win+L.");
    }

    public void Lock()
    {
        if (session != null || work.Active) return;
        // a dialog (e.g. change password) is open: bring it forward instead of locking over it
        foreach (Form f in Application.OpenForms) if (f.Modal) { f.Activate(); return; }
        if (!Config.HasPassword)
        {
            using (var password = new PasswordDialog(false)) password.ShowDialog(main);
            if (!Config.HasPassword) return;
        }
        if (MessageBox.Show("Privacy cover hides the desktop but is not a Windows security lock. " +
            "Use the tray's Lock Windows securely command when you need Windows authentication.\n\nStart the privacy cover?",
            "Privacy cover", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        bool mainWasVisible = main != null && main.Visible;
        if (main != null) main.Hide();
        session = new LockSession(() => { session = null; if (mainWasVisible) ShowMain(); });
        session.Start();
    }

    public void ShowMain()
    {
        if (session != null) return;
        if (main == null || main.IsDisposed) main = new MainForm(this);
        main.Show();
        if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
        main.Activate();
    }

    public void Quit()
    {
        if (session != null) return;
        Quitting = true;
        work.Dispose();
        msgWin.ClearHotkey();
        msgWin.DestroyHandle();
        tray.Visible = false;
        tray.Dispose();
        Power.RestoreNow();
        ExitThread();
    }
}

// invisible window that receives the global hotkey and "show yourself" from a second launch
class MessageWindow : NativeWindow
{
    readonly App app;
    bool registered;

    public MessageWindow(App app)
    {
        this.app = app;
        CreateHandle(new CreateParams { Caption = "LidGuard.MessageWindow" });
    }

    public bool SetHotkey(uint mods, uint vk)
    {
        ClearHotkey();
        registered = Native.RegisterHotKey(Handle, 1, mods | Native.MOD_NOREPEAT, vk);
        return registered;
    }

    public void ClearHotkey()
    {
        if (!registered) return;
        Native.UnregisterHotKey(Handle, 1);
        registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) app.Lock();
        else if ((uint)m.Msg == App.ShowMsg) app.ShowMain();
        base.WndProc(ref m);
    }
}

// ---------- the lock itself ----------
class LockSession
{
    public const int KeyAfterFails = 5;
    readonly List<CoverForm> covers = new List<CoverForm>();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
    readonly Action onUnlocked;
    int pwFails, keyFails;
    DateTime pwBlockedUntil = DateTime.MinValue, keyBlockedUntil = DateTime.MinValue;
    public bool Closing;

    public readonly DateTime Since = DateTime.Now;

    public LockSession(Action onUnlocked) { this.onUnlocked = onUnlocked; onForeground = OnForeground; }

    public bool KeyAvailable { get { return pwFails >= KeyAfterFails && Config.HasKey; } }

    public ILiveWallpaper Video;     // live wallpaper, if one is set

    public void Start()
    {
        if (Config.WallpaperMode == "video" && Config.Video.Length > 0 && System.IO.File.Exists(Config.Video))
        {
            if (Loop.Ready)
                Video = new VideoWallpaper(Config.VideoLoop) { Dissolves = false };   // seamless baked clip: just play it
            else
            {
                var parts = VideoWallpaper.ParseSections(Config.VideoSections);
                if (parts.Count == 0) parts.Add(new[] { Config.VideoStart, Config.VideoEnd });
                Video = new VideoWallpaper(Config.Video, parts) { Rate = Config.VideoSpeed };
            }
            Video.Start();
        }
        // cover the screens first so nothing is visible while power settings change
        foreach (var s in Screen.AllScreens)
        {
            var c = new CoverForm(this, s, s.Primary);
            if (s.Primary) main = c;
            covers.Add(c);
            c.Show();
        }
        KeyBlock.Install();
        Guard.Engage();
        // anything that grabs focus (Start, Task View, a touchpad gesture, a pop-up) is pushed straight back
        foreground = Native.SetWinEventHook(3, 3, IntPtr.Zero, onForeground, 0, 0, 2);   // EVENT_SYSTEM_FOREGROUND, skip own process
        SystemEvents.DisplaySettingsChanged += OnDisplays;
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);
        Power.EnterWorkModeAsync();
        timer.Tick += (a, b) => { Guard.HideTaskbars(); foreach (var c in covers) c.Keepalive(); };
        timer.Start();
        foreach (var c in covers) c.Keepalive();
    }

    CoverForm main;
    IntPtr foreground;
    Native.WinEventProc onForeground;

    void OnForeground(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
    {
        if (!Closing && main != null) main.Keepalive();
    }

    // a screen plugged in (or a resolution change) while locked gets covered too
    void OnDisplays(object sender, EventArgs e)
    {
        if (Closing) return;
        foreach (var s in Screen.AllScreens)
        {
            if (covers.Exists(c => !c.IsDisposed && c.Bounds.Contains(s.Bounds))) continue;
            var cover = new CoverForm(this, s, false);
            covers.Add(cover);
            cover.Show();
        }
        if (main != null) main.Keepalive();
    }

    public void ApplyTheme() { foreach (var c in covers) c.Apply(); }

    // each returns an error message, or null when unlocked
    public string TryUnlock(string pw)
    {
        string wait = Wait(pwBlockedUntil);
        if (wait != null) return wait;
        if (Config.Verify(pw)) { Unlock(); return null; }
        pwFails++;
        if (pwFails < 3) return "Wrong password.";
        int secs = 15 * (pwFails - 2);
        pwBlockedUntil = DateTime.Now.AddSeconds(secs);
        return "Wrong password. Try again in " + secs + "s.";
    }

    // the secret key has its own lockout, so it is usable as soon as it appears
    public string TryKey(string key)
    {
        if (!KeyAvailable) return "Secret key is not available yet.";
        string wait = Wait(keyBlockedUntil);
        if (wait != null) return wait;
        if (Config.VerifyKey(key)) { Unlock(); return null; }
        keyFails++;
        int secs = 30 * keyFails;
        keyBlockedUntil = DateTime.Now.AddSeconds(secs);
        return "Wrong secret key. Try again in " + secs + "s.";
    }

    static string Wait(DateTime until)
    {
        if (DateTime.Now >= until) return null;
        return "Too many attempts. Wait " + (int)Math.Ceiling((until - DateTime.Now).TotalSeconds) + "s.";
    }

    void Unlock()
    {
        Closing = true;
        timer.Stop();
        KeyBlock.Remove();
        if (foreground != IntPtr.Zero) { Native.UnhookWinEvent(foreground); foreground = IntPtr.Zero; }
        SystemEvents.DisplaySettingsChanged -= OnDisplays;
        Guard.Release();
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        Power.RestoreAsync();
        foreach (var c in covers) c.Close();
        if (Video != null) { Video.Dispose(); Video = null; }
        onUnlocked();
    }
}
