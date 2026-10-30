// Draws the LidGuard icon and writes a multi-size .ico (32-bit BMP frames + a PNG 256px frame).
// Usage: IconGen.exe <out.ico> [preview.png]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconGen
{
    static readonly Color Top = Color.FromArgb(120, 155, 255), Bottom = Color.FromArgb(58, 92, 225);

    static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var frames = new List<byte[]>();
        foreach (int n in sizes)
            using (var b = Draw(n)) frames.Add(n == 256 ? Png(b) : Dib(b));

        using (var w = new BinaryWriter(File.Create(args[0])))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length); w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) w.Write(f);
        }
        if (args.Length > 1) using (var b = Draw(256)) b.Save(args[1], ImageFormat.Png);
    }

    static Bitmap Draw(int n)
    {
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            using (var bg = RoundRect(0, 0, n, n, n * 0.22f))
            using (var brush = new LinearGradientBrush(new RectangleF(0, 0, n, n), Top, Bottom, 90f))
                g.FillPath(brush, bg);

            // shackle
            using (var pen = new Pen(Color.White, Math.Max(1.6f, n * 0.09f)))
            {
                float x = n * 0.33f, y = n * 0.17f, w = n * 0.34f;
                g.DrawArc(pen, x, y, w, w, 180, 180);
                g.DrawLine(pen, x, y + w / 2, x, n * 0.47f);
                g.DrawLine(pen, x + w, y + w / 2, x + w, n * 0.47f);
            }

            // body
            using (var body = RoundRect(n * 0.23f, n * 0.44f, n * 0.54f, n * 0.38f, n * 0.08f))
                g.FillPath(Brushes.White, body);

            // keyhole (skipped at tiny sizes where it turns to mush)
            if (n >= 24)
                using (var kh = new SolidBrush(Bottom))
                {
                    float r = n * 0.055f, cx = n * 0.5f, cy = n * 0.59f;
                    g.FillEllipse(kh, cx - r, cy - r, r * 2, r * 2);
                    g.FillRectangle(kh, cx - n * 0.0225f, cy, n * 0.045f, n * 0.11f);
                }
        }
        return bmp;
    }

    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static byte[] Png(Bitmap b)
    {
        using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    // classic ICO frame: BITMAPINFOHEADER, bottom-up BGRA pixels, then an all-zero AND mask
    static byte[] Dib(Bitmap b)
    {
        int n = b.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--)
                for (int x = 0; x < n; x++)
                {
                    var c = b.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            w.Write(new byte[((n + 31) / 32) * 4 * n]);
            w.Flush();
            return ms.ToArray();
        }
    }
}
