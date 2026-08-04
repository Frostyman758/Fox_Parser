// mog graph header, state nodes and blend nodes
namespace MgsvModBldr.Tools.MotionGraph;

// Graph header, stride 0x38. AnimLayerCount at +0x04 is confirmed by
// MogFileImpl::ConvertToGraphLayerIndex, which sums it across graphs.
public sealed class MogGraph
{
    public int Index, At, MaskArrayAt;
    public byte AnimLayerCount;
    public uint StateNodeCount;
    public int StateNodesAt;
    public uint EdgeCount, EntryNodeCount, SpecialNodeCount;
    public int EdgesAt, EntryNodesAt, SpecialNodesAt, AnimLayerInfosAt;
    public ushort[] EntryNodes = [], SpecialNodes = [];
    public List<MogStateNode> Nodes = [];
    public List<MogEdge> Edges = [];

    public static MogGraph Read(byte[] b, int index, int at)
    {
        var g = new MogGraph
        {
            Index = index,
            At = at,
            MaskArrayAt = MogFile.Rel(b, at),
            AnimLayerCount = b[at + 4],
            StateNodeCount = BitConverter.ToUInt32(b, at + 8),
            StateNodesAt = MogFile.Rel(b, at + 0xc),
            EdgeCount = BitConverter.ToUInt32(b, at + 0x10),
            EdgesAt = MogFile.Rel(b, at + 0x14),
            EntryNodeCount = BitConverter.ToUInt32(b, at + 0x18),
            EntryNodesAt = MogFile.Rel(b, at + 0x1c),
            SpecialNodeCount = BitConverter.ToUInt32(b, at + 0x20),
            SpecialNodesAt = MogFile.Rel(b, at + 0x24),
            AnimLayerInfosAt = MogFile.Rel(b, at + 0x34),
        };
        for (int j = 0; j < g.StateNodeCount; j++)
        {
            int n = g.StateNodesAt + j * MogFile.StateNodeSize;
            if (n < 0 || n > b.Length - MogFile.StateNodeSize) break;
            g.Nodes.Add(MogStateNode.Read(b, n));
        }
        g.EntryNodes = ReadIndices(b, g.EntryNodesAt, g.EntryNodeCount);
        g.SpecialNodes = ReadIndices(b, g.SpecialNodesAt, g.SpecialNodeCount);
        for (int j = 0; j < g.EdgeCount; j++)
        {
            int e = g.EdgesAt + j * MogFile.EdgeSize;
            if (e < 0 || e > b.Length - MogFile.EdgeSize) break;
            g.Edges.Add(MogEdge.Read(b, e, g));
        }
        // out-edge lists are stored as addresses; expose them as edge indices so the XML
        // stays valid when arrays move
        foreach (var n in g.Nodes)
        {
            n.OutEdgeIndices = new int[n.OutEdges.Length];
            for (int k = 0; k < n.OutEdges.Length; k++) n.OutEdgeIndices[k] = g.EdgeIndexAt(n.OutEdges[k]);
        }
        return g;
    }

    public int EdgeIndexAt(int at) =>
        at >= EdgesAt && at < EdgesAt + (int)EdgeCount * MogFile.EdgeSize
        && (at - EdgesAt) % MogFile.EdgeSize == 0
            ? (at - EdgesAt) / MogFile.EdgeSize : -1;

    static ushort[] ReadIndices(byte[] b, int at, uint n)
    {
        if (n > 65536 || at < 0 || at > b.Length - 2 * (int)n) return [];
        var v = new ushort[n];
        for (int k = 0; k < n; k++) v[k] = BitConverter.ToUInt16(b, at + k * 2);
        return v;
    }

    public int NodeIndexAt(int at) =>
        at >= StateNodesAt && at < StateNodesAt + (int)StateNodeCount * MogFile.StateNodeSize
        && (at - StateNodesAt) % MogFile.StateNodeSize == 0
            ? (at - StateNodesAt) / MogFile.StateNodeSize : -1;
}

