// mog invariant checks — every rule a hand-built graph has broken
namespace MgsvModBldr.Tools.MotionGraph;

// The engine validates almost nothing (MogFileImpl::Validate only null-checks the header), so a
// structurally broken graph loads and then silently produces no pose. These are the invariants
// stock files hold and authored ones have violated in practice.
public static class MogValidate
{
    // Every self-relative field. Move a struct and ALL of these must be recomputed — missing
    // one leaves a dangling pointer, which is what three broken builds turned out to be.
    public static readonly int[] NodeOffsetFields = [0x04, 0x0c, 0x18, 0x20, 0x30, 0x38, 0x40, 0x44];
    public static readonly int[] EdgeOffsetFields = [0x00, 0x04, 0x14, 0x1c, 0x24];

    public sealed class Result
    {
        public List<string> Errors = [];
        public bool Ok => Errors.Count == 0;
        void Add(string s) { if (Errors.Count < 40) Errors.Add(s); }
        internal void Fail(string s) => Add(s);
    }

    public static Result Run(byte[] b)
    {
        var r = new Result();
        MogFile m;
        try { m = MogFile.Read(b); }
        catch (Exception e) { r.Fail($"unreadable: {e.Message}"); return r; }

        int tagCount = m.Tags.Length;
        for (int i = 0; i + 1 < tagCount; i++)
            if (m.Tags[i] >= m.Tags[i + 1])
            { r.Fail($"tag map not sorted ascending at index {i}"); break; }

        int layerSum = 0;
        foreach (var g in m.Graphs) layerSum += g.AnimLayerCount;
        if (layerSum != m.AnimLayerCount)
            r.Fail($"per-graph layer sum {layerSum} != header AnimLayerCount {m.AnimLayerCount}");

        foreach (var g in m.Graphs)
        {
            int n = g.Nodes.Count, ec = g.Edges.Count;
            for (int j = 0; j < n; j++)
            {
                var nd = g.Nodes[j];
                if (BitConverter.ToUInt16(b, nd.At + 0x12) != (ushort)(j + 1))
                    r.Fail($"g{g.Index} node{j}: id at +0x12 is not index+1");
                foreach (int f in NodeOffsetFields)
                {
                    int t = MogFile.Rel(b, nd.At + f);
                    if (t < 0 || t >= b.Length)
                        r.Fail($"g{g.Index} node{j}: offset +0x{f:x2} -> {t:x} outside the file");
                }
                // NOT an error: SelectMoveEdge stops at the control's own cap, which comes from
                // createContext byte 2 and is only 100 by default. GZ ships nodes with 225
                // out-edges, so a graph that exceeds the default just needs a control built with a
                // bigger one. Flagging it refused files Konami itself ships.
                foreach (int e in nd.OutEdges)
                {
                    if (e < g.EdgesAt || e >= g.EdgesAt + ec * MogFile.EdgeSize
                        || (e - g.EdgesAt) % MogFile.EdgeSize != 0)
                        r.Fail($"g{g.Index} node{j}: out-edge pointer {e:x} is not an edge boundary");
                }
                CheckSet(r, b, tagCount, nd.CompTags, $"g{g.Index} node{j} compTags");
                // +0x34/+0x38 logical reachable set and +0x3C/+0x40 implicit successors are both
                // u16 NodeId lists (1-based), same numbering as node+0x12.
                CheckNodeIdList(r, b, nd.At + 0x34, nd.At + 0x38, n, $"g{g.Index} node{j} logicalSet");
                CheckNodeIdList(r, b, nd.At + 0x3c, nd.At + 0x40, n, $"g{g.Index} node{j} implicitSuccessors");
                // a Type-6 node redirects through +0x24 to another node in this graph
                if (nd.Type == 6 && BitConverter.ToInt32(b, nd.At + 0x24) != 0)
                {
                    int t = MogFile.Rel(b, nd.At + 0x24);
                    if (t < g.StateNodesAt || t >= g.StateNodesAt + n * MogFile.StateNodeSize
                        || (t - g.StateNodesAt) % MogFile.StateNodeSize != 0)
                        r.Fail($"g{g.Index} node{j}: Type-6 redirect +0x24 -> {t:x} is not a node boundary");
                }
                CheckAdjacency(r, b, nd, n, $"g{g.Index} node{j}");
                foreach (var bn in nd.BlendNodes)
                {
                    if (bn.At < 0) continue;
                    // control-port table: count at +0x10, self-relative array at +0x14, stride 8.
                    // Byte 0 is a value index (0xff = unconnected); +0x04 self-relatively addresses
                    // the port's name StringId, which must be a 48-bit id.
                    int pc = (int)BitConverter.ToUInt32(b, bn.At + 0x10);
                    if (pc <= 0) continue;
                    int pa = MogFile.Rel(b, bn.At + 0x14);
                    if (pa < 0 || pa + pc * 8 > b.Length)
                    { r.Fail($"g{g.Index} node{j} blend@{bn.At:x}: port table outside the file"); continue; }
                    for (int q = 0; q < pc; q++)
                    {
                        int nameAt = MogFile.Rel(b, pa + q * 8 + 4);
                        if (nameAt < 0 || nameAt > b.Length - 8 || (BitConverter.ToUInt64(b, nameAt) >> 48) != 0)
                        { r.Fail($"g{g.Index} node{j} blend@{bn.At:x} port{q}: name is not a 48-bit StringId"); break; }
                    }
                }
                foreach (int f in new[] { 0x18, 0x44 })
                {
                    int t = MogFile.Rel(b, nd.At + f);
                    if (t >= 0 && t <= b.Length - 8 && (BitConverter.ToUInt64(b, t) >> 48) != 0)
                        r.Fail($"g{g.Index} node{j}: +0x{f:x2} does not resolve to a 48-bit StringId");
                }
            }
            for (int k = 0; k < ec; k++)
            {
                var e = g.Edges[k];
                foreach (int f in EdgeOffsetFields)
                {
                    int t = MogFile.Rel(b, e.At + f);
                    if (t < 0 || t >= b.Length)
                        r.Fail($"g{g.Index} edge{k}: offset +0x{f:x2} -> {t:x} outside the file");
                }
                // An endpoint that lands INSIDE the node array but off-stride is corruption. One
                // that points elsewhere in the file is not: stock Soldier2 has an edge aimed
                // between two graphs' node arrays, and GZ's player and soldier have more. The
                // dangling case is already caught by the offset-range check above.
                CheckEndpoint(r, b, g, MogFile.Rel(b, e.At), $"g{g.Index} edge{k} source");
                CheckEndpoint(r, b, g, MogFile.Rel(b, e.At + 4), $"g{g.Index} edge{k} destination");
                // 0x08..0x17 is trigger data. Stock always has some, with 0xA7 filler at
                // +0x0B; an all-zero region is the engine's "no trigger" encoding (TriggerCheck
                // tests the i32 and skips). Anything in between is a bogus self-relative
                // pointer that faults on the first transition.
                bool triggerBlank = true;
                for (int q = 8; q < 0x18; q++) if (b[e.At + q] != 0) { triggerBlank = false; break; }
                if (!triggerBlank && b[e.At + 0x0b] != 0xA7)
                    r.Fail($"g{g.Index} edge{k}: trigger region present but +0x0b is not 0xA7 filler");
                // The trigger descriptor's start-frame pointer is dereferenced unconditionally by
                // the engine, so a zero there is a null-deref rather than an "absent" encoding.
                if (BitConverter.ToInt32(b, e.At + 0x14) == 0)
                    r.Fail($"g{g.Index} edge{k}: StartFrameData offset (+0x14) is 0 — the engine "
                         + "always dereferences it");
                CheckSet(r, b, tagCount, e.CompTags, $"g{g.Index} edge{k} compTags");
                CheckSet(r, b, tagCount, e.RequestTags, $"g{g.Index} edge{k} requestTags");
            }
        }
        return r;
    }

