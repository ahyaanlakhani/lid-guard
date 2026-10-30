// LidGuard lock screen: a macOS-style canvas with a liquid-glass clock, a generated
// grain wallpaper and frosted widgets. Everything is painted onto one surface.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// ---------- wallpaper: soft abstract shapes with film grain (plus a blurred copy for glass) ----------
static class Wallpaper
{
    static readonly Dictionary<string, Bitmap[]> cache = new Dictionary<string, Bitmap[]>();

    // { sharp, blurred } for this size and the current theme, generated once
    public static Bitmap[] Get(Size size)
    {
        bool dark = Config.Dark;
        string k = size.Width + "x" + size.Height + (dark ? "d" : "l") + Config.WallStyle;
        lock (cache)
        {
            Bitmap[] v;
            if (!cache.TryGetValue(k, out v))
            {
                var sharp = Make(size, dark);
                v = new[] { sharp, Blur(sharp, 16) };
                cache[k] = v;
            }
            return v;
        }
    }

    public static Color Average(bool dark)
    {
        Look look;
        if (looks.TryGetValue(Config.WallStyle, out look)) return Argb(dark ? look.Bottom : look.BottomLight);
        return dark ? C(40, 28, 72) : C(214, 190, 230);
    }

    static readonly Dictionary<string, Frame[]> frames = new Dictionary<string, Frame[]>();

    // the sharp wallpaper and its frosted copy as raw frames, for the glass clock
    public static Frame[] Frames(Size size)
    {
        string k = size.Width + "x" + size.Height + (Config.Dark ? "d" : "l") + Config.WallStyle;
        var bmps = Get(size);
        lock (frames)
        {
            Frame[] v;
            if (!frames.TryGetValue(k, out v))
            {
                var full = Frame.From(bmps[0]);
                v = new[] { full, full.Shrink(VideoWallpaper.Down) };
                frames[k] = v;
            }
            return v;
        }
    }

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
    static Color Argb(uint v) { return Color.FromArgb(unchecked((int)v)); }

    // the built-in choices in the Wallpaper menu; "Classic" is the original drawn scene
    public static readonly string[] Styles = { "Classic", "Sequoia", "Sonoma", "Monterey", "Lumen" };

    // soft-light blobs in an airy, layered gradient (original art in the spirit of macOS wallpapers)
    class Look
    {
        public uint Top, Bottom, TopLight, BottomLight;
        public Blob[] Blobs;
    }

    struct Blob
    {
        public float X, Y, R;          // centre as fractions of the width / height, radius as a fraction of the height
        public uint Dark, Light;       // ARGB for the dark and light themes
        public Blob(float x, float y, float r, uint dark, uint light) { X = x; Y = y; R = r; Dark = dark; Light = light; }
    }

    static readonly Dictionary<string, Look> looks = new Dictionary<string, Look>
    {
        { "Sequoia", new Look { Top = 0xFF0E2A3A, Bottom = 0xFF0A1830, TopLight = 0xFFCFEFEA, BottomLight = 0xFFB5DCEB, Blobs = new[] {
            new Blob(0.20f, 0.30f, 0.60f, 0x7037C4A0, 0x90FFFFFF),
            new Blob(0.85f, 0.25f, 0.50f, 0x602F7FD8, 0x80A8D8FF),
            new Blob(0.50f, 0.95f, 0.70f, 0x6018A088, 0x80B8F0DA),
            new Blob(0.95f, 0.90f, 0.45f, 0x40A0E0E0, 0x60FFFFFF) } } },
        { "Sonoma", new Look { Top = 0xFF2B1B3D, Bottom = 0xFF1A0F2A, TopLight = 0xFFFCE3CF, BottomLight = 0xFFF0C4D8, Blobs = new[] {
            new Blob(0.30f, 1.05f, 0.90f, 0xC0FF8A5B, 0xD0FFD2A6),
            new Blob(0.75f, 0.90f, 0.70f, 0xA0E0508F, 0xC0F7A8C8),
            new Blob(0.55f, 0.20f, 0.60f, 0x508E6BFF, 0x70C8B6FF),
            new Blob(0.10f, 0.20f, 0.40f, 0x40FFB3C6, 0x60FFF0E0) } } },
        { "Monterey", new Look { Top = 0xFF0F2148, Bottom = 0xFF1B0F30, TopLight = 0xFFDCE8FF, BottomLight = 0xFFFAD9C8, Blobs = new[] {
            new Blob(0.15f, 0.80f, 0.70f, 0xA0FF7A59, 0xB0FFA58A),
            new Blob(0.90f, 0.20f, 0.70f, 0x9038A8FF, 0xA0A8C8FF),
            new Blob(0.50f, 0.50f, 0.45f, 0x50FFD27A, 0x70FFE8B0),
            new Blob(0.80f, 0.85f, 0.50f, 0x60B05EE0, 0x70E0B0F0) } } },
        { "Lumen", new Look { Top = 0xFF0B0B14, Bottom = 0xFF05050A, TopLight = 0xFFF3F3F8, BottomLight = 0xFFE4E4EE, Blobs = new[] {
            new Blob(0.70f, 0.45f, 0.50f, 0x90A08CFF, 0x60C8C4FF),
            new Blob(0.30f, 0.70f, 0.40f, 0x6070C8FF, 0x40A8E0FF),
            new Blob(0.50f, 0.10f, 0.30f, 0x40FFFFFF, 0x30FFFFFF) } } },
    };

