// Uilb parser (GZ version 0, TPP version 1)
// 09/07/2026
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Ui.Uilb;

public static class UilbReader
{
    static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));
    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));
    static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o, 8));

    public static UilbFile Read(byte[] d)
    {
        if (d.Length < 0x30 || U32(d, 0) != 0x424c4955) throw new InvalidDataException("not a uilb");
        if (d[4] != 1) throw new InvalidDataException($"uilb magic4 {d[4]}");
        var f = new UilbFile { Version = d[5] };
        if (f.Version > 1) throw new InvalidDataException($"uilb version {f.Version}");

        int mdlCnt = U16(d, 0x08), animCnt = U16(d, 0x0A), camCnt = U16(d, 0x0C), graphCnt = U16(d, 0x0E);
        int idCnt = U16(d, 0x10), pathCnt = U16(d, 0x12);
        uint mdlOff = U32(d, 0x14), animOff = U32(d, 0x18), camOff = U32(d, 0x1C), graphOff = U32(d, 0x20);
        uint preSize = U32(d, 0x24), off28 = U32(d, 0x28), blobOff = U32(d, 0x2C);

        byte[] Table(uint off, int count, int stride) =>
            count == 0 || off == 0xFFFFFFFF ? [] : d.AsSpan((int)off, count * stride).ToArray();

        f.ModelTable = Table(mdlOff, mdlCnt, UilbFile.ModelStride);
        f.AnimTable = Table(animOff, animCnt, UilbFile.AnimStride);
        f.CameraTable = Table(camOff, camCnt, UilbFile.CameraStride);
        f.GraphTable = Table(graphOff, graphCnt, UilbFile.GraphStride);
        if (preSize == 0xFFFFFFFF) { f.BlobAbsent = true; return f; }
        f.PreLists = d.AsSpan((int)blobOff, (int)preSize).ToArray();

        int ids = (int)(blobOff + preSize);
        if (f.IsTpp)
        {
            for (int i = 0; i < idCnt; i++) f.TppIds.Add(U32(d, ids + i * 4));
            int paths = (int)(blobOff + off28);        // off28 rel to blob, 8-aligned
            for (int i = 0; i < pathCnt; i++) f.TppPathIds.Add(U64(d, paths + i * 8));
        }
        else
        {
            for (int i = 0; i < idCnt; i++) f.GzIds.Add(U64(d, ids + i * 8));
            for (int i = 0; i < pathCnt; i++)          // off28 abs: {u32 len, u32 relOff}
            {
                int e = (int)off28 + i * 8;
                uint len = U32(d, e);
                int abs = (int)(blobOff + U32(d, e + 4));
                f.GzPaths.Add(Encoding.UTF8.GetString(d, abs, (int)len));
            }
        }
        return f;
    }
}
