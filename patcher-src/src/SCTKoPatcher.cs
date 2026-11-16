// Splinter Cell Trilogy (PS3, BLUS30761) Korean patcher.
// The pack holds only differences from the original game files and the Korean text; game data itself is not shipped.
// Executables are patched from the user's own decrypted ELF files, and subtitled videos are encoded on the user's PC.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class Core
{
#if YMLPATCH
    public const bool Yml = true;
    public const string Title = "스플린터 셀 트릴로지 한글 패처 (BLUS30761) - RPCS3 게임 패치 방식";
#else
    public const bool Yml = false;
    public const string Title = "스플린터 셀 트릴로지 한글 패처 (BLUS30761) - 실행 파일 패치 방식";
#endif
    public const string Manifest = "KO_PATCH_FILES.txt";
    public static Action<string> Log = delegate { };
    public static readonly string[] SelfRel = { null, "Data_SC/sc1.ppu.self", "Data_SCPT/sc2.ppu.self", "Data_SCCT/sc3.ppu.self" };

    public static byte[] Res(string name)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) return null;
            using (var m = new MemoryStream()) { s.CopyTo(m); return m.ToArray(); }
        }
    }
    public static byte[] Gunzip(byte[] z) { using (var g = new GZipStream(new MemoryStream(z), CompressionMode.Decompress)) using (var m = new MemoryStream()) { g.CopyTo(m); return m.ToArray(); } }
    public static string Hex(byte[] h) { return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant(); }
    public static byte[] ShaB(byte[] b) { using (var h = SHA256.Create()) return h.ComputeHash(b); }
    public static string Sha(byte[] b) { return Hex(ShaB(b)); }
    public static string ShaFile(string p) { using (var h = SHA256.Create()) using (var f = File.OpenRead(p)) return Hex(h.ComputeHash(f)); }
    public static string Disk(string rel) { return rel.Replace('/', Path.DirectorySeparatorChar); }

    // Accepts the disc root, PS3_GAME or USRDIR itself.
    public static string UsrDir(string game)
    {
        if (string.IsNullOrEmpty(game) || !Directory.Exists(game)) throw new Exception("게임 폴더를 찾을 수 없습니다: " + game);
        var roots = new List<string> { game };
        try { roots.AddRange(Directory.GetDirectories(game)); } catch { }
        foreach (var r in roots)
            foreach (var c in new[] { r, Path.Combine(r, "USRDIR"), Path.Combine(r, "PS3_GAME", "USRDIR") })
                if (Directory.Exists(Path.Combine(c, "Data_SC")) && Directory.Exists(Path.Combine(c, "Data_SCCT"))) return c;
        throw new Exception("PS3_GAME\\USRDIR\\Data_SC 폴더가 없습니다. 게임 폴더(PS3_GAME이 들어 있는 폴더)를 지정해 주세요.");
    }
    public static bool IsIso(string p) { return !string.IsNullOrEmpty(p) && p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(p); }

    // RPCS3 keeps an installed copy of the game data that the game reads instead of the disc.
    public static string HddUsr(string rpcs3)
    {
        if (string.IsNullOrEmpty(rpcs3)) return null;
        if (!Directory.Exists(rpcs3)) throw new Exception("RPCS3 폴더를 찾을 수 없습니다: " + rpcs3);
        var hdd = new List<string> { Path.Combine(rpcs3, "dev_hdd0") };
        var vfs = Path.Combine(rpcs3, "config", "vfs.yml"); if (!File.Exists(vfs)) vfs = Path.Combine(rpcs3, "vfs.yml");
        if (File.Exists(vfs))
        {
            var m = Regex.Match(File.ReadAllText(vfs), @"^\s*/dev_hdd0/:\s*(.+?)\s*$", RegexOptions.Multiline);
            if (m.Success) { var v = m.Groups[1].Value.Trim('"', '\''); v = v.Replace("$(EmulatorDir)", rpcs3.TrimEnd('\\', '/') + "/"); hdd.Insert(0, v); }
        }
        foreach (var h in hdd) { var u = Path.Combine(h, "game", "BLUS30761DATA", "USRDIR"); if (Directory.Exists(u)) return u; }
        if (!File.Exists(Path.Combine(rpcs3, "rpcs3.exe"))) throw new Exception("RPCS3 폴더가 아닙니다 (rpcs3.exe 없음): " + rpcs3);
        return null;
    }
}

// ---------- binary differences ----------
static class Diff
{
    static void Var(Stream s, long v) { while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; } s.WriteByte((byte)v); }
    static long Var(byte[] b, ref int p) { long v = 0; int sh = 0; while (true) { byte x = b[p++]; v |= (long)(x & 0x7F) << sh; if (x < 0x80) return v; sh += 7; } }
    static ulong Key(byte[] b, int p) { return BitConverter.ToUInt64(b, p) * 0x9E3779B97F4A7C15UL ^ BitConverter.ToUInt64(b, p + 8); }
    static int Match(byte[] a, int i, byte[] b, int j) { int l = 0; while (i + l < a.Length && j + l < b.Length && a[i + l] == b[j + l]) l++; return l; }

    // ops: 0 = copy (source offset, length), 1 = literal (length, bytes)
    public static byte[] Make(byte[] a, byte[] b)
    {
        const int K = 16;
        var idx = new Dictionary<ulong, int>();
        for (int i = 0; i + K <= a.Length; i += K) { var k = Key(a, i); if (!idx.ContainsKey(k)) idx[k] = i; }
        var o = new MemoryStream(); int p = 0, lit = 0; long expect = 0;
        Action<int> flush = end => { if (end > lit) { o.WriteByte(1); Var(o, end - lit); o.Write(b, lit, end - lit); } };
        while (p < b.Length)
        {
            int bs = -1, bl = 0;
            if (expect >= 0 && expect < a.Length) { int l = Match(a, (int)expect, b, p); if (l >= 8) { bs = (int)expect; bl = l; } }
            if (bl < 64 && p + K <= b.Length)
            {
                int s; if (idx.TryGetValue(Key(b, p), out s)) { int l = Match(a, s, b, p); if (l >= K && l > bl) { bs = s; bl = l; } }
            }
            if (bl > 0)
            {
                while (bs > 0 && p > lit && a[bs - 1] == b[p - 1]) { bs--; p--; bl++; }
                flush(p); o.WriteByte(0); Var(o, bs); Var(o, bl); p += bl; lit = p; expect = bs + bl;
            }
            else { p++; expect++; }
        }
        flush(b.Length);
        return o.ToArray();
    }
    public static byte[] Apply(byte[] a, byte[] ops, long size)
    {
        var r = new byte[size]; int p = 0, w = 0;
        while (p < ops.Length)
        {
            int t = ops[p++];
            if (t == 0) { long s = Var(ops, ref p), l = Var(ops, ref p); Buffer.BlockCopy(a, (int)s, r, w, (int)l); w += (int)l; }
            else { int l = (int)Var(ops, ref p); Buffer.BlockCopy(ops, p, r, w, l); p += l; w += l; }
        }
        if (w != size) throw new Exception("패치 데이터 오류");
        return r;
    }
    public static byte[] Deflate(byte[] raw) { var m = new MemoryStream(); using (var d = new DeflateStream(m, CompressionLevel.Optimal, true)) d.Write(raw, 0, raw.Length); return m.ToArray(); }
    public static byte[] Inflate(byte[] z) { using (var d = new DeflateStream(new MemoryStream(z), CompressionMode.Decompress)) using (var m = new MemoryStream()) { d.CopyTo(m); return m.ToArray(); } }
}

