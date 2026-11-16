using System;
using System.IO;
using System.IO.Compression;

public static class SctCredit
{
    class Ovl { public int W, H, X, Y, w, h; public byte[] px; }

    static Ovl Load(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 28 || d[0] != 'O' || d[1] != 'V' || d[2] != 'L' || d[3] != '1') throw new Exception("overlay format");
        Ovl o = new Ovl();
        o.W = BitConverter.ToInt32(d, 4); o.H = BitConverter.ToInt32(d, 8);
        o.X = BitConverter.ToInt32(d, 12); o.Y = BitConverter.ToInt32(d, 16);
        o.w = BitConverter.ToInt32(d, 20); o.h = BitConverter.ToInt32(d, 24);
        o.px = new byte[o.w * o.h * 4];
        using (DeflateStream z = new DeflateStream(new MemoryStream(d, 28, d.Length - 28), CompressionMode.Decompress))
        {
            int n = 0;
            while (n < o.px.Length) { int r = z.Read(o.px, n, o.px.Length - n); if (r <= 0) throw new Exception("overlay data"); n += r; }
        }
        return o;
    }

    // overlay resampled to a lw x lh target (premultiplied area average). returns RGBA straight, full target bbox
    static void Sample(Ovl o, int lw, int lh, out int bx, out int by, out int bw, out int bh, out byte[] outPx)
    {
        double sx = (double)o.W / lw, sy = (double)o.H / lh;
        bx = Math.Max(0, (int)Math.Floor(o.X / sx)); by = Math.Max(0, (int)Math.Floor(o.Y / sy));
        int ex = Math.Min(lw, (int)Math.Ceiling((o.X + o.w) / sx)), ey = Math.Min(lh, (int)Math.Ceiling((o.Y + o.h) / sy));
        bw = Math.Max(0, ex - bx); bh = Math.Max(0, ey - by);
        outPx = new byte[bw * bh * 4];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                int x0 = (int)Math.Floor((bx + x) * sx), x1 = Math.Max(x0 + 1, (int)Math.Floor((bx + x + 1) * sx));
                int y0 = (int)Math.Floor((by + y) * sy), y1 = Math.Max(y0 + 1, (int)Math.Floor((by + y + 1) * sy));
                double r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        n++;
                        int ox = xx - o.X, oy = yy - o.Y;
                        if (ox < 0 || oy < 0 || ox >= o.w || oy >= o.h) continue;
                        int p = (oy * o.w + ox) * 4; double al = o.px[p + 3];
                        r += o.px[p] * al; g += o.px[p + 1] * al; b += o.px[p + 2] * al; a += al;
                    }
                int q = (y * bw + x) * 4;
                if (a > 0)
                {
                    outPx[q] = (byte)Math.Round(r / a); outPx[q + 1] = (byte)Math.Round(g / a); outPx[q + 2] = (byte)Math.Round(b / a);
                    outPx[q + 3] = (byte)Math.Round(a / n);
                }
            }
    }

    static int Blend(int s, int o, int a) { return (s * (255 - a) + o * a + 127) / 255; }

    static uint U32(byte[] d, int o) { return BitConverter.ToUInt32(d, o); }

    static int Shift(uint mask) { int s = 0; if (mask == 0) return -1; while ((mask & 1) == 0) { mask >>= 1; s++; } return s / 8; }

    static string Dds(byte[] d, Ovl o)
    {
        if (d.Length < 128 || U32(d, 0) != 0x20534444) return "DDS 아님";
        int H = (int)U32(d, 12), W = (int)U32(d, 16), mips = Math.Max(1, (int)U32(d, 28));
        uint pff = U32(d, 80), fourcc = U32(d, 84); int bits = (int)U32(d, 88);
        if (fourcc != 0 || (pff & 0x40) == 0 || (bits != 32 && bits != 24)) return "지원하지 않는 형식 (압축 DDS)";
        int bpp = bits / 8, ri = Shift(U32(d, 92)), gi = Shift(U32(d, 96)), bi = Shift(U32(d, 100));
        if (ri < 0 || gi < 0 || bi < 0) return "지원하지 않는 형식";
        int off = 128, done = 0;
        for (int l = 0; l < mips; l++)
        {
            int lw = Math.Max(1, W >> l), lh = Math.Max(1, H >> l);
            if (off + lw * lh * bpp > d.Length) break;
            if (lw * 4 >= W)
            {
                int bx, by, bw, bh; byte[] px;
                Sample(o, lw, lh, out bx, out by, out bw, out bh, out px);
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        int q = (y * bw + x) * 4, a = px[q + 3]; if (a == 0) continue;
                        int p = off + ((by + y) * lw + bx + x) * bpp;
                        d[p + ri] = (byte)Blend(d[p + ri], px[q], a);
                        d[p + gi] = (byte)Blend(d[p + gi], px[q + 1], a);
                        d[p + bi] = (byte)Blend(d[p + bi], px[q + 2], a);
                    }
                done++;
            }
            off += lw * lh * bpp;
        }
        return "OK (" + W + "x" + H + ", " + done + "단계)";
    }

    static void C565(int c, int[] rgb, int o)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        rgb[o] = (r << 3) | (r >> 2); rgb[o + 1] = (g << 2) | (g >> 4); rgb[o + 2] = (b << 3) | (b >> 2);
    }

    static int To565(int r, int g, int b) { return (((r * 31 + 127) / 255) << 11) | (((g * 63 + 127) / 255) << 5) | ((b * 31 + 127) / 255); }

    static void Palette(int c0, int c1, int[] pal)
    {
        C565(c0, pal, 0); C565(c1, pal, 3);
        for (int k = 0; k < 3; k++) { pal[6 + k] = (2 * pal[k] + pal[3 + k] + 1) / 3; pal[9 + k] = (pal[k] + 2 * pal[3 + k] + 1) / 3; }
    }

    static string Gtf(byte[] d, Ovl o)
    {
        if (d.Length < 0x80) return "GTF 아님";
        int off = (d[0x10] << 24) | (d[0x11] << 16) | (d[0x12] << 8) | d[0x13];
        int fmt = d[0x18] & 0x9f, W = (d[0x20] << 8) | d[0x21], H = (d[0x22] << 8) | d[0x23];
        if (fmt != 0x88) return "지원하지 않는 형식 (DXT5 아님)";
        int bwid = (W + 3) / 4, bhei = (H + 3) / 4;
        if (off + bwid * bhei * 16 > d.Length) return "크기 오류";
        int bx, by, bw, bh; byte[] px;
        Sample(o, W, H, out bx, out by, out bw, out bh, out px);
        int[] pal = new int[12], rgb = new int[48]; int n = 0;
        for (int Y = by / 4; Y <= (by + bh - 1) / 4 && Y < bhei; Y++)
            for (int X = bx / 4; X <= (bx + bw - 1) / 4 && X < bwid; X++)
            {
                int p = off + (Y * bwid + X) * 16;
                Palette(d[p + 8] | (d[p + 9] << 8), d[p + 10] | (d[p + 11] << 8), pal);
                uint idx = U32(d, p + 12); bool hit = false;
                for (int i = 0; i < 16; i++)
                {
                    int s = (int)((idx >> (2 * i)) & 3) * 3;
                    int r = pal[s], g = pal[s + 1], b = pal[s + 2];
                    int ox = X * 4 + (i & 3) - bx, oy = Y * 4 + (i >> 2) - by;
                    if (ox >= 0 && oy >= 0 && ox < bw && oy < bh)
                    {
                        int q = (oy * bw + ox) * 4, a = px[q + 3];
                        if (a > 0) { hit = true; r = Blend(r, px[q], a); g = Blend(g, px[q + 1], a); b = Blend(b, px[q + 2], a); }
                    }
                    rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
                }
                if (!hit) continue;
                int lo = 0, hi = 0, lmin = int.MaxValue, lmax = int.MinValue;
                for (int i = 0; i < 16; i++)
                {
                    int lum = rgb[i * 3] * 2 + rgb[i * 3 + 1] * 5 + rgb[i * 3 + 2];
                    if (lum < lmin) { lmin = lum; lo = i; }
                    if (lum > lmax) { lmax = lum; hi = i; }
                }
                int c0 = To565(rgb[hi * 3], rgb[hi * 3 + 1], rgb[hi * 3 + 2]), c1 = To565(rgb[lo * 3], rgb[lo * 3 + 1], rgb[lo * 3 + 2]);
                Palette(c0, c1, pal);
                uint nidx = 0;
                for (int i = 0; i < 16; i++)
                {
                    int best = 0, bd = int.MaxValue;
                    for (int k = 0; k < 4; k++)
                    {
                        int dr = rgb[i * 3] - pal[k * 3], dg = rgb[i * 3 + 1] - pal[k * 3 + 1], db = rgb[i * 3 + 2] - pal[k * 3 + 2];
                        int dd = dr * dr * 2 + dg * dg * 4 + db * db;
                        if (dd < bd) { bd = dd; best = k; }
                    }
                    nidx |= (uint)best << (2 * i);
                }
                d[p + 8] = (byte)c0; d[p + 9] = (byte)(c0 >> 8); d[p + 10] = (byte)c1; d[p + 11] = (byte)(c1 >> 8);
                d[p + 12] = (byte)nidx; d[p + 13] = (byte)(nidx >> 8); d[p + 14] = (byte)(nidx >> 16); d[p + 15] = (byte)(nidx >> 24);
                n++;
            }
        return "OK (" + W + "x" + H + ", 블록 " + n + "개)";
    }

    // 원본(.orig)이 있으면 항상 원본에서 다시 만든다. 없으면 먼저 .orig로 보관한다.
    public static string Apply(string file, string ovl)
    {
        if (!File.Exists(file)) return "파일 없음";
        string orig = file + ".orig";
        byte[] d = File.ReadAllBytes(File.Exists(orig) ? orig : file);
        Ovl o = Load(ovl);
        string r = file.EndsWith(".gtf", StringComparison.OrdinalIgnoreCase) ? Gtf(d, o) : Dds(d, o);
        if (!r.StartsWith("OK")) return r;
        if (!File.Exists(orig)) File.Copy(file, orig);
        File.SetAttributes(file, FileAttributes.Normal);
        File.WriteAllBytes(file, d);
        return r;
    }

    public static string Restore(string file)
    {
        string orig = file + ".orig";
        if (!File.Exists(orig)) return "백업 없음";
        File.SetAttributes(file, FileAttributes.Normal);
        File.Copy(orig, file, true); File.Delete(orig);
        return "복원";
    }
}
