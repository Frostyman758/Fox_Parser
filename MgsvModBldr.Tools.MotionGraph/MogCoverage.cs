// how much of a mog the edit model actually accounts for
namespace MgsvModBldr.Tools.MotionGraph;

/// <summary>
/// Marks every byte the XML model can reproduce from named fields. Anything left over is only
/// surviving because MogXml carries the original file as a base64 image — so this is the honest
/// measure of how far the format is decoded, which a byte-exact round trip is not.
/// </summary>
public static class MogCoverage
{
    public sealed class Result
    {
        public bool[] Hit;
        public long Covered;
        /// <summary>Unmodelled bytes grouped by the region they sit in — what to decode next.</summary>
        public Dictionary<string, long> GapsByRegion = new();
        /// <summary>Largest runs of unmodelled REAL data, with their first bytes, biggest first.</summary>
        public List<(int At, int Len, string Head)> TopRealRuns = new();
        public int Length => Hit.Length;
        public double Percent => Length == 0 ? 0 : 100.0 * Covered / Length;
    }

    /// <summary>Param 0x859bd53e — the per-graph name table (MogFileImpl::GetGraphName).</summary>
    public const uint GraphNamesParam = 0x859bd53e;

    // Edge 0x08..0x17 is a trigger descriptor, not one field:
    //   +0x08 TriggerType, +0x09 StartFrameCondType, +0x0A Flags, +0x0B 0xA7,
    //   +0x0C u16 InterpFrames, +0x0E/+0x0F 0xA7,
    //   +0x10 i32 self-rel TriggerData (0 = none), +0x14 i32 self-rel StartFrameData (never 0).
    static void MarkTrigger(byte[] b, int at, Action<int, int> mark)
    {
        byte trigType = b[at + 0x08], startType = b[at + 0x09];

        if (BitConverter.ToInt32(b, at + 0x10) != 0)
        {
            int t = MogFile.Rel(b, at + 0x10);
            switch (trigType)
            {
                case 0: case 4: mark(t, 4); break;                   // u16 frame + 2 filler
                case 3: case 5: mark(t, 4); mark(MogFile.Rel(b, t), 8); break;   // -> StringId
                case 6:                                              // frame-range list
                    mark(t, 8);
                    int rc = (int)BitConverter.ToUInt32(b, t);
                    int ra = MogFile.Rel(b, t + 4);
                    if (rc > 0 && rc < 4096 && ra > 0 && ra + rc * 4 <= b.Length) mark(ra, rc * 4);
                    break;
            }
        }

        int s2 = MogFile.Rel(b, at + 0x14);
        if (s2 <= 0 || s2 + 4 > b.Length) return;
        switch (startType)
        {
            case 0: mark(s2, 4); break;                              // u16 startFrame + 2 filler
            case 1: case 2: mark(s2, 4); mark(MogFile.Rel(b, s2), 8); break;
            case 3:                                                  // port StringId + event id
                mark(s2, 8);
                mark(MogFile.Rel(b, s2), 8);
                mark(MogFile.Rel(b, s2 + 4), 8);
                break;
        }
    }