// ---------- data pack ----------
class Entry
{
    public string Rel; public bool New; public long SrcSize, DstSize; public string SrcSha, DstSha; public byte[] Ops;
    public byte[] Build(byte[] src)
    {
        var raw = Diff.Inflate(Ops);
        var r = New ? raw : Diff.Apply(src, raw, DstSize);
        if (Core.Sha(r) != DstSha) throw new Exception("패치 결과 검증 실패: " + Rel);
        return r;
    }
}
static class Pack
{
    const string Magic = "SCKP";
    public static List<Entry> Load()
    {
        var b = Core.Res("data.pack");
        if (b == null)
        {
            var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SCT_KO_data.pack");
            if (!File.Exists(f)) throw new Exception("패치 데이터가 없습니다. build.ps1로 만든 패처를 사용해 주세요.");
            b = File.ReadAllBytes(f);
        }
        var r = new BinaryReader(new MemoryStream(b), Encoding.UTF8);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != Magic || r.ReadInt32() != 1) throw new Exception("패치 데이터 형식이 다릅니다.");
        int n = r.ReadInt32(); var l = new List<Entry>();
        for (int i = 0; i < n; i++)
        {
            var e = new Entry { Rel = r.ReadString(), New = r.ReadBoolean(), SrcSize = r.ReadInt64(), SrcSha = Core.Hex(r.ReadBytes(32)), DstSize = r.ReadInt64(), DstSha = Core.Hex(r.ReadBytes(32)) };
            e.Ops = r.ReadBytes(r.ReadInt32()); l.Add(e);
        }
        return l;
    }
    static void Save(string path, List<Entry> l)
    {
        using (var w = new BinaryWriter(File.Create(path), Encoding.UTF8))
        {
            w.Write(Encoding.ASCII.GetBytes(Magic)); w.Write(1); w.Write(l.Count);
            foreach (var e in l)
            {
                w.Write(e.Rel); w.Write(e.New); w.Write(e.SrcSize); w.Write(Hx(e.SrcSha)); w.Write(e.DstSize); w.Write(Hx(e.DstSha));
                w.Write(e.Ops.Length); w.Write(e.Ops);
            }
        }
    }
    static byte[] Hx(string h) { var b = new byte[32]; if (h != null) for (int i = 0; i < 32; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16); return b; }

    static readonly string[] SkipExt = { ".bik", ".orig", ".zip", ".7z", ".bak", ".tmp", ".log", ".self", ".elf", ".sprx", ".bat", ".cmd", ".ps1", ".exe", ".pkl", ".yml" };
    static readonly string[] NewExt = { ".int", ".u", ".tga", ".mft", ".txx", ".utx", ".ini" };
    static bool Skip(string rel)
    {
        var low = rel.ToLowerInvariant(); var name = Path.GetFileName(low);
        if (low.Contains(".bpatch") || name == Core.Manifest.ToLowerInvariant() || name.Contains("_boot")) return true;
        return SkipExt.Any(x => low.EndsWith(x));
    }
    // Compares the patched folder with the original (ISO, folder, or .orig backups next to the patched files).
    public static void Make(string orig, string patched, string output)
    {
        string pu = Core.UsrDir(patched); Iso iso = null; string ou = null;
        if (Core.IsIso(orig)) iso = new Iso(orig); else if (!string.IsNullOrEmpty(orig)) ou = Core.UsrDir(orig);
        if (ou != null && string.Equals(Path.GetFullPath(ou).TrimEnd('\\', '/'), Path.GetFullPath(pu).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        { Core.Log("원본 경로가 한글화 폴더와 같습니다. .orig 백업과 비교합니다."); ou = null; }
        bool full = iso != null || ou != null;
        Func<string, byte[]> readOrig = rel =>
        {
            if (iso != null) { var n = iso.Find("PS3_GAME/USRDIR/" + rel); if (n == null || n.Dir) return null; var m = new MemoryStream(); iso.Copy(n, m); return m.ToArray(); }
            if (ou != null) { var f = Path.Combine(ou, Core.Disk(rel)); return File.Exists(f) ? File.ReadAllBytes(f) : null; }
            return null;
        };
        var list = new List<Entry>(); var report = new StringBuilder();
        try
        {
            foreach (var f in Directory.EnumerateFiles(pu, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var rel = f.Substring(pu.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (!rel.StartsWith("Data_", StringComparison.OrdinalIgnoreCase) || Skip(rel)) continue;
                var dst = File.ReadAllBytes(f);
                var src = readOrig(rel);
                if (src == null && File.Exists(f + ".orig")) src = File.ReadAllBytes(f + ".orig");
                if (src == null)
                {
                    // without an original to compare with, only the known new subtitle tables are taken
                    if (!full && !File.Exists(f + ".orig") && !Path.GetFileName(rel).Equals("CineSub.int", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!NewExt.Any(x => rel.EndsWith(x, StringComparison.OrdinalIgnoreCase))) { report.AppendLine("제외(새 파일, 형식 미허용): " + rel); continue; }
                    list.Add(new Entry { Rel = rel, New = true, DstSize = dst.Length, DstSha = Core.Sha(dst), Ops = Diff.Deflate(dst) });
                    report.AppendLine("추가  " + rel + "  " + dst.Length); Core.Log("추가: " + rel); continue;
                }
                if (src.Length == dst.Length && src.SequenceEqual(dst)) continue;
                var e = new Entry { Rel = rel, SrcSize = src.Length, SrcSha = Core.Sha(src), DstSize = dst.Length, DstSha = Core.Sha(dst), Ops = Diff.Deflate(Diff.Make(src, dst)) };
                if (e.Build(src) == null) throw new Exception("검증 실패");
                list.Add(e); report.AppendLine("변경  " + rel + "  " + src.Length + " -> " + dst.Length + "  패치 " + e.Ops.Length); Core.Log("변경: " + rel + " (" + e.Ops.Length + " bytes)");
            }
        }
        finally { if (iso != null) iso.Dispose(); }
        if (list.Count == 0) throw new Exception("차이가 있는 파일을 찾지 못했습니다.");
        Save(output, list);
        File.WriteAllText(output + ".txt", report.ToString(), new UTF8Encoding(false));
        Core.Log("패치 데이터 저장: " + output + " (" + list.Count + "개 파일, " + new FileInfo(output).Length + " bytes)");
    }
}

// ---------- executables ----------
static class Exe
{
    public class Patch { public int Game; public string SrcSha, DstSha; public int Size; public List<KeyValuePair<int, byte[]>> Ranges = new List<KeyValuePair<int, byte[]>>(); }
    static List<Patch> all;
    public static List<Patch> All()
    {
        if (all != null) return all;
        var b = Core.Res("elfpatch.bin"); var r = new BinaryReader(new MemoryStream(b)); r.ReadBytes(4); all = new List<Patch>();
        while (r.BaseStream.Position < b.Length)
        {
            var p = new Patch { Game = r.ReadByte(), SrcSha = Core.Hex(r.ReadBytes(32)), DstSha = Core.Hex(r.ReadBytes(32)), Size = r.ReadInt32() };
            int n = r.ReadInt32(); for (int i = 0; i < n; i++) { int o = r.ReadInt32(), l = r.ReadInt32(); p.Ranges.Add(new KeyValuePair<int, byte[]>(o, r.ReadBytes(l))); }
            all.Add(p);
        }
        return all;
    }
    static ulong U64(byte[] b, int o) { ulong v = 0; for (int i = 0; i < 8; i++) v = v << 8 | b[o + i]; return v; }
    static uint U32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
    static void P64(byte[] b, int o, ulong v) { for (int i = 7; i >= 0; i--) { b[o + i] = (byte)v; v >>= 8; } }
    static void P32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static int U16(byte[] b, int o) { return b[o] << 8 | b[o + 1]; }

    // Returns the patched ELF for game n from an original decrypted ELF (or one already patched).
    public static byte[] PatchElf(int n, byte[] elf)
    {
        var p = All().First(x => x.Game == n); var s = Core.Sha(elf);
        if (s == p.DstSha) return elf;
        if (s != p.SrcSha) return null;
        var b = (byte[])elf.Clone(); foreach (var r in p.Ranges) r.Value.CopyTo(b, r.Key);
        if (Core.Sha(b) != p.DstSha) throw new Exception("실행 파일 패치 검증 실패");
        return b;
    }
    // A SELF made by this tool (or any fake-signed SELF) carries the plain ELF after the header.
    public static byte[] PlainElfInSelf(byte[] self)
    {
        if (self.Length < 0x20 || self[0] != 'S' || self[1] != 'C' || self[2] != 'E') return null;
        int hlen = (int)U64(self, 0x10);
        if (hlen <= 0 || hlen + 4 > self.Length || self[hlen] != 0x7F || self[hlen + 1] != 'E') return null;
        var e = new byte[self.Length - hlen]; Buffer.BlockCopy(self, hlen, e, 0, e.Length); return e;
    }
    // Fake-signed SELF: the retail header with key revision 0x8000, no metadata, and the plain ELF appended (runs on RPCS3).
    public static byte[] FSelf(byte[] retail, byte[] elf)
    {
        if (retail[0] != 'S' || retail[1] != 'C' || retail[2] != 'E') throw new Exception("원본 SELF 파일이 아닙니다.");
        int moff = (int)U32(retail, 0x0C); int hlen = (int)U64(retail, 0x10);
        var h = new byte[hlen]; Buffer.BlockCopy(retail, 0, h, 0, hlen);
        h[8] = 0x80; h[9] = 0; P64(h, 0x18, (ulong)elf.Length);
        var ext = new ulong[10]; for (int i = 0; i < 10; i++) ext[i] = U64(h, 0x20 + 8 * i);
        int ms = 0x20 + moff; for (int i = ms; i < hlen; i++) h[i] = 0;
        Buffer.BlockCopy(elf, 0, h, (int)ext[2], 0x40);
        int phoff = (int)U64(elf, 0x20), phnum = U16(elf, 0x38);
        Buffer.BlockCopy(elf, phoff, h, (int)ext[3], 56 * phnum);
        for (int i = 0; i < phnum; i++)
        {
            int e = phoff + i * 56; ulong off = U64(elf, e + 8), fs = U64(elf, e + 32);
            int so = (int)ext[5] + i * 0x20; uint enc = U32(h, so + 28);
            P64(h, so, (ulong)hlen + off); P64(h, so + 8, fs); P32(h, so + 16, 1); P32(h, so + 20, 0); P32(h, so + 24, 0); P32(h, so + 28, enc == 1 ? 2u : enc);
        }
        var r = new byte[hlen + elf.Length]; h.CopyTo(r, 0); elf.CopyTo(r, hlen); return r;
    }
    // Looks for the decrypted ELFs (RPCS3 "Decrypt PS3 Binaries" writes scN.ppu.elf next to the SELF).
    public static Dictionary<int, byte[]> FindElfs(IEnumerable<string> dirs)
    {
        var found = new Dictionary<int, byte[]>(); var sizes = All().ToDictionary(x => x.Size, x => x.Game);
        foreach (var d in dirs.Where(x => !string.IsNullOrEmpty(x) && Directory.Exists(x)).Distinct())
            foreach (var f in Directory.EnumerateFiles(d, "*.elf", SearchOption.AllDirectories))
            {
                int g; long len = new FileInfo(f).Length; if (len > int.MaxValue || !sizes.TryGetValue((int)len, out g) || found.ContainsKey(g)) continue;
                var b = PatchElf(g, File.ReadAllBytes(f)); if (b != null) { found[g] = b; Core.Log("복호화 실행 파일 확인: " + f); }
            }
        return found;
    }
}

// ---------- subtitles for the movies ----------
static class Subs
{
    public class Item { public string Key, Rel, Ass; public int Game { get { return Key[2] - '0'; } } }
    public static List<Item> All()
    {
        var t = Encoding.UTF8.GetString(Core.Gunzip(Core.Res("subs.txt.gz")));
        return t.TrimStart('#').Split(new[] { "\n#" }, StringSplitOptions.None).Select(p =>
        {
            int nl = p.IndexOf('\n'); var h = p.Substring(0, nl).Split('\t');
            return new Item { Key = h[0], Rel = h[1], Ass = p.Substring(nl + 1) };
        }).ToList();
    }
}

static class Video
{
    class Bink
    {
        public byte[] B; public uint Frames, W, H, N, FpsN, FpsD, VFlags; public int[] Off;
        public Bink(string p) : this(File.ReadAllBytes(p)) { }
        public Bink(byte[] data)
        {
            B = data; if (B.Length < 44 || B[0] != 'B' || B[1] != 'I' || B[2] != 'K') throw new Exception("Bink 영상이 아닙니다.");
            Frames = U(8); W = U(20); H = U(24); FpsN = U(28); FpsD = U(32); VFlags = U(36); N = U(40);
            int o = 44 + (int)N * 12; Off = new int[Frames + 1]; for (int i = 0; i <= Frames; i++) Off[i] = (int)U(o + i * 4);
        }
        public uint U(int o) { return BitConverter.ToUInt32(B, o); }
        public void Frame(int i, out int vStart, out int end, out int start) { start = Off[i] & ~1; end = Off[i + 1] & ~1; int p = start; for (int t = 0; t < N; t++) p += 4 + (int)U(p); vStart = p; }
        public long VideoBytes() { long s = 0; for (int i = 0; i < Frames; i++) { int v, e, st; Frame(i, out v, out e, out st); s += e - v; } return s; }
    }
    static void LE32(byte[] b, int o, uint v) { BitConverter.GetBytes(v).CopyTo(b, o); }
    // Keeps every audio track of the original and swaps in the new video packets.
    static void Remux(Bink o, Bink v, string path)
    {
        if (o.Frames != v.Frames || v.N != 0 || o.W != v.W || o.H != v.H) throw new Exception("영상 프레임 정보가 맞지 않습니다.");
        int n = (int)o.N, F = (int)o.Frames, hl = 44 + 12 * n + 4 * (F + 1); uint pos = (uint)hl, maxF = 0; var offs = new uint[F + 1];
        for (int i = 0; i < F; i++) { int ov, oe, os, vv, ve, vs; o.Frame(i, out ov, out oe, out os); v.Frame(i, out vv, out ve, out vs); uint len = (uint)((ov - os) + (ve - vv)); offs[i] = pos | (uint)(v.Off[i] & 1); pos += len; if (len > maxF) maxF = len; }
        offs[F] = pos; var h = new byte[hl]; Array.Copy(o.B, 0, h, 0, 4); LE32(h, 4, pos - 8); LE32(h, 8, (uint)F); LE32(h, 12, maxF); LE32(h, 16, (uint)F); LE32(h, 20, o.W); LE32(h, 24, o.H); LE32(h, 28, o.FpsN); LE32(h, 32, o.FpsD); LE32(h, 36, v.VFlags); LE32(h, 40, (uint)n);
        Array.Copy(o.B, 44, h, 44, 12 * n); for (int i = 0; i <= F; i++) LE32(h, 44 + 12 * n + i * 4, offs[i]);
        using (var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            f.Write(h, 0, hl);
            for (int i = 0; i < F; i++) { int ov, oe, os, vv, ve, vs; o.Frame(i, out ov, out oe, out os); v.Frame(i, out vv, out ve, out vs); f.Write(o.B, os, ov - os); f.Write(v.B, vv, ve - vv); }
        }
    }
    public static void Run(string exe, string args, string cwd)
    {
        var p = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        using (var x = Process.Start(p))
        {
            var err = new StringBuilder(); x.ErrorDataReceived += (s, e) => { if (e.Data != null && err.Length < 4000) err.AppendLine(e.Data); }; x.OutputDataReceived += (s, e) => { };
            x.BeginErrorReadLine(); x.BeginOutputReadLine(); x.WaitForExit();
            if (x.ExitCode != 0) throw new Exception(Path.GetFileName(exe) + " 오류 (" + x.ExitCode + "): " + err);
        }
    }
    // Burns the Korean subtitles into one movie: ffmpeg draws them on PNG frames, RAD Video Tools encodes Bink 1, and the original audio is kept.
    public static void Burn(Subs.Item it, string src, string output, string ffmpeg, string rad)
    {
        var orig = new Bink(src);
        long need = (long)orig.Frames * orig.W * orig.H * 2 + (300L << 20);
        string tmp = Path.Combine(Path.GetTempPath(), "SCTKO_" + Guid.NewGuid().ToString("N"));
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(tmp))).AvailableFreeSpace;
        if (free < need) throw new Exception("임시 폴더 드라이브 공간이 부족합니다 (" + (need >> 20) + "MB 필요).");
        Directory.CreateDirectory(Path.Combine(tmp, "f"));
        try
        {
            File.WriteAllText(Path.Combine(tmp, "s.ass"), it.Ass, new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(tmp, "NanumGothicBold.ttf"), Core.Gunzip(Core.Res("NanumGothicBold.ttf.gz")));
            File.Copy(src, Path.Combine(tmp, "in.bik"));
            Run(ffmpeg, "-v error -y -i in.bik -an -fps_mode passthrough -vf \"subtitles=s.ass:fontsdir=.\" -compression_level 1 f/f%05d.png", tmp);
            int cnt = Directory.GetFiles(Path.Combine(tmp, "f"), "f*.png").Length;
            if (cnt != orig.Frames) throw new Exception("프레임 수가 맞지 않습니다: " + cnt + " / " + orig.Frames);
            long rate = (long)(orig.VideoBytes() * (double)orig.FpsN / orig.FpsD / orig.Frames);
            string fps = ((double)orig.FpsN / orig.FpsD).ToString("0.###", CultureInfo.InvariantCulture);
            Run(rad, "Bink \"" + Path.Combine(tmp, "f", "f?????.png") + "*1-" + cnt + "\" \"" + Path.Combine(tmp, "v.bik") + "\" /Bink1 /F" + fps + " /DataRate" + rate + " /NoSound /O /#", tmp);
            if (!File.Exists(Path.Combine(tmp, "v.bik"))) throw new Exception("Bink 인코딩 결과가 없습니다.");
            Remux(orig, new Bink(Path.Combine(tmp, "v.bik")), output);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}

// ---------- RPCS3 game patch (memory patch by PPU hash; no decryption needed) ----------
static class Rpcs3Patch
{
    const string File_ = "BLUS30761_patch.yml", Begin = "# SCT_KO begin", End = "# SCT_KO end", Title = "Tom Clancy's Splinter Cell Trilogy";
    public static void Check(string rpcs3)
    {
        if (string.IsNullOrEmpty(rpcs3)) throw new Exception("RPCS3 게임 패치 방식은 RPCS3 폴더를 지정해야 합니다.");
        if (!File.Exists(Path.Combine(rpcs3, "rpcs3.exe"))) throw new Exception("RPCS3 폴더가 아닙니다 (rpcs3.exe 없음): " + rpcs3);
    }
    static string[] Hashes() { return Encoding.ASCII.GetString(Core.Res("rpcs3_hashes.txt")).Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries); }
    static string Strip(string t)
    {
        int a = t.IndexOf(Begin); if (a < 0) return t; int b = t.IndexOf(End, a); if (b < 0) return t.Substring(0, a);
        b = t.IndexOf('\n', b); return t.Substring(0, a) + (b < 0 ? "" : t.Substring(b + 1));
    }
    public static void Install(string rpcs3)
    {
        Check(rpcs3);
        var body = Encoding.UTF8.GetString(Core.Res("rpcs3_patch.yml")).Replace("\r\n", "\n");
        var pd = Path.Combine(rpcs3, "patches"); Directory.CreateDirectory(pd); var pf = Path.Combine(pd, File_);
        string prev = File.Exists(pf) ? Strip(File.ReadAllText(pf).Replace("\r\n", "\n")) : "";
        foreach (var h in Hashes()) if (Regex.IsMatch(prev, "^" + h + ":", RegexOptions.Multiline)) throw new Exception(File_ + "에 같은 실행 파일용 다른 패치가 있습니다. 해당 항목을 정리한 뒤 다시 시도해 주세요.");
        string entries = body.Substring(body.IndexOf("PPU-"));
        if (prev.Replace("Version: 1.2", "").Trim().Length == 0) prev = "Version: 1.2\n\n";
        else { File.Copy(pf, pf + ".sct_ko.bak", true); if (!prev.EndsWith("\n")) prev += "\n"; }
        File.WriteAllText(pf, prev + Begin + "\n" + entries.TrimEnd('\n') + "\n" + End + "\n", new UTF8Encoding(false));
        var cd = Path.Combine(rpcs3, "config"); Directory.CreateDirectory(cd); var cf = Path.Combine(cd, "patch_config.yml");
        string old = File.Exists(cf) ? Strip(File.ReadAllText(cf).Replace("\r\n", "\n")) : "";
        var add = new StringBuilder(Begin + "\n"); int n = 0, skip = 0; var hs = Hashes();
        for (int i = 0; i < hs.Length; i++)
        {
            if (Regex.IsMatch(old, "^" + hs[i] + ":", RegexOptions.Multiline)) { skip++; continue; }
            add.Append(hs[i] + ":\n  \"Korean patch (SC" + (i + 1) + ")\":\n    \"" + Title + "\":\n      BLUS30761:\n        All:\n          Enabled: true\n"); n++;
        }
        add.Append(End + "\n");
        if (old.Length > 0 && File.Exists(cf)) { File.Copy(cf, cf + ".sct_ko.bak", true); if (!old.EndsWith("\n")) old += "\n"; }
        File.WriteAllText(cf, old + add, new UTF8Encoding(false));
        Core.Log("RPCS3 게임 패치 설치: patches\\" + File_ + (skip > 0 ? "\r\n주의: patch_config.yml에 이미 항목이 있는 실행 파일이 있어, RPCS3 [Manage → Game Patches]에서 'Korean patch'를 직접 켜 주세요." : " (자동 활성화)"));
    }
    public static void Uninstall(string rpcs3)
    {
        if (string.IsNullOrEmpty(rpcs3)) return;
        foreach (var f in new[] { Path.Combine(rpcs3, "patches", File_), Path.Combine(rpcs3, "config", "patch_config.yml") })
        {
            if (!File.Exists(f)) continue; var t = File.ReadAllText(f).Replace("\r\n", "\n"); if (!t.Contains(Begin)) continue;
            var r = Strip(t); if (f.EndsWith(File_) && r.Replace("Version: 1.2", "").Trim().Length == 0) File.Delete(f); else File.WriteAllText(f, r, new UTF8Encoding(false));
        }
        Core.Log("RPCS3 게임 패치 제거 완료");
    }
}

// ---------- install ----------
static class Installer
{
    public class Options
    {
        public string Game, Rpcs3, ElfDir, Ffmpeg, Rad; public bool Exe = true, RealPs3; public HashSet<int> VideoGames = new HashSet<int>();
        public bool Video { get { return VideoGames.Count > 0; } }
    }
    static void Record(string usr, string kind, string rel)
    {
        var m = Path.Combine(usr, Core.Manifest); var line = kind + " " + rel;
        if (File.Exists(m) && File.ReadAllLines(m).Contains(line)) return;
        File.AppendAllText(m, line + "\r\n", new UTF8Encoding(false));
    }
    // Writes one patched file in place; the original goes to <name>.orig the first time.
    static void Put(string usr, string rel, byte[] data, bool isNew)
    {
        var f = Path.Combine(usr, Core.Disk(rel)); Directory.CreateDirectory(Path.GetDirectoryName(f));
        if (!isNew && File.Exists(f) && !File.Exists(f + ".orig")) File.Copy(f, f + ".orig");
        File.WriteAllBytes(f + ".new", data); if (File.Exists(f)) File.Delete(f); File.Move(f + ".new", f);
        Record(usr, isNew ? "A" : "M", rel);
    }
    // Returns the original bytes of a file in a folder: the current file or its .orig backup, whichever matches.
    static byte[] Original(string f, string sha)
    {
        if (File.Exists(f + ".orig")) { var o = File.ReadAllBytes(f + ".orig"); if (Core.Sha(o) == sha) return o; }
        if (File.Exists(f)) { var c = File.ReadAllBytes(f); if (Core.Sha(c) == sha) return c; }
        return null;
    }
    public static void Data(string usr, List<Entry> pack, bool hdd)
    {
        int done = 0, same = 0, bad = 0;
        foreach (var e in pack)
        {
            var f = Path.Combine(usr, Core.Disk(e.Rel));
            if (e.New)
            {
                var top = e.Rel.Split('/')[0];
                if (hdd && !Directory.Exists(Path.Combine(usr, top))) continue;
                if (File.Exists(f) && Core.ShaFile(f) == e.DstSha) { same++; Record(usr, "A", e.Rel); continue; }
                if (File.Exists(f) && !File.Exists(f + ".orig")) File.Copy(f, f + ".orig");
                Put(usr, e.Rel, e.Build(null), !File.Exists(f + ".orig")); done++; continue;
            }
            if (!File.Exists(f)) { if (!hdd) { Core.Log("없는 파일: " + e.Rel); bad++; } continue; }
            if (new FileInfo(f).Length == e.DstSize && Core.ShaFile(f) == e.DstSha) { same++; if (File.Exists(f + ".orig")) Record(usr, "M", e.Rel); continue; }
            var src = Original(f, e.SrcSha);
            if (src == null) { Core.Log("원본과 다른 파일이라 건너뜀: " + e.Rel); bad++; continue; }
            Put(usr, e.Rel, e.Build(src), false); done++;
        }
        Core.Log((hdd ? "[RPCS3 설치 데이터] " : "") + "파일 패치 " + done + "개, 이미 적용 " + same + "개" + (bad > 0 ? ", 문제 " + bad + "개" : ""));
        if (!hdd && bad > 0) throw new Exception("원본과 다른 파일이 있습니다. 덤프한 원본 게임 파일인지 확인해 주세요.");
    }

    static Dictionary<int, byte[]> Elfs(Options o, IEnumerable<string> extraDirs, Func<int, byte[]> selfOf)
    {
        var found = new Dictionary<int, byte[]>();
        for (int g = 1; g <= 3; g++) { var s = selfOf(g); var e = s == null ? null : Exe.PlainElfInSelf(s); var p = e == null ? null : Exe.PatchElf(g, e); if (p != null) found[g] = p; }
        var more = Exe.FindElfs(new[] { o.ElfDir }.Concat(extraDirs));
        foreach (var kv in more) if (!found.ContainsKey(kv.Key)) found[kv.Key] = kv.Value;
        var miss = Enumerable.Range(1, 3).Where(g => !found.ContainsKey(g)).ToList();
        if (miss.Count > 0)
            throw new Exception("복호화한 실행 파일(sc" + string.Join(", sc", miss) + ".ppu.elf)을 찾지 못했습니다.\r\n" +
                "RPCS3 메뉴 [Utilities → Decrypt PS3 Binaries]로 sc1/sc2/sc3.ppu.self를 복호화한 뒤, 만들어진 .elf가 있는 폴더를 지정해 주세요.\r\n" +
                "(실행 파일 패치를 빼고 설치하려면 '실행 파일 패치' 체크를 해제하세요. 이 경우 일부 한글이 깨집니다.)");
        return found;
    }
    static string ElfOutDir(string game) { var d = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(game.TrimEnd('\\', '/'))), "SCT_KO_ELF"); Directory.CreateDirectory(d); return d; }
    static void SaveElfs(Dictionary<int, byte[]> elfs, string dir)
    {
        foreach (var kv in elfs) File.WriteAllBytes(Path.Combine(dir, "sc" + kv.Key + ".ppu.elf"), kv.Value);
        Core.Log("PS3 실기용: 한글 패치된 ELF를 저장했습니다 → " + dir + "\r\n  재서명 도구(TrueAncestor SELF Resigner, scetool 등)로 SELF를 만든 뒤 게임의 sc1/sc2/sc3.ppu.self와 바꿔 주세요.");
    }

    public static void Folder(Options o)
    {
        var pack = Pack.Load(); var usr = Core.UsrDir(o.Game); var hdd = Core.HddUsr(o.Rpcs3);
        Dictionary<int, byte[]> elfs = null; bool yml = o.Exe && Core.Yml && !o.RealPs3;
        if (yml) Rpcs3Patch.Check(o.Rpcs3);
        if (o.Exe && !yml)
            elfs = Elfs(o, Enumerable.Range(1, 3).Select(g => Path.GetDirectoryName(Path.Combine(usr, Core.Disk(Core.SelfRel[g])))),
                g => { var f = Path.Combine(usr, Core.Disk(Core.SelfRel[g])); return File.Exists(f) ? File.ReadAllBytes(f) : null; });
        Data(usr, pack, false);
        if (elfs != null)
        {
            if (o.RealPs3) SaveElfs(elfs, ElfOutDir(o.Game));
            else
                foreach (var kv in elfs)
                {
                    var rel = Core.SelfRel[kv.Key]; var f = Path.Combine(usr, Core.Disk(rel));
                    if (!File.Exists(f)) throw new Exception("실행 파일이 없습니다: " + rel);
                    var tpl = File.Exists(f + ".orig") ? File.ReadAllBytes(f + ".orig") : File.ReadAllBytes(f);
                    var cur = File.ReadAllBytes(f); var inCur = Exe.PlainElfInSelf(cur);
                    if (inCur != null && Core.Sha(inCur) == Core.Sha(kv.Value)) { if (File.Exists(f + ".orig")) Record(usr, "M", rel); continue; }
                    Put(usr, rel, Exe.FSelf(tpl, kv.Value), false); Core.Log("실행 파일 패치: " + rel);
                }
        }
        if (yml)
        {
            // the game patch matches only the original executables, so undo a SELF replaced earlier by the ELF method
            for (int g = 1; g <= 3; g++)
            {
                var rel = Core.SelfRel[g]; var f = Path.Combine(usr, Core.Disk(rel));
                if (File.Exists(f + ".orig") && File.Exists(f) && Exe.PlainElfInSelf(File.ReadAllBytes(f)) != null) { File.Delete(f); File.Move(f + ".orig", f); Core.Log("실행 파일을 원본으로 되돌림: " + rel); }
            }
            Rpcs3Patch.Install(o.Rpcs3);
        }
        if (hdd != null) { Data(hdd, pack, true); Core.Log("RPCS3 설치 데이터도 패치했습니다: " + hdd); }
        if (o.Video) Tools.With(o.Ffmpeg, o.Rad, (ff, rad) => Videos(o, ff, rad, it =>
        {
            var f = Path.Combine(usr, Core.Disk(it.Rel)); return File.Exists(f + ".orig") ? f + ".orig" : File.Exists(f) ? f : null;
        }, (it, file) => { var f = Path.Combine(usr, Core.Disk(it.Rel)); if (!File.Exists(f + ".orig")) File.Move(f, f + ".orig"); else File.Delete(f); File.Move(file, f); Record(usr, "M", it.Rel); }));
        Core.Log("설치가 끝났습니다." + (o.RealPs3 ? "" : " RPCS3에서 게임을 실행하세요."));
    }

    static void Videos(Options o, string ff, string rad, Func<Subs.Item, string> source, Action<Subs.Item, string> done)
    {
        var items = Subs.All().Where(x => o.VideoGames.Contains(x.Game)).ToList(); int i = 0;
        var sums = Encoding.ASCII.GetString(Core.Res("videosha.txt")).Split('\n').Where(l => l.Length > 64).ToDictionary(l => l.Substring(65).Trim(), l => l.Substring(0, 64));
        foreach (var it in items)
        {
            i++; var src = source(it);
            if (src == null) { Core.Log("[" + i + "/" + items.Count + "] 영상 없음: " + it.Rel); continue; }
            string want; if (sums.TryGetValue(it.Rel, out want) && Core.ShaFile(src) != want) { Core.Log("[" + i + "/" + items.Count + "] 원본 영상이 아니라 건너뜀 (이미 자막이 들어갔을 수 있음): " + it.Rel); continue; }
            Core.Log("[" + i + "/" + items.Count + "] 자막 입히는 중: " + it.Rel);
            var outp = Path.Combine(Path.GetTempPath(), "SCTKO_" + Guid.NewGuid().ToString("N") + ".bik");
            try { Video.Burn(it, src, outp, ff, rad); done(it, outp); }
            finally { if (File.Exists(outp)) File.Delete(outp); }
        }
        Core.Log("영상 자막 작업 완료");
    }

    public static void ExtractSelfs(string isoPath)
    {
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(isoPath)), Path.GetFileNameWithoutExtension(isoPath) + "_SELF"); Directory.CreateDirectory(dir);
        using (var iso = new Iso(isoPath))
            for (int g = 1; g <= 3; g++)
            {
                var n = iso.Find("PS3_GAME/USRDIR/" + Core.SelfRel[g]); if (n == null) throw new Exception("ISO에서 찾을 수 없습니다: " + Core.SelfRel[g]);
                using (var f = File.Create(Path.Combine(dir, Path.GetFileName(Core.SelfRel[g])))) iso.Copy(n, f);
            }
        Core.Log("실행 파일 3개를 꺼냈습니다 → " + dir + "\r\nRPCS3 메뉴 [Utilities → Decrypt PS3 Binaries]에서 이 폴더의 .self 3개를 선택하면 같은 폴더에 .elf가 만들어집니다.\r\n그다음 '복호화 ELF 폴더'에 이 폴더를 지정하고 설치하세요.");
    }

    public static string IsoInstall(Options o)
    {
        var pack = Pack.Load(); var hdd = Core.HddUsr(o.Rpcs3); var isoPath = o.Game;
        string output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(isoPath)), Path.GetFileNameWithoutExtension(isoPath) + "_KOR.iso");
        string work = Path.Combine(Path.GetTempPath(), "SCTKO_iso_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            using (var iso = new Iso(isoPath))
            {
                Func<Iso.Node, byte[]> read = n => { var m = new MemoryStream(); iso.Copy(n, m); return m.ToArray(); };
                Func<string, Iso.Node> node = rel => iso.Find("PS3_GAME/USRDIR/" + rel);
                if (node("Data_SC") == null) throw new Exception("스플린터 셀 트릴로지 ISO가 아닙니다.");
                long need = new FileInfo(isoPath).Length + (500L << 20), free = new DriveInfo(Path.GetPathRoot(output)).AvailableFreeSpace;
                if (free < need) throw new Exception("저장 공간이 부족합니다. " + (need >> 30) + "GB 이상의 여유 공간이 필요합니다.");
                Dictionary<int, byte[]> elfs = null; bool yml = o.Exe && Core.Yml && !o.RealPs3;
                if (yml) Rpcs3Patch.Check(o.Rpcs3);
                if (o.Exe && !yml) elfs = Elfs(o, new[] { Path.Combine(Path.GetDirectoryName(Path.GetFullPath(isoPath)), Path.GetFileNameWithoutExtension(isoPath) + "_SELF") },
                    g => { var n = node(Core.SelfRel[g]); return n == null ? null : read(n); });
                int k = 0; Action<string, byte[]> replace = (rel, data) =>
                {
                    var file = Path.Combine(work, (k++).ToString()); File.WriteAllBytes(file, data);
                    var n = node(rel);
                    if (n == null)
                    {
                        var parts = rel.Split('/'); var dir = iso.Find("PS3_GAME/USRDIR");
                        for (int i = 0; i < parts.Length - 1; i++)
                        {
                            var c = dir.Kids.FirstOrDefault(x => x.Dir && x.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                            if (c == null) { c = new Iso.Node { Name = parts[i], Dir = true }; dir.Kids.Add(c); }
                            dir = c;
                        }
                        n = new Iso.Node { Name = parts[parts.Length - 1] }; dir.Kids.Add(n);
                    }
                    n.Src = file; n.Size = data.Length;
                };
                int done = 0;
                foreach (var e in pack)
                {
                    var n = node(e.Rel);
                    if (e.New) { if (n != null && !n.Dir && Core.Sha(read(n)) == e.DstSha) continue; replace(e.Rel, e.Build(null)); done++; continue; }
                    if (n == null) throw new Exception("ISO에서 찾을 수 없습니다: " + e.Rel);
                    var src = read(n); var s = Core.Sha(src);
                    if (s == e.DstSha) continue;
                    if (s != e.SrcSha) throw new Exception("원본 ISO가 아닙니다 (" + e.Rel + "). 덤프한 원본 ISO를 지정해 주세요.");
                    replace(e.Rel, e.Build(src)); done++;
                }
                Core.Log("파일 패치 " + done + "개");
                if (elfs != null)
                {
                    if (o.RealPs3) SaveElfs(elfs, ElfOutDir(isoPath));
                    else foreach (var kv in elfs)
                        {
                            var rel = Core.SelfRel[kv.Key]; var cur = read(node(rel)); var inCur = Exe.PlainElfInSelf(cur);
                            if (inCur != null && Core.Sha(inCur) == Core.Sha(kv.Value)) continue;
                            replace(rel, Exe.FSelf(cur, kv.Value)); Core.Log("실행 파일 패치: " + rel); done++;
                        }
                }
                if (done == 0 && !o.Video) throw new Exception("이미 한글 패치가 적용된 ISO입니다.");
                if (o.Video) Tools.With(o.Ffmpeg, o.Rad, (ff, rad) => Videos(o, ff, rad, it =>
                {
                    var n = node(it.Rel); if (n == null) return null;
                    var f = Path.Combine(work, "v" + (k++) + ".bik"); using (var s = File.Create(f)) iso.Copy(n, s); return f;
                }, (it, file) => { var f = Path.Combine(work, "v" + (k++) + ".bik"); File.Move(file, f); var n = node(it.Rel); n.Src = f; n.Size = new FileInfo(f).Length; }));
                Core.Log("한글 ISO 만드는 중 (몇 분 걸립니다)...");
                iso.Write(output + ".tmp");
            }
            if (File.Exists(output)) File.Delete(output);
            File.Move(output + ".tmp", output);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
        Core.Log("한글 ISO 완성: " + output);
        if (hdd != null) { Data(hdd, pack, true); Core.Log("RPCS3 설치 데이터도 패치했습니다: " + hdd); }
        if (o.Exe && Core.Yml && !o.RealPs3) Rpcs3Patch.Install(o.Rpcs3);
        return output;
    }

    public static void Uninstall(string usr)
    {
        var m = Path.Combine(usr, Core.Manifest); if (!File.Exists(m)) { Core.Log("되돌릴 기록이 없습니다: " + usr); return; }
        int n = 0;
        foreach (var line in File.ReadAllLines(m).Where(l => l.Length > 2).Distinct())
        {
            var f = Path.Combine(usr, Core.Disk(line.Substring(2)));
            if (File.Exists(f + ".orig")) { if (File.Exists(f)) File.Delete(f); File.Move(f + ".orig", f); n++; }
            else if (line[0] == 'A' && File.Exists(f)) { File.Delete(f); n++; }
        }
        File.Delete(m); Core.Log("되돌리기 완료 (" + n + "개 파일): " + usr);
    }
}
class Iso : IDisposable
{
    public const int S = 2048;
    public class Node { public string Name, Src; public bool Dir; public long Lba, Size; public List<Node> Kids = new List<Node>(); }
    class Entry { public Node N; public Entry Parent; public byte[] Name; public int Index; public List<Node> Sorted; public long Lba, Size; }
    FileStream f; Node root; byte[] s1, pvd, svd, term;
    byte[] Sector(long lba, int len) { f.Position = lba * S; var b = new byte[len]; int got = 0, n; while (got < len && (n = f.Read(b, got, len - got)) > 0) got += n; if (got != len) throw new Exception("ISO 파일이 손상되었거나 잘렸습니다."); return b; }
    public Iso(string path)
    {
        f = File.OpenRead(path);
        if (f.Length < 32 * S) throw new Exception("PS3 ISO 파일이 아닙니다.");
        s1 = Sector(1, S); pvd = Sector(16, S); svd = Sector(17, S); term = Sector(18, S);
        if (Encoding.ASCII.GetString(s1, 0, 12) != "PlayStation3" || pvd[0] != 1 || svd[0] != 2 || term[0] != 255) throw new Exception("지원하는 PS3 ISO 형식이 아닙니다.");
        root = new Node { Name = "", Dir = true, Lba = BitConverter.ToUInt32(svd, 158), Size = BitConverter.ToUInt32(svd, 166) };
        Read(root);
    }
    void Read(Node d)
    {
        var b = Sector(d.Lba, (int)d.Size); int o = 0;
        Node open = null; // a file recorded as several consecutive extents
        while (o < b.Length)
        {
            int len = b[o]; if (len == 0) { o = (o / S + 1) * S; continue; }
            int nl = b[o + 32]; int flags = b[o + 25];
            if (!(nl == 1 && b[o + 33] <= 1))
            {
                string name = Encoding.BigEndianUnicode.GetString(b, o + 33, nl); int semi = name.LastIndexOf(';'); if (semi > 0) name = name.Substring(0, semi);
                var n = new Node { Name = name, Dir = (flags & 2) != 0, Lba = BitConverter.ToUInt32(b, o + 2), Size = BitConverter.ToUInt32(b, o + 10) };
                if (open != null)
                {
                    if (open.Name != n.Name || open.Size % S != 0 || open.Lba + open.Size / S != n.Lba) throw new Exception("연속되지 않은 파일 조각이 있는 ISO는 지원하지 않습니다.");
                    open.Size += n.Size;
                    if (open.Size > uint.MaxValue) throw new Exception("4GB가 넘는 파일이 있는 ISO는 지원하지 않습니다.");
                    if ((flags & 0x80) == 0) open = null;
                }
                else
                {
                    d.Kids.Add(n); if ((flags & 0x80) != 0) open = n;
                    if (n.Dir) Read(n);
                }
            }
            o += len;
        }
    }
    public Node Find(string path)
    {
        var c = root;
        foreach (var part in path.Split('/')) { if (c == null) return null; c = c.Kids.FirstOrDefault(k => k.Name.Equals(part, StringComparison.OrdinalIgnoreCase)); }
        return c;
    }
    public void Copy(Node n, Stream o) { CopyRange(f, n.Lba * S, n.Size, o); }
    static void CopyRange(Stream s, long pos, long size, Stream o)
    {
        s.Position = pos; var buf = new byte[1 << 20]; long left = size;
        while (left > 0) { int n = s.Read(buf, 0, (int)Math.Min(left, buf.Length)); if (n <= 0) throw new Exception("파일을 끝까지 읽지 못했습니다."); o.Write(buf, 0, n); left -= n; }
    }
    static long Align(long n) { return (n + S - 1) / S; }
    static byte[] Enc(Node n, bool joliet)
    {
        string s = (joliet ? n.Name : n.Name.ToUpperInvariant()) + (n.Dir ? "" : ";1");
        return joliet ? Encoding.BigEndianUnicode.GetBytes(s) : Encoding.GetEncoding(28591).GetBytes(s);
    }
    static int Cmp(byte[] a, byte[] b) { for (int i = 0; i < a.Length && i < b.Length; i++) if (a[i] != b[i]) return a[i] - b[i]; return a.Length - b.Length; }
    static List<Entry> Tree(Node top, bool joliet)
    {
        var list = new List<Entry>(); var first = new Entry { N = top, Name = new byte[] { 0 } }; list.Add(first);
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i]; c.Index = i + 1; c.Sorted = c.N.Kids.ToList(); c.Sorted.Sort((x, y) => Cmp(Enc(x, joliet), Enc(y, joliet)));
            foreach (var k in c.Sorted) if (k.Dir) list.Add(new Entry { N = k, Parent = c, Name = Enc(k, joliet) });
            long used = 68;
            foreach (var k in c.Sorted) { int nl = Enc(k, joliet).Length, l = 33 + nl + (nl % 2 == 0 ? 1 : 0); if (used % S + l > S) used += S - used % S; used += l; }
            c.Size = Align(used) * S;
        }
        return list;
    }
    static void Both32(byte[] b, int o, uint v) { BitConverter.GetBytes(v).CopyTo(b, o); b[o + 4] = (byte)(v >> 24); b[o + 5] = (byte)(v >> 16); b[o + 6] = (byte)(v >> 8); b[o + 7] = (byte)v; }
    static void Be32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static readonly byte[] Date = { 107, 8, 24, 0, 0, 0, 0 };
    static byte[] Rec(long lba, long size, int flags, byte[] name)
    {
        var b = new byte[33 + name.Length + (name.Length % 2 == 0 ? 1 : 0)]; b[0] = (byte)b.Length; Both32(b, 2, (uint)lba); Both32(b, 10, (uint)size); Date.CopyTo(b, 18); b[25] = (byte)flags;
        b[28] = 1; b[31] = 1; b[32] = (byte)name.Length; name.CopyTo(b, 33); return b;
    }
    static byte[] PathTable(List<Entry> l, bool big)
    {
        var m = new MemoryStream();
        foreach (var e in l)
        {
            int nl = e.Name.Length; var b = new byte[8 + nl + (nl & 1)]; b[0] = (byte)nl; int parent = e.Parent != null ? e.Parent.Index : 1;
            if (big) { Be32(b, 2, (uint)e.Lba); b[6] = (byte)(parent >> 8); b[7] = (byte)parent; } else { BitConverter.GetBytes((uint)e.Lba).CopyTo(b, 2); b[6] = (byte)parent; b[7] = (byte)(parent >> 8); }
            e.Name.CopyTo(b, 8); m.Write(b, 0, b.Length);
        }
        return m.ToArray();
    }
    static byte[] DirData(Entry e, List<Entry> l, bool joliet)
    {
        var buf = new byte[e.Size]; int o = 0; Action<byte[]> put = r => { if (o % S + r.Length > S) o += S - o % S; r.CopyTo(buf, o); o += r.Length; };
        var p = e.Parent ?? e; put(Rec(e.Lba, e.Size, 2, new byte[] { 0 })); put(Rec(p.Lba, p.Size, 2, new byte[] { 1 }));
        foreach (var k in e.Sorted)
        {
            if (k.Dir) { var c = l.First(x => x.N == k); put(Rec(c.Lba, c.Size, 2, c.Name)); }
            else put(Rec(k.Lba, k.Size, 0, Enc(k, joliet)));
        }
        return buf;
    }
    static byte[] Vol(byte[] tpl, List<Entry> l, int ptSize, long lm, long big, long total)
    {
        var b = (byte[])tpl.Clone(); Both32(b, 80, (uint)total); Both32(b, 132, (uint)ptSize);
        BitConverter.GetBytes((uint)lm).CopyTo(b, 140); BitConverter.GetBytes(0u).CopyTo(b, 144); Be32(b, 148, (uint)big); Be32(b, 152, 0);
        Rec(l[0].Lba, l[0].Size, 2, new byte[] { 0 }).CopyTo(b, 156); return b;
    }
    static void Flatten(Node d, string path, List<KeyValuePair<string, Node>> o)
    {
        foreach (var k in d.Kids) { var r = path + "/" + k.Name; if (k.Dir) Flatten(k, r, o); else o.Add(new KeyValuePair<string, Node>(r, k)); }
    }
    public void Write(string output)
    {
        List<Entry> P = Tree(root, false), J = Tree(root, true);
        // table sizes do not depend on LBAs, so measure them with zeroed entries first
        byte[] pt = PathTable(P, false), jt = PathTable(J, false);
        long lba = 32, pL = lba; lba += Align(pt.Length); long pM = lba; lba += Align(pt.Length); long jL = lba; lba += Align(jt.Length); long jM = lba; lba += Align(jt.Length);
        foreach (var e in P) { e.Lba = lba; lba += e.Size / S; }
        foreach (var e in J) { e.Lba = lba; lba += e.Size / S; }
        var files = new List<KeyValuePair<string, Node>>(); Flatten(root, "", files); files.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var oldLba = new Dictionary<Node, long>();
        foreach (var kv in files) { oldLba[kv.Value] = kv.Value.Lba; kv.Value.Lba = lba; lba += Align(kv.Value.Size); }
        long total = lba;
        using (var o = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Action<byte[], long> put = (b, sec) => { o.Position = sec * S; o.Write(b, 0, b.Length); };
            var s0 = new byte[S]; Be32(s0, 0, 1); Be32(s0, 12, (uint)(total - 1)); put(s0, 0); put(s1, 1);
            put(Vol(pvd, P, pt.Length, pL, pM, total), 16); put(Vol(svd, J, jt.Length, jL, jM, total), 17); put(term, 18);
            put(PathTable(P, false), pL); put(PathTable(P, true), pM); put(PathTable(J, false), jL); put(PathTable(J, true), jM);
            foreach (var e in P) put(DirData(e, P, false), e.Lba);
            foreach (var e in J) put(DirData(e, J, true), e.Lba);
            foreach (var kv in files)
            {
                var n = kv.Value; o.Position = n.Lba * S;
                if (n.Src != null) using (var s = File.OpenRead(n.Src)) CopyRange(s, 0, n.Size, o);
                else CopyRange(f, oldLba[n] * S, n.Size, o);
            }
            o.SetLength(total * S);
        }
    }
    public void Dispose() { f.Close(); }
}

