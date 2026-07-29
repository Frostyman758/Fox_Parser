// Uif GZ to TPP transform: node repack + shared-pool geometry
// 09/07/2026
using System.Buffers.Binary;
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Ui.Uif;

public static class UifConvert
{
    // technique str32 -> constant param (whole TPP corpus, stable per technique)
    static readonly Dictionary<uint, uint> TechniqueParam = new()
    {
        [0xC165ABAD] = 359,     // fox_2d_Basic_LyBL
        [0x1A676027] = 1651,    // fox_2d_Basic_LyADD
        [0x7104866A] = 17803,   // fox_2d_Basic_LyMUL
        [0x53467B49] = 52522,   // fox_2d_Basic_LyBL_CenteringScreen
        [0xC6C07AAF] = 49634,   // tpp_2d_DirDisp
        [0x4D3379AB] = 33623,   // tpp_2d_Map (param unseen in corpus tables; fsop mined TBD)
        [0x82F8CA62] = 33624,   // tpp_2d_Map_ScreenMask (TBD)
    };

    sealed class Ctx
    {
        public byte[] Src;
        public GzUif Gz;
        public List<byte> Out = new();
        public List<byte> Buffers = new();      // remap/idx/tri tables; pools PREPENDED at assembly
        public List<byte> VertPool = new(), UvPool = new(), ColorPool = new();
        public List<int> TableOffFixups = new();      // out positions of table offsets (shift by pools total)
        public List<int> StencilSizeFixups = new();   // out positions of stencil +0x28 (= final strRel)
        public Func<uint, uint> Extent;               // node data end boundary
        public List<string> Log = new();
    }

