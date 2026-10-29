using System;
class WorkPolicyTests
{
    static int passed;
    static void Assert(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        passed++; Console.WriteLine("PASS " + name);
    }
    static void Main()
    {
        var ac = new WorkPolicy(120, true, 20);
        Assert(ac.StopReason(TimeSpan.Zero, true, null) == null, "AC-only starts on charger without battery telemetry");
        Assert(ac.StopReason(TimeSpan.Zero, false, 90) != null, "AC-only rejects disconnected charger");
        Assert(ac.StopReason(TimeSpan.FromMinutes(119), true, 5) == null, "timer stays active before deadline on AC");
        Assert(ac.StopReason(TimeSpan.FromMinutes(120), true, 100) != null, "timer ends at exact deadline");
        Assert(ac.StopReason(TimeSpan.FromMinutes(125), true, 100) != null, "overdue session ends after delayed checks");
        Assert(ac.StopReason(TimeSpan.Zero, null, 80) != null, "unknown power source stops session");
        var battery = new WorkPolicy(30, false, 25);
        Assert(battery.StopReason(TimeSpan.Zero, false, 26) == null, "battery operation above threshold allowed");
        Assert(battery.StopReason(TimeSpan.Zero, false, 25) != null, "battery stops at exact threshold");
        Assert(battery.StopReason(TimeSpan.Zero, false, 24) != null, "battery below threshold rejected");
        Assert(battery.StopReason(TimeSpan.Zero, false, null) != null, "missing battery telemetry stops battery operation");
        Assert(battery.StopReason(TimeSpan.Zero, true, 10) == null, "charging permits low battery");
        Assert(battery.StopReason(TimeSpan.FromMinutes(30), false, 90) != null, "timer applies on battery too");
        try { new WorkPolicy(0, true, 20); throw new Exception("expected rejection"); }
        catch (ArgumentOutOfRangeException) { passed++; }
        try { new WorkPolicy(1441, true, 20); throw new Exception("expected rejection"); }
        catch (ArgumentOutOfRangeException) { passed++; }
        try { new WorkPolicy(30, false, 9); throw new Exception("expected rejection"); }
        catch (ArgumentOutOfRangeException) { passed++; }
        try { new WorkPolicy(30, false, 91); throw new Exception("expected rejection"); }
        catch (ArgumentOutOfRangeException) { passed++; }
        Console.WriteLine(passed + " work-policy checks passed.");
    }
}