static class Tools
{
    const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    const string RadUrl = "https://www.radgametools.com/down/Bink/RADTools.7z";
    const string RadPassword = "RAD"; // published on radgametools.com/bnkdown.htm

    static void Get(string url, string path)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        var q = (HttpWebRequest)WebRequest.Create(url); q.UserAgent = "SCTKO-Patcher"; q.Timeout = 60000; q.ReadWriteTimeout = 60000;
        using (var r = q.GetResponse()) using (var s = r.GetResponseStream()) using (var f = File.Create(path))
        {
            long total = r.ContentLength, got = 0; int next = 20, n; var buf = new byte[1 << 16];
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                f.Write(buf, 0, n); got += n;
                if (total > 10000000 && got * 100 / total >= next) { Core.Log("  다운로드 " + next + "%"); next += 20; }
            }
            if (total > 0 && got != total) throw new Exception("다운로드가 중간에 끊겼습니다.");
        }
    }
    static string FileSha256(string p) { using (var h = SHA256.Create()) using (var f = File.OpenRead(p)) return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant(); }

    static string Capture(string exe, string args)
    {
        var p = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var x = Process.Start(p)) { x.ErrorDataReceived += (s, e) => { }; x.BeginErrorReadLine(); string o = x.StandardOutput.ReadToEnd(); x.WaitForExit(); return o; }
    }
    // the video step needs the subtitles filter (libass) and -fps_mode (ffmpeg 5.1+)
    public static bool FfmpegOk(string exe)
    {
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        try { return Regex.IsMatch(Capture(exe, "-hide_banner -filters"), @"\bsubtitles\b") && Capture(exe, "-hide_banner -h full").Contains("fps_mode"); }
        catch { return false; }
    }

    static string GetFfmpeg(string dir)
    {
        string zip = Path.Combine(dir, "ffmpeg.zip"), sum = zip + ".sha256", exe = Path.Combine(dir, "ffmpeg.exe");
        Core.Log("ffmpeg 내려받는 중 (gyan.dev, 약 115MB)...");
        Get(FfmpegUrl, zip); Get(FfmpegUrl + ".sha256", sum);
        if (FileSha256(zip) != File.ReadAllText(sum).Trim().Split(' ')[0].ToLowerInvariant()) throw new Exception("ffmpeg 파일의 SHA-256이 공식 값과 다릅니다.");
        using (var a = new ZipArchive(File.OpenRead(zip), ZipArchiveMode.Read))
        using (var s = a.Entries.First(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)).Open())
        using (var f = File.Create(exe)) s.CopyTo(f);
        File.Delete(zip);
        if (!FfmpegOk(exe)) throw new Exception("내려받은 ffmpeg에 자막 기능이 없습니다.");
        return exe;
    }
    static string GetRad(string dir)
    {
        string arc = Path.Combine(dir, "RADTools.7z"), setup = Path.Combine(dir, "radtools.exe"), inst = Path.Combine(dir, "rad");
        Core.Log("RAD Video Tools 내려받는 중 (radgametools.com, 약 1.2MB)...");
        Get(RadUrl, arc);
        File.WriteAllBytes(setup, SevenZip.ExtractSingle(File.ReadAllBytes(arc), RadPassword));
        Trust.Require(setup, "Epic Games", "RAD Game Tools");
        Directory.CreateDirectory(inst);
        // RunAsInvoker: unpack into temp as the normal user, with no UAC prompt and no system-wide install
        var p = new ProcessStartInfo(setup, "/S /D=" + inst) { UseShellExecute = false, CreateNoWindow = true }; p.EnvironmentVariables["__COMPAT_LAYER"] = "RunAsInvoker";
        using (var x = Process.Start(p)) { if (!x.WaitForExit(180000)) { x.Kill(); throw new Exception("RAD Video Tools 압축 해제가 끝나지 않습니다."); } }
        var rad = Path.Combine(inst, "radvideo64.exe");
        if (!File.Exists(rad)) throw new Exception("radvideo64.exe를 찾을 수 없습니다.");
        Trust.Require(rad, "Epic Games", "RAD Game Tools");
        return rad;
    }
    // ffmpeg/rad: an existing tool path, or null to fetch that tool for the duration of the job
    public static void With(string ffmpeg, string rad, Action<string, string> job)
    {
        string tmp = null;
        try
        {
            if (ffmpeg == null || rad == null)
            {
                tmp = Path.Combine(Path.GetTempPath(), "SCTKO_tools_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
                try { if (ffmpeg == null) ffmpeg = GetFfmpeg(tmp); if (rad == null) rad = GetRad(tmp); }
                catch (Exception x) { throw new Exception("도구 자동 다운로드 실패: " + x.Message + "\r\n각 공식 사이트에서 직접 받아 경로를 지정하면 계속할 수 있습니다."); }
            }
            job(ffmpeg, rad);
        }
        finally { if (tmp != null) try { Directory.Delete(tmp, true); Core.Log("임시 도구 삭제 완료"); } catch { Core.Log("주의: 임시 폴더를 지우지 못했습니다: " + tmp); } }
    }
}