    static ushort U16(byte[] d, int o) => GzUif.U16(d, o);
    static uint U32(byte[] d, int o) => GzUif.U32(d, o);
    static void P16(List<byte> b, ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); b.AddRange(s); }
    static void P32(List<byte> b, uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); b.AddRange(s); }
    static void P64(List<byte> b, ulong v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(s, v); b.AddRange(s); }
    static void Patch32(List<byte> b, int at, uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); for (int i = 0; i < 4; i++) b[at + i] = s[i]; }

    public static byte[] GzToTpp(byte[] src, out List<string> log)
    {
        var c = new Ctx { Src = src, Gz = GzUif.Read(src) };
        var d = src; var gz = c.Gz; var b = c.Out;

        // node data extents (text tails are positional, no offset fields)
        var bounds = gz.Nodes.Where(n => n.DataOff is not (0 or 0xFFFFFFFF)).Select(n => n.DataOff)
            .Append(gz.BuffersOff).Append(GzUif.U32(d, 0x18)).Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
        c.Extent = off => { foreach (var x in bounds) if (x > off) return x; return gz.BuffersOff; };

        P32(b, 0x20464955); P32(b, 0x202);
        P16(b, (ushort)(gz.Flags | 2));         // bit1 set on every TPP uif (796/796)
        P16(b, (ushort)gz.Nodes.Count);
        P16(b, (ushort)gz.Ids.Count);           // TPP counts u32 hashes
        P16(b, (ushort)gz.Paths.Count);
        P32(b, 0x30);                           // node table
        for (int i = 0; i < 7; i++) P32(b, 0);  // 0x14..0x2C patched

        int nodeTable = b.Count;
        foreach (var n in gz.Nodes) { P16(b, (ushort)n.NameIdx); P16(b, n.Type); P32(b, 0); }
        P16(b, 0xFFFF); P16(b, 0); P32(b, 0);   // sentinel entry

        for (int i = 0; i < gz.Nodes.Count; i++)
        {
            var n = gz.Nodes[i];
            uint newOff;
            if (n.Type == 0 || n.DataOff is 0 or 0xFFFFFFFF) newOff = n.DataOff;   // Null: engine ignores
            else
            {
                newOff = (uint)b.Count;
                WriteNodeData(c, n);
            }
            Patch32(b, nodeTable + i * 8 + 4, newOff);
        }

        // buffers region: pools FIRST (engine sizes streams from consecutive rel offsets,
        // 796/796 TPP files: vertsRel=0 <= uvsRel <= colorsRel, all set), tables after
        uint vertsRel = 0;
        uint uvsRel = (uint)c.VertPool.Count;
        uint colorsRel = uvsRel + (uint)c.UvPool.Count;
        uint tableShift = colorsRel + (uint)c.ColorPool.Count;
        foreach (int at in c.TableOffFixups)
        {
            uint v = (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24)) + tableShift;
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }
        while (b.Count % 16 != 0) b.Add(0);
        uint buffers = (uint)b.Count;
        b.AddRange(c.VertPool); b.AddRange(c.UvPool); b.AddRange(c.ColorPool);
        b.AddRange(c.Buffers);

        uint strRel = (uint)b.Count - buffers;
        foreach (int at in c.StencilSizeFixups)
        {
            b[at] = (byte)strRel; b[at + 1] = (byte)(strRel >> 8);
        }
        foreach (var id in gz.Ids) P32(b, (uint)id);
        while ((b.Count - buffers) % 8 != 0) b.Add(0);

        uint techRel = (uint)b.Count - buffers;
        uint tech = PickTechnique(gz);
        if (tech != 0)
        {
            if (!TechniqueParam.TryGetValue(tech, out var param)) { param = 359; c.Log.Add($"unknown technique {tech:x8}, LyBL param used"); }
            P32(b, tech); P32(b, param);
        }
        uint pathRel = (uint)b.Count - buffers;
        foreach (var p in gz.Paths)
        {
            var (mapped, wasAs) = ResolveTexPath(p);
            if (wasAs && mapped == p) c.Log.Add($"unmapped /as/ texture ref kept as-is: {p.Length} chars");
            P64(b, mapped.Length == 0 ? 0ul : GameHash.PathCode(mapped));
        }

        Patch32(b, 0x14, strRel); Patch32(b, 0x18, pathRel); Patch32(b, 0x1C, buffers);
        Patch32(b, 0x20, vertsRel); Patch32(b, 0x24, uvsRel); Patch32(b, 0x28, colorsRel);
        Patch32(b, 0x2C, techRel);
        log = c.Log;
        return b.ToArray();
    }

    static readonly Lazy<Dictionary<string, string>> _texMap = new(LoadTexMap, isThreadSafe: true);

    static Dictionary<string, string> LoadTexMap()
    {
        var m = new Dictionary<string, string>();
        var asm = typeof(UifConvert).Assembly;
        var res = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("gz_as_texmap.tsv", StringComparison.Ordinal));
        if (res == null) return m;
        using var r = new StreamReader(asm.GetManifestResourceStream(res)!);
        string line;
        while ((line = r.ReadLine()) != null)
        {
            int t = line.IndexOf('\t');
            if (t <= 0) continue;
            m[System.Text.Encoding.UTF8.GetString(Convert.FromHexString(line[..t]))] = line[(t + 1)..];
        }
        return m;
    }

    public static (string path, bool wasAs) ResolveTexPath(string p)
    {
        if (!p.StartsWith("/as/", StringComparison.Ordinal)) return (p, false);
        return _texMap.Value.TryGetValue(p, out var real) ? (real, true) : (p, true);
    }

    // technique = str-table entry whose low32 is a known technique (MUL > ADD > BL > rest)
    static uint PickTechnique(GzUif gz)
    {
        uint mul = 0, add = 0, bl = 0, other = 0;
        foreach (var id in gz.Ids)
            switch ((uint)id)
            {
                case 0x7104866A: mul = (uint)id; break;
                case 0x1A676027: add = (uint)id; break;
                case 0xC165ABAD: bl = (uint)id; break;
                case 0x53467B49 or 0x4D3379AB or 0x82F8CA62 or 0xC6C07AAF: other = (uint)id; break;
            }
        return mul != 0 ? mul : add != 0 ? add : bl != 0 ? bl : other;
    }

    static void WriteNodeData(Ctx c, GzUifNode n)
    {
        var d = c.Src; var b = c.Out;
        int g = (int)n.DataOff;

        // common 0x50 -> 0x4C; float priority → i16 (corpus scale x4095: 0.5→2047, 0.1→409)
        float gzPrio = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(g + 0x38, 4));
        short prio = (short)Math.Clamp(MathF.Round(gzPrio * 4095f), short.MinValue, short.MaxValue);
        b.Add(d[g]); b.Add(d[g + 1]);
        b.Add((byte)prio); b.Add((byte)(prio >> 8));
        for (int i = 4; i < 0x38; i++) b.Add(d[g + i]);
        for (int i = 0x3C; i < 0x4C; i++) b.Add(d[g + i]);          // color
        b.Add(d[g + 0x4C]); b.Add(d[g + 0x4D]);                     // secondary idx
        b.Add(d[g + 0x4E]); b.Add(d[g + 0x4F]);                     // palette idx

        switch (n.Type)
        {
            case 2: WriteMeshInfo(c, g, stencil: false); break;
            case 4: WriteMeshInfo(c, g, stencil: true); break;
            case 3:                                                 // text -4 shift + positional tail
                for (int i = 0x50; i < 0x8C; i++) b.Add(g + i < d.Length ? d[g + i] : (byte)0);
                uint end = c.Extent((uint)g);
                for (uint i = (uint)g + 0x8C; i < end; i++) b.Add(d[i]);   // index-based param lists, copy verbatim
                break;
            case 5: throw new NotSupportedException("line nodes not supported yet");
        }
    }

    static void WriteMeshInfo(Ctx c, int g, bool stencil)
    {
        var d = c.Src; var b = c.Out; uint bufs = c.Gz.BuffersOff;
        int mf = g + 0x50;
        int v = U16(d, mf), tri = U16(d, mf + 2);
        uint vertsOff = U32(d, mf + 4), uvsOff = U32(d, mf + 8), colorsOff = U32(d, mf + 0xC);
        uint idxOff = U32(d, mf + 0x10), triOff = U32(d, mf + 0x14);
        int cCnt = U16(d, mf + 0x18), vCnt2 = U16(d, mf + 0x1A);
        uint cOff = U32(d, mf + 0x1C), vOff = U32(d, mf + 0x20);

        // shared pools + identity remaps (u16 tables in buffers)
        uint Remap(uint gzDirectOff, List<byte> pool)
        {
            if (gzDirectOff == 0xFFFFFFFF) return 0xFFFFFFFF;
            int baseRec = pool.Count / 16;
            for (int i = 0; i < v * 16; i++) pool.Add(d[bufs + gzDirectOff + i]);
            uint at = (uint)c.Buffers.Count;
            for (int i = 0; i < v; i++) P16(c.Buffers, (ushort)(baseRec + i));
            return at;
        }
        uint vertexRemap = Remap(vertsOff, c.VertPool);
        uint uvRemap = Remap(uvsOff, c.UvPool);
        uint colorIdx = Remap(colorsOff, c.ColorPool);
        uint uvIdx = 0xFFFFFFFF;
        if (idxOff != 0xFFFFFFFF)
        {
            uvIdx = (uint)c.Buffers.Count;
            for (int i = 0; i < v * 2; i++) c.Buffers.Add(d[bufs + idxOff + i]);
        }
        uint triIdx = 0xFFFFFFFF;
        if (triOff != 0xFFFFFFFF)
        {
            triIdx = (uint)c.Buffers.Count;
            for (int i = 0; i < tri * 6; i++) c.Buffers.Add(d[bufs + triOff + i]);
            while (c.Buffers.Count % 4 != 0) c.Buffers.Add(0);
        }

        int mfOut = b.Count;                    // TPP meshinfo 0x38, offsets patched below
        for (int i = 0; i < 0x38; i++) b.Add(0);
        // controls: GZ 16 B {desc 8, f32 a, f32 b} → TPP 24 B {desc 8, vec4 (a,b,0,0)}
        uint WriteCtrls(int count, uint off)
        {
            if (count == 0 || off is 0 or 0xFFFFFFFF) return 0xFFFFFFFF;
            uint at = (uint)b.Count;
            for (int k = 0; k < count; k++)
            {
                int r = (int)off + k * 16;
                for (int i = 0; i < 8; i++) b.Add(d[r + i]);        // descriptor kept verbatim
                for (int i = 0; i < 8; i++) b.Add(d[r + 8 + i]);    // params a, b
                for (int i = 0; i < 8; i++) b.Add(0);
            }
            c.Log.Add($"mesh@0x{g:x}: {count} vertex control(s) converted fail-soft (verify in-game)");
            return at;
        }
        uint cCtrls = WriteCtrls(cCnt, cOff);
        uint vCtrls = WriteCtrls(vCnt2, vOff);

        if (stencil)
        {
            // tail +0x24..0x38 copied; +0x28 = geometry bytes before strT (patched later)
            Span<byte> st = stackalloc byte[0x38];
            BinaryPrimitives.WriteUInt16LittleEndian(st[0..], (ushort)v);
            BinaryPrimitives.WriteUInt16LittleEndian(st[2..], (ushort)tri);
            BinaryPrimitives.WriteUInt32LittleEndian(st[4..], vertexRemap);
            BinaryPrimitives.WriteUInt32LittleEndian(st[8..], uvRemap);
            BinaryPrimitives.WriteUInt32LittleEndian(st[0xC..], colorIdx);
            BinaryPrimitives.WriteUInt32LittleEndian(st[0x10..], uvIdx);
            BinaryPrimitives.WriteUInt32LittleEndian(st[0x14..], triIdx);
            BinaryPrimitives.WriteUInt16LittleEndian(st[0x18..], (ushort)cCnt);
            BinaryPrimitives.WriteUInt16LittleEndian(st[0x1A..], (ushort)vCnt2);
            BinaryPrimitives.WriteUInt32LittleEndian(st[0x1C..], cCtrls);
            BinaryPrimitives.WriteUInt32LittleEndian(st[0x20..], vCtrls);
            for (int i = 0x24; i < 0x38; i++) st[i] = d[mf + i];
            for (int i = 0; i < 0x38; i++) b[mfOut + i] = st[i];
            RegisterTableFixups(c, mfOut);
            c.StencilSizeFixups.Add(mfOut + 0x28);
            return;
        }

        int texCnt = U16(d, mf + 0x28), shCnt = U16(d, mf + 0x2A);
        uint texOff = U32(d, mf + 0x2C), shOff = U32(d, mf + 0x30), bbOff = U32(d, mf + 0x34);
        uint texAt = 0xFFFFFFFF, shAt = 0xFFFFFFFF, bbAt = 0xFFFFFFFF;
        if (texCnt > 0 && texOff != 0xFFFFFFFF)
        {
            texAt = (uint)b.Count;
            for (int i = 0; i < texCnt * 4; i++) b.Add(d[texOff + i]);
        }
        if (shCnt > 0 && shOff != 0xFFFFFFFF)
        {
            shAt = (uint)b.Count;
            for (int i = 0; i < shCnt * 8; i++) b.Add(d[shOff + i]);
        }
        if (bbOff != 0xFFFFFFFF && bbOff != 0)
        {
            bbAt = (uint)b.Count;
            for (int i = 0; i < 16 && bbOff + i < d.Length; i++) b.Add(d[bbOff + i]);
        }
        while (b.Count % 4 != 0) b.Add(0);

        Span<byte> s = stackalloc byte[0x38];
        BinaryPrimitives.WriteUInt16LittleEndian(s[0..], (ushort)v);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)tri);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], vertexRemap);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], uvRemap);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0xC..], colorIdx);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x10..], uvIdx);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], triIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x18..], (ushort)cCnt);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x1A..], (ushort)vCnt2);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1C..], cCtrls);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x20..], vCtrls);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x24..], U16(d, mf + 0x24));
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x26..], U16(d, mf + 0x26));
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x28..], (ushort)texCnt);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x2A..], (ushort)shCnt);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x2C..], texAt);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x30..], shAt);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x34..], bbAt);
        for (int i = 0; i < 0x38; i++) b[mfOut + i] = s[i];
        RegisterTableFixups(c, mfOut);
    }

    // remap/uvIdx/colorIdx/tri offsets were table-list relative; shifted past pools at assembly
    static void RegisterTableFixups(Ctx c, int mfOut)
    {
        foreach (int f in new[] { 4, 8, 0xC, 0x10, 0x14 })
        {
            int at = mfOut + f;
            uint v = (uint)(c.Out[at] | (c.Out[at + 1] << 8) | (c.Out[at + 2] << 16) | (c.Out[at + 3] << 24));
            if (v != 0xFFFFFFFF) c.TableOffFixups.Add(at);
        }
    }
}
