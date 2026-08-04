// .mog.xml -> .mog, including newly authored nodes and edges
namespace MgsvModBldr.Tools.MotionGraph;

// Builds from the XML's base image, overwriting only what the XML describes. Every offset in
// a mog is self-relative, so an array can live anywhere — moving one invalidates just the
// field that addresses it. Arrays that keep their length are written back in place; arrays
// that grow (or gain new members) are re-laid at the end of the file and repointed.
//
// Node and edge arrays must stay contiguous, because a node is addressed by its slot and an
// edge by its address. Adding either relocates the whole array and rewrites the pointers into
// it: edges carry node addresses, node out-edge lists carry edge addresses.
public static class MogBuilder
{
    sealed class Buf
    {
        public byte[] Data;
        public int End;
        public Buf(byte[] image) { Data = (byte[])image.Clone(); End = image.Length; }

        public void I32(int at, int v) => BitConverter.GetBytes(v).CopyTo(Data, at);
        public void U32(int at, uint v) => BitConverter.GetBytes(v).CopyTo(Data, at);
        public void U16(int at, ushort v) => BitConverter.GetBytes(v).CopyTo(Data, at);
        public void U64(int at, ulong v) => BitConverter.GetBytes(v).CopyTo(Data, at);
        public void Point(int field, int target) => I32(field, target - field);

        public int Alloc(int bytes, int align = 4)
        {
            int at = (End + align - 1) & ~(align - 1);
            Grow(at + bytes);
            End = at + bytes;
            return at;
        }
        void Grow(int need)
        {
            if (need <= Data.Length) return;
            int cap = Math.Max(need, Data.Length * 2);
            var d = new byte[cap];
            Data.CopyTo(d, 0);
            for (int i = Data.Length; i < cap; i++) d[i] = 0xA7;
            Data = d;
        }
        public byte[] Trim() { var r = new byte[End]; Array.Copy(Data, r, End); return r; }
    }