    /// <summary>
    /// An AnimParamBinary / PoseOperatorParamBinary record. Header is three self-relative i32s —
    /// Desc, type-id StringId, instance-name StringId — and the record's TOTAL size is authored in
    /// the Desc, at Desc+0x08 (engine reads the low u16; BlendControlImpl::AddOperators rounds it
    /// up to 16 for allocation only). Desc itself is 0x0C bytes: a type-name StringId offset, a
    /// per-type constant the engine never reads, then that size.
    /// </summary>
    static void MarkParamRecord(byte[] b, int at, Action<int, int> mark)
    {
        if (at < 0 || at + 0x0c > b.Length) return;
        int desc = MogFile.Rel(b, at);
        if (desc < 0 || desc + 0x0c > b.Length) return;

        int size = BitConverter.ToUInt16(b, desc + 8);
        if (size < 0x0c || at + size > b.Length) size = 0x0c;
        mark(at, size);                       // header + operator payload + its 0xA7 tail

        mark(desc, 0x0c);
        mark(MogFile.Rel(b, desc), 8);        // StrCode64 of the param blob's type name
        mark(MogFile.Rel(b, at + 4), 8);      // type id
        mark(MogFile.Rel(b, at + 8), 8);      // instance name key

        // Mirror carries two rig-mask names; harmless to follow on any record whose size covers it,
        // since every operator's payload words at +0x08/+0x0C are self-relative StringId offsets.
        if (size >= 0x10)
        {
            mark(MogFile.Rel(b, at + 0x08), 8);
            mark(MogFile.Rel(b, at + 0x0c), 8);
        }
    }

