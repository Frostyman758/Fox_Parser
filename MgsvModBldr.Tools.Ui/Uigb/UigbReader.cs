// Uigb parser (GZ version 0, TPP version 1)
// 09/07/2026
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Ui.Uigb;

public static class UigbReader
{
    static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));
    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));
    static int I32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4));
    static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o, 8));

    // inline body bytes per type when size byte is 0 (unseen in corpus)
    internal static int InlineSize(byte type) => type switch { 2 => 30, 3 => 34, 4 => 26, _ => 6 };

    public static UigbFile Read(byte[] d)
    {
        if (d.Length < 0x28 || U32(d, 0) != 0x42474955) throw new InvalidDataException("not a uigb");
        if (d[4] != 1) throw new InvalidDataException($"uigb magic4 {d[4]}");
        var f = new UigbFile { Version = d[5] };
        if (f.Version > 1) throw new InvalidDataException($"uigb version {f.Version}");

        int nodeCount = U16(d, 0x08);
        int uilbCount = d[0x0A];
        f.UigbRefCount = d[0x0B];
        int pathCount = U16(d, 0x0E);
        int strCount, nodeT, layT, s4, s6 = -1, pathT = -1, pool;
        uint poolSize;
        if (f.IsTpp)
        {
            f.S6Count = d[0x0D];
            strCount = (int)U32(d, 0x10);
            nodeT = I32(d, 0x14); layT = I32(d, 0x18); s4 = I32(d, 0x1C); s6 = I32(d, 0x24);
            pool = I32(d, 0x34);
            poolSize = U32(d, 0x2C);            // strRel == pool size
        }
        else
        {
            strCount = U16(d, 0x0C);
            nodeT = I32(d, 0x10); layT = I32(d, 0x14); s4 = I32(d, 0x18);
            poolSize = U32(d, 0x1C);
            pathT = I32(d, 0x20); pool = I32(d, 0x24);
        }

        int off = nodeT;
        for (int i = 0; i < nodeCount; i++)
        {
            var n = new UigbNode { TypeIdx = U16(d, off), NameIdx = U16(d, off + 2), Size = d[off + 4], Type = d[off + 5] };
            int total = n.Size > 0 ? n.Size : 6 + InlineSize(n.Type);
            n.Body = d.AsSpan(off + 6, total - 6).ToArray();
            f.Nodes.Add(n);
            off += total;
        }

        // edge slab runs to the first following section
        int slabEnd = new[] { layT, s4, s6, f.IsTpp ? pool : pathT, pool }
            .Where(x => x > 0 && x >= off).DefaultIfEmpty(pool).Min();
        f.EdgeSlabPos = (uint)off;
        f.EdgeSlab = d.AsSpan(off, slabEnd - off).ToArray();

        f.LayoutAbsent = layT <= 0;
        if (!f.LayoutAbsent) f.LayoutTable = d.AsSpan(layT, uilbCount * (f.IsTpp ? 12 : 8)).ToArray();
        f.S4Absent = s4 <= 0;
        if (!f.S4Absent)
        {
            int s4End = new[] { s6, f.IsTpp ? pool : pathT, pool }.Where(x => x > 0 && x >= s4).Min();
            f.Section4 = d.AsSpan(s4, s4End - s4).ToArray();
        }
        if (f.IsTpp && s6 > 0) f.Section6 = d.AsSpan(s6, pool - s6).ToArray();

        int prevEnd = new[] { f.IsTpp ? -1 : pathT + pathCount * 8, s6 > 0 ? s6 + f.Section6.Length : -1,
            s4 > 0 ? s4 + f.Section4.Length : -1, layT > 0 ? layT + f.LayoutTable.Length : -1, (int)f.EdgeSlabPos + f.EdgeSlab.Length }
            .Where(x => x > 0 && x <= pool).DefaultIfEmpty(pool).Max();
        f.PrePoolPad = pool - prevEnd;
        if (f.PrePoolPad is < 0 or > 8) throw new InvalidDataException($"pool pad {f.PrePoolPad}");

        f.Pool = d.AsSpan(pool, (int)poolSize).ToArray();
        int strT = pool + (int)poolSize;
        if (f.IsTpp)
        {
            for (int i = 0; i < strCount; i++) f.TppIds.Add(U32(d, strT + i * 4));
            int pt = pool + I32(d, 0x28);
            f.PrePathPad = pt - (strT + strCount * 4);
            if (f.PrePathPad is < 0 or > 8) throw new InvalidDataException($"path pad {f.PrePathPad}");
            for (int i = 0; i < pathCount; i++) f.TppPathIds.Add(U64(d, pt + i * 8));
            f.TailPad = d.Length - (pt + pathCount * 8);
            if (f.TailPad < 0 || f.TailPad > 8) throw new InvalidDataException($"tail {f.TailPad}");
        }
        else
        {
            for (int i = 0; i < strCount; i++) f.GzIds.Add(U64(d, strT + i * 8));
            for (int i = 0; i < pathCount; i++)
            {
                uint len = U32(d, pathT + i * 8);
                int abs = pool + (int)U32(d, pathT + i * 8 + 4);
                f.GzPaths.Add((len, len == 0 ? "" : Encoding.UTF8.GetString(d, abs, (int)len)));
            }
        }
        return f;
    }
}