    public static byte[] Build(MogXml.Doc d)
    {
        var b = new Buf(d.Image);
        b.Data[0x18] = d.AnimLayerCount;
        b.Data[0x19] = d.UnknownD;

        // Tag map (param 0x185ebb9f). Grafting GZ states brings GZ tag StringIds with them, so
        // the array can grow; it moves to the end of the file and the param is repointed.
        if (d.Tags.Count > 0)
        {
            int po = 0x30 + BitConverter.ToInt32(b.Data, 0x30);
            for (int guard = 0; guard < 256; guard++)
            {
                uint nm = BitConverter.ToUInt32(b.Data, po + 4);
                if (nm == MogFile.TagMapParam)
                {
                    int cur = (int)BitConverter.ToUInt32(b.Data, po + 8);
                    int at = d.Tags.Count == cur
                           ? (po + 0xc) + BitConverter.ToInt32(b.Data, po + 0xc)
                           : b.Alloc(d.Tags.Count * 8, 8);
                    b.U32(po + 8, (uint)d.Tags.Count);
                    b.Point(po + 0xc, at);
                    for (int k = 0; k < d.Tags.Count; k++) b.U64(at + k * 8, d.Tags[k].Id);
                    break;
                }
                int nxt = BitConverter.ToInt32(b.Data, po);
                if (nxt == 0) break;
                po += nxt;
            }
        }

        // gani pool first: a new blend node may reference an id already sitting there
        var poolAt = new Dictionary<ulong, int>();
        foreach (var (at, id) in d.Pool)
            if (at >= 0 && at <= b.Data.Length - 8) { b.U64(at, id); poolAt.TryAdd(id, at); }

        foreach (var g in d.Graphs)
        {
            b.Data[g.At + 4] = g.AnimLayerCount;
            WriteU16Set(b, g.At + 0x18, g.At + 0x1c, g.Entry);
            WriteU16Set(b, g.At + 0x20, g.At + 0x24, g.Special);

            int oldNodes = (int)BitConverter.ToUInt32(b.Data, g.At + 8);
            int oldEdges = (int)BitConverter.ToUInt32(b.Data, g.At + 0x10);
            bool relayNodes = g.Nodes.Count != oldNodes || g.Nodes.Any(n => n.At < 0);
            bool relayEdges = g.Edges.Count != oldEdges || g.Edges.Any(e => e.At < 0);

            int nodeArr = relayNodes
                ? Relay(b, g.Nodes.Select(n => n.At), MogFile.StateNodeSize)
                : MogFile.Rel(b.Data, g.At + 0xc);
            int edgeArr = relayEdges
                ? Relay(b, g.Edges.Select(e => e.At), MogFile.EdgeSize)
                : MogFile.Rel(b.Data, g.At + 0x14);

            b.U32(g.At + 8, (uint)g.Nodes.Count);
            b.Point(g.At + 0xc, nodeArr);
            b.U32(g.At + 0x10, (uint)g.Edges.Count);
            b.Point(g.At + 0x14, edgeArr);

            int NodeAddr(int i) => nodeArr + i * MogFile.StateNodeSize;
            int EdgeAddr(int i) => edgeArr + i * MogFile.EdgeSize;

            for (int i = 0; i < g.Nodes.Count; i++)
            {
                var n = g.Nodes[i];
                int at = NodeAddr(i);
                // Sub-arrays are addressed FROM the node, so relocating the node only
                // invalidates the offset value, not the data. Recompute against the original
                // targets instead of re-emitting kilobytes of unchanged arrays.
                int oOut = n.At >= 0 ? MogFile.Rel(b.Data, n.At + 4) : -1;
                int oBlend = n.At >= 0 ? MogFile.Rel(b.Data, n.At + 0xc) : -1;
                int oComp = n.At >= 0 ? MogFile.Rel(b.Data, n.At + 0x20) : -1;
                // EVERY self-relative field has to be recomputed when the node moves — the
                // copied bytes still hold offsets measured from the old address. Missing one
                // leaves a dangling pointer on every existing node.
                if (n.At >= 0 && n.At != at)
                    foreach (int f in new[] { 0x18, 0x30, 0x38, 0x40, 0x44 })
                        b.Point(at + f, MogFile.Rel(b.Data, n.At + f));
                b.Data[at + 0x14] = n.Type;
                // +0x12 is a 1-based node id — it equals index+1 on every node of every graph
                // in both games. A new node left at 0 is what makes the graph produce no pose.
                b.U16(at + 0x12, (ushort)(i + 1));
                if (n.At < 0)
                {
                    // A newly authored node starts zeroed, but several fields are mandatory:
                    // stock has them non-zero on every node, and leaving them at 0 yields a
                    // graph that loads and produces no pose at all.
                    b.Data[at + 0x15] = 0xA7; b.Data[at + 0x16] = 0xA7; b.Data[at + 0x17] = 0xA7;
                    b.U16(at + 0x10, n.Unk10);
                    foreach (var (tag, field) in new[] { (n.NameTag, at + 0x18), (n.GroupTag, at + 0x44) })
                    {
                        int ns = b.Alloc(8, 8);
                        b.U64(ns, tag);
                        b.Point(field, ns);
                    }
                    // +0x2C/+0x30 is the precomputed logical-adjacency list: sorted 1-based
                    // node ids. Derive it from where this node's out-edges actually lead.
                    var adj = new SortedSet<ushort>();
                    foreach (int ei in n.OutEdges)
                        if (ei >= 0 && ei < g.Edges.Count && g.Edges[ei].To >= 0)
                            adj.Add((ushort)(g.Edges[ei].To + 1));
                    int aa = b.Alloc(Math.Max(adj.Count, 1) * 2, 2);
                    b.U32(at + 0x2c, (uint)adj.Count);
                    b.Point(at + 0x30, aa);
                    int q = 0; foreach (var v in adj) b.U16(aa + q++ * 2, v);
                    // the two remaining arrays are empty on a fresh node, but their offsets
                    // still have to address somewhere real
                    foreach (int f in new[] { at + 0x38, at + 0x40 })
                        b.Point(f, b.Alloc(2, 2));
                }
                // SelfOffset: TPP stores -0x28 (a self-pointer), GZ stores 0. Preserve what
                // was there; only a newly authored node needs one written.
                if (n.At < 0) b.I32(at + 0x28, -0x28);

                // out-edge list: i32 self-relative pointers, given in the XML as edge indices
                int cur = n.At >= 0 ? (int)BitConverter.ToUInt32(b.Data, n.At) : -1;
                int listAt = (oOut >= 0 && n.OutEdges.Length == cur)
                           ? oOut
                           : b.Alloc(Math.Max(n.OutEdges.Length, 1) * 4);
                b.U32(at, (uint)n.OutEdges.Length);
                b.Point(at + 4, listAt);
                for (int k = 0; k < n.OutEdges.Length; k++) b.Point(listAt + k * 4, EdgeAddr(n.OutEdges[k]));

                WriteU16Set(b, at + 0x1c, at + 0x20, n.CompTags,
                            origCount: n.At >= 0 ? (int)BitConverter.ToUInt32(b.Data, n.At + 0x1c) : -1,
                            origAt: oComp, alwaysAllocate: n.At < 0);

                // blend nodes
                int oldBc = n.At >= 0 ? (int)BitConverter.ToUInt32(b.Data, n.At + 8) : -1;
                bool relayBlend = n.Blends.Count != oldBc || n.Blends.Any(x => x.At < 0);
                int blendArr = relayBlend
                    ? Relay(b, n.Blends.Select(x => x.At), MogFile.BlendNodeSize)
                    : oBlend;
                b.U32(at + 8, (uint)n.Blends.Count);
                b.Point(at + 0xc, blendArr);

                for (int k = 0; k < n.Blends.Count; k++)
                {
                    var bl = n.Blends[k];
                    int ba = blendArr + k * MogFile.BlendNodeSize;
                    b.Data[ba] = bl.Type; b.Data[ba + 1] = bl.FloatIndex; b.Data[ba + 2] = bl.Flags;
                    if (bl.Anim == 0) continue;
                    if (!poolAt.TryGetValue(bl.Anim, out int slot))
                    {
                        slot = b.Alloc(8, 8);
                        b.U64(slot, bl.Anim);
                        poolAt[bl.Anim] = slot;
                    }
                    // AnimParamBinaryPath: a 4-byte self-relative pointer to the PathId.
                    // For an existing blend node repoint that pointer and leave +0x04 alone —
                    // it may address the path via a small record, and rewriting it would
                    // change bytes we did not mean to touch.
                    if (!relayBlend && bl.AnimAt >= 0)
                    {
                        // leave it alone when it already resolves to this id — the pool can
                        // hold the same id in more than one slot, and collapsing duplicates
                        // would rewrite bytes we did not mean to touch
                        int curT = bl.AnimAt + BitConverter.ToInt32(b.Data, bl.AnimAt);
                        bool same = curT >= 0 && curT <= b.Data.Length - 8
                                    && BitConverter.ToUInt64(b.Data, curT) == bl.Anim;
                        if (!same) b.Point(bl.AnimAt, slot);
                    }
                    else
                    {
                        int ptr = b.Alloc(4);
                        b.Point(ptr, slot);
                        b.Point(ba + 4, ptr);
                    }
                }
            }

            for (int i = 0; i < g.Edges.Count; i++)
            {
                var e = g.Edges[i];
                int at = EdgeAddr(i);
                if (e.At < 0)
                {
                    // 0x08..0x17 is trigger data, not enum bytes: TriggerCheck reads an i32
                    // there and, when non-zero, follows it as a self-relative pointer into an
                    // event-name list. Copying a donor's bytes builds a pointer that means
                    // nothing here and faults the moment the player transitions.
                    // The engine's own encoding for "this edge has no trigger" is zero —
                    // `test eax,eax / je` skips the whole path — so leave the region cleared.
                    Array.Clear(b.Data, at + 8, 0x10);
                }
                // -1 means the original pointer resolved to no node (GZ has one such edge);
                // leave it as authored rather than inventing node 0
                if (e.At >= 0 && e.At != at)
                    foreach (int f in new[] { 0x14, 0x1c, 0x24 })
                        b.Point(at + f, MogFile.Rel(b.Data, e.At + f));
                if (e.From >= 0) b.Point(at, NodeAddr(e.From));
                if (e.To >= 0) b.Point(at + 4, NodeAddr(e.To));
                WriteU16Set(b, at + 0x18, at + 0x1c, e.CompTags,
                            origCount: e.At >= 0 ? (int)BitConverter.ToUInt32(b.Data, e.At + 0x18) : -1,
                            origAt: e.At >= 0 ? MogFile.Rel(b.Data, e.At + 0x18 + 4) : -1,
                            alwaysAllocate: e.At < 0);
                WriteU16Set(b, at + 0x20, at + 0x24, e.RequestTags,
                            origCount: e.At >= 0 ? (int)BitConverter.ToUInt32(b.Data, e.At + 0x20) : -1,
                            origAt: e.At >= 0 ? MogFile.Rel(b.Data, e.At + 0x20 + 4) : -1,
                            alwaysAllocate: e.At < 0);
            }
        }
        return b.Trim();
    }