// Authenticode check via WinVerifyTrust, plus a publisher name match
static class Trust
{
    [StructLayout(LayoutKind.Sequential)] struct FileInfo { public uint Size; public IntPtr Path, Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)] struct Data { public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Union; public IntPtr File; public uint State; public IntPtr StateData, Url; public uint Prov, Context; public IntPtr Settings; }
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref Data data);
    public static void Require(string path, params string[] publishers)
    {
        var p = Marshal.StringToHGlobalUni(path); var fi = new FileInfo { Size = (uint)Marshal.SizeOf(typeof(FileInfo)), Path = p };
        var m = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
        try
        {
            Marshal.StructureToPtr(fi, m, false);
            var d = new Data { Size = (uint)Marshal.SizeOf(typeof(Data)), Ui = 2, Union = 1, File = m };
            if (WinVerifyTrust(IntPtr.Zero, new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), ref d) != 0) throw new Exception(Path.GetFileName(path) + "의 디지털 서명이 유효하지 않습니다.");
            string subject = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path).Subject;
            if (!publishers.Any(subject.Contains)) throw new Exception(Path.GetFileName(path) + "의 서명자가 예상과 다릅니다: " + subject);
        }
        finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(m); }
    }
}

// Reads a .7z holding one file through a chain of AES-256 / LZMA2 / BCJ-x86 coders.
// Windows 10's tar.exe cannot read this, and no 7-Zip install can be assumed.
static class SevenZip
{
    static long Num(byte[] b, ref int p)
    {
        int first = b[p++], mask = 0x80; long v = 0;
        for (int i = 0; i < 8; i++) { if ((first & mask) == 0) return v + ((long)(first & (mask - 1)) << (8 * i)); v |= (long)b[p++] << (8 * i); mask >>= 1; }
        return v;
    }
    static void Expect(byte[] b, ref int p, int v) { if (b[p++] != v) throw new Exception("지원하지 않는 7z 구조입니다."); }
    class Coder { public string Id; public byte[] Props; public int OutSize; }
    public static byte[] ExtractSingle(byte[] a, string password)
    {
        int p = 32 + (int)BitConverter.ToInt64(a, 12);
        Expect(a, ref p, 1); Expect(a, ref p, 4); Expect(a, ref p, 6);
        int dataStart = 32 + (int)Num(a, ref p); if (Num(a, ref p) != 1) throw new Exception("지원하지 않는 7z 구조입니다.");
        Expect(a, ref p, 9); int packSize = (int)Num(a, ref p); Expect(a, ref p, 0);
        Expect(a, ref p, 7); Expect(a, ref p, 11); if (Num(a, ref p) != 1) throw new Exception("지원하지 않는 7z 구조입니다."); Expect(a, ref p, 0);
        int n = (int)Num(a, ref p); var coders = new Coder[n];
        for (int i = 0; i < n; i++)
        {
            int flag = a[p++]; if ((flag & 0x10) != 0) throw new Exception("지원하지 않는 7z 구조입니다.");
            var c = new Coder { Id = BitConverter.ToString(a, p, flag & 15).Replace("-", ""), Props = new byte[0] }; p += flag & 15;
            if ((flag & 0x20) != 0) { int l = (int)Num(a, ref p); c.Props = new byte[l]; Array.Copy(a, p, c.Props, 0, l); p += l; }
            coders[i] = c;
        }
        var nextOf = new int[n]; for (int i = 0; i < n; i++) nextOf[i] = -1; var bound = new bool[n];
        for (int i = 0; i < n - 1; i++) { int inp = (int)Num(a, ref p), o = (int)Num(a, ref p); nextOf[o] = inp; bound[inp] = true; }
        Expect(a, ref p, 12); foreach (var c in coders) c.OutSize = (int)Num(a, ref p);
        var d = new byte[packSize]; Array.Copy(a, dataStart, d, 0, packSize);
        for (int i = Array.IndexOf(bound, false); i >= 0; i = nextOf[i])
        {
            var c = coders[i];
            if (c.Id == "06F10701") d = Aes(d, c.Props, password);
            else if (c.Id == "21") d = Lzma2(d, c.OutSize);
            else if (c.Id == "03030103") X86(d);
            else throw new Exception("지원하지 않는 7z 압축 방식입니다: " + c.Id);
            if (d.Length != c.OutSize) Array.Resize(ref d, c.OutSize);
        }
        return d;
    }
    static byte[] Aes(byte[] d, byte[] pr, string password)
    {
        int cycles = pr[0] & 0x3f, saltLen = ((pr[0] >> 7) & 1) + (pr[1] >> 4), ivLen = ((pr[0] >> 6) & 1) + (pr[1] & 15);
        var iv = new byte[16]; Array.Copy(pr, 2 + saltLen, iv, 0, ivLen);
        var seed = new byte[saltLen + password.Length * 2]; Array.Copy(pr, 2, seed, 0, saltLen); Encoding.Unicode.GetBytes(password).CopyTo(seed, saltLen);
        var block = new byte[seed.Length + 8]; seed.CopyTo(block, 0); byte[] key;
        using (var h = SHA256.Create())
        {
            for (long r = 0; r < 1L << cycles; r++) { BitConverter.GetBytes(r).CopyTo(block, seed.Length); h.TransformBlock(block, 0, block.Length, null, 0); }
            h.TransformFinalBlock(new byte[0], 0, 0); key = h.Hash;
        }
        using (var a = System.Security.Cryptography.Aes.Create())
        {
            a.Mode = CipherMode.CBC; a.Padding = PaddingMode.None; a.Key = key; a.IV = iv;
            using (var t = a.CreateDecryptor()) return t.TransformFinalBlock(d, 0, d.Length);
        }
    }
    static void X86(byte[] b)
    {
        bool[] allowed = { true, true, true, false, true, false, false, false }; int[] bitNum = { 0, 1, 2, 2, 3, 3, 3, 3 };
        uint mask = 0; int prev = -5, i = 0, limit = b.Length - 4;
        Func<byte, bool> ms = x => x == 0 || x == 0xFF;
        while (i < limit)
        {
            if (b[i] != 0xE8 && b[i] != 0xE9) { i++; continue; }
            int off = i - prev; prev = i;
            if (off > 5) mask = 0; else for (int k = 0; k < off; k++) { mask &= 0x77; mask <<= 1; }
            byte top = b[i + 4];
            if (ms(top) && allowed[(mask >> 1) & 7] && (mask >> 1) < 0x10)
            {
                uint src = (uint)top << 24 | (uint)b[i + 3] << 16 | (uint)b[i + 2] << 8 | b[i + 1], dest;
                while (true)
                {
                    dest = src - (uint)(i + 5);
                    if (mask == 0) break;
                    int idx = bitNum[mask >> 1];
                    if (!ms((byte)(dest >> (24 - idx * 8 - 8)))) break;
                    src = dest ^ ((1u << (32 - idx * 8)) - 1);
                }
                b[i + 4] = (byte)~(((dest >> 24) & 1) - 1); b[i + 3] = (byte)(dest >> 16); b[i + 2] = (byte)(dest >> 8); b[i + 1] = (byte)dest;
                i += 5; mask = 0;
            }
            else { i++; mask |= 1; if (ms(top)) mask |= 0x10; }
        }
    }