    // u16 NodeId list addressed by a (count, self-relative offset) pair.
    static void CheckNodeIdList(Result r, byte[] b, int countAt, int offAt, int nodeCount, string what)
    {
        int c = (int)BitConverter.ToUInt32(b, countAt);
        if (c <= 0) return;                       // count 0 leaves the offset dangling by design
        int at = MogFile.Rel(b, offAt);
        if (at < 0 || at + c * 2 > b.Length) { r.Fail($"{what}: array outside the file"); return; }
        for (int i = 0; i < c; i++)
        {
            ushort id = BitConverter.ToUInt16(b, at + i * 2);
            if (id < 1 || id > nodeCount) { r.Fail($"{what}: NodeId {id} outside 1..{nodeCount}"); return; }
        }
    }

    static void CheckEndpoint(Result r, byte[] b, MogGraph g, int target, string what)
    {
        int lo = g.StateNodesAt, hi = lo + g.Nodes.Count * MogFile.StateNodeSize;
        if (target >= lo && target < hi && (target - lo) % MogFile.StateNodeSize != 0)
            r.Fail($"{what}: {target:x} is inside the node array but not on a node boundary");
    }

    static void CheckSet(Result r, byte[] b, int tagCount, ushort[] v, string what)
    {
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] >= tagCount) { r.Fail($"{what}: index {v[i]} >= tag count {tagCount}"); return; }
            if (i > 0 && v[i - 1] >= v[i])
            { r.Fail($"{what}: not sorted ascending — CompTag merge-intersect needs it"); return; }
        }
    }

    static void CheckAdjacency(Result r, byte[] b, MogStateNode nd, int nodeCount, string what)
    {
        var v = nd.Adjacent;
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] < 1 || v[i] > nodeCount)
            { r.Fail($"{what}: adjacency id {v[i]} outside 1..{nodeCount}"); return; }
            if (i > 0 && v[i - 1] >= v[i])
            { r.Fail($"{what}: adjacency not sorted ascending"); return; }
        }
    }
}
