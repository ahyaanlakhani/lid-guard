// Live video wallpaper: Windows Media Foundation decodes the clip on a background thread into
// raw frames (looping, no audio). Decoding stops completely while nobody can see the screen.
// Also: ClockGlass, the liquid-glass clock precomputed once a minute and re-sampled every frame.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

// what the lock screen needs from a moving wallpaper
interface ILiveWallpaper : IDisposable
{
    event Action FrameReady;
    bool Paused { get; set; }
    bool Hardware { get; }
    string Error { get; }
    string Place { get; }          // reserved for place names; the video has none
    void Start();
    bool Use(Action<Frame, Frame> draw);
}

// ---------- Media Foundation interop (only the calls we use; other vtable slots are placeholders) ----------
static class MF
{
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)] public static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes attributes, out IMFSourceReader reader);

    public const int Version = 0x20070;
    public const uint FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE, EndOfStream = 0x2;
    public static readonly Guid AdvancedVideoProcessing = new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");

    public static readonly Guid D3DManager = new Guid("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid AvgBitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid InterlaceMode = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspect = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");

    [DllImport("mfreadwrite.dll", ExactSpelling = true)] public static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter writer);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateSample(out IMFSample sample);
    public static readonly Guid HardwareTransforms = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");

    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateDXGIDeviceManager(out uint token, out IMFDXGIDeviceManager manager);
    [DllImport("d3d11.dll", ExactSpelling = true)] public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdk, out IntPtr device, out int level, out IntPtr context);

    public static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
}

