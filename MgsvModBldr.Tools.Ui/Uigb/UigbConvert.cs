// Uigb GZ to TPP transform
// 09/07/2026
using System.Buffers.Binary;
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Ui.Uigb;

public static class UigbConvert
{
    const uint SetTextStr32 = 2414984170;   // UiActSetTextNode

    public static UigbFile GzToTpp(UigbFile gz)
    {
        if (gz.IsTpp) throw new ArgumentException("already TPP");
        if (!gz.S4Absent) throw new NotSupportedException("uigb with child-graph section4 not supported yet");
        var t = new UigbFile
        {
            Version = 1,
            UigbRefCount = gz.UigbRefCount,
            EdgeSlab = gz.EdgeSlab, EdgeSlabPos = gz.EdgeSlabPos,
            LayoutAbsent = gz.LayoutAbsent,
            S4Absent = true,
            S6Count = 0,
            Pool = gz.Pool.ToArray(),
            TailPad = 0,
        };
        // dominant TPP convention: pool and PathCode64 table 8-aligned
        uint predict = 0x38;
        foreach (var n in gz.Nodes) predict += (uint)(6 + n.Body.Length);
        predict += (uint)gz.EdgeSlab.Length + (uint)(gz.LayoutAbsent ? 0 : gz.UilbCount * 12);
        t.PrePoolPad = (int)((8 - predict % 8) % 8);
        uint strEnd = (uint)((predict + t.PrePoolPad) + gz.Pool.Length + gz.GzIds.Count * 4);
        t.PrePathPad = (int)((8 - strEnd % 8) % 8);
        foreach (var id in gz.GzIds) t.TppIds.Add((uint)id);
        foreach (var (len, s) in gz.GzPaths) t.TppPathIds.Add(len == 0 ? 0ul : GameHash.PathCode(s));

        foreach (var n in gz.Nodes)
        {
            var c = new UigbNode { TypeIdx = n.TypeIdx, NameIdx = n.NameIdx, Size = n.Size, Type = n.Type, Body = n.Body.ToArray() };
            if (n.Type == 3 && n.TypeIdx < gz.GzIds.Count && (uint)gz.GzIds[n.TypeIdx] == SetTextStr32 && c.Body.Length >= 9 && c.Body[7] == 72)
            {
                uint par = BinaryPrimitives.ReadUInt32LittleEndian(c.Body.AsSpan(14, 4));
                if (par != 0xFFFFFFFF && par + 72 <= t.Pool.Length)
                {
                    var tail = t.Pool.AsSpan((int)par + 56, 16);
                    Span<byte> narrowed = stackalloc byte[16];
                    for (int i = 0; i < 4; i++) narrowed[i] = tail[i * 4];   // u32 flags → u8
                    narrowed[4..].Clear();
                    narrowed.CopyTo(tail);
                    c.Body[7] = 64;
                }
            }
            t.Nodes.Add(c);
        }

        if (!gz.LayoutAbsent)
        {
            var lt = new byte[gz.UilbCount * 12];
            for (int i = 0; i < gz.UilbCount; i++)
            {
                gz.LayoutTable.AsSpan(i * 8, 8).CopyTo(lt.AsSpan(i * 12));
                lt[i * 12 + 8] = 0xFF; lt[i * 12 + 9] = 0xFF;   // connIdx none
            }
            t.LayoutTable = lt;
        }
        return t;
    }
}
