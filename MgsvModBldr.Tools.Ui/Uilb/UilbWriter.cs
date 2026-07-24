// Uilb serializer (GZ version 0, TPP version 1)
// 09/07/2026
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Ui.Uilb;

public static class UilbWriter
{
    static void P16(List<byte> b, ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); b.AddRange(s); }
    static void P32(List<byte> b, uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); b.AddRange(s); }
    static void P64(List<byte> b, ulong v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(s, v); b.AddRange(s); }
    static void Patch32(List<byte> b, int at, uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); for (int i = 0; i < 4; i++) b[at + i] = s[i]; }

    public static byte[] Write(UilbFile f)
    {
        var b = new List<byte>();
        P32(b, 0x424c4955);
        b.Add(1); b.Add(f.Version); P16(b, 0);
        P16(b, (ushort)f.ModelCount); P16(b, (ushort)f.AnimCount);
        P16(b, (ushort)f.CameraCount); P16(b, (ushort)f.GraphCount);
        P16(b, (ushort)f.IdCount); P16(b, (ushort)f.PathCount);
        for (int i = 0; i < 7; i++) P32(b, 0);     // 0x14..0x2C patched below

        uint TableAt(byte[] t)
        {
            if (t.Length == 0) return 0xFFFFFFFF;
            uint at = (uint)b.Count; b.AddRange(t); return at;
        }
        Patch32(b, 0x14, TableAt(f.ModelTable));
        Patch32(b, 0x18, TableAt(f.AnimTable));
        Patch32(b, 0x1C, TableAt(f.CameraTable));
        Patch32(b, 0x20, TableAt(f.GraphTable));
        if (f.BlobAbsent)
        {
            Patch32(b, 0x24, 0xFFFFFFFF); Patch32(b, 0x28, 0xFFFFFFFF);
            Patch32(b, 0x2C, (uint)b.Count);
            return b.ToArray();
        }
        Patch32(b, 0x24, (uint)f.PreLists.Length);

        if (f.IsTpp)
        {
            uint blob = (uint)b.Count;
            b.AddRange(f.PreLists);
            foreach (var id in f.TppIds) P32(b, id);
            while (b.Count % 8 != 0) b.Add(0);      // PathCode64 table 8-aligned
            Patch32(b, 0x28, (uint)b.Count - blob);
            Patch32(b, 0x2C, blob);
            foreach (var p in f.TppPathIds) P64(b, p);
        }
        else
        {
            int entryTable = b.Count;               // {len, relOff} patched after strings placed
            Patch32(b, 0x28, (uint)entryTable);
            for (int i = 0; i < f.GzPaths.Count; i++) P64(b, 0);
            while (b.Count % 8 != 0) b.Add(0);      // blob 8-aligned
            uint blob = (uint)b.Count;
            Patch32(b, 0x2C, blob);
            b.AddRange(f.PreLists);
            foreach (var id in f.GzIds) P64(b, id);
            for (int i = 0; i < f.GzPaths.Count; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(f.GzPaths[i]);
                Patch32(b, entryTable + i * 8, (uint)bytes.Length);
                Patch32(b, entryTable + i * 8 + 4, (uint)b.Count - blob);
                b.AddRange(bytes); b.Add(0);
            }
        }
        return b.ToArray();
    }
}
