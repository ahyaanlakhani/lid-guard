using System;
using System.Windows.Forms;
using Microsoft.Win32;

class SessionPlatform : ISecureWorkPlatform
{
    internal bool Pending, FailRestore, RejectLock, RejectAwake, FailMonitor, Awake;
    internal bool Plugged = true;
    internal bool RejectDisplays;
    internal Guid Plan = Guid.NewGuid();
    internal int Entries, Locks, Monitors, Stops, DisplayRequests, Restores;
    public bool RecoveryPending { get { return Pending; } }
    public Guid ActivePlan { get { return Plan; } }
    public void EnterPower() { Entries++; Pending = true; }
    public void Restore() { Restores++; if (!FailRestore) Pending = false; }
    public bool KeepAwake(bool enabled) { Awake = enabled && !RejectAwake; return !enabled || !RejectAwake; }
    public bool LockWindows() { Locks++; return !RejectLock; }
    public bool TurnOffDisplays(IntPtr window) { DisplayRequests++; return !RejectDisplays; }
    class Watcher : IDisposable
    {
        readonly Action stop;
        internal Watcher(Action stop) { this.stop = stop; }
        public void Dispose() { stop(); }
    }
    public IDisposable Monitor()
    {
        if (FailMonitor) throw new InvalidOperationException("test monitor failure");
        Monitors++; return new Watcher(() => Stops++);
    }
    public string BatteryReason(WorkPolicy policy, TimeSpan time) { return policy.StopReason(time, Plugged, 80); }
}
class SecureSessionTests
{
    static int passed;
    static void Assert(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("expected failure"); }
    [STAThread]
    static void Main(string[] args)
    {
        var options = new WorkPolicy(2, true, 20);
        var api = new SessionPlatform();
        using (var session = new SecureWorkSession(_ => {}, api))
        {
            session.Start(options);
            Assert(session.Active && api.Pending && api.Awake && api.Locks == 1, "secure start holds power override and requests Windows lock");
            int restores = api.Restores;
            Assert(!session.RetryRecovery() && api.Restores == restores && api.Pending, "retry cannot interrupt active session");
            session.ObserveSession(SessionSwitchReason.SessionLock);
            session.CheckAt(TimeSpan.FromSeconds(16));
            Assert(session.Active, "confirmed lock keeps session running");
            Assert(api.DisplayRequests == 0, "display-off remains opt-in");
            session.ObserveSession(SessionSwitchReason.SessionUnlock);
            Assert(!session.Active && !api.Pending && !api.Awake && api.Stops == 1, "Windows unlock restores power and releases monitor");
            session.Start(options); session.ObserveSession(SessionSwitchReason.SessionLock);
            session.CheckAt(TimeSpan.FromMinutes(2));
            Assert(!session.Active && !api.Pending && api.Locks == 2, "deadline restores power without another lock or unlock request");
            session.Start(options); api.Plugged = false;
            session.CheckAt(TimeSpan.FromSeconds(1));
            Assert(!session.Active && !api.Pending && !api.Awake, "charger disconnect ends override");
            api.Plugged = true;
            session.Start(options); session.CheckAt(TimeSpan.FromSeconds(15));
            Assert(!session.Active && !api.Pending, "unconfirmed Windows lock cancels session");
            session.Start(options); session.ObserveSession(SessionSwitchReason.SessionLock);
            api.Plan = Guid.NewGuid(); session.CheckAt(TimeSpan.FromSeconds(1));
            Assert(!session.Active && !api.Pending, "power plan switch ends session");
            api.RejectLock = true; Reject(() => session.Start(options));
            Assert(!session.Active && !api.Pending && !api.Awake, "rejected Windows lock rolls back power and awake request");
            api.RejectLock = false; api.RejectAwake = true; Reject(() => session.Start(options));
            Assert(!session.Active && !api.Pending, "failed awake request rolls back power");
            api.RejectAwake = false; api.FailMonitor = true; Reject(() => session.Start(options));
            Assert(!session.Active && !api.Pending, "monitor startup failure rolls back power");
            api.FailMonitor = false; session.Start(options); api.FailRestore = true;
            session.End("manual");
            Assert(session.Status == "Recovery needs attention" && api.Pending, "failed restore remains visible");
            int entries = api.Entries; Reject(() => session.Start(options));
            Assert(api.Entries == entries, "unresolved recovery blocks new power override");
            Assert(!session.RetryRecovery() && api.Pending, "failed retry preserves recovery state");
            api.FailRestore = false;
            Assert(session.RetryRecovery() && !api.Pending, "successful retry clears recovery state");
            var displays = new WorkPolicy(2, true, 20, true);
            session.Start(displays);
            Assert(api.DisplayRequests == 0, "displays stay on until Windows confirms lock");
            session.ObserveSession(SessionSwitchReason.SessionLock);
            Assert(api.DisplayRequests == 1 && session.Active && api.Awake, "display request runs after lock without ending awake session");
            session.ObserveSession(SessionSwitchReason.SessionLock);
            Assert(api.DisplayRequests == 1, "duplicate lock notifications do not turn displays off again");
            session.ObserveSession(SessionSwitchReason.SessionUnlock);
            session.ObserveSession(SessionSwitchReason.SessionLock);
            Assert(api.DisplayRequests == 1, "late lock notification after unlock cannot turn displays off");
            api.RejectDisplays = true;
            session.Start(displays); session.ObserveSession(SessionSwitchReason.SessionLock);
            Assert(session.Active && api.Awake && api.Pending, "display-off failure preserves secure work session");
            session.End(null);
            api.RejectLock = true;
            int requests = api.DisplayRequests;
            Reject(() => session.Start(displays));
            session.ObserveSession(SessionSwitchReason.SessionLock);
            Assert(api.DisplayRequests == requests, "rejected lock never requests display-off");
        }
        Assert(!api.Pending && !api.Awake, "disposing releases remaining recovery state");
        if (args.Length > 0)
        {
            Application.EnableVisualStyles();
            using (var form = new WorkSessionDialog())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new System.Drawing.Point(-20000, -20000);
                form.Show();
                Application.DoEvents();
                using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(args[0]);
                }
            }
        }
        Console.WriteLine(passed + " secure session checks passed without changing machine power or locking Windows.");
    }
}