    // ---------- LZMA2 ----------
    const int IsRep = 192, RepG0 = 204, RepG1 = 216, RepG2 = 228, Rep0Long = 240, PosSlot = 432, Align = 688, PosDec = 704, LenA = 820, LenB = 1334, Lit = 1848;
    static byte[] Lzma2(byte[] s, int size)
    {
        var d = new Lz { O = new byte[size], S = s }; int p = 0;
        while (true)
        {
            int c = s[p++]; if (c == 0) break;
            if (c < 0x80)
            {
                if (c > 2) throw new Exception("LZMA2 데이터 오류");
                if (c == 1) d.DictStart = d.Op;
                int n = (s[p] << 8 | s[p + 1]) + 1; p += 2; Array.Copy(s, p, d.O, d.Op, n); p += n; d.Op += n; continue;
            }
            int un = ((c & 0x1f) << 16) + (s[p] << 8) + s[p + 1] + 1, pack = (s[p + 2] << 8) + s[p + 3] + 1, reset = (c >> 5) & 3; p += 4;
            if (reset == 3) d.DictStart = d.Op;
            if (reset >= 2) d.SetProps(s[p++]);
            if (reset >= 1) d.Reset();
            d.Sp = p; d.Decode(d.Op + un); p += pack;
        }
        return d.O;
    }
    class Lz
    {
        public byte[] O, S; public int Op, Sp, DictStart;
        int lc, lpMask, pbMask, st; uint range, code, r0, r1, r2, r3; ushort[] P;
        public void SetProps(int b) { lc = b % 9; b /= 9; pbMask = (1 << (b / 5)) - 1; int lp = b % 5; lpMask = (1 << lp) - 1; P = new ushort[Lit + (0x300 << (lc + lp))]; }
        public void Reset() { for (int i = 0; i < P.Length; i++) P[i] = 1024; st = 0; r0 = r1 = r2 = r3 = 0; }
        void Norm() { if (range < (1u << 24)) { range <<= 8; code = (code << 8) | S[Sp++]; } }
        int Bit(int i)
        {
            uint b = (range >> 11) * P[i]; int r;
            if (code < b) { range = b; P[i] = (ushort)(P[i] + ((2048 - P[i]) >> 5)); r = 0; }
            else { range -= b; code -= b; P[i] = (ushort)(P[i] - (P[i] >> 5)); r = 1; }
            Norm(); return r;
        }
        int Tree(int b, int bits) { int m = 1; for (int i = 0; i < bits; i++) m = (m << 1) + Bit(b + m); return m - (1 << bits); }
        int RevTree(int b, int bits) { int m = 1, sym = 0; for (int i = 0; i < bits; i++) { int x = Bit(b + m); m = (m << 1) + x; sym |= x << i; } return sym; }
        uint Direct(int n) { uint v = 0; while (n-- > 0) { range >>= 1; code -= range; uint t = 0 - (code >> 31); code += range & t; v = (v << 1) + t + 1; Norm(); } return v; }
        int Len(int b, int ps) { if (Bit(b) == 0) return Tree(b + 2 + ps * 8, 3); if (Bit(b + 1) == 0) return 8 + Tree(b + 130 + ps * 8, 3); return 16 + Tree(b + 258, 8); }
        public void Decode(int end)
        {
            range = 0xFFFFFFFF; code = 0; Sp++; for (int i = 0; i < 4; i++) code = (code << 8) | S[Sp++];
            while (Op < end)
            {
                int pos = Op - DictStart, ps = pos & pbMask;
                if (Bit((st << 4) + ps) == 0)
                {
                    int prev = Op > DictStart ? O[Op - 1] : 0, lb = Lit + 0x300 * (((pos & lpMask) << lc) + (prev >> (8 - lc))), sym = 1;
                    if (st >= 7)
                    {
                        int mb = O[Op - (int)r0 - 1];
                        do { int mbit = (mb >> 7) & 1; mb <<= 1; int x = Bit(lb + ((1 + mbit) << 8) + sym); sym = (sym << 1) | x; if (mbit != x) break; } while (sym < 0x100);
                    }
                    while (sym < 0x100) sym = (sym << 1) | Bit(lb + sym);
                    O[Op++] = (byte)sym; st = st < 4 ? 0 : st < 10 ? st - 3 : st - 6; continue;
                }
                int len;
                if (Bit(IsRep + st) == 0)
                {
                    r3 = r2; r2 = r1; r1 = r0; len = Len(LenA, ps); st = st < 7 ? 7 : 10;
                    int slot = Tree(PosSlot + (len < 4 ? len : 3) * 64, 6);
                    if (slot < 4) r0 = (uint)slot;
                    else
                    {
                        int n = (slot >> 1) - 1; uint dist = (uint)(2 | (slot & 1)) << n;
                        if (slot < 14) dist += (uint)RevTree(PosDec + (int)dist - slot, n);
                        else { dist += Direct(n - 4) << 4; dist += (uint)RevTree(Align, 4); }
                        r0 = dist;
                    }
                }
                else
                {
                    if (Bit(RepG0 + st) == 0)
                    {
                        if (Bit(Rep0Long + (st << 4) + ps) == 0) { st = st < 7 ? 9 : 11; O[Op] = O[Op - (int)r0 - 1]; Op++; continue; }
                    }
                    else
                    {
                        uint dist;
                        if (Bit(RepG1 + st) == 0) dist = r1;
                        else { if (Bit(RepG2 + st) == 0) dist = r2; else { dist = r3; r3 = r2; } r2 = r1; }
                        r1 = r0; r0 = dist;
                    }
                    len = Len(LenB, ps); st = st < 7 ? 8 : 11;
                }
                len += 2; if (r0 >= Op - DictStart) throw new Exception("LZMA2 데이터 오류");
                for (int i = 0; i < len; i++) { O[Op] = O[Op - (int)r0 - 1]; Op++; }
            }
        }
    }
}