    static Bitmap MakeLook(Size s, bool dark, Look look)
    {
        int w = Math.Max(1, s.Width), h = Math.Max(1, s.Height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, w, h), Argb(dark ? look.Top : look.TopLight), Argb(dark ? look.Bottom : look.BottomLight), 90f))
                g.FillRectangle(b, 0, 0, w, h);
            foreach (var blob in look.Blobs)
            {
                float rx = blob.R * h * 1.5f, ry = blob.R * h;
                Glow(g, new RectangleF(blob.X * w - rx, blob.Y * h - ry, 2 * rx, 2 * ry), Argb(dark ? blob.Dark : blob.Light));
            }
        }
        Grain(bmp, dark ? 7 : 6);
        return bmp;
    }

    static Bitmap Make(Size s, bool dark)
    {
        Look look;
        if (looks.TryGetValue(Config.WallStyle, out look)) return MakeLook(s, dark, look);
        int w = Math.Max(1, s.Width), h = Math.Max(1, s.Height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color baseA = dark ? C(30, 22, 52) : C(250, 233, 214);
            Color baseB = dark ? C(54, 30, 72) : C(242, 208, 224);
            Color discIn = dark ? C(136, 108, 230) : C(200, 182, 248);
            Color discOut = dark ? C(34, 20, 88) : C(90, 60, 176);
            Color pinkIn = dark ? C(156, 78, 134) : C(253, 218, 230);
            Color pinkOut = dark ? C(92, 40, 88) : C(238, 172, 200);
            Color cream = dark ? C(76, 54, 104) : C(255, 242, 218);

            using (var b = new LinearGradientBrush(new Rectangle(0, 0, w, h), baseA, baseB, 20f)) g.FillRectangle(b, 0, 0, w, h);
            Glow(g, new RectangleF(-0.35f * w, -0.7f * h, 0.95f * w, 1.25f * h), Color.FromArgb(dark ? 90 : 210, cream));

            // the big disc, with a soft shadow and a light streak across it
            float r = 0.72f * h;
            var c = new PointF(0.66f * w, 0.46f * h);
            Blurred(g, w, h, gg => { using (var sb = new SolidBrush(Color.FromArgb(dark ? 130 : 80, 40, 10, 80))) gg.FillEllipse(sb, c.X - r * 1.03f + w * 0.01f, c.Y - r * 1.03f + h * 0.035f, r * 2.06f, r * 2.06f); });
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(c.X - r, c.Y - r, 2 * r, 2 * r);
                using (var pb = new PathGradientBrush(path))
                {
                    pb.CenterPoint = new PointF(c.X - r * 0.35f, c.Y - r * 0.25f);
                    pb.CenterColor = discIn;
                    pb.SurroundColors = new[] { discOut };
                    pb.FocusScales = new PointF(0.12f, 0.12f);
                    g.FillPath(pb, path);
                }
                var st = g.Save();
                g.SetClip(path);
                g.TranslateTransform(c.X, c.Y);
                g.RotateTransform(-28);
                Glow(g, new RectangleF(-r * 1.2f, -r * 0.24f, r * 2.4f, r * 0.48f), Color.FromArgb(dark ? 70 : 120, 255, 255, 255));
                g.Restore(st);
            }

            // pink shape rising from the bottom-left corner
            float pr = 0.62f * h;
            var pc = new PointF(0.06f * w, 1.12f * h);
            Blurred(g, w, h, gg => { using (var sb = new SolidBrush(Color.FromArgb(dark ? 120 : 70, 60, 10, 60))) gg.FillEllipse(sb, pc.X - pr * 1.04f, pc.Y - pr * 1.07f, pr * 2.08f, pr * 2.08f); });
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(pc.X - pr, pc.Y - pr, 2 * pr, 2 * pr);
                using (var pb = new PathGradientBrush(path))
                {
                    pb.CenterPoint = new PointF(pc.X + pr * 0.3f, pc.Y - pr * 0.5f);
                    pb.CenterColor = pinkIn;
                    pb.SurroundColors = new[] { pinkOut };
                    g.FillPath(pb, path);
                }
            }
        }
        Grain(bmp, dark ? 7 : 6);
        return bmp;
    }

    static void Glow(Graphics g, RectangleF r, Color c)
    {
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(r);
            using (var b = new PathGradientBrush(path))
            {
                b.CenterColor = c;
                b.SurroundColors = new[] { Color.FromArgb(0, c) };
                g.FillPath(b, path);
            }
        }
    }

    // draw at a tenth of the size and stretch back up: a cheap, smooth blur for shadows
    static void Blurred(Graphics g, int w, int h, Action<Graphics> draw)
    {
        const int k = 10;
        using (var small = new Bitmap(Math.Max(1, w / k), Math.Max(1, h / k), PixelFormat.Format32bppPArgb))
        {
            using (var sg = Graphics.FromImage(small))
            {
                sg.SmoothingMode = SmoothingMode.AntiAlias;
                sg.ScaleTransform(1f / k, 1f / k);
                draw(sg);
            }
            Stretch(g, small, w, h);
        }
    }

    static void Stretch(Graphics g, Bitmap small, int w, int h)
    {
        using (var attr = new ImageAttributes())
        {
            attr.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe at the edges
            var im = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(small, new Rectangle(0, 0, w, h), 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, attr);
            g.InterpolationMode = im;
        }
    }

    public static Bitmap Blur(Bitmap src, int factor)
    {
        int w = src.Width, h = src.Height;
        using (var small = new Bitmap(Math.Max(1, w / factor), Math.Max(1, h / factor), PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, new Rectangle(0, 0, small.Width, small.Height));
            }
            var dst = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dst)) Stretch(g, small, w, h);
            return dst;
        }
    }

    static void Grain(Bitmap bmp, int amount)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        int bytes = data.Stride * bmp.Height;
        var buf = new byte[bytes];
        Marshal.Copy(data.Scan0, buf, 0, bytes);
        uint x = 2463534242;
        uint span = (uint)(amount * 2 + 1);
        for (int i = 0; i + 3 < bytes; i += 4)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            int n = (int)(x % span) - amount;
            buf[i] = Clamp(buf[i] + n);
            buf[i + 1] = Clamp(buf[i + 1] + n);
            buf[i + 2] = Clamp(buf[i + 2] + n);
        }
        Marshal.Copy(buf, 0, data.Scan0, bytes);
        bmp.UnlockBits(data);
    }

    static byte Clamp(int v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }
}

// ---------- glass ----------
static class Glass
{
    // frosted panel: blurred wallpaper behind, a light tint, a soft shadow and a bright top edge
    public static void Panel(Graphics g, Bitmap blurred, RectangleF r, float radius, bool shadow = true)
    {
        bool dark = Config.Dark;
        if (shadow)
        {
            // only outside the panel, so it never darkens the glass itself
            var st0 = g.Save();
            using (var inside = Ui.Round(r, radius)) g.SetClip(inside, CombineMode.Exclude);
            for (int i = 1; i <= 6; i++)
            {
                float d = i * Ui.S(1.5f);
                var sr = RectangleF.Inflate(r, d, d);
                sr.Offset(0, Ui.S(6));
                using (var p = Ui.Round(sr, radius + d))
                using (var b = new SolidBrush(Color.FromArgb(dark ? 10 : 7, 30, 10, 50))) g.FillPath(b, p);
            }
            g.Restore(st0);
        }
        using (var p = Ui.Round(r, radius))
        {
            var st = g.Save();
            g.SetClip(p, CombineMode.Intersect);
            if (blurred != null) g.DrawImage(blurred, r, r, GraphicsUnit.Pixel);   // null: the live video frost is painted underneath
            using (var b = new LinearGradientBrush(Expand(r), Color.FromArgb(dark ? 44 : 96, 255, 255, 255), Color.FromArgb(dark ? 16 : 52, 255, 255, 255), 90f)) g.FillRectangle(b, r);
            if (dark) using (var b = new SolidBrush(Color.FromArgb(46, 12, 8, 32))) g.FillRectangle(b, r);
            g.Restore(st);
            using (var b = new LinearGradientBrush(Expand(r), Color.FromArgb(dark ? 96 : 170, 255, 255, 255), Color.FromArgb(dark ? 18 : 50, 255, 255, 255), 90f))
            using (var pen = new Pen(b, Math.Max(1f, Ui.Scale))) g.DrawPath(pen, p);
        }
    }

    static RectangleF Expand(RectangleF r) { return new RectangleF(r.X, r.Y - 1, Math.Max(1, r.Width), Math.Max(2, r.Height + 2)); }

    static readonly Dictionary<string, FontFamily> families = new Dictionary<string, FontFamily>();

    public static FontFamily Family(params string[] names)
    {
        string key = string.Join("|", names);
        FontFamily f;
        if (families.TryGetValue(key, out f)) return f;
        var installed = new InstalledFontCollection().Families;
        foreach (var n in names)
            foreach (var fam in installed)
                if (fam.Name == n) { families[key] = fam; return fam; }
        families[key] = FontFamily.GenericSansSerif;
        return FontFamily.GenericSansSerif;
    }

    // the time as a path, stretched tall like the reference and fitted into the box
    public static GraphicsPath ClockPath(string text, RectangleF box, out RectangleF bounds)
    {
        const float stretch = 1.5f;
        var fam = Family("Bahnschrift SemiLight Condensed", "Bahnschrift Condensed", "Bahnschrift Light Condensed", "Segoe UI");
        var path = new GraphicsPath();
        // hours and minutes as glyphs; the colon as two round dots (the font's colon is square)
        string[] parts = text.Split(':');
        using (var hp = new GraphicsPath())
        using (var mp = new GraphicsPath())
        {
            hp.AddString(parts[0], fam, (int)FontStyle.Regular, 1000f, PointF.Empty, StringFormat.GenericTypographic);
            mp.AddString(parts.Length > 1 ? parts[1] : "", fam, (int)FontStyle.Regular, 1000f, PointF.Empty, StringFormat.GenericTypographic);
            var hb = hp.GetBounds();
            float digitH = hb.Height, dot = digitH * 0.11f, gap = digitH * 0.07f;
            using (var m = new Matrix()) { m.Translate(-hb.X, -hb.Y); hp.Transform(m); }
            path.AddPath(hp, false);
            float x = hb.Width + gap;
            // dots are drawn squashed so they come out round after the tall stretch below
            float dh = dot / stretch;
            path.AddEllipse(x, digitH * 0.3f - dh / 2, dot, dh);
            path.AddEllipse(x, digitH * 0.78f - dh / 2, dot, dh);
            var mb = mp.GetBounds();
            using (var m = new Matrix()) { m.Translate(x + dot + gap - mb.X, -hb.Y); mp.Transform(m); }   // same baseline as the hours
            path.AddPath(mp, false);
        }
        var b = path.GetBounds();
        float sy = box.Height / b.Height, sx = sy / stretch;
        if (b.Width * sx > box.Width) { float f = box.Width / (b.Width * sx); sx *= f; sy *= f; }
        using (var m = new Matrix())
        {
            float w = b.Width * sx, h = b.Height * sy;
            m.Translate(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2);
            m.Scale(sx, sy);
            m.Translate(-b.X, -b.Y);
            path.Transform(m);
        }
        bounds = path.GetBounds();
        return path;
    }
}