[ComImport, Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ID3D10Multithread
{
    void Enter(); void Leave();
    [PreserveSig] int SetMultithreadProtected([MarshalAs(UnmanagedType.Bool)] bool on);
}

[ComImport, Guid("eb533d5d-2db6-40f8-97a9-494692014f07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFDXGIDeviceManager
{
    void _0(); void _1(); void _2(); void _3();
    [PreserveSig] int ResetDevice([MarshalAs(UnmanagedType.IUnknown)] object device, uint token);
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public long value; }

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFAttributes
{
    void _0(); void _1(); void _2(); void _3();
    [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int value);
    [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out long value);
    void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
    [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
    void _19(); void _20();
    [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
    void _22(); void _23();
    [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    void _25(); void _26(); void _27(); void _28(); void _29();
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaType
{
    void _0(); void _1(); void _2(); void _3();
    [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int value);
    [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out long value);
    void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
    [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
    [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
    void _20();
    [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
    void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSample
{
    void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
    void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
    void _20(); void _21(); void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
    void _30(); void _31(); void _32();
    [PreserveSig] int SetSampleTime(long time);
    void _34();
    [PreserveSig] int SetSampleDuration(long duration);
    void _36(); void _37();
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    [PreserveSig] int Unlock();
    void _2();
    [PreserveSig] int SetCurrentLength(int length);
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType type, out int stream);
    [PreserveSig] int SetInputMediaType(int stream, IMFMediaType type, IMFAttributes encodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(int stream, IMFSample sample);
    void _4(); void _5(); void _6(); void _7();
    [PreserveSig] int Finalize_();
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSourceReader
{
    void _0();
    [PreserveSig] int SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
    void _2();
    [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
    [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
    [PreserveSig] int SetCurrentPosition([In] ref Guid timeFormat, [In] ref PropVariant position);
    [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample sample);
}

// ---------- a pixel buffer that a Bitmap wraps directly (drawing it needs no copy) ----------
class Frame : IDisposable
{
    public readonly int W, H;
    public readonly int[] Px;
    public readonly Bitmap Bmp;
    public long Seq;
    public double Time;                 // position in the video, seconds
    GCHandle pin;

    public Frame(int w, int h, PixelFormat format = PixelFormat.Format32bppRgb)
    {
        W = Math.Max(1, w); H = Math.Max(1, h);
        Px = new int[W * H];
        pin = GCHandle.Alloc(Px, GCHandleType.Pinned);
        Bmp = new Bitmap(W, H, W * 4, format, pin.AddrOfPinnedObject());
    }

    public static Frame From(Bitmap src)
    {
        var f = new Frame(src.Width, src.Height);
        var data = src.LockBits(new Rectangle(0, 0, f.W, f.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < f.H; y++) Marshal.Copy(data.Scan0 + y * data.Stride, f.Px, y * f.W, f.W);
        src.UnlockBits(data);
        return f;
    }

    // a small, soft copy for frosted glass: average blocks, then a light extra blur
    public Frame Shrink(int down) { var s = new Frame(W / down, H / down); ShrinkInto(s, down); return s; }

    public void ShrinkInto(Frame dst, int down)
    {
        int[] src = Px, d = dst.Px;
        for (int sy = 0; sy < dst.H; sy++)
            for (int sx = 0; sx < dst.W; sx++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                int y1 = Math.Min(H, (sy + 1) * down), x1 = Math.Min(W, (sx + 1) * down);
                for (int y = sy * down; y < y1; y += 2)
                {
                    int row = y * W;
                    for (int x = sx * down; x < x1; x += 2)
                    {
                        int c = src[row + x];
                        r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; n++;
                    }
                }
                if (n == 0) n = 1;
                d[sy * dst.W + sx] = (255 << 24) | ((r / n) << 16) | ((g / n) << 8) | (b / n);
            }
        Box3(dst);
        dst.Seq = Seq;
    }

    static void Box3(Frame f)
    {
        var tmp = (int[])f.Px.Clone();
        for (int y = 0; y < f.H; y++)
            for (int x = 0; x < f.W; x++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= f.H) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= f.W) continue;
                        int c = tmp[yy * f.W + xx];
                        r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; n++;
                    }
                }
                f.Px[y * f.W + x] = (255 << 24) | ((r / n) << 16) | ((g / n) << 8) | (b / n);
            }
    }

    public void Dispose()
    {
        Bmp.Dispose();
        if (pin.IsAllocated) pin.Free();
    }
}

// ---------- the decoder ----------
class VideoWallpaper : ILiveWallpaper
{
    public const int Down = 16;                       // the frosted copy is 1/16 of the size
    readonly string path;
    readonly object gate = new object();
    readonly ManualResetEvent awake = new ManualResetEvent(true);
    Frame front, back, frontSmall, backSmall;
    Thread thread;
    volatile bool stop;
    volatile string error;
    volatile bool hardware;
    public string Error { get { return error; } }
    public bool Hardware { get { return hardware; } }   // decoding on the graphics chip
    public string Place { get { return null; } }
    public event Action FrameReady;                   // raised on the decoder thread

    // the sections to play, in order and looping (100 ns units); startT/endT is the one playing now
    readonly long[][] sections;
    public double Rate = 1;              // playback speed: 0.8 = a little slower and calmer
    public bool Dissolves = true;        // off for a baked loop, which already contains them
    const int FadeFrames = 15;           // ~0.5 s dissolve wherever one road shot hands over to the next
    int fadeLeft;
    int[] hold;                          // the outgoing shot's last frame, dissolving into the new one
    int current;
    long startT, endT;

    public VideoWallpaper(string path, double startSec = 0, double endSec = 0)
        : this(path, new[] { new[] { startSec, endSec } }) { }

    public VideoWallpaper(string path, IList<double[]> parts)
    {
        this.path = path;
        var list = new List<long[]>();
        foreach (var p in parts)
        {
            if (p == null || p.Length < 2) continue;
            long a = (long)(Math.Max(0, p[0]) * 1e7), b = p[1] > p[0] ? (long)(p[1] * 1e7) : long.MaxValue;
            list.Add(new[] { a, b });
        }
        if (list.Count == 0) list.Add(new[] { 0L, long.MaxValue });
        sections = list.ToArray();
        startT = sections[0][0];
        endT = sections[0][1];
    }

    // "3.4-7.01;15.2-16.41" -> sections; empty or invalid parts are skipped
    public static List<double[]> ParseSections(string text)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var result = new List<double[]>();
        foreach (var part in (text ?? "").Split(';'))
        {
            var ab = part.Split('-');
            double a, b;
            if (ab.Length == 2 && double.TryParse(ab[0].Trim(), System.Globalization.NumberStyles.Float, inv, out a)
                && double.TryParse(ab[1].Trim(), System.Globalization.NumberStyles.Float, inv, out b) && b > a)
                result.Add(new[] { a, b });
        }
        return result;
    }

    void Seek(IMFSourceReader reader)
    {
        var pos = new PropVariant { vt = 20, value = startT };   // VT_I8
        var fmt = Guid.Empty;
        MF.Check(reader.SetCurrentPosition(ref fmt, ref pos));
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "LidGuard video" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public bool Paused
    {
        get { return !awake.WaitOne(0); }
        set { if (value) awake.Reset(); else awake.Set(); }
    }

    // draw with the newest frame (and its frosted copy); false until the first frame arrives
    public bool Use(Action<Frame, Frame> draw)
    {
        lock (gate)
        {
            if (front == null) return false;
            draw(front, frontSmall);
            return true;
        }
    }

    // each section gets its own decoder, parked at its start with the first frame already decoded,
    // so hand-overs are instant (seeking means decoding from the previous keyframe: up to ~1.7 s)
    class Slot
    {
        public IMFSourceReader Reader;
        public long Start, End;
        public int[] First;
        public long FirstTs;
        public volatile bool Ready;
    }

    int frameW, frameH, frameStride;
    bool useHardware;
    Frame fresh, freshSmall;
    Stopwatch clock;
    long timeline, lastTs, frameLen = 400000, seq, playStart;
    Slot pending;                        // a finished decoder waiting to be re-parked

    IMFSourceReader OpenReader(bool first)
    {
        IMFSourceReader reader = null;
        if (first)
        {
            // prefer the graphics chip's video decoder; fall back to software if anything is missing
            device = CreateDevice(out manager);
            useHardware = manager != null && Open(manager, out reader);
            if (!useHardware) Open(null, out reader);
            hardware = useHardware;
        }
        else Open(useHardware ? manager : null, out reader);
        reader.SetStreamSelection(MF.AllStreams, false);
        reader.SetStreamSelection(MF.FirstVideoStream, true);
        IMFMediaType want;
        MF.Check(MF.MFCreateMediaType(out want));
        want.SetGUID(MF.MajorType, MF.Video);
        want.SetGUID(MF.Subtype, MF.RGB32);
        MF.Check(reader.SetCurrentMediaType(MF.FirstVideoStream, IntPtr.Zero, want));
        Marshal.ReleaseComObject(want);
        if (first)
        {
            IMFMediaType got;
            MF.Check(reader.GetCurrentMediaType(MF.FirstVideoStream, out got));
            long size;
            MF.Check(got.GetUINT64(MF.FrameSize, out size));
            frameW = (int)(size >> 32);
            frameH = (int)(size & 0xFFFFFFFF);
            if (got.GetUINT32(MF.DefaultStride, out frameStride) < 0) frameStride = frameW * 4;
            Marshal.ReleaseComObject(got);
        }
        return reader;
    }

    // park a slot's decoder at its section start, with the first frame decoded
    void Arm(Slot s, bool throttle = false)
    {
        try
        {
            var fmt = Guid.Empty;
            var pos = new PropVariant { vt = 20, value = s.Start };   // VT_I8
            MF.Check(s.Reader.SetCurrentPosition(ref fmt, ref pos));
            while (!stop)
            {
                uint actual, flags;
                long ts;
                IMFSample sample;
                MF.Check(s.Reader.ReadSample(MF.FirstVideoStream, 0, out actual, out flags, out ts, out sample));
                if ((flags & MF.EndOfStream) != 0) { if (sample != null) Marshal.ReleaseComObject(sample); s.First = null; break; }
                if (sample == null) continue;
                if (ts < s.Start) { Marshal.ReleaseComObject(sample); if (throttle) Thread.Sleep(12); continue; }   // decoding up from the keyframe
                if (s.First == null) s.First = new int[frameW * frameH];
                CopyInto(sample, s.First, frameW, frameH, frameStride);
                Marshal.ReleaseComObject(sample);
                s.FirstTs = ts;
                break;
            }
        }
        catch (Exception ex) { error = ex.Message; }
        s.Ready = true;
    }

    // finish the frame in 'fresh' (dissolve, frosted copy), wait until it is due, then publish it
    void Emit(long ts)
    {
        if (fadeLeft > 0 && hold != null && hold.Length == fresh.Px.Length)
        {
            float f = (float)fadeLeft / FadeFrames;
            Dissolve(fresh.Px, hold, f * f * (3 - 2 * f));     // eased: gentle at both ends
            fadeLeft--;
        }
        fresh.Seq = ++seq;
        fresh.Time = ts / 1e7;
        fresh.ShrinkInto(freshSmall, Down);
        long due = (long)((timeline + ts - playStart) / 10000.0 / Rate), wait = due - clock.ElapsedMilliseconds;
        if (wait > 0) Thread.Sleep((int)Math.Min(wait, 250));
        else if (wait < -250) timeline -= (long)(wait * 10000 * Rate);   // far behind: move the timeline, don't rush
        lock (gate)
        {
            var t = front ?? back; front = fresh; fresh = t;
            var t2 = frontSmall ?? backSmall; frontSmall = freshSmall; freshSmall = t2;
        }
        var h = FrameReady;
        if (h != null) h();
    }

    void Run()
    {
        var slots = new List<Slot>();
        bool started = false;
        try
        {
            MF.Check(MF.MFStartup(MF.Version, 0));
            started = true;
            // one slot per section; a single section gets two, used alternately
            int count = sections.Length == 1 ? 2 : sections.Length;
            for (int i = 0; i < count; i++)
            {
                var sec = sections[i % sections.Length];
                slots.Add(new Slot { Reader = OpenReader(i == 0), Start = sec[0], End = sec[1] });
            }
            back = new Frame(frameW, frameH);
            backSmall = new Frame(frameW / Down, frameH / Down);
            fresh = new Frame(frameW, frameH);
            freshSmall = new Frame(frameW / Down, frameH / Down);
            foreach (var sl in slots) Arm(sl);

            clock = Stopwatch.StartNew();
            int si = 0, sinceCut = 0;
            var cur = slots[0];
            playStart = cur.Start;
            if (cur.First != null) { Array.Copy(cur.First, fresh.Px, fresh.Px.Length); lastTs = cur.FirstTs; Emit(cur.FirstTs); }
            while (!stop)
            {
                if (!awake.WaitOne(0))
                {
                    clock.Stop();
                    awake.WaitOne();
                    if (stop) break;
                    clock.Start();
                }
                uint actual, flags;
                long ts;
                IMFSample sample;
                MF.Check(cur.Reader.ReadSample(MF.FirstVideoStream, 0, out actual, out flags, out ts, out sample));
                if ((flags & MF.EndOfStream) != 0 || (sample != null && ts >= cur.End))
                {
                    if (sample != null) Marshal.ReleaseComObject(sample);
                    // the outgoing shot's last frame dissolves into the next one
                    lock (gate)
                        if (front != null && Dissolves)
                        {
                            if (hold == null || hold.Length != front.Px.Length) hold = new int[front.Px.Length];
                            Array.Copy(front.Px, hold, hold.Length);
                            fadeLeft = FadeFrames;
                        }
                    timeline += lastTs - playStart + frameLen;
                    // the finished decoder is re-parked later, once the next shot has settled
                    if (pending != null) { var early = pending; ThreadPool.QueueUserWorkItem(_ => Arm(early, true)); }
                    pending = cur;
                    pending.Ready = false;
                    sinceCut = 0;
                    si = (si + 1) % slots.Count;
                    cur = slots[si];
                    if (cur == pending) { pending = null; Arm(cur); }   // only possible with very short sections
                    while (!cur.Ready && !stop) Thread.Sleep(5);
                    if (stop) break;
                    playStart = cur.Start;
                    if (cur.First == null) continue;
                    Array.Copy(cur.First, fresh.Px, fresh.Px.Length);
                    lastTs = cur.FirstTs;
                    Emit(cur.FirstTs);
                    continue;
                }
                if (sample == null) continue;
                if (ts < cur.Start) { Marshal.ReleaseComObject(sample); continue; }
                if (ts > lastTs && ts - lastTs < 2000000) frameLen = ts - lastTs;
                lastTs = ts;
                CopyInto(sample, fresh.Px, frameW, frameH, frameStride);
                Marshal.ReleaseComObject(sample);
                Emit(ts);
                if (pending != null && ++sinceCut > FadeFrames + 5)
                {
                    var p2 = pending;
                    pending = null;
                    ThreadPool.QueueUserWorkItem(_ => Arm(p2, true));
                }
            }
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            // let any background re-parking finish before the decoders are released
            if (pending != null) pending.Ready = true;
            var until = DateTime.Now.AddSeconds(3);
            foreach (var sl in slots) while (!sl.Ready && DateTime.Now < until) Thread.Sleep(5);
            foreach (var sl in slots) if (sl.Reader != null) Marshal.ReleaseComObject(sl.Reader);
            if (manager != null) Marshal.ReleaseComObject(manager);
            if (device != IntPtr.Zero) Marshal.Release(device);
            if (started) MF.MFShutdown();
        }
    }

    IntPtr device = IntPtr.Zero;
    IMFDXGIDeviceManager manager;

    bool Open(IMFDXGIDeviceManager mgr, out IMFSourceReader reader)
    {
        reader = null;
        IMFAttributes attr;
        MF.Check(MF.MFCreateAttributes(out attr, 3));
        try
        {
            attr.SetUINT32(MF.AdvancedVideoProcessing, 1);      // the reader converts to RGB32 for us
            if (mgr != null)
            {
                attr.SetUnknown(MF.D3DManager, mgr);
                attr.SetUINT32(MF.HardwareTransforms, 1);
            }
            int hr = MF.MFCreateSourceReaderFromURL(path, attr, out reader);
            if (mgr == null) MF.Check(hr);
            return hr >= 0;
        }
        finally { Marshal.ReleaseComObject(attr); }
    }

    static IntPtr CreateDevice(out IMFDXGIDeviceManager mgr)
    {
        mgr = null;
        IntPtr dev, ctx;
        int level;
        // hardware driver, with video and BGRA support
        if (MF.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x800 | 0x20, IntPtr.Zero, 0, 7, out dev, out level, out ctx) < 0) return IntPtr.Zero;
        if (ctx != IntPtr.Zero) Marshal.Release(ctx);
        try
        {
            object devObj = Marshal.GetObjectForIUnknown(dev);
            var mt = devObj as ID3D10Multithread;
            if (mt != null) mt.SetMultithreadProtected(true);       // Media Foundation uses it from several threads
            uint token;
            IMFDXGIDeviceManager m;
            if (MF.MFCreateDXGIDeviceManager(out token, out m) < 0) return dev;
            if (m.ResetDevice(devObj, token) < 0) { Marshal.ReleaseComObject(m); return dev; }
            mgr = m;
        }
        catch { mgr = null; }
        return dev;
    }

    // dst = dst * (1 - a) + from * a
    static void Dissolve(int[] dst, int[] from, float a)
    {
        int ia = (int)(a * 256), ib = 256 - ia;
        for (int i = 0; i < dst.Length; i++)
        {
            int d = dst[i], f = from[i];
            int r = (((d >> 16) & 0xFF) * ib + ((f >> 16) & 0xFF) * ia) >> 8;
            int g = (((d >> 8) & 0xFF) * ib + ((f >> 8) & 0xFF) * ia) >> 8;
            int b = ((d & 0xFF) * ib + (f & 0xFF) * ia) >> 8;
            dst[i] = (255 << 24) | (r << 16) | (g << 8) | b;
        }
    }

    static void CopyInto(IMFSample sample, int[] px, int w, int h, int stride)
    {
        IMFMediaBuffer buf;
        MF.Check(sample.ConvertToContiguousBuffer(out buf));
        try
        {
            IntPtr p;
            int max, len;
            MF.Check(buf.Lock(out p, out max, out len));
            try
            {
                int abs = Math.Abs(stride);
                for (int y = 0; y < h; y++)
                {
                    int srcRow = stride < 0 ? h - 1 - y : y;      // negative stride = bottom-up rows
                    if ((long)(srcRow + 1) * abs > len) break;
                    Marshal.Copy(p + srcRow * abs, px, y * w, w);
                }
            }
            finally { buf.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buf); }
    }

    public void Dispose()
    {
        stop = true;
        awake.Set();
        if (thread != null) thread.Join(1500);
        lock (gate)
        {
            if (front != null) front.Dispose();
            if (frontSmall != null) frontSmall.Dispose();
            front = frontSmall = null;
        }
    }
}

// ---------- the liquid-glass clock ----------
// Built once a minute from the time's outline: a merged, rounded mask; smooth "height" fields from
// blurring it; and, for every glass pixel, how much light it catches, how much it darkens and where
// it looks through to (refraction). Each frame only re-samples the background at those spots.
class ClockGlass : IDisposable
{
    public readonly Rectangle R;
    readonly Frame output;
    readonly int[] baseline, gi;
    readonly float[] gm, gw, gs, gx, gy;
    long lastSeq = long.MinValue;

    public Bitmap Bitmap { get { return output.Bmp; } }

    public ClockGlass(GraphicsPath path, Size screen)
    {
        bool dark = Config.Dark;
        var full = new Rectangle(Point.Empty, screen);
        R = Rectangle.Inflate(Rectangle.Ceiling(path.GetBounds()), Ui.S(48), Ui.S(48));
        R.Intersect(full);
        int w = Math.Max(4, R.Width), h = Math.Max(4, R.Height);
        output = new Frame(w, h, PixelFormat.Format32bppPArgb);
        baseline = new int[w * h];

        float[] mask = new float[w * h];
        using (var mb = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(mb))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(-R.X, -R.Y);
                path.FillMode = FillMode.Winding;
                g.FillPath(Brushes.White, path);
            }
            var data = mb.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new int[w * h];
            for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w, w);
            mb.UnlockBits(data);
            for (int i = 0; i < px.Length; i++) mask[i] = ((px[i] >> 24) & 0xFF) / 255f;
        }
        // round the corners: blur and re-threshold softly
        float[] soft = Blur(mask, w, h, Ui.S(4));
        for (int i = 0; i < mask.Length; i++) mask[i] = Smooth(0.38f, 0.62f, soft[i]);

        int rn = Math.Max(2, Ui.S(5)), rw = Math.Max(4, Ui.S(15));
        float[] near = Blur(mask, w, h, rn), wide = Blur(mask, w, h, rw), shadow = Blur(mask, w, h, Ui.S(20));

        float lx = -0.55f, ly = -0.83f;                     // light from the top-left
        float dispMax = Ui.S(24), kWide = 2f * rw, kNear = 2f * rn;
        int shadowOff = Ui.S(14);
        float body = dark ? 0.10f : 0.16f, shadowStrength = dark ? 0.40f : 0.28f;

        int count = 0;
        for (int i = 0; i < mask.Length; i++) if (mask[i] > 0.001f) count++;
        gi = new int[count]; gm = new float[count]; gw = new float[count]; gs = new float[count]; gx = new float[count]; gy = new float[count];

        int k = 0;
        for (int y = 0; y < h; y++)
        {
            int ym = Math.Max(0, y - 1), yp = Math.Min(h - 1, y + 1), ys = y - shadowOff;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float m = mask[i];
                float sh = ys >= 0 ? shadow[ys * w + x] : 0f;
                float sa = shadowStrength * sh * (1f - m);
                baseline[i] = (int)(sa * 255) << 24;            // black shadow, premultiplied
                if (m <= 0.001f) continue;

                int xm = Math.Max(0, x - 1), xp = Math.Min(w - 1, x + 1);
                float wx = (wide[y * w + xp] - wide[y * w + xm]) * 0.5f, wy = (wide[yp * w + x] - wide[ym * w + x]) * 0.5f;
                float nx = (near[y * w + xp] - near[y * w + xm]) * 0.5f, ny = (near[yp * w + x] - near[ym * w + x]) * 0.5f;

                float t = body + 0.12f * (1f - (float)y / h);
                float sheen = (x + y * 0.6f) / (w + h * 0.6f);
                t += 0.10f * Smooth(0.30f, 0.38f, sheen) * (1f - Smooth(0.42f, 0.55f, sheen));
                float glow = m * (1f - wide[i]) * 0.45f + Smooth(0.25f, 0.6f, m * (1f - near[i])) * 0.45f;
                float magW = (float)Math.Sqrt(wx * wx + wy * wy), magN = (float)Math.Sqrt(nx * nx + ny * ny);
                float hi = 0f, lo = 0f;
                if (magN > 1e-5f)
                {
                    float d = (-nx * lx - ny * ly) / magN;
                    if (d > 0) hi = d * d * Math.Min(1f, magN * kNear);
                }
                if (magW > 1e-5f)
                {
                    float d = (-wx * lx - wy * ly) / magW, e = Math.Min(1f, magW * kWide);
                    if (d < 0) lo = d * d * e; else hi += 0.35f * d * d * e;
                }
                float add = Math.Min(1f, glow + hi);
                gi[k] = i;
                gm[k] = m;
                gw[k] = 1f - (1f - t) * (1f - add);
                gs[k] = lo * (dark ? 0.22f : 0.16f);
                gx[k] = R.X + x + wx * kWide * dispMax;
                gy[k] = R.Y + y + wy * kWide * dispMax;
                k++;
            }
        }
    }

    // re-sample the background behind the glass; 'scale/ox/oy' map screen pixels onto the source frame
    public void Render(Frame sharp, Frame small, int down, float scale, float ox, float oy)
    {
        if (sharp.Seq == lastSeq && lastSeq != long.MinValue) return;
        lastSeq = sharp.Seq;
        int[] px = output.Px, sp = sharp.Px, bp = small.Px;
        Array.Copy(baseline, px, baseline.Length);
        int sw = sharp.W, sh = sharp.H, bw = small.W, bh = small.H;
        float inv = 1f / scale;
        for (int k = 0; k < gi.Length; k++)
        {
            float vx = (gx[k] - ox) * inv, vy = (gy[k] - oy) * inv;
            int ix = (int)vx, iy = (int)vy;
            ix = ix < 0 ? 0 : ix >= sw ? sw - 1 : ix;
            iy = iy < 0 ? 0 : iy >= sh ? sh - 1 : iy;
            int c2 = sp[iy * sw + ix];

            float u = vx / down - 0.5f, v = vy / down - 0.5f;
            int u0 = (int)Math.Floor(u), v0 = (int)Math.Floor(v);
            float fu = u - u0, fv = v - v0;
            int ua = u0 < 0 ? 0 : u0 >= bw ? bw - 1 : u0, ub = u0 + 1 < 0 ? 0 : u0 + 1 >= bw ? bw - 1 : u0 + 1;
            int va = v0 < 0 ? 0 : v0 >= bh ? bh - 1 : v0, vb = v0 + 1 < 0 ? 0 : v0 + 1 >= bh ? bh - 1 : v0 + 1;
            int p00 = bp[va * bw + ua], p10 = bp[va * bw + ub], p01 = bp[vb * bw + ua], p11 = bp[vb * bw + ub];
            float w00 = (1 - fu) * (1 - fv), w10 = fu * (1 - fv), w01 = (1 - fu) * fv, w11 = fu * fv;
            float br = ((p00 >> 16) & 0xFF) * w00 + ((p10 >> 16) & 0xFF) * w10 + ((p01 >> 16) & 0xFF) * w01 + ((p11 >> 16) & 0xFF) * w11;
            float bg = ((p00 >> 8) & 0xFF) * w00 + ((p10 >> 8) & 0xFF) * w10 + ((p01 >> 8) & 0xFF) * w01 + ((p11 >> 8) & 0xFF) * w11;
            float bb = (p00 & 0xFF) * w00 + (p10 & 0xFF) * w10 + (p01 & 0xFF) * w01 + (p11 & 0xFF) * w11;

            float r = br * 0.45f + ((c2 >> 16) & 0xFF) * 0.55f;
            float g = bg * 0.45f + ((c2 >> 8) & 0xFF) * 0.55f;
            float b = bb * 0.45f + (c2 & 0xFF) * 0.55f;
            float a = gw[k], shade = 1f - gs[k], m = gm[k];
            r = (r + (255 - r) * a) * shade * m;
            g = (g + (255 - g) * a) * shade * m;
            b = (b + (255 - b) * a) * shade * m;
            int i = gi[k];
            float baseA = ((baseline[i] >> 24) & 0xFF) / 255f;
            int A = (int)((m + baseA * (1 - m)) * 255);
            px[i] = (A << 24) | (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
        }
    }

    static int Clamp(float v) { return v < 0 ? 0 : v > 255 ? 255 : (int)v; }

    static float Smooth(float a, float b, float v)
    {
        float t = (v - a) / (b - a);
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        return t * t * (3 - 2 * t);
    }

    // three box-blur passes per axis: a fast, smooth approximation of a Gaussian
    static float[] Blur(float[] src, int w, int h, int radius)
    {
        int r = Math.Max(1, radius / 2);
        var a = (float[])src.Clone();
        var t = new float[a.Length];
        for (int pass = 0; pass < 3; pass++) { BoxH(a, t, w, h, r); BoxV(t, a, w, h, r); }
        return a;
    }

    static void BoxH(float[] s, float[] d, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float acc = 0;
            for (int k = -r; k <= r; k++) acc += s[row + Math.Min(w - 1, Math.Max(0, k))];
            for (int x = 0; x < w; x++)
            {
                d[row + x] = acc * inv;
                acc += s[row + Math.Min(w - 1, x + r + 1)] - s[row + Math.Max(0, x - r)];
            }
        }
    }

    static void BoxV(float[] s, float[] d, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        for (int x = 0; x < w; x++)
        {
            float acc = 0;
            for (int k = -r; k <= r; k++) acc += s[Math.Min(h - 1, Math.Max(0, k)) * w + x];
            for (int y = 0; y < h; y++)
            {
                d[y * w + x] = acc * inv;
                acc += s[Math.Min(h - 1, y + r + 1) * w + x] - s[Math.Max(0, y - r) * w + x];
            }
        }
    }

    public void Dispose() { output.Dispose(); }
}


// ---------- bakes the chosen road shots into one seamless clip ----------
// Decodes each section once, dissolves every hand-over (including the wrap from the last shot back
// to the first), re-times the frames for the chosen speed, and encodes it all with Windows' own
// H.264 encoder. Playing that single file needs no mid-loop seeking, so it never stutters.
static class LoopBuilder
{
    const int FadeFrames = 15;

    // a fingerprint of everything the loop depends on; when it changes, the loop is rebuilt
    public static string Key(string video, string sections, double speed)
    {
        try
        {
            var fi = new System.IO.FileInfo(video);
            return fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + sections + "|" + speed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|v1";
        }
        catch { return ""; }
    }

    public static void Build(string video, IList<double[]> sections, double speed, string dest)
    {
        MF.Check(MF.MFStartup(MF.Version, 0));
        IMFSourceReader reader = null;
        IMFSinkWriter writer = null;
        try
        {
            IMFAttributes ra;
            MF.Check(MF.MFCreateAttributes(out ra, 1));
            ra.SetUINT32(MF.AdvancedVideoProcessing, 1);
            MF.Check(MF.MFCreateSourceReaderFromURL(video, ra, out reader));
            Marshal.ReleaseComObject(ra);
            reader.SetStreamSelection(MF.AllStreams, false);
            reader.SetStreamSelection(MF.FirstVideoStream, true);
            IMFMediaType want;
            MF.Check(MF.MFCreateMediaType(out want));
            want.SetGUID(MF.MajorType, MF.Video);
            want.SetGUID(MF.Subtype, MF.RGB32);
            MF.Check(reader.SetCurrentMediaType(MF.FirstVideoStream, IntPtr.Zero, want));
            Marshal.ReleaseComObject(want);
            IMFMediaType got;
            MF.Check(reader.GetCurrentMediaType(MF.FirstVideoStream, out got));
            long size;
            MF.Check(got.GetUINT64(MF.FrameSize, out size));
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF), stride;
            if (got.GetUINT32(MF.DefaultStride, out stride) < 0) stride = w * 4;
            Marshal.ReleaseComObject(got);

            var px = new int[w * h];
            var hold = new int[w * h];
            var parts = new List<long[]>();
            foreach (var sec in sections) parts.Add(new[] { (long)(sec[0] * 1e7), (long)(sec[1] * 1e7) });

            // the last frame of the last shot, so the clip can open by dissolving out of it (seamless wrap)
            long srcFrame = 333333;
            ReadSection(reader, parts[parts.Count - 1], w, h, stride, px, (f, ts) => Array.Copy(f, hold, f.Length), ref srcFrame);

            long outFrame = (long)(srcFrame / speed);
            int fpsNum = (int)Math.Round(1e7 / outFrame * 1000), fpsDen = 1000;

            // the encoder: H.264, high bitrate so the motion-blurred footage stays clean
            IMFAttributes wa;
            MF.Check(MF.MFCreateAttributes(out wa, 1));
            wa.SetUINT32(MF.HardwareTransforms, 1);
            if (System.IO.File.Exists(dest)) System.IO.File.Delete(dest);
            MF.Check(MF.MFCreateSinkWriterFromURL(dest, IntPtr.Zero, wa, out writer));
            Marshal.ReleaseComObject(wa);
            IMFMediaType outType, inType;
            MF.Check(MF.MFCreateMediaType(out outType));
            outType.SetGUID(MF.MajorType, MF.Video);
            outType.SetGUID(MF.Subtype, MF.H264);
            outType.SetUINT32(MF.AvgBitrate, 16000000);
            outType.SetUINT32(MF.InterlaceMode, 2);                    // progressive
            outType.SetUINT64(MF.FrameSize, ((long)w << 32) | (uint)h);
            outType.SetUINT64(MF.FrameRate, ((long)fpsNum << 32) | (uint)fpsDen);
            outType.SetUINT64(MF.PixelAspect, (1L << 32) | 1);
            int stream;
            MF.Check(writer.AddStream(outType, out stream));
            MF.Check(MF.MFCreateMediaType(out inType));
            inType.SetGUID(MF.MajorType, MF.Video);
            inType.SetGUID(MF.Subtype, MF.RGB32);
            inType.SetUINT32(MF.InterlaceMode, 2);
            inType.SetUINT32(MF.DefaultStride, w * 4);                  // top-down rows
            inType.SetUINT64(MF.FrameSize, ((long)w << 32) | (uint)h);
            inType.SetUINT64(MF.FrameRate, ((long)fpsNum << 32) | (uint)fpsDen);
            inType.SetUINT64(MF.PixelAspect, (1L << 32) | 1);
            MF.Check(writer.SetInputMediaType(stream, inType, null));
            Marshal.ReleaseComObject(outType);
            Marshal.ReleaseComObject(inType);
            MF.Check(writer.BeginWriting());

            long time = 0;
            var wr = writer;
            foreach (var part in parts)
            {
                int fade = FadeFrames;
                ReadSection(reader, part, w, h, stride, px, (f, ts) =>
                {
                    if (fade > 0)
                    {
                        float a = (float)fade / FadeFrames;
                        Blend(f, hold, a * a * (3 - 2 * a));
                        fade--;
                    }
                    Write(wr, stream, f, time, outFrame);
                    time += outFrame;
                    Array.Copy(f, hold, f.Length);      // keeps this shot's last frame for the next dissolve
                }, ref srcFrame);
            }
            MF.Check(writer.Finalize_());
        }
        finally
        {
            if (writer != null) Marshal.ReleaseComObject(writer);
            if (reader != null) Marshal.ReleaseComObject(reader);
            MF.MFShutdown();
        }
    }

    // decode one section, calling 'frame' for every frame inside it
    static void ReadSection(IMFSourceReader reader, long[] part, int w, int h, int stride, int[] px, Action<int[], long> frame, ref long frameLen)
    {
        var fmt = Guid.Empty;
        var pos = new PropVariant { vt = 20, value = part[0] };
        MF.Check(reader.SetCurrentPosition(ref fmt, ref pos));
        long last = -1;
        while (true)
        {
            uint actual, flags;
            long ts;
            IMFSample sample;
            MF.Check(reader.ReadSample(MF.FirstVideoStream, 0, out actual, out flags, out ts, out sample));
            if ((flags & MF.EndOfStream) != 0 || (sample != null && ts >= part[1])) { if (sample != null) Marshal.ReleaseComObject(sample); break; }
            if (sample == null) continue;
            if (ts < part[0]) { Marshal.ReleaseComObject(sample); continue; }
            if (last >= 0 && ts > last && ts - last < 2000000) frameLen = ts - last;
            last = ts;
            IMFMediaBuffer buf;
            MF.Check(sample.ConvertToContiguousBuffer(out buf));
            IntPtr p; int max, len;
            MF.Check(buf.Lock(out p, out max, out len));
            int abs = Math.Abs(stride);
            for (int y = 0; y < h; y++)
            {
                int srcRow = stride < 0 ? h - 1 - y : y;
                if ((long)(srcRow + 1) * abs > len) break;
                Marshal.Copy(p + srcRow * abs, px, y * w, w);
            }
            buf.Unlock();
            Marshal.ReleaseComObject(buf);
            Marshal.ReleaseComObject(sample);
            frame(px, ts);
        }
    }

    static void Write(IMFSinkWriter writer, int stream, int[] px, long time, long duration)
    {
        IMFMediaBuffer buf;
        MF.Check(MF.MFCreateMemoryBuffer(px.Length * 4, out buf));
        IntPtr p; int max, len;
        MF.Check(buf.Lock(out p, out max, out len));
        Marshal.Copy(px, 0, p, px.Length);
        buf.Unlock();
        buf.SetCurrentLength(px.Length * 4);
        IMFSample sample;
        MF.Check(MF.MFCreateSample(out sample));
        sample.AddBuffer(buf);
        sample.SetSampleTime(time);
        sample.SetSampleDuration(duration);
        MF.Check(writer.WriteSample(stream, sample));
        Marshal.ReleaseComObject(sample);
        Marshal.ReleaseComObject(buf);
    }

    // dst = dst * (1 - a) + from * a
    static void Blend(int[] dst, int[] from, float a)
    {
        int ia = (int)(a * 256), ib = 256 - ia;
        for (int i = 0; i < dst.Length; i++)
        {
            int d = dst[i], f = from[i];
            int r = (((d >> 16) & 0xFF) * ib + ((f >> 16) & 0xFF) * ia) >> 8;
            int g = (((d >> 8) & 0xFF) * ib + ((f >> 8) & 0xFF) * ia) >> 8;
            int b = ((d & 0xFF) * ib + (f & 0xFF) * ia) >> 8;
            dst[i] = (255 << 24) | (r << 16) | (g << 8) | b;
        }
    }
}
