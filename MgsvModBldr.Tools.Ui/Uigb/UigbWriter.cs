// Uigb serializer (GZ version 0, TPP version 1)
// 09/07/2026
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Ui.Uigb;

public static class UigbWriter
{
    static void P16(List<byte> b, ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); b.AddRange(s); }
    static void P32(List<byte> b, uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); b.AddRange(s); }
    static void P64(List<byte> b, ulong v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(s, v); b.AddRange(s); }
    static void Patch32(List<byte> b, int at, uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); for (int i = 0; i < 4; i++) b[at + i] = s[i]; }

    public static byte[] Write(UigbFile f)
    {
        var b = new List<byte>();
        P32(b, 0x42474955);
        b.Add(1); b.Add(f.Version); P16(b, 0);
        P16(b, (ushort)f.Nodes.Count);
        b.Add((byte)f.UilbCount);
        b.Add(f.UigbRefCount);

        if (f.IsTpp)
        {
            b.Add(0); b.Add(f.S6Count);
            P16(b, (ushort)f.PathCount);
            P32(b, (uint)f.TppIds.Count);
            for (int i = 0; i < 9; i++) P32(b, 0);      // 0x14..0x34 patched
        }
        else
        {
            P16(b, (ushort)f.GzIds.Count);
            P16(b, (ushort)f.PathCount);
            for (int i = 0; i < 6; i++) P32(b, 0);      // 0x10..0x24 patched
        }

        // nodes with rebased slab offsets
        int nodeT = b.Count;
        uint slabDelta = 0;                             // patched after slab position known
        int slabFieldFixupStart = b.Count;
        foreach (var n in f.Nodes)
        {
            P16(b, n.TypeIdx); P16(b, n.NameIdx); b.Add(n.Size); b.Add(n.Type);
            b.AddRange(n.Body);
        }
        uint newSlabPos = (uint)b.Count;
        slabDelta = newSlabPos - f.EdgeSlabPos;
        if (slabDelta != 0) RebaseNodeOffsets(b, nodeT, f, slabDelta);
        b.AddRange(f.EdgeSlab);

        uint layT = 0xFFFFFFFF, s4 = 0xFFFFFFFF, s6 = 0xFFFFFFFF;
        if (!f.LayoutAbsent) { layT = (uint)b.Count; b.AddRange(f.LayoutTable); }
        if (!f.S4Absent) { s4 = (uint)b.Count; b.AddRange(f.Section4); }
        if (f.IsTpp && f.Section6.Length > 0) { s6 = (uint)b.Count; b.AddRange(f.Section6); }

        if (f.IsTpp)
        {
            for (int i = 0; i < f.PrePoolPad; i++) b.Add(0);
            uint pool = (uint)b.Count;
            b.AddRange(f.Pool);
            uint strRel = (uint)b.Count - pool;
            foreach (var id in f.TppIds) P32(b, id);
            for (int i = 0; i < f.PrePathPad; i++) b.Add(0);
            uint pathRel = (uint)b.Count - pool;
            foreach (var p in f.TppPathIds) P64(b, p);
            for (int i = 0; i < f.TailPad; i++) b.Add(0);
            Patch32(b, 0x14, (uint)nodeT); Patch32(b, 0x18, layT); Patch32(b, 0x1C, s4);
            Patch32(b, 0x20, 0xFFFFFFFF); Patch32(b, 0x24, s6);
            Patch32(b, 0x28, pathRel); Patch32(b, 0x2C, strRel);
            Patch32(b, 0x30, 0xFFFFFFFF); Patch32(b, 0x34, pool);
        }
        else
        {
            uint pathT = 0xFFFFFFFF;
            int pathEntries = b.Count;
            if (f.PathCount > 0) { pathT = (uint)pathEntries; for (int i = 0; i < f.PathCount; i++) P64(b, 0); }
            for (int i = 0; i < f.PrePoolPad; i++) b.Add(0);
            uint pool = (uint)b.Count;
            b.AddRange(f.Pool);
            foreach (var id in f.GzIds) P64(b, id);
            for (int i = 0; i < f.GzPaths.Count; i++)
            {
                var (len, s) = f.GzPaths[i];
                Patch32(b, pathEntries + i * 8, len);
                Patch32(b, pathEntries + i * 8 + 4, (uint)b.Count - pool);
                if (len > 0) b.AddRange(Encoding.UTF8.GetBytes(s));
                b.Add(0);
            }
            Patch32(b, 0x10, (uint)nodeT); Patch32(b, 0x14, layT); Patch32(b, 0x18, s4);
            Patch32(b, 0x1C, (uint)f.Pool.Length); Patch32(b, 0x20, pathT); Patch32(b, 0x24, pool);
        }
        return b.ToArray();
    }

    // node body offset fields that point into the edge slab
    static void RebaseNodeOffsets(List<byte> b, int nodeT, UigbFile f, uint delta)
    {
        int off = nodeT;
        foreach (var n in f.Nodes)
        {
            int body = off + 6;
            switch (n.Type)
            {
                case 1:                                  // Phase: u16 edges
                    if (b[body + 1] > 0)
                    {
                        ushort v = (ushort)(b[body + 2] | (b[body + 3] << 8));
                        if (v != 0) { v = checked((ushort)(v + delta)); b[body + 2] = (byte)v; b[body + 3] = (byte)(v >> 8); }
                    }
                    break;
                case 2: Fix32(b, body + 2, b[body + 1] > 0, delta); break;
                case 3:
                    Fix32(b, body + 2, b[body + 1] > 0, delta);
                    Fix32(b, body + 10, true, delta);    // frefs (-1/0 skipped)
                    Fix32(b, body + 30, Rd32(b, body + 26) > 0, delta);
                    break;
                case 4: Fix32(b, body + 2, b[body + 1] > 0, delta); break;
            }
            off += n.Size > 0 ? n.Size : 6 + UigbReader.InlineSize(n.Type);
        }
    }

    static uint Rd32(List<byte> b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
    static void Fix32(List<byte> b, int at, bool cond, uint delta)
    {
        uint v = Rd32(b, at);
        if (!cond || v == 0 || v == 0xFFFFFFFF) return;
        v += delta;
        b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
    }
}