// ---------- vector icons ----------
static class Icons
{
    static readonly Color SunCol = Color.FromArgb(255, 196, 46), RainCol = Color.FromArgb(96, 170, 255);

    public static void Weather(Graphics g, RectangleF r, int code, bool day)
    {
        bool partly = code == 1 || code == 2;
        bool rain = (code >= 51 && code <= 67) || (code >= 80 && code <= 82);
        bool snow = (code >= 71 && code <= 77) || code == 85 || code == 86;
        bool storm = code >= 95, fog = code == 45 || code == 48;
        if (code == 0) { if (day) Sun(g, r); else Moon(g, r); return; }
        if (partly)
        {
            var small = new RectangleF(r.X + r.Width * 0.36f, r.Y, r.Width * 0.64f, r.Height * 0.64f);
            if (day) Sun(g, small); else Moon(g, small);
            Cloud(g, new RectangleF(r.X, r.Y + r.Height * 0.32f, r.Width * 0.86f, r.Height * 0.58f));
            return;
        }
        Cloud(g, new RectangleF(r.X, r.Y + r.Height * 0.06f, r.Width, r.Height * 0.6f));
        float y0 = r.Y + r.Height * 0.72f, y1 = r.Y + r.Height * 0.96f;
        if (rain || storm)
            using (var pen = new Pen(RainCol, Math.Max(1.5f, r.Width * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i < 3; i++)
                {
                    float x = r.X + r.Width * (0.3f + i * 0.2f);
                    g.DrawLine(pen, x, y0, x - r.Width * 0.06f, y1);
                }
        if (storm)
            using (var b = new SolidBrush(SunCol))
                g.FillPolygon(b, new[] { P(r, 0.56f, 0.52f), P(r, 0.40f, 0.80f), P(r, 0.52f, 0.80f), P(r, 0.44f, 1.0f), P(r, 0.66f, 0.70f), P(r, 0.54f, 0.70f) });
        if (snow)
            using (var b = new SolidBrush(Color.FromArgb(235, 245, 255)))
                for (int i = 0; i < 3; i++)
                {
                    float d = r.Width * 0.1f;
                    g.FillEllipse(b, r.X + r.Width * (0.28f + i * 0.2f), y0 + (i % 2) * r.Height * 0.1f, d, d);
                }
        if (fog)
            using (var pen = new Pen(Color.FromArgb(200, 230, 232, 240), Math.Max(1.5f, r.Width * 0.06f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, r.X + r.Width * 0.15f, y0, r.X + r.Width * 0.85f, y0);
                g.DrawLine(pen, r.X + r.Width * 0.25f, y1 - r.Height * 0.06f, r.X + r.Width * 0.75f, y1 - r.Height * 0.06f);
            }
    }

    static PointF P(RectangleF r, float x, float y) { return new PointF(r.X + r.Width * x, r.Y + r.Height * y); }

    static void Sun(Graphics g, RectangleF r)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rad = Math.Min(r.Width, r.Height) * 0.24f;
        using (var pen = new Pen(SunCol, Math.Max(1.5f, rad * 0.32f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                g.DrawLine(pen, cx + c * rad * 1.45f, cy + s * rad * 1.45f, cx + c * rad * 1.95f, cy + s * rad * 1.95f);
            }
        var core = new RectangleF(cx - rad, cy - rad, rad * 2, rad * 2);
        using (var b = new LinearGradientBrush(core, Color.FromArgb(255, 222, 96), Color.FromArgb(255, 170, 30), 60f)) g.FillEllipse(b, core);
    }

    static void Moon(Graphics g, RectangleF r)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rad = Math.Min(r.Width, r.Height) * 0.34f;
        var st = g.Save();
        using (var cut = new GraphicsPath())
        {
            cut.AddEllipse(cx - rad * 0.35f, cy - rad * 1.25f, rad * 2, rad * 2);
            g.SetClip(cut, CombineMode.Exclude);
            // pale moon on dark glass, a warmer gold on light glass so it stays visible
            using (var b = new SolidBrush(Config.Dark ? Color.FromArgb(246, 236, 196) : Color.FromArgb(236, 178, 52))) g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
        }
        g.Restore(st);
    }

    static void Cloud(Graphics g, RectangleF r)
    {
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(r.X + r.Width * 0.06f, r.Y + r.Height * 0.38f, r.Width * 0.42f, r.Height * 0.58f);
            path.AddEllipse(r.X + r.Width * 0.24f, r.Y + r.Height * 0.04f, r.Width * 0.5f, r.Height * 0.8f);
            path.AddEllipse(r.X + r.Width * 0.52f, r.Y + r.Height * 0.3f, r.Width * 0.42f, r.Height * 0.64f);
            path.AddRectangle(new RectangleF(r.X + r.Width * 0.26f, r.Y + r.Height * 0.6f, r.Width * 0.48f, r.Height * 0.36f));
            path.FillMode = FillMode.Winding;
            using (var b = new LinearGradientBrush(r, Color.FromArgb(255, 255, 255), Color.FromArgb(222, 228, 240), 90f)) g.FillPath(b, path);
        }
    }

    public static void Padlock(Graphics g, RectangleF r, Color c)
    {
        using (var pen = new Pen(c, Math.Max(1.3f, r.Width * 0.14f)))
            g.DrawArc(pen, r.X + r.Width * 0.24f, r.Y, r.Width * 0.52f, r.Height * 0.6f, 180, 180);
        using (var p = Ui.Round(new RectangleF(r.X + r.Width * 0.1f, r.Y + r.Height * 0.4f, r.Width * 0.8f, r.Height * 0.6f), r.Width * 0.14f))
        using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    public static void Battery(Graphics g, RectangleF r, float level, bool charging, Color c)
    {
        using (var pen = new Pen(Color.FromArgb(170, c), Math.Max(1f, Ui.Scale)))
        using (var p = Ui.Round(new RectangleF(r.X, r.Y, r.Width - r.Width * 0.1f, r.Height), r.Height * 0.25f))
            g.DrawPath(pen, p);
        using (var b = new SolidBrush(Color.FromArgb(170, c)))
            g.FillRectangle(b, r.Right - r.Width * 0.08f, r.Y + r.Height * 0.3f, r.Width * 0.08f, r.Height * 0.4f);
        float inset = Math.Max(2f, r.Height * 0.18f);
        var fill = new RectangleF(r.X + inset, r.Y + inset, (r.Width * 0.9f - inset * 2) * Math.Max(0.05f, Math.Min(1f, level)), r.Height - inset * 2);
        using (var b = new SolidBrush(level < 0.2f && !charging ? Color.FromArgb(255, 90, 90) : c)) g.FillRectangle(b, fill);
    }
}

// ---------- weather (Open-Meteo, no account needed) ----------
static class Weather
{
    public class Hour { public DateTime Time; public double Temp; public int Code; public bool Day; }
    public class Data { public double Temp, Max, Min; public int Code; public bool Day; public List<Hour> Hours = new List<Hour>(); public DateTime Fetched; }

    public static Data Current;
    public static int Stamp;            // bumps on every update so the lock screen knows to redraw
    public static event Action Updated;
    static int busy;

    static Weather() { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }   // TLS 1.2

    public static void RefreshIfStale()
    {
        if (Current != null && (DateTime.Now - Current.Fetched).TotalMinutes < 30) return;
        Refresh();
    }

    public static void Refresh()
    {
        if (!Config.HasPlace || Interlocked.Exchange(ref busy, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Current = Fetch(Config.Lat, Config.Lon);
                Stamp++;
                var h = Updated;
                if (h != null) h();
            }
            catch { }
            finally { busy = 0; }
        });
    }

    static string Get(string url)
    {
        using (var wc = new WebClient())
        {
            wc.Encoding = System.Text.Encoding.UTF8;
            wc.Headers[HttpRequestHeader.UserAgent] = "LidGuard";
            return wc.DownloadString(url);
        }
    }

    static string N(double v) { return v.ToString(CultureInfo.InvariantCulture); }
    static double D(object o) { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }

    static Data Fetch(double lat, double lon)
    {
        string json = Get("https://api.open-meteo.com/v1/forecast?latitude=" + N(lat) + "&longitude=" + N(lon) +
            "&current=temperature_2m,weather_code,is_day&hourly=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=2");
        var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
        var cur = (Dictionary<string, object>)root["current"];
        var daily = (Dictionary<string, object>)root["daily"];
        var hourly = (Dictionary<string, object>)root["hourly"];
        var d = new Data
        {
            Temp = D(cur["temperature_2m"]),
            Code = Convert.ToInt32(cur["weather_code"]),
            Day = Convert.ToInt32(cur["is_day"]) == 1,
            Max = D(((object[])daily["temperature_2m_max"])[0]),
            Min = D(((object[])daily["temperature_2m_min"])[0]),
            Fetched = DateTime.Now
        };
        var times = (object[])hourly["time"];
        var temps = (object[])hourly["temperature_2m"];
        var codes = (object[])hourly["weather_code"];
        var days = (object[])hourly["is_day"];
        for (int i = 0; i < times.Length; i++)
        {
            var t = DateTime.ParseExact((string)times[i], "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
            d.Hours.Add(new Hour { Time = t, Temp = D(temps[i]), Code = Convert.ToInt32(codes[i]), Day = Convert.ToInt32(days[i]) == 1 });
        }
        return d;
    }

    // city name -> coordinates and a display name ("City, Country"); null if not found
    public static string Geocode(string city, out double lat, out double lon)
    {
        lat = lon = 0;
        string json = Get("https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + Uri.EscapeDataString(city));
        var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
        object results;
        if (!root.TryGetValue("results", out results)) return null;
        var arr = (object[])results;
        if (arr.Length == 0) return null;
        var r = (Dictionary<string, object>)arr[0];
        lat = D(r["latitude"]);
        lon = D(r["longitude"]);
        object country;
        return (string)r["name"] + (r.TryGetValue("country", out country) ? ", " + country : "");
    }

    public static string Describe(int code)
    {
        if (code == 0) return "Clear";
        if (code == 1) return "Mostly clear";
        if (code == 2) return "Partly cloudy";
        if (code == 3) return "Cloudy";
        if (code == 45 || code == 48) return "Fog";
        if (code >= 51 && code <= 57) return "Drizzle";
        if (code >= 61 && code <= 67) return "Rain";
        if (code >= 71 && code <= 77) return "Snow";
        if (code >= 80 && code <= 82) return "Showers";
        if (code == 85 || code == 86) return "Snow showers";
        if (code >= 95) return "Thunderstorm";
        return "";
    }
}

static class Quotes
{
    static readonly string[][] All =
    {
        new[] { "Make something people want.", "Paul Graham" },
        new[] { "The best way to predict the future is to invent it.", "Alan Kay" },
        new[] { "Done is better than perfect.", "Sheryl Sandberg" },
        new[] { "Stay hungry. Stay foolish.", "Stewart Brand" },
        new[] { "Well done is better than well said.", "Benjamin Franklin" },
        new[] { "It always seems impossible until it's done.", "Nelson Mandela" },
        new[] { "What gets measured gets managed.", "Peter Drucker" },
        new[] { "If you're not embarrassed by the first version of your product, you've launched too late.", "Reid Hoffman" },
        new[] { "Simplicity is the ultimate sophistication.", "Leonardo da Vinci" },
        new[] { "The secret of getting ahead is getting started.", "Mark Twain" },
        new[] { "Ideas are easy. Implementation is hard.", "Guy Kawasaki" },
        new[] { "Focusing is about saying no.", "Steve Jobs" },
    };

    public static string[] Today() { return All[DateTime.Now.DayOfYear % All.Length]; }
}

// ---------- the lock screen ----------
class CoverForm : Form
{
    readonly LockSession session;
    readonly bool primary;
    Bitmap layer;
    string layerKey = "";
    readonly List<char> input = new List<char>();
    bool keyMode;
    string message = "";
    readonly System.Windows.Forms.Timer shakeTimer = new System.Windows.Forms.Timer { Interval = 28 };
    static readonly int[] ShakePattern = { -14, 12, -10, 8, -5, 3, 0 };
    int shakeStep, shake, lastSecond = -1;
    bool hoverLink, hoverTheme, hoverArrow;

    // layout
    RectangleF bar, rWeather, rCalendar, rNote, rTimer, rBattery, rQuote, rDate, rClock, rAvatar, rName, rPill, rArrow, rMsg, rLink, rTheme, rLogin;

    // primary: the cover with the clock and the password box (exactly one per lock)
    public CoverForm(LockSession s, Screen screen, bool primary)
    {
        session = s;
        this.primary = primary;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        ControlBox = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Wallpaper.Average(Config.Dark);
        shakeTimer.Tick += (a, e) => ShakeTick();
        video = s.Video;
        UpdateTitle();
        if (primary)
        {
            Weather.Updated += OnWeather;
            Weather.RefreshIfStale();
        }
        if (video != null) video.FrameReady += OnFrame;
        // never block sign-out / shutdown (Ctrl+Alt+Del is the emergency exit)
        FormClosing += (a, e) => { if (!session.Closing && e.CloseReason != CloseReason.WindowsShutDown) e.Cancel = true; };
        FormClosed += (a, e) =>
        {
            Weather.Updated -= OnWeather;
            if (video != null) video.FrameReady -= OnFrame;
            foreach (var hn in powerNotifications) Native.UnregisterPowerSettingNotification(hn);
            if (layer != null) layer.Dispose();
            if (clock != null) clock.Dispose();
        };
        Shown += (a, e) => { Focus(); Keepalive(); };
    }

    // the primary cover listens for the lid and the display so the video only plays while it can be seen
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!primary || video == null) return;
        foreach (var g in new[] { Native.LidSwitchState, Native.ConsoleDisplayState })
        {
            var guid = g;
            var hn = Native.RegisterPowerSettingNotification(Handle, ref guid, 0);
            if (hn != IntPtr.Zero) powerNotifications.Add(hn);
        }
        UpdateTitle();
    }

    // ----- video -----
    readonly ILiveWallpaper video;
    ClockGlass clock;
    readonly List<IntPtr> powerNotifications = new List<IntPtr>();
    bool lidOpen = true, displayOn = true;
    int framePending;

    void OnWeather() { try { BeginInvoke((Action)(() => Invalidate())); } catch { } }

    // a new frame arrived (decoder thread): repaint, but never queue up more than one paint
    void OnFrame()
    {
        if (Interlocked.Exchange(ref framePending, 1) == 1) return;
        try { BeginInvoke((Action)(() => { Invalidate(); Update(); framePending = 0; })); }
        catch { framePending = 0; }
    }

    protected override void WndProc(ref Message m)
    {
        // WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE: lid opened/closed, display on/off/dimmed
        if (m.Msg == 0x218 && (long)m.WParam == 0x8013 && m.LParam != IntPtr.Zero)
        {
            var which = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
            int data = Marshal.ReadInt32(m.LParam, 20);
            if (which == Native.LidSwitchState) lidOpen = data != 0;
            else if (which == Native.ConsoleDisplayState) displayOn = data != 0;
            if (video != null) video.Paused = !(lidOpen && displayOn);
            UpdateTitle();
        }
        base.WndProc(ref m);
    }

    // the parts of the overlay that hold anything (widgets with their shadows, the bar, date, avatar and name)
    IEnumerable<Rectangle> OverlayAreas()
    {
        int pad = S(16);
        yield return Rectangle.Ceiling(RectangleF.Inflate(rDate, pad, pad));
        if (!primary) yield break;
        yield return Rectangle.Ceiling(bar);
        foreach (var r in new[] { rWeather, rCalendar, rNote, rTimer, rBattery, rQuote })
            yield return Rectangle.Ceiling(RectangleF.FromLTRB(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad + S(8)));
        yield return Rectangle.Ceiling(RectangleF.Inflate(RectangleF.Union(rAvatar, rName), pad, pad));
    }

    // where a screen pixel lands on the video frame (cover the screen, keep the aspect ratio)
    void Mapping(Frame f, out float scale, out float ox, out float oy)
    {
        scale = Math.Max((float)ClientSize.Width / f.W, (float)ClientSize.Height / f.H);
        ox = (ClientSize.Width - f.W * scale) / 2;
        oy = (ClientSize.Height - f.H * scale) / 2;
    }

    // frosted glass from the live video: its small blurred copy, stretched back over the screen
    static TextureBrush Frost(Frame small, float scale, float ox, float oy)
    {
        var tb = new TextureBrush(small.Bmp, WrapMode.TileFlipXY);
        tb.ScaleTransform(VideoWallpaper.Down * scale, VideoWallpaper.Down * scale);
        tb.TranslateTransform(ox, oy, MatrixOrder.Append);
        return tb;
    }

    void FrostPanel(Graphics g, TextureBrush tb, RectangleF r, float radius)
    {
        using (var p = Ui.Round(r, radius)) g.FillPath(tb, p);
    }

    static int S(float px) { return Ui.S(px); }
    static Font Fnt(string family, float pt, FontStyle st = FontStyle.Regular) { return new Font(Glass.Family(family, "Segoe UI"), pt, st); }

    // ink colours: dark plum text on light glass, white on dark glass
    static Color Ink(int a = 255) { return Config.Dark ? Color.FromArgb(a, 244, 242, 252) : Color.FromArgb(a, 46, 30, 82); }
    static readonly Color White = Color.FromArgb(250, 250, 255);

    static string DisplayName()
    {
        if (Config.Name.Length > 0) return Config.Name;
        string n = Environment.UserName;
        return n.Length == 0 ? "Locked" : char.ToUpper(n[0]) + n.Substring(1);
    }

    void ComputeLayout()
    {
        float W = ClientSize.Width, H = ClientSize.Height;
        if (!primary)
        {
            rDate = new RectangleF(0, H * 0.14f, W, S(44));
            rClock = new RectangleF(W * 0.15f, rDate.Bottom + S(8), W * 0.7f, H * 0.55f);
            return;
        }
        bar = new RectangleF(0, 0, W, S(30));
        float m = S(36), gap = S(16), top = bar.Bottom + S(24);
        float col = Math.Max(S(250), Math.Min(S(350), W * 0.19f));
        float rx = W - m - col;
        rWeather = new RectangleF(m, top, col, S(204));
        rCalendar = new RectangleF(m, rWeather.Bottom + gap, col, S(236));
        rNote = new RectangleF(m, rCalendar.Bottom + gap, col, S(132));
        rTimer = new RectangleF(rx, top, col, S(204));
        rBattery = new RectangleF(rx, rTimer.Bottom + gap, col, S(132));
        rQuote = new RectangleF(rx, rBattery.Bottom + gap, col, S(236));

        float cl = m + col + S(40), cr = W - m - col - S(40);
        rDate = new RectangleF(cl, top - S(2), cr - cl, S(44));
        rClock = new RectangleF(cl, rDate.Bottom + S(6), cr - cl, H * 0.5f);

        float cx = W / 2;
        rAvatar = new RectangleF(cx - S(34), H - S(262), S(68), S(68));
        rName = new RectangleF(cx - S(200), rAvatar.Bottom + S(10), S(400), S(28));
        rPill = new RectangleF(cx - S(140), rName.Bottom + S(14), S(280), S(40));
        rArrow = new RectangleF(rPill.Right - S(36), rPill.Y + S(6), S(28), S(28));
        rMsg = new RectangleF(cx - S(320), rPill.Bottom + S(12), S(640), S(22));
        rLink = new RectangleF(cx - S(170), rMsg.Bottom + S(2), S(340), S(22));
        rLogin = RectangleF.FromLTRB(cx - S(340), rPill.Y - S(4), cx + S(340), rLink.Bottom + S(4));
    }

    // static content (wallpaper, clock, widgets) is cached and rebuilt when the minute, theme or weather changes
    void EnsureLayer()
    {
        var now = DateTime.Now;
        string key = ClientSize + "|" + now.ToString("yyyyMMddHHmm") + "|" + Config.Dark + "|" + Weather.Stamp + "|" + (video != null ? video.Place : "");
        if (key == layerKey && layer != null) return;
        layerKey = key;
        ComputeLayout();
        if (layer == null || layer.Size != ClientSize)
        {
            if (layer != null) layer.Dispose();
            layer = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), PixelFormat.Format32bppPArgb);
        }
        // the glass clock's shape and lighting for this minute
        if (clock != null) clock.Dispose();
        RectangleF cb;
        using (var path = Glass.ClockPath(now.ToString("HH:mm"), rClock, out cb)) clock = new ClockGlass(path, ClientSize);

        Bitmap blurred = null;
        using (var g = Graphics.FromImage(layer))
        {
            if (video != null) g.Clear(Color.Transparent);   // the video plays underneath; this layer only holds what sits on top
            else
            {
                var wp = Wallpaper.Get(ClientSize);
                blurred = wp[1];
                g.DrawImageUnscaled(wp[0], 0, 0);
                var fr = Wallpaper.Frames(ClientSize);
                clock.Render(fr[0], fr[1], VideoWallpaper.Down, 1f, 0f, 0f);
                g.DrawImageUnscaled(clock.Bitmap, clock.R.X, clock.R.Y);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Shadowed(g, now.ToString("ddd d MMM", CultureInfo.GetCultureInfo("en-GB")), Fnt("Segoe UI Variable Display Semib", 21), White, rDate, StringAlignment.Center);
            if (!primary) return;
            DrawBar(g, blurred, now);
            DrawWeather(g, blurred, now);
            DrawCalendar(g, blurred, now);
            DrawNote(g, blurred);
            Glass.Panel(g, blurred, rTimer, S(22));
            DrawBattery(g, blurred);
            DrawQuote(g, blurred);
            DrawAvatar(g, blurred);
        }
    }

    // ----- text helpers -----
    static void Txt(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Near, bool wrap = false)
    {
        using (var sf = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter })
        using (var b = new SolidBrush(c))
        {
            if (!wrap) sf.FormatFlags |= StringFormatFlags.NoWrap;
            g.DrawString(s, f, b, r, sf);
        }
        f.Dispose();
    }