// 0x28 bytes. +0x00 is the SOURCE node, +0x04 the DESTINATION: every edge is listed by
// exactly one node's out-edge list, and that owner is always the node at +0x00 (verified
// 3087/3087 TPP, 5610/5610 GZ, no exceptions).
public sealed class MogEdge
{
    public int At, NodeAAt, NodeBAt, NodeA = -1, NodeB = -1;
    public uint RequestTagCount;
    public int RequestTagsAt;
    public ushort[] RequestTags = [];
    public ushort[] CompTags = [];      // +0x18/+0x1C, tested against a node's CompTag set
    public byte B8, B9, BA, BC;         // single bytes; +0x0B is 0xA7 filler
    public ushort[] Layers = [];        // +0x10/+0x14, one u16 per anim layer

    static ushort[] ReadU16Set(byte[] b, int countField, int offsetField)
    {
        uint c = BitConverter.ToUInt32(b, countField);
        int o = MogFile.Rel(b, offsetField);
        if (c > 4096 || o < 0 || o > b.Length - 2 * (int)c) return [];
        var v = new ushort[c];
        for (int k = 0; k < c; k++) v[k] = BitConverter.ToUInt16(b, o + k * 2);
        return v;
    }

    public static MogEdge Read(byte[] b, int at, MogGraph g)
    {
        var e = new MogEdge
        {
            At = at,
            NodeAAt = MogFile.Rel(b, at),
            NodeBAt = MogFile.Rel(b, at + 4),
            RequestTagCount = BitConverter.ToUInt32(b, at + 0x20),
            RequestTagsAt = MogFile.Rel(b, at + 0x24),
        };
        e.B8 = b[at + 8]; e.B9 = b[at + 9]; e.BA = b[at + 0xa]; e.BC = b[at + 0xc];
        e.Layers = ReadU16Set(b, at + 0x10, at + 0x14);
        e.CompTags = ReadU16Set(b, at + 0x18, at + 0x1c);
        e.NodeA = g.NodeIndexAt(e.NodeAAt);
        e.NodeB = g.NodeIndexAt(e.NodeBAt);
        // sorted u16 indices into the file's tag map — the transition's condition
        if (e.RequestTagCount < 4096 &&
            e.RequestTagsAt >= 0 && e.RequestTagsAt <= b.Length - 2 * (int)e.RequestTagCount)
        {
            e.RequestTags = new ushort[e.RequestTagCount];
            for (int k = 0; k < e.RequestTagCount; k++)
                e.RequestTags[k] = BitConverter.ToUInt16(b, e.RequestTagsAt + k * 2);
        }
        return e;
    }
}

// 0x48 bytes. +0x00/+0x04 is the out-edge list; +0x1C/+0x20 the CompTag gate that
// CheckPathTransition tests for Type 2 and 7 nodes; +0x28 is always -0x28 (self-pointer).
public sealed class MogStateNode
{
    public int At;
    public uint OutEdgeCount;
    public int OutEdgeArrayAt;
    public int[] OutEdges = [];
    public int[] OutEdgeIndices = [];
    public uint BlendNodeCount;
    public int BlendNodesAt;
    public byte Type;
    public int NameTagAt, SelfOffset, SelfTarget;
    public ushort[] CompTags = [];
    public ulong NameTag, GroupTag;
    public ushort Unk10;
    public ushort[] Adjacent = [];
    public List<MogBlendNode> BlendNodes = [];

