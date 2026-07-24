// uif convert gate: independent TPP parse vs GZ source
// 09/07/2026
using System.Buffers.Binary;
using MgsvModBldr.Tools.GameHashing;
using MgsvModBldr.Tools.Ui.Uif;

namespace MgsvModBldr.Tools.Ui.Tests;

/// <summary>
/// Parses converter output with the TPP formulas (shared pools + remap
/// tables) and checks geometry, ids and texture refs match the GZ source.
/// </summary>
public static class UifGate
{
    static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));
    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));
    static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o, 8));

    public static (bool ok, string note) Check(string path)
    {
        var src = File.ReadAllBytes(path);
        var gz = GzUif.Read(src);
        byte[] tpp;
        try { tpp = UifConvert.GzToTpp(src, out _); }
        catch (NotSupportedException e) { return (true, $"skip ({e.Message})"); }

        if (U32(tpp, 4) != 0x202) return (false, "version");
        if (U16(tpp, 0x0A) != gz.Nodes.Count) return (false, "node count");
        if (U16(tpp, 0x0C) != gz.Ids.Count) return (false, "str count");
        if (U16(tpp, 0x0E) != gz.Paths.Count) return (false, "path count");

        uint buffers = U32(tpp, 0x1C), strRel = U32(tpp, 0x14), pathRel = U32(tpp, 0x18);
        uint vertsRel = U32(tpp, 0x20), uvsRel = U32(tpp, 0x24);
        for (int i = 0; i < gz.Ids.Count; i++)
            if (U32(tpp, (int)(buffers + strRel) + i * 4) != (uint)gz.Ids[i]) return (false, $"id {i}");
        for (int i = 0; i < gz.Paths.Count; i++)
        {
            var (mapped, _) = UifConvert.ResolveTexPath(gz.Paths[i]);
            ulong want = mapped.Length == 0 ? 0ul : GameHash.PathCode(mapped);
            if (U64(tpp, (int)(buffers + pathRel) + i * 8) != want) return (false, $"path {i}");
        }

        uint nodesOff = U32(tpp, 0x10);
        for (int i = 0; i < gz.Nodes.Count; i++)
        {
            var gn = gz.Nodes[i];
            int to = (int)nodesOff + i * 8;
            if (U16(tpp, to) != (ushort)gn.NameIdx || U16(tpp, to + 2) != gn.Type) return (false, $"node hdr {i}");
            uint tOff = U32(tpp, to + 4);
            if (gn.Type == 0 || gn.DataOff is 0 or 0xFFFFFFFF) continue;
            int g = (int)gn.DataOff, t = (int)tOff;

            // common: parent/flags/scale/rot/translate + color + palette; prio = f38 x 4095
            for (int k = 0; k < 0x34; k++)
                if (k is not (2 or 3) && src[g + k] != tpp[t + k]) return (false, $"n{i} common+{k:x}");
            float f38 = BinaryPrimitives.ReadSingleLittleEndian(src.AsSpan(g + 0x38, 4));
            short wantPrio = (short)Math.Clamp(MathF.Round(f38 * 4095f), short.MinValue, short.MaxValue);
            if (BinaryPrimitives.ReadInt16LittleEndian(tpp.AsSpan(t + 2, 2)) != wantPrio) return (false, $"n{i} prio");
            for (int k = 0; k < 0x10; k++) if (src[g + 0x3C + k] != tpp[t + 0x38 + k]) return (false, $"n{i} color");
            if (src[g + 0x4E] != tpp[t + 0x4A]) return (false, $"n{i} palette");

            if (gn.Type is 2 or 4)
            {
                int gm = g + 0x50, tm = t + 0x4C;
                int v = U16(src, gm), tri = U16(src, gm + 2);
                if (U16(tpp, tm) != v || U16(tpp, tm + 2) != tri) return (false, $"n{i} mesh counts");
                uint gVerts = U32(src, gm + 4), tRemap = U32(tpp, tm + 4);
                if ((gVerts == 0xFFFFFFFF) != (tRemap == 0xFFFFFFFF)) return (false, $"n{i} verts presence");
                if (gVerts != 0xFFFFFFFF)
                    for (int k = 0; k < v; k++)
                    {
                        int slot = U16(tpp, (int)(buffers + tRemap) + k * 2);
                        for (int q = 0; q < 16; q++)
                            if (src[gz.BuffersOff + gVerts + k * 16 + q] != tpp[buffers + vertsRel + slot * 16 + q])
                                return (false, $"n{i} vert {k}");
                    }
                uint gUvs = U32(src, gm + 8), tUvRemap = U32(tpp, tm + 8);
                if (gUvs != 0xFFFFFFFF)
                    for (int k = 0; k < v; k++)
                    {
                        int slot = U16(tpp, (int)(buffers + tUvRemap) + k * 2);
                        for (int q = 0; q < 16; q++)
                            if (src[gz.BuffersOff + gUvs + k * 16 + q] != tpp[buffers + uvsRel + slot * 16 + q])
                                return (false, $"n{i} uv {k}");
                    }
                uint gTri = U32(src, gm + 0x14), tTri = U32(tpp, tm + 0x14);
                if (gTri != 0xFFFFFFFF)
                    for (int k = 0; k < tri * 6; k++)
                        if (src[gz.BuffersOff + gTri + k] != tpp[buffers + tTri + k]) return (false, $"n{i} tri");
                if (gn.Type == 2)
                {
                    int texCnt = U16(src, gm + 0x28);
                    if (U16(tpp, tm + 0x28) != texCnt) return (false, $"n{i} texcnt");
                    uint gTex = U32(src, gm + 0x2C), tTex = U32(tpp, tm + 0x2C);
                    for (int k = 0; k < texCnt * 4 && gTex != 0xFFFFFFFF; k++)
                        if (src[gTex + k] != tpp[tTex + k]) return (false, $"n{i} texparam");
                }
            }
            else if (gn.Type == 3)
            {
                for (int k = 0; k < 0x3C && g + 0x50 + k < src.Length; k++)
                    if (src[g + 0x50 + k] != tpp[t + 0x4C + k]) return (false, $"n{i} text+{k:x}");
                // positional tail copied verbatim
                var bounds = gz.Nodes.Where(x => x.DataOff is not (0 or 0xFFFFFFFF)).Select(x => x.DataOff)
                    .Append(gz.BuffersOff).Append(U32(src, 0x18)).Where(x => x > 0 && x > gn.DataOff).DefaultIfEmpty(gz.BuffersOff).Min();
                for (uint k = 0x8C; k < bounds - gn.DataOff; k++)
                    if (src[gn.DataOff + k] != tpp[t + 0x88 + (k - 0x8C)]) return (false, $"n{i} textTail+{k:x}");
            }
        }
        return (true, "");
    }
}