    static void Shadowed(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h)
    {
        var sr = r;
        sr.Offset(0, Math.Max(1, Ui.Scale * 1.5f));
        using (var f2 = (Font)f.Clone()) Txt(g, s, f2, Color.FromArgb(Config.Dark ? 120 : 70, 30, 10, 60), sr, h, StringAlignment.Center);
        Txt(g, s, f, c, r, h, StringAlignment.Center);
    }

    static RectangleF Inner(RectangleF r) { return RectangleF.Inflate(r, -S(18), -S(16)); }

    // ----- widgets -----
    void DrawBar(Graphics g, Bitmap blurred, DateTime now)
    {
        Glass.Panel(g, blurred, bar, 0, false);
        float y = bar.Y, h = bar.Height, x = S(18);
        Icons.Padlock(g, new RectangleF(x, y + h / 2 - S(7), S(12), S(14)), Ink());
        Txt(g, "LidGuard", Fnt("Segoe UI Variable Text Semibold", 10), Ink(), new RectangleF(x + S(20), y, S(120), h), StringAlignment.Near, StringAlignment.Center);
        Txt(g, "Locked" + (video != null && video.Place != null ? "   ·   Now: " + video.Place : ""), Fnt("Segoe UI Variable Text", 10), Ink(150), new RectangleF(x + S(92), y, S(320), h), StringAlignment.Near, StringAlignment.Center);

        float rx = bar.Right - S(18);
        string clock = now.ToString("ddd d MMM  HH:mm", CultureInfo.GetCultureInfo("en-GB"));
        var cf = Fnt("Segoe UI Variable Text Semibold", 10);
        float cw = g.MeasureString(clock, cf).Width;
        Txt(g, clock, cf, Ink(), new RectangleF(rx - cw, y, cw + S(4), h), StringAlignment.Far, StringAlignment.Center);
        rx -= cw + S(18);

        rTheme = new RectangleF(rx - S(82), y + S(4), S(82), h - S(8));
        using (var p = Ui.Round(rTheme, rTheme.Height / 2))
        using (var b = new SolidBrush(Color.FromArgb(hoverTheme ? 70 : 40, 255, 255, 255))) g.FillPath(b, p);
        Txt(g, Config.Dark ? "Light mode" : "Dark mode", Fnt("Segoe UI Variable Text", 9), Ink(), rTheme, StringAlignment.Center, StringAlignment.Center);
        rx = rTheme.X - S(16);

        var ps = SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
        {
            string pct = (int)Math.Round(ps.BatteryLifePercent * 100) + "%";
            var pf = Fnt("Segoe UI Variable Text", 9.5f);
            float pw = g.MeasureString(pct, pf).Width;
            Txt(g, pct, pf, Ink(), new RectangleF(rx - pw, y, pw + S(2), h), StringAlignment.Far, StringAlignment.Center);
            rx -= pw + S(6);
            Icons.Battery(g, new RectangleF(rx - S(24), y + h / 2 - S(6), S(24), S(12)), ps.BatteryLifePercent, ps.PowerLineStatus == PowerLineStatus.Online, Ink());
            rx -= S(24) + S(16);
        }
        rx = Dot(g, rx, y, h, "Silenced", Theme.Current.Accent);
        Dot(g, rx, y, h, "Awake", Color.FromArgb(74, 222, 128));
    }

