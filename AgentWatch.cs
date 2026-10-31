using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Watches AI coding agents (Claude Code, Codex, Gemini CLI) and reports whether one is busy.
// An agent counts as busy when its process tree (the agent plus everything it started) used CPU
// during the last sample. An open but idle agent uses almost no CPU, so it does not count as work.
internal sealed class AgentWatch : IDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    internal const double BusyCpuSeconds = 0.06;     // 2% of one core over one sample
    static readonly string[] AgentNames = { "claude", "codex", "gemini" };
    static readonly string[] Runtimes = { "node", "python", "pythonw", "pwsh", "powershell", "cmd" };
    // helpers an agent starts that do work of their own, not the agent's: browsers and language servers
    static readonly string[] Helpers = { "chrome", "msedge", "msedgewebview2", "firefox", "chromium", "electron", "tsserver", "gopls", "rust-analyzer", "pyright" };
    static readonly string[] Packages ={ "@anthropic-ai/claude-code", "@openai/codex", "@google/gemini-cli", "gemini-cli" };

    readonly Timer timer;
    readonly object gate = new object();
    Dictionary<int, TimeSpan> lastCpu = new Dictionary<int, TimeSpan>();
    bool primed;
    bool busy;
    int busyStreak;
    int agents;

    internal AgentWatch()
    {
        timer = new Timer(_ => Sample(), null, TimeSpan.Zero, Interval);
    }

    // true while any agent's process tree used CPU during the last sample
    internal bool Busy { get { lock (gate) return busy; } }
    // agent processes found at the last sample (0 = no agent is running)
    internal int Agents { get { lock (gate) return agents; } }

    static string BaseName(string name)
    {
        string n = (name ?? "").ToLowerInvariant();
        return n.EndsWith(".exe") ? n.Substring(0, n.Length - 4) : n;
    }

    // true when a process is an agent's root process (not one of the processes it starts).
    // The Claude desktop app also runs a claude.exe, so Claude only counts from the Claude Code install.
    internal static bool IsAgentRoot(string name, string path, string commandLine)
    {
        string n = BaseName(name);
        string p = (path ?? "").ToLowerInvariant().Replace('/', '\\');
        if (n == "claude") return p.Contains("\\claude-code\\") && !p.Contains("\\anthropicclaude\\");
        if (n == "codex" || n == "gemini") return true;
        if (Array.IndexOf(Runtimes, n) < 0 || string.IsNullOrEmpty(commandLine)) return false;
        string c = commandLine.ToLowerInvariant().Replace('\\', '/');
        foreach (var package in Packages) if (c.Contains(package)) return true;
        return false;
    }

    void Sample()
    {
        try { Measure(); }
        catch
        {
            // a process vanishing mid-sample: report nothing busy rather than guess
            lock (gate) { busy = false; agents = 0; }
        }
    }

    void Measure()
    {
        var children = new Dictionary<int, List<int>>();
        var names = new Dictionary<int, string>();
        var roots = new List<int>();
        foreach (var entry in NativeProcesses.List())
        {
            List<int> kids;
            if (!children.TryGetValue(entry.Parent, out kids)) children[entry.Parent] = kids = new List<int>();
            kids.Add(entry.Pid);
            names[entry.Pid] = BaseName(entry.Name);

            // only look closer at processes that could be an agent: a path or command line costs a system call
            string n = BaseName(entry.Name);
            if (Array.IndexOf(AgentNames, n) < 0 && Array.IndexOf(Runtimes, n) < 0) continue;
            string path = n == "claude" ? NativeProcesses.ExePath(entry.Pid) : null;
            string commandLine = Array.IndexOf(Runtimes, n) >= 0 ? NativeProcesses.CommandLine(entry.Pid) : null;
            if (IsAgentRoot(entry.Name, path, commandLine)) roots.Add(entry.Pid);
        }

        // every process under an agent root, including the root itself. LidGuard itself is never part of
        // the tree, even when an agent launched it, so its own work is not mistaken for agent work.
        // Helpers an agent starts (browsers, editor language servers) are left out with everything they start.
        int self = Process.GetCurrentProcess().Id;
        var tree = new HashSet<int>();
        var queue = new Queue<int>(roots);
        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            string name;
            if (pid == self || (names.TryGetValue(pid, out name) && Array.IndexOf(Helpers, name) >= 0) || !tree.Add(pid)) continue;
            List<int> kids;
            if (children.TryGetValue(pid, out kids)) foreach (var k in kids) queue.Enqueue(k);
        }

        var cpu = new Dictionary<int, TimeSpan>();
        double used = 0;
        foreach (var pid in tree)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    var total = p.TotalProcessorTime;
                    cpu[pid] = total;
                    TimeSpan before;
                    // a process seen for the first time only starts counting from the next sample
                    if (primed && lastCpu.TryGetValue(pid, out before)) used += (total - before).TotalSeconds;
                    else if (primed) used += total.TotalSeconds;
                }
            }
            catch (ArgumentException) { }   // exited while we were looking
            catch (InvalidOperationException) { }
        }

        lock (gate)
        {
            lastCpu = cpu;
            // two busy samples in a row: a short spike (a shell starting up, a log write) is not work
            busyStreak = primed && used >= BusyCpuSeconds ? busyStreak + 1 : 0;
            busy = busyStreak >= 2;
            agents = roots.Count;
            primed = true;
        }
    }

    public void Dispose()
    {
        timer.Dispose();
    }
}

// Reads the process table and per-process details directly from Windows. WMI is avoided on purpose:
// a watcher that samples every few seconds exhausts WMI's per-client quota.
internal static class NativeProcesses
{
    const uint TH32CS_SNAPPROCESS = 0x2;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ProcessCommandLineInformation = 60;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    internal sealed class Entry
    {
        internal int Pid, Parent;
        internal string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UNICODE_STRING { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, uint length, out uint returned);

    internal static List<Entry> List()
    {
        var list = new List<Entry>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) throw new InvalidOperationException("Could not read the process table.");
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
            bool more = Process32FirstW(snapshot, ref entry);
            while (more)
            {
                list.Add(new Entry { Pid = (int)entry.th32ProcessID, Parent = (int)entry.th32ParentProcessID, Name = entry.szExeFile });
                more = Process32NextW(snapshot, ref entry);
            }
        }
        finally { CloseHandle(snapshot); }
        return list;
    }

    // full path of the program, or null if it cannot be read (other user, exited)
    internal static string ExePath(int pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            uint size = (uint)name.Capacity;
            return QueryFullProcessImageNameW(process, 0, name, ref size) ? name.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    // the command line the process was started with, or null if it cannot be read
    internal static string CommandLine(int pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            uint size = 1024, returned;
            buffer = Marshal.AllocHGlobal((int)size);
            int status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out returned);
            if (status == STATUS_INFO_LENGTH_MISMATCH && returned > 0)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                size = returned;
                buffer = Marshal.AllocHGlobal((int)size);
                status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out returned);
            }
            if (status != 0) return null;
            var text = (UNICODE_STRING)Marshal.PtrToStructure(buffer, typeof(UNICODE_STRING));
            return text.Length == 0 ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(process);
        }
    }
}
