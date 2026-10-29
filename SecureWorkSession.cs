using System;
using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

internal interface ISecureWorkPlatform
{
    bool RecoveryPending { get; }
    Guid ActivePlan { get; }
    void EnterPower();
    void Restore();
    bool KeepAwake(bool enabled);
    bool LockWindows();
    bool TurnOffDisplays(IntPtr window);
    IDisposable Monitor();
    string BatteryReason(WorkPolicy policy, TimeSpan elapsed);
}

internal sealed class WindowsWorkPlatform : ISecureWorkPlatform
{
    public bool RecoveryPending { get { return Power.HasPending; } }
    public Guid ActivePlan { get { return Power.Backend.Active; } }
    public void EnterPower() { Power.EnterSecureWorkMode(); }
    public void Restore() { Power.RestoreNow(); }
    public bool KeepAwake(bool enabled)
    {
        return Native.SetThreadExecutionState(Native.ES_CONTINUOUS | (enabled ? Native.ES_SYSTEM_REQUIRED : 0)) != 0;
    }
    public bool LockWindows() { return Native.LockWorkStation(); }
    public bool TurnOffDisplays(IntPtr window)
    {
        UIntPtr result;
        // Target our helper window, whose default procedure handles SC_MONITORPOWER.
        // Broadcasting would let every unresponsive app consume a separate timeout.
        return Native.SendMessageTimeout(window, 0x0112, (UIntPtr)0xF170, (IntPtr)2, 2, 1000, out result) != IntPtr.Zero;
    }
    sealed class MonitorProcess : IDisposable
    {
        readonly Process process;
        internal MonitorProcess(Process process) { this.process = process; }
        public void Dispose()
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            process.Dispose();
        }
    }
    public IDisposable Monitor()
    {
        var process = Process.Start(new ProcessStartInfo(Application.ExecutablePath,
            "--work-watchdog " + Process.GetCurrentProcess().Id) { UseShellExecute = false, CreateNoWindow = true });
        if (process == null) throw new InvalidOperationException("Could not start recovery monitoring.");
        if (process.HasExited) { process.Dispose(); throw new InvalidOperationException("Recovery monitor exited unexpectedly."); }
        return new MonitorProcess(process);
    }
    public string BatteryReason(WorkPolicy policy, TimeSpan elapsed)
    {
        var power = SystemInformation.PowerStatus;
        bool? plugged = power.PowerLineStatus == PowerLineStatus.Unknown ? (bool?)null
            : power.PowerLineStatus == PowerLineStatus.Online;
        float charge = power.BatteryLifePercent;
        bool unknown = power.BatteryChargeStatus == BatteryChargeStatus.Unknown
            || (power.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0;
        int? percent = unknown || charge < 0 || charge > 1 ? (int?)null : (int)Math.Round(charge * 100);
        return policy.StopReason(elapsed, plugged, percent);
    }
}

