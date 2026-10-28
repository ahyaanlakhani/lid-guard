using System;
using System.IO;
using System.Collections.Generic;
class FakePower : IPowerBackend
{
    public Guid Current = Guid.NewGuid();
    public Guid Active { get { return Current; } }
    public bool Fail;
    public List<Guid> Written = new List<Guid>();
    public List<uint> Values = new List<uint>();
    public int Applies;
    public uint Read(Guid p, bool b, Guid s, Guid v) { return v == PowerRecovery.Idle ? 600u : b ? 2u : 1u; }
    public void Write(Guid p, bool b, Guid s, Guid v, uint value)
    {
        if (Fail) throw new IOException("simulated write failure");
        Written.Add(p); Values.Add(value);
    }
    public void Apply(Guid p) { Applies++; }
}
class RecoveryTests
{
    static void Assert(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "LidGuardTests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "restore.txt");
        try
        {
            var api = new FakePower(); Guid original = api.Active;
            PowerRecovery.Enter(file, api);
            Assert(File.Exists(file), "journal exists before restoration");
            api.Current = Guid.NewGuid(); api.Written.Clear(); api.Values.Clear();
            PowerRecovery.Restore(file, api);
            Assert(api.Written.TrueForAll(p => p == original) && api.Written.Count == 3, "restore targets original plan after switch");
            Assert(api.Applies == 1, "restore does not switch active plan");
            Assert(api.Values[0] == 1 && api.Values[1] == 2 && api.Values[2] == 600, "restore original values");
            Assert(!File.Exists(file), "successful recovery removes journal");
            PowerRecovery.Enter(file, api); api.Fail = true;
            try { PowerRecovery.Restore(file, api); throw new Exception("failure expected"); } catch (IOException) { }
            Assert(File.Exists(file), "failed recovery retains journal");
            api.Fail = false; PowerRecovery.Restore(file, api);
            File.WriteAllText(file, "1,2,600"); api.Written.Clear();
            try { PowerRecovery.Restore(file, api); throw new Exception("legacy rejection expected"); } catch (InvalidDataException) { }
            Assert(File.Exists(file) && api.Written.Count == 0, "legacy journal preserved without guessing plan");
            File.WriteAllText(file, original + ",1,broken,600");
            try { PowerRecovery.Restore(file, api); throw new Exception("corruption rejection expected"); } catch (FormatException) { }
            Assert(api.Written.Count == 0, "all journal values validated before writes");
        }
        finally { Directory.Delete(dir, true); }
    }
}
