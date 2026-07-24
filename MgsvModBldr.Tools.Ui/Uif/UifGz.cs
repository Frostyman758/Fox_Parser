// GZ uif parse (version 0x102)
// 09/07/2026
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Ui.Uif;

public sealed class GzUifNode
{
    public short NameIdx;
    public ushort Type;                     // 0 Null 1 Common 2 Mesh 3 Text 4 Stencil 5 Line
    public uint DataOff;
}

/// <summary>Parsed GZ .uif: header, nodes, StrCode64 table, path strings.</summary>
public sealed class GzUif
{
    public ushort Flags;
    public List<GzUifNode> Nodes = new();
    public List<ulong> Ids = new();         // StrCode64 x count (header @0x0C = entry count)
    public List<string> Paths = new();
    public uint BuffersOff;
    public byte[] Data = [];

    public static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));
    public static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));
    public static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o, 8));

    public static GzUif Read(byte[] d)
    {
        if (d.Length < 0x20 || U32(d, 0) != 0x20464955) throw new InvalidDataException("not a uif");
        if (U32(d, 4) != 0x102) throw new InvalidDataException($"uif version 0x{U32(d, 4):X} (not GZ)");
        var f = new GzUif { Data = d, Flags = U16(d, 0x08) };
        int nodeCount = U16(d, 0x0A);
        int idCount = U16(d, 0x0C);
        int pathCount = U16(d, 0x0E);
        uint nodesOff = U32(d, 0x10), strRel = U32(d, 0x14), pathOff = U32(d, 0x18);
        f.BuffersOff = U32(d, 0x1C);

        for (int i = 0; i < nodeCount; i++)
        {
            int o = (int)nodesOff + i * 8;
            f.Nodes.Add(new GzUifNode
            {
                NameIdx = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o, 2)),
                Type = U16(d, o + 2),
                DataOff = U32(d, o + 4),
            });
        }
        int strT = (int)(f.BuffersOff + strRel);
        for (int i = 0; i < idCount; i++) f.Ids.Add(U64(d, strT + i * 8));
        for (int i = 0; i < pathCount; i++)
        {
            int e = (int)pathOff + i * 8;
            uint len = U32(d, e), rel = U32(d, e + 4);
            f.Paths.Add(len is 0 or 0xFFFFFFFF ? "" : Encoding.UTF8.GetString(d, (int)(f.BuffersOff + rel), (int)len));
        }
        return f;
    }
}