// Owns secure sessions on the UI thread. Windows owns authentication; this class
// only holds the temporary power override and releases it on every end path.
internal sealed class SecureWorkSession : IDisposable
{
    // A hidden top-level window also receives the monitor-power system command.
    readonly Control dispatch = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None };
    readonly Timer timer = new Timer { Interval = 1000 };
    readonly Action<string> report;
    readonly Stopwatch elapsed = new Stopwatch();
    WorkPolicy policy;
    IDisposable watchdog;
    readonly ISecureWorkPlatform platform;
    Guid plan;
    bool lockConfirmed;
    internal bool Active { get; private set; }
    internal string Status
    {
        get
        {
            if (!Active) return platform.RecoveryPending ? "Recovery needs attention" : "No work session";
            int minutes = Math.Max(0, (int)Math.Ceiling((policy.Duration - elapsed.Elapsed).TotalMinutes));
            return "Secure work: " + minutes + " min left";
        }
    }

    internal SecureWorkSession(Action<string> report) : this(report, new WindowsWorkPlatform()) { }
    internal SecureWorkSession(Action<string> report, ISecureWorkPlatform platform)
    {
        this.platform = platform;
        this.report = report;
        IntPtr handle = dispatch.Handle; // capture a UI dispatcher before system events arrive
        timer.Tick += (s, e) => Check();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    internal void Start(WorkPolicy options)
    {
        if (Active) throw new InvalidOperationException("End the current session first.");
        string reason = platform.BatteryReason(options, TimeSpan.Zero);
        if (reason != null) throw new InvalidOperationException(reason);
        // Drain older cover work before touching the shared recovery journal.
        platform.Restore();
        if (platform.RecoveryPending) throw new InvalidOperationException("Restore the previous power settings before starting a session.");
        policy = options;
        lockConfirmed = false;
        try
        {
            platform.EnterPower();
            plan = platform.ActivePlan;
            watchdog = platform.Monitor();
            if (!platform.KeepAwake(true))
                throw new InvalidOperationException("Windows could not keep this session awake.");
            Active = true;
            elapsed.Restart();
            timer.Start();
            if (!platform.LockWindows()) throw new InvalidOperationException("Windows could not lock the session.");
        }
        catch
        {
            End(null);
            throw;
        }
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // SystemEvents uses another thread. Never restore or clear the execution
        // request there: SetThreadExecutionState is tied to its calling thread.
        if (dispatch.IsDisposed) return;
        try
        {
            dispatch.BeginInvoke((Action)(() =>
            {
                ObserveSession(e.Reason);
            }));
        }
        catch (InvalidOperationException) { }
    }

    internal void ObserveSession(SessionSwitchReason reason)
    {
        if (!Active) return;
        if (reason == SessionSwitchReason.SessionLock && !lockConfirmed)
        {
            lockConfirmed = true;
            if (policy.TurnOffDisplays)
            {
                try
                {
                    if (!platform.TurnOffDisplays(dispatch.Handle)) report("Windows could not accept the display-off request. The secure session is still active.");
                }
                catch { report("Display-off is unavailable. The secure session is still active."); }
            }
        }
        if (reason == SessionSwitchReason.SessionUnlock || reason == SessionSwitchReason.SessionLogoff)
            End("Windows sign-in ended the work session.");
    }

    void Check() { CheckAt(elapsed.Elapsed); }
    internal void CheckAt(TimeSpan time)
    {
        if (!Active) return;
        string reason = platform.BatteryReason(policy, time);
        if (!lockConfirmed && time >= TimeSpan.FromSeconds(15)) reason = "Windows locking was not confirmed.";
        try { if (platform.ActivePlan != plan) reason = "The active power plan changed."; }
        catch { reason = "Power settings could not be checked."; }
        if (reason != null) End(reason);
    }

    internal bool RetryRecovery()
    {
        // A recovery journal is expected during a live session. Never restore it early.
        if (Active) return false;
        platform.Restore();
        return !platform.RecoveryPending;
    }

    internal void End(string reason)
    {
        Active = false;
        timer.Stop();
        elapsed.Stop();
        platform.KeepAwake(false);
        platform.Restore();
        if (watchdog != null) { watchdog.Dispose(); watchdog = null; }
        if (platform.RecoveryPending) report("Power recovery is incomplete. Recovery files have been preserved. Use Retry recovery before starting another session.");
        else if (reason != null) report(reason + " Normal power settings restored.");
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        End(null);
        timer.Dispose(); dispatch.Dispose();
    }

    internal static void Watch(int pid)
    {
        try { using (var parent = Process.GetProcessById(pid)) parent.WaitForExit(); } catch (ArgumentException) { }
        Power.RestoreNow();
    }
}

internal sealed class WorkSessionDialog : Form
{
    internal WorkPolicy Options { get; private set; }
    internal WorkSessionDialog()
    {
        Text = "Secure closed-lid session";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new System.Drawing.Size(440, 345);
        var intro = new Label { Text = "Keep work running with the lid closed and lock Windows.\nSign-in, the timer, or battery rules end the power override.", AutoSize = false };
        intro.SetBounds(20, 18, 400, 45);
        var durationLabel = new Label { Text = "Duration (minutes, up to 24 hours)", AutoSize = true };
        durationLabel.SetBounds(20, 78, 290, 23);
        var duration = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = 120 };
        duration.SetBounds(320, 75, 95, 26);
        var plugged = new CheckBox { Text = "Only while plugged in", Checked = true, AutoSize = true };
        plugged.SetBounds(20, 119, 300, 26);
        var batteryLabel = new Label { Text = "Stop at battery level (%)", AutoSize = true };
        batteryLabel.SetBounds(20, 166, 280, 23);
        var battery = new NumericUpDown { Minimum = 10, Maximum = 90, Value = 20, Enabled = false };
        battery.SetBounds(320, 162, 95, 26);
        plugged.CheckedChanged += (s, e) => battery.Enabled = !plugged.Checked;
        var displays = new CheckBox { Text = "Turn off displays after Windows locks", AutoSize = true };
        displays.SetBounds(20, 204, 395, 26);
        var start = new Button { Text = "Start and lock Windows" };
        start.SetBounds(20, 276, 235, 36);
        start.Click += (s, e) => { Options = new WorkPolicy((int)duration.Value, plugged.Checked, (int)battery.Value, displays.Checked); DialogResult = DialogResult.OK; };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(305, 276, 110, 36);
        Controls.AddRange(new Control[] { intro, durationLabel, duration, plugged, batteryLabel, battery, displays, start, cancel });
        AcceptButton = start; CancelButton = cancel;
    }
}
