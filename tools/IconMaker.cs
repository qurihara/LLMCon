using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// ゲームパッドの図を描き、複数サイズ（256/48/32/16）を1つの .ico にまとめて書き出す。
public static class IconMaker
{
    static void RR(GraphicsPath p, float x, float y, float w, float h, float r)
    {
        float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
    }

    static Bitmap Pad(int size, string accentHex)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size;
            var bg = new SolidBrush(ColorTranslator.FromHtml("#14161f"));
            var accent = new SolidBrush(ColorTranslator.FromHtml(accentHex));
            var dark = new SolidBrush(ColorTranslator.FromHtml("#14161f"));
            using (var p = new GraphicsPath()) { RR(p, 0, 0, s, s, s * 0.22f); g.FillPath(bg, p); }
            float bw = s * 0.66f, bh = s * 0.40f, bx = (s - bw) / 2, by = (s - bh) / 2;
            using (var p = new GraphicsPath()) { RR(p, bx, by, bw, bh, bh * 0.45f); g.FillPath(accent, p); }
            g.FillEllipse(accent, bx - s * 0.05f, by + bh * 0.12f, s * 0.20f, bh * 0.95f);
            g.FillEllipse(accent, bx + bw - s * 0.15f, by + bh * 0.12f, s * 0.20f, bh * 0.95f);
            float cx = bx + bw * 0.24f, cy = s / 2, arm = s * 0.05f, len = s * 0.075f;
            g.FillRectangle(dark, cx - len, cy - arm, len * 2, arm * 2);
            g.FillRectangle(dark, cx - arm, cy - len, arm * 2, len * 2);
            float r2 = s * 0.05f;
            g.FillEllipse(dark, bx + bw * 0.66f - r2, cy - s * 0.085f - r2, r2 * 2, r2 * 2);
            g.FillEllipse(dark, bx + bw * 0.80f - r2, cy + s * 0.02f - r2, r2 * 2, r2 * 2);
            bg.Dispose(); accent.Dispose(); dark.Dispose();
        }
        return bmp;
    }

    public static void Write(string path, string accentHex)
    {
        int[] sizes = { 256, 48, 32, 16 };
        var pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (var b = Pad(sizes[i], accentHex)) { var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); pngs[i] = ms.ToArray(); }
        }
        using (var fs = new FileStream(path, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int off = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int sz = sizes[i]; byte b = (byte)(sz >= 256 ? 0 : sz);
                w.Write(b); w.Write(b); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)pngs[i].Length); w.Write((uint)off); off += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }
}
