// Uigb graph container, GZ and TPP
// 09/07/2026
namespace MgsvModBldr.Tools.Ui.Uigb;

/// <summary>One graph node: 6 B header + raw inline body (offsets rebased on write).</summary>
public sealed class UigbNode
{
    public ushort TypeIdx, NameIdx;
    public byte Size, Type;                 // type: 0 Page 1 Phase 2 Event 3 Action 4 Operation
    public byte[] Body = [];
}

/// <summary>
/// Parsed .uigb. Section order (both versions): header, nodes, edge slab
/// (in-edges/frefs/out-edges), layout table, section4, [section6 TPP],
/// pool (links+params), str table, paths. See FORMATS.md.
/// </summary>
public sealed class UigbFile
{
    public byte Version;                    // 0 GZ, 1 TPP
    public byte UigbRefCount;               // header 0x0B, kept verbatim
    public List<UigbNode> Nodes = new();
    public byte[] EdgeSlab = [];            // raw [nodesEnd, next section)
    public uint EdgeSlabPos;                // original abs position (rebase base)
    public byte[] LayoutTable = [];         // raw entries: GZ 8 B, TPP 12 B
    public bool LayoutAbsent;
    public byte[] Section4 = [];            // raw child-graph refs
    public bool S4Absent;
    public byte S6Count;                    // TPP only
    public byte[] Section6 = [];
    public byte[] Pool = [];                // links + params ("section 5")
    public List<ulong> GzIds = new();       // StrCode64
    public List<uint> TppIds = new();       // StrCode32
    public List<(uint Len, string S)> GzPaths = new();   // len 0 = empty entry
    public List<ulong> TppPathIds = new();
    public int TailPad;                     // TPP: stray trailing zero bytes
    public int PrePoolPad;                  // zero bytes before pool (authoring artifact)
    public int PrePathPad;                  // TPP: zero bytes between strT end and pathT

    public bool IsTpp => Version == 1;
    public int UilbCount => LayoutTable.Length / (IsTpp ? 12 : 8);
    public int IdCount => IsTpp ? TppIds.Count : GzIds.Count;
    public int PathCount => IsTpp ? TppPathIds.Count : GzPaths.Count;
}