    public static MogStateNode Read(byte[] b, int at)
    {
        var n = new MogStateNode
        {
            At = at,
            OutEdgeCount = BitConverter.ToUInt32(b, at),
            OutEdgeArrayAt = MogFile.Rel(b, at + 4),
            BlendNodeCount = BitConverter.ToUInt32(b, at + 8),
            BlendNodesAt = MogFile.Rel(b, at + 0xc),
            Type = b[at + 0x14],
            NameTagAt = MogFile.Rel(b, at + 0x18),
            SelfOffset = BitConverter.ToInt32(b, at + 0x28),
        };
        n.SelfTarget = n.SelfOffset == 0 ? at : (at + 0x28) + n.SelfOffset;
        n.Unk10 = BitConverter.ToUInt16(b, at + 0x10);
        int gt = MogFile.Rel(b, at + 0x44);
        if (gt >= 0 && gt <= b.Length - 8)
        {
            ulong gv = BitConverter.ToUInt64(b, gt);
            if ((gv >> 48) == 0) n.GroupTag = gv;
        }
        uint ac = BitConverter.ToUInt32(b, at + 0x2c);
        int ao = MogFile.Rel(b, at + 0x30);
        if (ac < 4096 && ao >= 0 && ao <= b.Length - 2 * (int)ac)
        {
            n.Adjacent = new ushort[ac];
            for (int k = 0; k < ac; k++) n.Adjacent[k] = BitConverter.ToUInt16(b, ao + k * 2);
        }
        if (n.NameTagAt >= 0 && n.NameTagAt <= b.Length - 8)
        {
            ulong nv = BitConverter.ToUInt64(b, n.NameTagAt);
            if ((nv >> 48) == 0) n.NameTag = nv;
        }

        if (n.OutEdgeCount < 65536 && n.OutEdgeArrayAt >= 0
            && n.OutEdgeArrayAt <= b.Length - 4 * (int)n.OutEdgeCount)
        {
            n.OutEdges = new int[n.OutEdgeCount];
            for (int k = 0; k < n.OutEdgeCount; k++)
            {
                int p = n.OutEdgeArrayAt + k * 4;
                n.OutEdges[k] = p + BitConverter.ToInt32(b, p);
            }
        }
        // CompTag set: sorted u16 indices into the file tag map — the node's transition gate
        uint ct = BitConverter.ToUInt32(b, at + 0x1c);
        int co = MogFile.Rel(b, at + 0x20);
        if (ct < 4096 && co >= 0 && co <= b.Length - 2 * (int)ct)
        {
            n.CompTags = new ushort[ct];
            for (int k = 0; k < ct; k++) n.CompTags[k] = BitConverter.ToUInt16(b, co + k * 2);
        }
        if (n.BlendNodeCount < 4096 &&
            n.BlendNodesAt >= 0 && n.BlendNodesAt <= b.Length - MogFile.BlendNodeSize * (int)n.BlendNodeCount)
            for (int k = 0; k < n.BlendNodeCount; k++)
                n.BlendNodes.Add(MogBlendNode.Read(b, n.BlendNodesAt + k * MogFile.BlendNodeSize));
        return n;
    }
}

// 0x2c bytes. Count/offset at +0x10/+0x14 address 8-byte records whose first byte is a
// blend-value index (0xff = none) — the set MotionGraphBlendValueBinderImpl marks.
public sealed class MogBlendNode
{
    public int At;
    public byte Type, FloatIndex, Flags;
    public uint ValueCount;
    public int ValuesAt;
    public byte[] ValueIndices = [];
    public int AnimPathAt = -1;     // the AnimParamBinaryPath this blend node owns
    public ulong AnimId;            // the PathId it resolves to (0 = non-leaf blend)

    public static MogBlendNode Read(byte[] b, int at)
    {
        var n = new MogBlendNode
        {
            At = at,
            Type = b[at],
            FloatIndex = b[at + 1],
            Flags = b[at + 2],
            ValueCount = BitConverter.ToUInt32(b, at + 0x10),
            ValuesAt = MogFile.Rel(b, at + 0x14),
        };
        if (n.ValueCount < 4096 && n.ValuesAt >= 0 && n.ValuesAt <= b.Length - 8 * (int)n.ValueCount)
        {
            n.ValueIndices = new byte[n.ValueCount];
            for (int k = 0; k < n.ValueCount; k++) n.ValueIndices[k] = b[n.ValuesAt + k * 8];
        }
        // +0x04 reaches the animation: self-rel -> AnimParamBinaryPath -> 8-byte PathId
        int d = MogFile.Rel(b, at + 4);
        foreach (int cand in (int[])[d, d + 8, d + 16])
        {
            if (cand < 0 || cand > b.Length - 4) continue;
            int t = cand + BitConverter.ToInt32(b, cand);
            if (t < 0 || t > b.Length - 8 || (t & 7) != 0) continue;
            ulong v = BitConverter.ToUInt64(b, t);
            if (MogPathPool.IsGaniId(v)) { n.AnimPathAt = cand; n.AnimId = v; break; }
        }
        return n;
    }
}