    // Re-lay a fixed-stride array at the end of the file, carrying each member's original
    // bytes across; members with no source (newly authored) start zeroed.
    static int Relay(Buf b, IEnumerable<int> sources, int stride)
    {
        var src = sources.ToArray();
        int at = b.Alloc(Math.Max(src.Length, 1) * stride, 8);
        for (int i = 0; i < src.Length; i++)
        {
            int dst = at + i * stride;
            if (src[i] >= 0 && src[i] <= b.Data.Length - stride)
                Array.Copy(b.Data, src[i], b.Data, dst, stride);
            else
                Array.Clear(b.Data, dst, stride);
        }
        return at;
    }

    static void WriteU16Set(Buf b, int countField, int offsetField, ushort[] v,
                            int origCount = -1, int origAt = -1, bool alwaysAllocate = false)
    {
        // origCount/origAt describe where the set lived BEFORE its owner moved. Without them
        // the owner is in place, so the fields themselves still say where it is.
        int cur = origCount >= 0 ? origCount : (int)BitConverter.ToUInt32(b.Data, countField);
        int oa = origAt >= 0 ? origAt : MogFile.Rel(b.Data, offsetField);
        if (v.Length == 0)
        {
            // An empty set is never read, but stock never leaves the offset at 0 — a freshly
            // authored struct still needs it addressing somewhere real.
            b.U32(countField, 0);
            b.Point(offsetField, alwaysAllocate ? b.Alloc(2, 2) : oa);
            return;
        }
        int at = v.Length == cur ? oa : b.Alloc(v.Length * 2, 2);
        b.U32(countField, (uint)v.Length);
        b.Point(offsetField, at);
        for (int k = 0; k < v.Length; k++) b.U16(at + k * 2, v[k]);
    }
}