    float Dot(Graphics g, float right, float y, float h, string text, Color c)
    {
        var f = Fnt("Segoe UI Variable Text", 9.5f);
        float w = g.MeasureString(text, f).Width;
        Txt(g, text, f, Ink(), new RectangleF(right - w, y, w + S(2), h), StringAlignment.Far, StringAlignment.Center);
        float d = S(7);
        using (var b = new SolidBrush(c)) g.FillEllipse(b, right - w - S(6) - d, y + (h - d) / 2, d, d);
        return right - w - S(6) - d - S(16);
    }

    void DrawWeather(Graphics g, Bitmap blurred, DateTime now)
    {
        Glass.Panel(g, blurred, rWeather, S(22));
        var r = Inner(rWeather);
        int hr = now.Hour;
        string greet = hr < 5 ? "Good night" : hr < 12 ? "Good morning" : hr < 17 ? "Good afternoon" : hr < 21 ? "Good evening" : "Good night";
        Txt(g, greet + ", " + DisplayName(), Fnt("Segoe UI Variable Text Semibold", 11.5f), Ink(), new RectangleF(r.X, r.Y, r.Width, S(22)));
        Txt(g, Config.City.Length > 0 ? Config.City : "Weather", Fnt("Segoe UI Variable Text", 9.5f), Ink(160), new RectangleF(r.X, r.Y + S(22), r.Width, S(18)));
        var w = Weather.Current;
        if (w == null)
        {
            Txt(g, Config.HasPlace ? "Updating weather..." : "Set your city in LidGuard to see the weather.", Fnt("Segoe UI Variable Text", 10), Ink(170),
                new RectangleF(r.X, r.Y + S(56), r.Width, S(60)), StringAlignment.Near, StringAlignment.Near, true);
            return;
        }
        Txt(g, Math.Round(w.Temp) + "\u00B0", Fnt("Segoe UI Variable Display Light", 34), Ink(), new RectangleF(r.X - S(4), r.Y + S(40), S(140), S(60)));
        Icons.Weather(g, new RectangleF(r.Right - S(58), r.Y + S(44), S(54), S(50)), w.Code, w.Day);
        Txt(g, Weather.Describe(w.Code) + "   H " + Math.Round(w.Max) + "\u00B0  L " + Math.Round(w.Min) + "\u00B0", Fnt("Segoe UI Variable Text", 9.5f), Ink(180),
            new RectangleF(r.X, r.Y + S(100), r.Width, S(18)));

        // next five hours
        var next = w.Hours.FindAll(x => x.Time > now.AddMinutes(-30));
        float cw = r.Width / 5, y = r.Y + S(126);
        for (int i = 0; i < 5 && i < next.Count; i++)
        {
            var h = next[i];
            var col = new RectangleF(r.X + i * cw, y, cw, S(18));
            Txt(g, i == 0 ? "Now" : h.Time.ToString("HH"), Fnt("Segoe UI Variable Text", 8.5f), Ink(160), col, StringAlignment.Center);
            Icons.Weather(g, new RectangleF(col.X + cw / 2 - S(10), y + S(19), S(20), S(18)), h.Code, h.Day);
            Txt(g, Math.Round(h.Temp) + "\u00B0", Fnt("Segoe UI Variable Text Semibold", 9), Ink(), new RectangleF(col.X, y + S(39), cw, S(18)), StringAlignment.Center);
        }
    }