    public static Result Measure(byte[] b)
    {
        var hit = new bool[b.Length];
        void Mark(int at, int len)
        {
            if (at < 0 || len <= 0) return;
            for (int i = at; i < at + len && i < hit.Length; i++) hit[i] = true;
        }
        // an array addressed by a (count, self-relative offset) pair
        void MarkArray(int countAt, int offsetAt, int stride)
        {
            if (countAt + 4 > b.Length || offsetAt + 4 > b.Length) return;
            int n = (int)BitConverter.ToUInt32(b, countAt);
            if (n <= 0 || n > 1 << 20) return;
            Mark(MogFile.Rel(b, offsetAt), n * stride);
        }

        var m = MogFile.Read(b);
        Mark(0, 0x34);                                          // header, through ParamsOffset

        foreach (var g in m.Graphs)
        {
            Mark(g.At, MogFile.GraphHeaderSize);
            // already carried by the XML, just never marked
            Mark(g.EntryNodesAt, g.EntryNodes.Length * 2);
            Mark(g.SpecialNodesAt, g.SpecialNodes.Length * 2);
            Mark(g.AnimLayerInfosAt, g.AnimLayerCount * 2);   // {u8 maxDatas, u8 maxNodes} per layer

            // MotionGraphFormatValueControls at graph+0x28: { u32 Count; i32 Offset; } then Count
            // 8-byte records, each with a self-relative i32 to an 8-byte StringId. This is the
            // table a blend port's ValueIndex indexes — MotionGraphBlendValueBinderImpl::InitWork.
            int vc = MogFile.Rel(b, g.At + 0x28);
            if (vc > 0 && vc + 8 <= b.Length)
            {
                Mark(vc, 8);
                int vn = (int)BitConverter.ToUInt32(b, vc);
                int va = MogFile.Rel(b, vc + 4);
                if (vn > 0 && vn < 4096 && va > 0 && va + vn * 8 <= b.Length)
                {
                    Mark(va, vn * 8);
                    for (int q = 0; q < vn; q++) Mark(MogFile.Rel(b, va + q * 8), 8);
                }
            }

            foreach (var n in g.Nodes)
            {
                Mark(n.At, MogFile.StateNodeSize);
                MarkArray(n.At + 0x00, n.At + 0x04, 4);         // out-edge pointers
                MarkArray(n.At + 0x08, n.At + 0x0c, MogFile.BlendNodeSize);
                MarkArray(n.At + 0x1c, n.At + 0x20, 2);         // comp tags
                MarkArray(n.At + 0x2c, n.At + 0x30, 2);         // direct adjacency (u16 NodeIds)
                MarkArray(n.At + 0x34, n.At + 0x38, 2);         // logical/indirect reachable set
                MarkArray(n.At + 0x3c, n.At + 0x40, 2);         // edge-less implicit successors
                Mark(MogFile.Rel(b, n.At + 0x18), 8);           // name StringId
                Mark(MogFile.Rel(b, n.At + 0x44), 8);           // group StringId

                foreach (var bn in n.BlendNodes)
                {
                    if (bn.At < 0) continue;
                    // blend +0x04 -> AnimParamBinaryPath -> 8-byte PathId
                    // +0x04 is TYPE-SPECIFIC data, not always an anim path. A leaf (type 0)
                    // points at an AnimParamBinaryPath; Layers and the select types point at a
                    // { u32 Count; i32 RecordsOffset; } block of 8-byte records, each of which
                    // self-relatively addresses an 8-byte StringId — MotionGraphLayersBlendNodeData
                    // ::BuildTree walks exactly that.
                    int p = MogFile.Rel(b, bn.At + 0x04);
                    if (p >= 0 && p + 8 <= b.Length)
                    {
                        if (bn.Type == 0 || bn.Type == 9)
                        {
                            Mark(p, 4); Mark(MogFile.Rel(b, p), 8);
                            if (bn.Type == 9) Mark(p + 4, 8);   // flags, port index, static value
                        }
                        else if (bn.Type == 1) Mark(p, 2);      // Two: flags + port index
                        else if (bn.Type == 6) Mark(p, 1);      // Add: flags only, weight hard 1.0
                        else if (bn.Type == 7) Mark(p, 2);      // Subtract: flags + port index
                        else if (bn.Type == 4 || bn.Type == 5)  // Select / StringSelect
                            Mark(p, 3);                         // flags, layer value, port index
                        else if (bn.Type == 3)
                        {
                            // Custom: port BASE index, then a plugin blob whose only decodable
                            // part is the chain to the plugin's name StringId. The blob's own
                            // contents are plugin-defined and stay undecoded.
                            Mark(p, 8);
                            int cp = MogFile.Rel(b, p + 4);
                            if (cp > 0 && cp + 4 <= b.Length)
                            {
                                Mark(cp, 4);
                                int x = MogFile.Rel(b, cp);
                                if (x > 0 && x + 4 <= b.Length) { Mark(x, 4); Mark(MogFile.Rel(b, x), 8); }
                            }
                        }
                        else
                        {
                            Mark(p, 8);
                            int rc = (int)BitConverter.ToUInt32(b, p);
                            int ra = MogFile.Rel(b, p + 4);
                            if (rc > 0 && rc < 4096 && ra > 0 && ra + rc * 8 <= b.Length)
                            {
                                Mark(ra, rc * 8);
                                for (int q = 0; q < rc; q++) Mark(MogFile.Rel(b, ra + q * 8), 8);
                            }
                        }
                    }
                    // LinkDescs: 8-byte records, self-relative from +0x0C. Byte 0 is the child's
                    // INDEX into this state node's own blend array (0xff = unconnected), so the
                    // tree needs no pointers — MotionGraphFormatUtility::GetConnectBlendNode.
                    MarkArray(bn.At + 0x08, bn.At + 0x0c, 8);

                    // Control-port table: count at +0x10, self-relative array at +0x14, stride 8.
                    // Each port is { u8 ValueIndex (0xff = unconnected); 3x 0xA7; i32 self-rel to
                    // an 8-byte StringId naming the port }. MotionGraphBlendValueBinderImpl::
                    // SetUseValuesNew walks exactly this.
                    int pc = (int)BitConverter.ToUInt32(b, bn.At + 0x10);
                    int pa = MogFile.Rel(b, bn.At + 0x14);
                    if (pc > 0 && pc < 4096 && pa > 0 && pa + pc * 8 <= b.Length)
                    {
                        Mark(pa, pc * 8);
                        for (int q = 0; q < pc; q++) Mark(MogFile.Rel(b, pa + q * 8 + 4), 8);
                    }

                    // The THIRD (count, self-relative offset) pair on a blend node: an array of
                    // 4-byte self-relative pointers to AnimParamBinary records, 0 = skip and
                    // negative = a back-reference to a record emitted earlier (they are deduped).
                    // MotionGraphBlendNodeTraverser::SetAnimParam walks it.
                    int mc = (int)BitConverter.ToUInt32(b, bn.At + 0x18);
                    int ma = MogFile.Rel(b, bn.At + 0x1c);
                    if (mc > 0 && mc < 4096 && ma > 0 && ma + mc * 4 <= b.Length)
                    {
                        Mark(ma, mc * 4);
                        for (int q = 0; q < mc; q++)
                        {
                            int slot = ma + q * 4;
                            if (BitConverter.ToInt32(b, slot) == 0) continue;
                            MarkParamRecord(b, MogFile.Rel(b, slot), Mark);
                        }
                    }

                    // ...and the single pose-operator record at +0x24, same record shape.
                    if (BitConverter.ToInt32(b, bn.At + 0x24) != 0)
                        MarkParamRecord(b, MogFile.Rel(b, bn.At + 0x24), Mark);
                }
            }

            foreach (var e in g.Edges)
            {
                Mark(e.At, MogFile.EdgeSize);
                MarkTrigger(b, e.At, Mark);
                MarkArray(e.At + 0x10, e.At + 0x14, 2);         // per-layer array
                MarkArray(e.At + 0x18, e.At + 0x1c, 2);         // comp tags
                MarkArray(e.At + 0x20, e.At + 0x24, 2);         // request tags
            }
        }

        // param chain, the tag map, and the per-graph name table
        for (int p = MogFile.Rel(b, 0x30); p > 0 && p + 0x10 <= b.Length; )
        {
            Mark(p, 0x10);
            int count = (int)BitConverter.ToUInt32(b, p + 8);
            uint pname = BitConverter.ToUInt32(b, p + 4);
            if (pname == MogFile.TagMapParam)
                Mark(MogFile.Rel(b, p + 0x0c), count * 8);
            else if (pname == GraphNamesParam)
            {
                // one AnimParamBinaryString per graph: i32 self-relative to an 8-byte StringId
                int ga = MogFile.Rel(b, p + 0x0c);
                if (ga > 0 && ga + count * 4 <= b.Length)
                {
                    Mark(ga, count * 4);
                    for (int q = 0; q < count; q++) Mark(MogFile.Rel(b, ga + q * 4), 8);
                }
            }
            int next = BitConverter.ToInt32(b, p);
            if (next == 0) break;
            p += next;
        }

        long covered = 0;
        foreach (var x in hit) if (x) covered++;
        // Most useful split: 0xA7 is the format's filler, so unmodelled bytes that are all
        // filler are alignment slack, not undecoded structure.
        var by = new Dictionary<string, long>();
        void Bump(string k) { by.TryAdd(k, 0); by[k] += 1; }
        for (int i = 0; i < hit.Length; i++)
        {
            if (hit[i]) continue;
            if (b[i] == 0xA7) { Bump("0xA7 filler (alignment slack)"); continue; }
            if (b[i] == 0x00) { Bump("zero bytes"); continue; }
            Bump("REAL DATA still undecoded");
        }
        // biggest runs that are neither filler nor zero — the actual remaining structures
        var runs = new List<(int, int, string)>();
        for (int i = 0; i < hit.Length; )
        {
            if (hit[i]) { i++; continue; }
            int j = i;
            while (j < hit.Length && !hit[j]) j++;
            bool real = false;
            for (int k = i; k < j; k++) if (b[k] != 0xA7 && b[k] != 0) { real = true; break; }
            if (real)
            {
                int n = Math.Min(16, j - i);
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < n; k++) sb.Append(b[i + k].ToString("x2")).Append(' ');
                runs.Add((i, j - i, sb.ToString().TrimEnd()));
            }
            i = j;
        }
        runs.Sort((x, y) => y.Item2.CompareTo(x.Item2));
        return new Result { Hit = hit, Covered = covered, GapsByRegion = by, TopRealRuns = runs };
    }
}
