using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Watches AI coding agents (Claude Code, Codex, Gemini CLI) and reports which are running and which are busy.
// An agent counts as busy when its process tree (the agent plus everything it started) used CPU in two
// samples in a row. An open but idle agent uses almost no CPU, so it does not count as work.
internal sealed class AgentWatch : IDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    internal const double BusyCpuSeconds = 0.06;     // 2% of one core over one sample
    static readonly string[] Runtimes = { "node", "python", "pythonw", "pwsh", "powershell", "cmd" };
    // helpers an agent starts that do work of their own, not the agent's: browsers and language servers
    static readonly string[] Helpers = { "chrome", "msedge", "msedgewebview2", "firefox", "chromium", "electron", "tsserver", "gopls", "rust-analyzer", "pyright" };
    // npm package paths that show up in a runtime's command line, with the name shown for them
    static readonly KeyValuePair<string, string>[] Packages =
    {
        new KeyValuePair<string, string>("@anthropic-ai/claude-code", "Claude Code"),
        new KeyValuePair<string, string>("@openai/codex", "Codex"),
        new KeyValuePair<string, string>("@google/gemini-cli", "Gemini CLI"),
        new KeyValuePair<string, string>("gemini-cli", "Gemini CLI"),
    };

    // one agent kind as the window shows it
    internal sealed class Status
    {
        internal string Name;
        internal int Sessions;
        internal bool Working;
        internal DateTime Since;        // when the current working period began (only while working)
    }

    // something an agent did: it started working, or it finished
    internal sealed class Event
    {
        internal DateTime Time;
        internal string Name;
        internal bool Started;          // false = it finished
        internal TimeSpan Worked;       // for a finish: how long it had been working
    }

    // called on a background thread for each start or finish
    internal Action<Event> OnEvent;
    readonly List<Event> events = new List<Event>();
    readonly Dictionary<string, DateTime> workingSince = new Dictionary<string, DateTime>();
    const int KeepEvents = 50;

    readonly Timer timer;
    readonly object gate = new object();
    Dictionary<int, TimeSpan> lastCpu = new Dictionary<int, TimeSpan>();
    readonly Dictionary<string, int> busyStreak = new Dictionary<string, int>();
    List<Status> statuses = new List<Status>();
    bool primed;

    internal AgentWatch()
    {
        timer = new Timer(_ => Sample(), null, TimeSpan.Zero, Interval);
    }

    // true while any agent used CPU in its last two samples
    internal bool Busy { get { lock (gate) { foreach (var s in statuses) if (s.Working) return true; return false; } } }
    // agent sessions found at the last sample (0 = no agent is running)
    internal int Agents { get { lock (gate) { int n = 0; foreach (var s in statuses) n += s.Sessions; return n; } } }
    // each agent kind that is running, with its sessions and whether it is working
    internal List<Status> Snapshot() { lock (gate) { var copy = new List<Status>(); foreach (var s in statuses) copy.Add(new Status { Name = s.Name, Sessions = s.Sessions, Working = s.Working, Since = s.Since }); return copy; } }
    // the most recent starts and finishes, newest first
    internal List<Event> Events() { lock (gate) return new List<Event>(events); }

    static string BaseName(string name)
    {
        string n = (name ?? "").ToLowerInvariant();
        return n.EndsWith(".exe") ? n.Substring(0, n.Length - 4) : n;
    }

    // the agent a process is, by its display name, or null if it is not an agent's root process.
    // The Claude desktop app also runs a claude.exe, so Claude only counts from the Claude Code install.
    internal static string AgentKind(string name, string path, string commandLine)
    {
        string n = BaseName(name);
        string p = (path ?? "").ToLowerInvariant().Replace('/', '\\');
        if (n == "claude") return p.Contains("\\claude-code\\") && !p.Contains("\\anthropicclaude\\") ? "Claude Code" : null;
        if (n == "codex") return "Codex";
        if (n == "gemini") return "Gemini CLI";
        if (Array.IndexOf(Runtimes, n) < 0 || string.IsNullOrEmpty(commandLine)) return null;
        string c = commandLine.ToLowerInvariant().Replace('\\', '/');
        foreach (var package in Packages) if (c.Contains(package.Key)) return package.Value;
        return null;
    }

    internal static bool IsAgentRoot(string name, string path, string commandLine)
    {
        return AgentKind(name, path, commandLine) != null;
    }

    void Sample()
    {
        try { Measure(); }
        catch
        {
            // a process vanishing mid-sample: report nothing busy rather than guess
            lock (gate) { statuses = new List<Status>(); }
        }
    }

    void Measure()
    {
        var children = new Dictionary<int, List<int>>();
        var names = new Dictionary<int, string>();
        var roots = new List<KeyValuePair<int, string>>();
        foreach (var entry in NativeProcesses.List())
        {
            List<int> kids;
            if (!children.TryGetValue(entry.Parent, out kids)) children[entry.Parent] = kids = new List<int>();
            kids.Add(entry.Pid);
            names[entry.Pid] = BaseName(entry.Name);

            // only look closer at processes that could be an agent: a path or command line costs a system call
            string n = BaseName(entry.Name);
            if (n != "claude" && n != "codex" && n != "gemini" && Array.IndexOf(Runtimes, n) < 0) continue;
            string path = n == "claude" ? NativeProcesses.ExePath(entry.Pid) : null;
            string commandLine = Array.IndexOf(Runtimes, n) >= 0 ? NativeProcesses.CommandLine(entry.Pid) : null;
            string kind = AgentKind(entry.Name, path, commandLine);
            if (kind != null) roots.Add(new KeyValuePair<int, string>(entry.Pid, kind));
        }

        // Each agent's tree: the agent plus everything it started. LidGuard itself is never part of a tree,
        // even when an agent launched it. Helpers (browsers, editor language servers) are left out with
        // everything they start. A process belongs to the first agent that reaches it.
        int self = Process.GetCurrentProcess().Id;
        var claimed = new HashSet<int>();
        var perKind = new Dictionary<string, Status>();
        var used = new Dictionary<string, double>();
        var cpu = new Dictionary<int, TimeSpan>();
        foreach (var root in roots)
        {
            Status status;
            if (!perKind.TryGetValue(root.Value, out status)) perKind[root.Value] = status = new Status { Name = root.Value };
            status.Sessions++;

            var queue = new Queue<int>();
            queue.Enqueue(root.Key);
            while (queue.Count > 0)
            {
                int pid = queue.Dequeue();
                string name;
                if (pid == self || (names.TryGetValue(pid, out name) && Array.IndexOf(Helpers, name) >= 0) || !claimed.Add(pid)) continue;
                List<int> kids;
                if (children.TryGetValue(pid, out kids)) foreach (var k in kids) queue.Enqueue(k);
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        var total = p.TotalProcessorTime;
                        cpu[pid] = total;
                        TimeSpan before;
                        double add = 0;
                        // a process seen for the first time only starts counting from the next sample
                        if (primed && lastCpu.TryGetValue(pid, out before)) add = (total - before).TotalSeconds;
                        else if (primed) add = total.TotalSeconds;
                        double so_far;
                        used.TryGetValue(root.Value, out so_far);
                        used[root.Value] = so_far + add;
                    }
                }
                catch (ArgumentException) { }   // exited while we were looking
                catch (InvalidOperationException) { }
            }
        }

        var raised = new List<Event>();
        lock (gate)
        {
            lastCpu = cpu;
            var list = new List<Status>();
            var now = DateTime.Now;
            foreach (var status in perKind.Values)
            {
                double work;
                used.TryGetValue(status.Name, out work);
                // two busy samples in a row: a short spike (a shell starting up, a log write) is not work
                int streak;
                busyStreak.TryGetValue(status.Name, out streak);
                streak = primed && work >= BusyCpuSeconds ? streak + 1 : 0;
                busyStreak[status.Name] = streak;
                status.Working = streak >= 2;

                bool wasWorking = workingSince.ContainsKey(status.Name);
                if (status.Working && !wasWorking)
                {
                    workingSince[status.Name] = now;
                    raised.Add(new Event { Time = now, Name = status.Name, Started = true });
                }
                else if (!status.Working && wasWorking)
                {
                    raised.Add(Finished(status.Name, now));
                }
                if (status.Working) status.Since = workingSince[status.Name];
                list.Add(status);
            }
            // an agent that stopped entirely while working has finished too
            foreach (var name in new List<string>(workingSince.Keys))
                if (!perKind.ContainsKey(name)) raised.Add(Finished(name, now));

            statuses = list;
            foreach (var e in raised) events.Insert(0, e);
            if (events.Count > KeepEvents) events.RemoveRange(KeepEvents, events.Count - KeepEvents);
            primed = true;
        }
        if (OnEvent != null) foreach (var e in raised) OnEvent(e);
    }

    // ends a working period: forgets its start and returns the finish event
    Event Finished(string name, DateTime now)
    {
        DateTime since;
        workingSince.TryGetValue(name, out since);
        workingSince.Remove(name);
        return new Event { Time = now, Name = name, Started = false, Worked = now - since };
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