class MainForm : Form
{
    TextBox game = Box(), rp = Box(), elfDir = Box(), ff = Box(), rad = Box(), log = new TextBox();
    RadioButton pc = new RadioButton { Text = "PC (RPCS3)", Checked = true, AutoSize = true }, hw = new RadioButton { Text = "PS3 실기 (커스텀 펌웨어)", AutoSize = true };
    CheckBox exe = new CheckBox { Text = "실행 파일(ELF) 패치 (필수 권장)", Checked = true, AutoSize = true };
    CheckBox vid = new CheckBox { Text = "동영상에 한글 자막 입히기 (선택, 인터넷 필요, 오래 걸림)", AutoSize = true };
    CheckBox v1 = new CheckBox { Text = "1편", Checked = true, AutoSize = true }, v2 = new CheckBox { Text = "2편 (판도라 투모로우)", Checked = true, AutoSize = true }, v3 = new CheckBox { Text = "3편 (카오스 씨어리)", Checked = true, AutoSize = true };
    Button go = new Button { Text = "설치", Width = 110, Height = 32 }, undo = new Button { Text = "되돌리기", Width = 110, Height = 32 }, ext = new Button { Text = "ISO에서 실행 파일 꺼내기", Width = 190, Height = 32 };
    Label elfLabel = new Label();
    static TextBox Box() { return new TextBox { Width = 470 }; }
    int y = 14;
    void Row(string label, TextBox t, bool file, string filter)
    {
        Controls.Add(new Label { Text = label, Left = 12, Top = y + 3, Width = 120 }); t.Left = 135; t.Top = y; Controls.Add(t);
        var b = new Button { Text = "찾기", Left = 612, Top = y - 1, Width = 60 }; Controls.Add(b);
        b.Click += (s, e) => { if (file) { var d = new OpenFileDialog { Filter = filter }; if (d.ShowDialog() == DialogResult.OK) t.Text = d.FileName; } else { var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) t.Text = d.SelectedPath; } };
        y += 32;
    }
    public MainForm()
    {
        Text = Core.Title; ClientSize = new Size(690, 610); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; Font = new Font("Malgun Gothic", 9);
        Controls.Add(new Label { Text = "게임 폴더 / ISO", Left = 12, Top = y + 3, Width = 120 }); game.Left = 135; game.Top = y; game.Width = 405; Controls.Add(game);
        var pickFolder = new Button { Text = "폴더", Left = 547, Top = y - 1, Width = 60 }; var pickIso = new Button { Text = "ISO", Left = 612, Top = y - 1, Width = 60 }; Controls.Add(pickFolder); Controls.Add(pickIso);
        pickFolder.Click += (s, e) => { var d = new FolderBrowserDialog { Description = "PS3_GAME 폴더가 들어 있는 게임 폴더" }; if (d.ShowDialog() == DialogResult.OK) game.Text = d.SelectedPath; };
        pickIso.Click += (s, e) => { var d = new OpenFileDialog { Filter = "PS3 ISO (*.iso)|*.iso" }; if (d.ShowDialog() == DialogResult.OK) game.Text = d.FileName; };
        y += 32;
        pc.Left = 135; pc.Top = y; hw.Left = 280; hw.Top = y; Controls.Add(pc); Controls.Add(hw); y += 30;
        Row("RPCS3 폴더", rp, false, null);
        Controls.Add(new Label { Text = "RPCS3에 이미 설치된 게임 데이터(dev_hdd0\\game\\BLUS30761DATA)도 함께 패치합니다.", Left = 135, Top = y - 6, AutoSize = true, ForeColor = Color.DimGray }); y += 18;
        exe.Left = 135; exe.Top = y; Controls.Add(exe); y += 26;
        Row("복호화 ELF 폴더", elfDir, false, null);
        Controls.Add(new Label { Text = "RPCS3 [Utilities → Decrypt PS3 Binaries]로 sc1/sc2/sc3.ppu.self를 복호화한 .elf가 있는 폴더.\r\n폴더 설치는 게임 폴더 안의 .elf를 자동으로 찾습니다.", Left = 135, Top = y - 6, AutoSize = true, ForeColor = Color.DimGray }); y += 34;
        elfLabel.Left = 135; elfLabel.Top = y - 6; elfLabel.AutoSize = true; elfLabel.ForeColor = Color.Firebrick; Controls.Add(elfLabel); if (Core.Yml) y += 18;
        vid.Left = 135; vid.Top = y; Controls.Add(vid); y += 26;
        v1.Left = 155; v1.Top = y; v2.Left = 225; v2.Top = y; v3.Left = 395; v3.Top = y; Controls.AddRange(new Control[] { v1, v2, v3 }); y += 28;
        Row("ffmpeg.exe", ff, true, "ffmpeg|ffmpeg.exe"); Row("radvideo64.exe", rad, true, "RAD Video Tools|radvideo64.exe");
        Controls.Add(new Label { Text = "도구 경로를 비워 두면 공식 사이트에서 임시 폴더로 자동으로 받아 쓰고, 끝나면 지웁니다.", Left = 135, Top = y - 6, AutoSize = true, ForeColor = Color.DimGray }); y += 22;
        go.Left = 135; go.Top = y; undo.Left = 255; undo.Top = y; ext.Left = 375; ext.Top = y; Controls.AddRange(new Control[] { go, undo, ext }); y += 42;
        log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.Left = 12; log.Top = y; log.Width = 660; log.Height = 600 - y; Controls.Add(log);
        Core.Log = s =>
        {
            if (IsDisposed) return; Action add = () => log.AppendText(s.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n");
            if (IsHandleCreated && InvokeRequired) BeginInvoke(add); else add();
        };
        if (Core.Yml) { exe.Text = "실행 파일 한글 패치 (PC: RPCS3 게임 패치로 적용, 복호화 불필요)"; elfLabel.Text = "PS3 실기 전용: 실기는 복호화한 .elf로 패치한 뒤 재서명이 필요합니다."; }
        Action sync = () =>
        {
            foreach (var c in new Control[] { v1, v2, v3, ff, rad }) c.Enabled = vid.Checked;
            elfDir.Enabled = ext.Enabled = exe.Checked && (!Core.Yml || hw.Checked); rp.Enabled = pc.Checked;
        };
        vid.CheckedChanged += (s, e) => sync(); exe.CheckedChanged += (s, e) => sync(); pc.CheckedChanged += (s, e) => sync(); sync();
        Func<string, string> onPath = n => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(d => { try { return Path.Combine(d.Trim(), n); } catch { return ""; } }).FirstOrDefault(File.Exists) ?? "";
        ff.Text = onPath("ffmpeg.exe"); rad.Text = onPath("radvideo64.exe");
        try { Core.Log("패치 데이터: " + Pack.Load().Count + "개 파일 / 영상 자막: " + Subs.All().Count + "개"); } catch (Exception x) { Core.Log("오류: " + x.Message); }
        go.Click += (s, e) =>
        {
            var o = new Installer.Options { Game = game.Text.Trim(), Rpcs3 = pc.Checked ? rp.Text.Trim() : null, ElfDir = elfDir.Text.Trim(), Exe = exe.Checked, RealPs3 = hw.Checked };
            if (vid.Checked) { if (v1.Checked) o.VideoGames.Add(1); if (v2.Checked) o.VideoGames.Add(2); if (v3.Checked) o.VideoGames.Add(3); }
            if (o.Video)
            {
                bool getFf = !Tools.FfmpegOk(ff.Text), getRad = !File.Exists(rad.Text);
                if ((getFf || getRad) && MessageBox.Show(this, Consent(getFf, getRad), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) { Core.Log("취소했습니다. 도구 경로를 직접 지정하거나 영상 자막 체크를 해제해 주세요."); return; }
                o.Ffmpeg = getFf ? null : ff.Text; o.Rad = getRad ? null : rad.Text;
            }
            if (Core.IsIso(o.Game)) Job(() => { Installer.IsoInstall(o); Core.Log(o.RealPs3 ? "완료. 한글 ISO와 재서명한 실행 파일을 PS3에서 사용하세요. PS3에 이미 설치된 게임 데이터(BLUS30761DATA)는 지워 주세요." : "완료. RPCS3에서 위의 _KOR.iso를 실행하세요."); });
            else Job(() => Installer.Folder(o));
        };
        undo.Click += (s, e) => Job(() =>
        {
            if (!Core.IsIso(game.Text)) Installer.Uninstall(Core.UsrDir(game.Text)); else Core.Log("ISO 설치는 만들어진 _KOR.iso 파일을 지우면 됩니다.");
            var h = Core.HddUsr(pc.Checked ? rp.Text.Trim() : null); if (h != null) Installer.Uninstall(h);
            if (pc.Checked) Rpcs3Patch.Uninstall(rp.Text.Trim());
        });
        ext.Click += (s, e) => { if (!Core.IsIso(game.Text)) { Core.Log("먼저 게임 ISO를 지정해 주세요. (폴더 설치는 게임 폴더의 sc1/sc2/sc3.ppu.self를 RPCS3로 바로 복호화하면 됩니다.)"); return; } Job(() => Installer.ExtractSelfs(game.Text)); };
    }
    static string Consent(bool getFf, bool getRad)
    {
        var s = new StringBuilder("영상 자막을 만들려면 아래 무료 도구가 필요합니다.\r\n공식 사이트에서 받아 임시 폴더에서 사용하고, 작업이 끝나면 지웁니다.\r\n\r\n");
        if (getFf) s.Append("• ffmpeg (gyan.dev, 약 100MB, GPL 라이선스)\r\n");
        if (getRad) s.Append("• RAD Video Tools (radgametools.com, 약 1.2MB)\r\n");
        if (getRad) s.Append("\r\nRAD Video Tools 사용권의 주요 제한:\r\n재배포 금지, 대여·재판매 금지, 수정·역공학 금지, 허가 없는 상업적 사용 금지\r\n");
        return s.Append("\r\n위 내용에 동의하고 내려받을까요?").ToString();
    }
    void Job(Action a)
    {
        go.Enabled = undo.Enabled = ext.Enabled = false;
        new Thread(() => { try { a(); } catch (Exception x) { Core.Log("오류: " + x.Message); } BeginInvoke((Action)(() => { go.Enabled = undo.Enabled = true; ext.Enabled = elfDir.Enabled; })); }) { IsBackground = true }.Start();
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length == 0) { Application.EnableVisualStyles(); Application.Run(new MainForm()); return 0; }
        // --game P [--rpcs3 D] [--elf-dir D] [--no-exe] [--ps3] [--video 123] [--ffmpeg F] [--rad F] [--uninstall] [--extract-self] [--log F]
        // --make-pack --patched D [--orig ISO|D] --out F
        var o = new Dictionary<string, string>(); var flags = new[] { "--no-exe", "--ps3", "--uninstall", "--extract-self", "--make-pack" };
        for (int i = 0; i < a.Length; i++) { if (flags.Contains(a[i])) o[a[i]] = "1"; else if (i + 1 < a.Length) o[a[i]] = a[++i]; }
        Func<string, string> G = k => o.ContainsKey(k) ? o[k] : null;
        var w = new StreamWriter(G("--log") ?? "patcher.log", false, new UTF8Encoding(false)) { AutoFlush = true };
        Core.Log = s => { w.WriteLine(s); try { Console.WriteLine(s); } catch { } };
        try
        {
            if (G("--make-pack") != null) { Pack.Make(G("--orig"), G("--patched"), G("--out") ?? "SCT_KO_data.pack"); w.WriteLine("OK"); return 0; }
            if (G("--extract-self") != null) { Installer.ExtractSelfs(G("--game")); return 0; }
            if (G("--uninstall") != null)
            {
                if (!Core.IsIso(G("--game")) && G("--game") != null) Installer.Uninstall(Core.UsrDir(G("--game")));
                var h = Core.HddUsr(G("--rpcs3")); if (h != null) Installer.Uninstall(h); Rpcs3Patch.Uninstall(G("--rpcs3")); return 0;
            }
            var opt = new Installer.Options { Game = G("--game"), Rpcs3 = G("--rpcs3"), ElfDir = G("--elf-dir"), Exe = G("--no-exe") == null, RealPs3 = G("--ps3") != null, Ffmpeg = G("--ffmpeg"), Rad = G("--rad") };
            foreach (var c in G("--video") ?? "") if (c >= '1' && c <= '3') opt.VideoGames.Add(c - '0');
            if (Core.IsIso(opt.Game)) Installer.IsoInstall(opt); else Installer.Folder(opt);
            w.WriteLine("OK"); return 0;
        }
        catch (Exception x) { w.WriteLine("ERROR " + x); try { Console.WriteLine("ERROR " + x.Message); } catch { } return 1; }
        finally { w.Close(); }
    }
}
