using System;
using System.IO;
using System.Runtime.InteropServices;
using System.ComponentModel;

internal interface IPowerBackend
{
    Guid Active { get; }
    uint Read(Guid plan, bool battery, Guid subgroup, Guid setting);
    void Write(Guid plan, bool battery, Guid subgroup, Guid setting, uint value);
    void Apply(Guid plan);
}

internal static class PowerRecovery
{
    internal static readonly Guid Buttons = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
    internal static readonly Guid Lid = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");
    internal static readonly Guid Sleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    internal static readonly Guid Idle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    internal static void Enter(string file, IPowerBackend api)
    {
        // Never overwrite a recovery journal left by a failed restoration.
        if (File.Exists(file)) Restore(file, api);
        Guid plan = api.Active;
        uint ac = api.Read(plan, false, Buttons, Lid);
        uint dc = api.Read(plan, true, Buttons, Lid);
        uint idle = api.Read(plan, false, Sleep, Idle);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
        File.WriteAllText(file + ".tmp", plan + "," + ac + "," + dc + "," + idle);
        File.Move(file + ".tmp", file);
        try
        {
            api.Write(plan, false, Buttons, Lid, 0);
            api.Write(plan, true, Buttons, Lid, 0);
            api.Write(plan, false, Sleep, Idle, 0);
            if (api.Active == plan) api.Apply(plan);
        }
        catch { Restore(file, api); throw; }
    }

    internal static void Restore(string file, IPowerBackend api)
    {
        if (!File.Exists(file)) return;
        string[] parts = File.ReadAllText(file).Trim().Split(',');
        // Old journals have no plan identity. Preserve them for manual recovery.
        if (parts.Length != 4) throw new InvalidDataException("Legacy recovery file: restore the original plan manually before removing restore.txt.");
        Guid plan = Guid.Parse(parts[0]);
        uint ac = uint.Parse(parts[1]), dc = uint.Parse(parts[2]), idle = uint.Parse(parts[3]);
        api.Write(plan, false, Buttons, Lid, ac);
        api.Write(plan, true, Buttons, Lid, dc);
        api.Write(plan, false, Sleep, Idle, idle);
        // Do not switch back if the user selected another plan while covered.
        if (api.Active == plan) api.Apply(plan);
        File.Delete(file);
    }
}

internal sealed class WindowsPowerBackend : IPowerBackend
{
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid plan, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid plan, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid plan, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid plan, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid plan);
    static void Check(uint code) { if (code != 0) throw new Win32Exception((int)code); }
    public Guid Active
    {
        get
        {
            IntPtr p; Check(PowerGetActiveScheme(IntPtr.Zero, out p));
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            finally { LocalFree(p); }
        }
    }
    public uint Read(Guid plan, bool battery, Guid subgroup, Guid setting)
    {
        uint value;
        uint code = battery ? PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, out value)
            : PowerReadACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, out value);
        Check(code); return value;
    }
    public void Write(Guid plan, bool battery, Guid subgroup, Guid setting, uint value)
    {
        Check(battery ? PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, value)
            : PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, value));
    }
    public void Apply(Guid plan) { Check(PowerSetActiveScheme(IntPtr.Zero, ref plan)); }
}