    void DrawCalendar(Graphics g, Bitmap blurred, DateTime now)
    {
        Glass.Panel(g, blurred, rCalendar, S(22));
        var r = Inner(rCalendar);
        Color red = Color.FromArgb(255, 92, 92);
        Txt(g, now.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant(), Fnt("Segoe UI Variable Text Semibold", 9.5f), red, new RectangleF(r.X, r.Y, r.Width, S(18)));
        Txt(g, now.Year.ToString(), Fnt("Segoe UI Variable Text", 9.5f), Ink(150), new RectangleF(r.X, r.Y, r.Width, S(18)), StringAlignment.Far);
        string[] wd = { "M", "T", "W", "T", "F", "S", "S" };
        float cw = r.Width / 7, gy = r.Y + S(28), rh = S(27);
        for (int i = 0; i < 7; i++)
            Txt(g, wd[i], Fnt("Segoe UI Variable Text Semibold", 8.5f), Ink(130), new RectangleF(r.X + i * cw, gy, cw, S(18)), StringAlignment.Center);
        var first = new DateTime(now.Year, now.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7;   // Monday first
        int days = DateTime.DaysInMonth(now.Year, now.Month);
        for (int d = 1; d <= days; d++)
        {
            int idx = offset + d - 1, row = idx / 7, colI = idx % 7;
            var cell = new RectangleF(r.X + colI * cw, gy + S(22) + row * rh, cw, rh);
            bool today = d == now.Day;
            if (today)
            {
                float dd = S(24);
                using (var b = new SolidBrush(red)) g.FillEllipse(b, cell.X + (cw - dd) / 2, cell.Y + (rh - dd) / 2, dd, dd);
            }
            Txt(g, d.ToString(), Fnt(today ? "Segoe UI Variable Text Semibold" : "Segoe UI Variable Text", 9.5f), today ? Color.White : Ink(colI >= 5 ? 150 : 230),
                cell, StringAlignment.Center, StringAlignment.Center);
        }
    }

    void DrawNote(Graphics g, Bitmap blurred)
    {
        Glass.Panel(g, blurred, rNote, S(22));
        var r = Inner(rNote);
        using (var b = new SolidBrush(Color.FromArgb(255, 204, 64))) g.FillEllipse(b, r.X, r.Y + S(5), S(8), S(8));
        Txt(g, "NOTE", Fnt("Segoe UI Variable Text Semibold", 9), Ink(150), new RectangleF(r.X + S(14), r.Y, r.Width, S(18)));
        string note = Config.Note.Length > 0 ? Config.Note : "Back soon";
        Txt(g, note, Fnt("Segoe UI Variable Display Semib", 14.5f), Ink(), new RectangleF(r.X, r.Y + S(24), r.Width, S(54)), StringAlignment.Near, StringAlignment.Near, true);
        Txt(g, "- " + DisplayName(), Fnt("Segoe UI Variable Text", 9.5f), Ink(150), new RectangleF(r.X, r.Bottom - S(18), r.Width, S(18)));
    }

    void DrawBattery(Graphics g, Bitmap blurred)
    {
        Glass.Panel(g, blurred, rBattery, S(22));
        var r = Inner(rBattery);
        var ps = SystemInformation.PowerStatus;
        bool hasBattery = ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
        float level = hasBattery ? ps.BatteryLifePercent : 1f;
        bool charging = ps.PowerLineStatus == PowerLineStatus.Online;
        float d = S(66);
        var ring = new RectangleF(r.X + S(2), r.Y + (r.Height - d) / 2, d, d);
        Color fill = level < 0.2f && !charging ? Color.FromArgb(255, 96, 96) : Color.FromArgb(74, 222, 128);
        Ring(g, ring, level, fill, S(6));
        Txt(g, hasBattery ? (int)Math.Round(level * 100) + "%" : "AC", Fnt("Segoe UI Variable Text Semibold", 11), Ink(), ring, StringAlignment.Center, StringAlignment.Center);
        float tx = ring.Right + S(16);
        var tr = new RectangleF(tx, r.Y + S(4), r.Right - tx, S(20));
        Txt(g, "Battery", Fnt("Segoe UI Variable Text Semibold", 11), Ink(), tr);
        string sub = !hasBattery ? "Plugged in" : charging ? "Charging" : ps.BatteryLifeRemaining > 0 ? FormatSpan(TimeSpan.FromSeconds(ps.BatteryLifeRemaining)) + " left" : "On battery";
        tr.Offset(0, S(21));
        Txt(g, sub, Fnt("Segoe UI Variable Text", 9.5f), Ink(160), tr);
        tr.Offset(0, S(26));
        Txt(g, "Lid closed: keeps running", Fnt("Segoe UI Variable Text", 9.5f), Ink(200), tr);
    }

    void DrawQuote(Graphics g, Bitmap blurred)
    {
        Glass.Panel(g, blurred, rQuote, S(22));
        var r = Inner(rQuote);
        Txt(g, "TODAY'S THOUGHT", Fnt("Segoe UI Variable Text Semibold", 9), Ink(150), new RectangleF(r.X, r.Y, r.Width, S(18)));
        var q = Quotes.Today();
        Txt(g, "\u201C", Fnt("Georgia", 46), Ink(80), new RectangleF(r.X - S(4), r.Y + S(12), S(60), S(70)));
        Txt(g, q[0], Fnt("Segoe UI Variable Display Semib", 18), Ink(), new RectangleF(r.X, r.Y + S(64), r.Width, r.Height - S(96)), StringAlignment.Near, StringAlignment.Near, true);
        Txt(g, "- " + q[1], Fnt("Segoe UI Variable Text", 10), Ink(160), new RectangleF(r.X, r.Bottom - S(20), r.Width, S(20)));
    }

    void DrawAvatar(Graphics g, Bitmap blurred)
    {
        using (var p = new GraphicsPath())
        {
            p.AddEllipse(rAvatar);
            for (int i = 1; i <= 5; i++)
                using (var b = new SolidBrush(Color.FromArgb(10, 30, 10, 60)))
                    g.FillEllipse(b, RectangleF.Inflate(new RectangleF(rAvatar.X, rAvatar.Y + S(4), rAvatar.Width, rAvatar.Height), i * S(1.5f), i * S(1.5f)));
            var st = g.Save();
            g.SetClip(p);
            if (blurred != null) g.DrawImage(blurred, rAvatar, rAvatar, GraphicsUnit.Pixel);
            using (var b = new LinearGradientBrush(rAvatar, Color.FromArgb(150, 255, 255, 255), Color.FromArgb(70, 255, 255, 255), 90f)) g.FillRectangle(b, rAvatar);
            g.Restore(st);
            using (var pen = new Pen(Color.FromArgb(170, 255, 255, 255), Math.Max(1f, Ui.Scale))) g.DrawPath(pen, p);
        }
        string name = DisplayName();
        Txt(g, name.Substring(0, 1).ToUpperInvariant(), Fnt("Segoe UI Variable Display Semib", 25), Color.FromArgb(220, 60, 40, 110), rAvatar, StringAlignment.Center, StringAlignment.Center);
        Shadowed(g, name, Fnt("Segoe UI Variable Text Semibold", 13), White, rName, StringAlignment.Center);
    }

    static void Ring(Graphics g, RectangleF r, float progress, Color c, float width)
    {
        var rr = RectangleF.Inflate(r, -width / 2, -width / 2);
        using (var pen = new Pen(Color.FromArgb(Config.Dark ? 50 : 70, 255, 255, 255), width)) g.DrawEllipse(pen, rr);
        using (var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, rr, -90, Math.Max(1f, 360f * Math.Min(1f, progress)));
    }

    static string FormatSpan(TimeSpan t)
    {
        if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
        return t.Minutes + "m";
    }

    // ----- live parts: away timer and the password pill -----
    void DrawTimer(Graphics g)
    {
        var r = Inner(rTimer);
        var away = DateTime.Now - session.Since;
        float d = S(128);
        var ring = new RectangleF(r.X, r.Y + (r.Height - d) / 2, d, d);
        Ring(g, ring, (float)(away.TotalSeconds % 3600 / 3600), Theme.Current.Accent, S(8));
        string big = away.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)away.TotalHours, away.Minutes, away.Seconds) : string.Format("{0:00}:{1:00}", away.Minutes, away.Seconds);
        Txt(g, big, Fnt("Segoe UI Variable Display Semib", away.TotalHours >= 1 ? 15 : 19), Ink(), new RectangleF(ring.X, ring.Y + d / 2 - S(20), d, S(30)), StringAlignment.Center, StringAlignment.Center);
        Txt(g, "AWAY", Fnt("Segoe UI Variable Text Semibold", 8), Ink(140), new RectangleF(ring.X, ring.Y + d / 2 + S(10), d, S(16)), StringAlignment.Center, StringAlignment.Center);
        float tx = ring.Right + S(18);
        var tr = new RectangleF(tx, r.Y + S(24), r.Right - tx, S(20));
        Txt(g, "Away timer", Fnt("Segoe UI Variable Text Semibold", 11), Ink(), tr);
        tr.Offset(0, S(22));
        Txt(g, "Locked at " + session.Since.ToString("HH:mm"), Fnt("Segoe UI Variable Text", 9.5f), Ink(160), tr);
        tr.Offset(0, S(34));
        Txt(g, "Apps keep running", Fnt("Segoe UI Variable Text", 9.5f), Ink(200), tr);
        tr.Offset(0, S(20));
        Txt(g, "behind this screen", Fnt("Segoe UI Variable Text", 9.5f), Ink(200), tr);
    }

    void DrawLogin(Graphics g)
    {
        var pill = rPill;
        pill.Offset(shake, 0);
        // with a video, the pill's frost was already painted from the live frame
        Glass.Panel(g, video != null ? null : Wallpaper.Get(ClientSize)[1], pill, pill.Height / 2);
        var tr = RectangleF.Inflate(pill, -S(18), 0);
        if (input.Count == 0)
        {
            Txt(g, keyMode ? "Enter Secret Key" : "Enter Password", Fnt("Segoe UI Variable Text", 10.5f), Ink(150), tr, StringAlignment.Near, StringAlignment.Center);
            if (DateTime.Now.Millisecond < 500 && ContainsFocus)
                using (var pen = new Pen(Ink(200), Math.Max(1f, Ui.Scale * 1.2f))) g.DrawLine(pen, tr.X - S(3), pill.Y + S(11), tr.X - S(3), pill.Bottom - S(11));
        }
        else
        {
            float dd = S(7), step = S(12);
            int max = (int)((tr.Width - S(36)) / step);
            int n = Math.Min(input.Count, max);
            using (var b = new SolidBrush(Ink()))
                for (int i = 0; i < n; i++) g.FillEllipse(b, tr.X + i * step, pill.Y + (pill.Height - dd) / 2, dd, dd);
            var ar = rArrow;
            ar.Offset(shake, 0);
            using (var b = new SolidBrush(Color.FromArgb(hoverArrow ? 230 : 190, Theme.Current.Accent))) g.FillEllipse(b, ar);
            using (var pen = new Pen(Color.White, Math.Max(1.6f, Ui.Scale * 1.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = ar.X + ar.Width / 2, cy = ar.Y + ar.Height / 2, s = ar.Width * 0.2f;
                g.DrawLine(pen, cx - s * 1.2f, cy, cx + s * 1.1f, cy);
                g.DrawLines(pen, new[] { new PointF(cx, cy - s), new PointF(cx + s * 1.1f, cy), new PointF(cx, cy + s) });
            }
        }

        string sub = message;
        if (sub.Length == 0 && Control.IsKeyLocked(Keys.CapsLock)) sub = "Caps Lock is on";
        if (sub.Length > 0)
            Shadowed(g, sub, Fnt("Segoe UI Variable Text Semibold", 10), message.Length > 0 ? Color.FromArgb(255, 214, 222) : White, rMsg, StringAlignment.Center);
        if (session.KeyAvailable)
        {
            string link = keyMode ? "Use password instead (Tab)" : "Forgot password? Use your secret key (Tab)";
            var lf = Fnt("Segoe UI Variable Text", 10, hoverLink ? FontStyle.Underline : FontStyle.Regular);
            Shadowed(g, link, lf, White, rLink, StringAlignment.Center);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureLayer();
        var g = e.Graphics;
        if (video != null)
        {
            // live background: the frame, frosted glass cut from it, and the clock refracting it
            bool drew = video.Use((frame, small) =>
            {
                float scale, ox, oy;
                Mapping(frame, out scale, out ox, out oy);
                g.InterpolationMode = InterpolationMode.Bilinear;
                if (Math.Abs(scale - 1f) < 0.001f && ox == 0 && oy == 0) g.DrawImageUnscaled(frame.Bmp, 0, 0);
                else g.DrawImage(frame.Bmp, ox, oy, frame.W * scale, frame.H * scale);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var tb = Frost(small, scale, ox, oy))
                {
                    if (primary)
                    {
                        g.FillRectangle(tb, bar);
                        foreach (var r in new[] { rWeather, rCalendar, rNote, rTimer, rBattery, rQuote }) FrostPanel(g, tb, r, S(22));
                        g.FillEllipse(tb, rAvatar);
                        var pill = rPill;
                        pill.Offset(shake, 0);
                        FrostPanel(g, tb, pill, pill.Height / 2);
                    }
                }
                clock.Render(frame, small, VideoWallpaper.Down, scale, ox, oy);
                g.DrawImageUnscaled(clock.Bitmap, clock.R.X, clock.R.Y);
            });
            if (!drew) using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        }
        if (video == null) g.DrawImageUnscaled(layer, 0, 0);
        else foreach (var r in OverlayAreas()) g.DrawImage(layer, r, r, GraphicsUnit.Pixel);   // the rest of the overlay is empty
        if (!primary) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        DrawTimer(g);
        DrawLogin(g);
    }

    // ----- input -----
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!primary) return base.ProcessCmdKey(ref msg, keyData);
        if (keyData == Keys.Enter) { Submit(); return true; }
        if (keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab)) { ToggleKeyMode(); return true; }
        if (keyData == Keys.Escape) { input.Clear(); if (keyMode) keyMode = false; Changed(); return true; }
        if (keyData == (Keys.Control | Keys.Back)) { input.Clear(); Changed(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        e.Handled = true;
        if (!primary) return;
        char c = e.KeyChar;
        if (c == '\b') { if (input.Count > 0) input.RemoveAt(input.Count - 1); }
        else if (!char.IsControl(c) && input.Count < 256) input.Add(c);
        else return;
        Changed();
    }

    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.CapsLock) Invalidate(Rectangle.Ceiling(rLogin)); }

    void ToggleKeyMode()
    {
        if (!session.KeyAvailable) return;
        keyMode = !keyMode;
        input.Clear();
        Changed();
    }

    void Submit()
    {
        if (input.Count == 0) return;
        string s = new string(input.ToArray());
        input.Clear();
        string err = keyMode ? session.TryKey(s) : session.TryUnlock(s);
        if (err == null) return;
        message = err;
        shakeStep = 0;
        shakeTimer.Start();
        Changed();
    }

    void Changed()
    {
        UpdateTitle();
        Invalidate(Rectangle.Ceiling(RectangleF.Inflate(rLogin, S(20), S(4))));
    }

    // the window title mirrors the lock state for screen readers (and LidGuard's own tests)
    void UpdateTitle()
    {
        Text = "LidGuard locked" + (message.Length > 0 ? " | " + message : "") +
               (session.KeyAvailable ? " | Forgot password? Use your secret key" : "") + (keyMode ? " | Secret key mode" : "") +
               (video == null ? "" : (video.Paused ? " | video paused" : " | video playing") + (video.Hardware ? " (GPU)" : "") +
                   (primary ? " | watching " + powerNotifications.Count + "/2" : ""));
    }

    void ShakeTick()
    {
        shake = S(ShakePattern[shakeStep]);
        if (++shakeStep >= ShakePattern.Length) { shakeTimer.Stop(); shake = 0; }
        Invalidate(Rectangle.Ceiling(RectangleF.Inflate(rLogin, S(20), S(4))));
    }

    // ----- mouse -----
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!primary) return;
        bool l = session.KeyAvailable && rLink.Contains(e.Location), t = rTheme.Contains(e.Location), a = input.Count > 0 && rArrow.Contains(e.Location);
        if (l != hoverLink || a != hoverArrow) { hoverLink = l; hoverArrow = a; Invalidate(Rectangle.Ceiling(RectangleF.Inflate(rLogin, S(20), S(4)))); }
        if (t != hoverTheme) { hoverTheme = t; layerKey = ""; Invalidate(Rectangle.Ceiling(bar)); }
        Cursor = l || t || a ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!primary || e.Button != MouseButtons.Left) return;
        if (session.KeyAvailable && rLink.Contains(e.Location)) ToggleKeyMode();
        else if (rTheme.Contains(e.Location)) { Config.Dark = !Config.Dark; Config.Save(); session.ApplyTheme(); }
        else if (input.Count > 0 && rArrow.Contains(e.Location)) Submit();
    }

    public void Apply()
    {
        BackColor = Wallpaper.Average(Config.Dark);
        layerKey = "";
        Invalidate();
    }

    // keep this cover above everything (even other topmost windows), focused, and the clock current
    public void Keepalive()
    {
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        var now = DateTime.Now;
        if (now.ToString("yyyyMMddHHmm") != (layerKey.Length > 0 ? layerKey.Split('|')[1] : "")) Invalidate();
        if (!primary) return;
        if (Native.GetForegroundWindow() != Handle) Native.ForceForeground(Handle);
        if (now.Second != lastSecond) { lastSecond = now.Second; Invalidate(Rectangle.Ceiling(rTimer)); }
        if (input.Count == 0) Invalidate(Rectangle.Ceiling(rPill));   // caret blink
    }

    protected override bool ShowWithoutActivation { get { return !primary; } }
}
