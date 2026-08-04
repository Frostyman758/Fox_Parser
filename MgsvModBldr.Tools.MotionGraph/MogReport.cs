// human-readable mog dump and two-file comparison
using System.Text;

namespace MgsvModBldr.Tools.MotionGraph;

public static class MogReport
{
    public static string Dump(MogFile m, string name, List<MogPathPool.Slot> pool)
    {
        var s = new StringBuilder();
        s.AppendLine($"{name}   {m.Raw.Length:N0} bytes");
        s.AppendLine($"  animLayerCount {m.AnimLayerCount}   unknownD {m.UnknownD}   graphs {m.GraphCount}");
        s.AppendLine($"  unknown@0x10 {m.Unknown10}   paramsRelated {m.ParamsRelated}");
        s.AppendLine($"  defaultAnimParams count {m.DefaultAnimParamsCount} @ 0x{m.DefaultAnimParamsAt:x}");

        int layerSum = 0, nodes = 0, blends = 0;
        foreach (var g in m.Graphs)
        {
            layerSum += g.AnimLayerCount;
            nodes += g.Nodes.Count;
            int gb = 0;
            foreach (var n in g.Nodes) gb += n.BlendNodes.Count;
            blends += gb;
            s.AppendLine($"  graph[{g.Index}] layers {g.AnimLayerCount}  stateNodes {g.StateNodeCount} @ 0x{g.StateNodesAt:x}" +
                         $"  blendNodes {gb}  mask@0x{g.MaskArrayAt:x}");
            int bad = g.Edges.Count(e => e.NodeA < 0 || e.NodeB < 0);
            int cond = g.Edges.Count(e => e.RequestTagCount > 0);
            s.AppendLine($"           edges {g.Edges.Count} (unresolved {bad}, tag-gated {cond})" +
                         $"  entryNodes {g.EntryNodes.Length} special {g.SpecialNodes.Length}");
        }
        s.AppendLine($"  layer sum {layerSum} (header says {m.AnimLayerCount})" +
                     (layerSum == m.AnimLayerCount ? "  ok" : "  MISMATCH"));
        int edges = m.Graphs.Sum(g => g.Edges.Count);
        s.AppendLine($"  state nodes {nodes}   blend nodes {blends}   edges {edges}");

        foreach (var p in m.Params)
            s.AppendLine($"  param 0x{p.Name:x8} count {p.Count} data@0x{p.DataAt:x}" +
                         (p.Name == MogFile.TagMapParam ? "   <- tag map" : ""));
        s.AppendLine($"  tags {m.Tags.Length}");

        int distinct = new HashSet<ulong>(pool.Select(x => x.Id)).Count;
        int refs = pool.Sum(x => x.RefCount);
        s.AppendLine($"  gani pool: {pool.Count} slots, {distinct} distinct ids, {refs} pointers");
        if (pool.Count > 0)
            s.AppendLine($"             0x{pool[0].At:x} .. 0x{pool[^1].At:x}");
        return s.ToString();
    }

    public static string Diff(MogFile a, MogFile b, string an, string bn)
    {
        var s = new StringBuilder();
        s.AppendLine($"A = {an}");
        s.AppendLine($"B = {bn}");
        s.AppendLine($"  graphs        {a.GraphCount} vs {b.GraphCount}");
        s.AppendLine($"  animLayers    {a.AnimLayerCount} vs {b.AnimLayerCount}");
        s.AppendLine($"  unknownD      {a.UnknownD} vs {b.UnknownD}");

        int an2 = a.Graphs.Sum(g => g.Nodes.Count), bn2 = b.Graphs.Sum(g => g.Nodes.Count);
        s.AppendLine($"  state nodes   {an2} vs {bn2}");

        var at = new HashSet<ulong>(a.Tags);
        var bt = new HashSet<ulong>(b.Tags);
        s.AppendLine($"  tags          {a.Tags.Length} vs {b.Tags.Length}   shared {at.Intersect(bt).Count()}" +
                     $"   A-only {at.Except(bt).Count()}   B-only {bt.Except(at).Count()}");

        // A tag present in A but missing from B is a behaviour B's graph cannot answer.
        var missing = at.Except(bt).ToList();
        if (missing.Count > 0)
        {
            s.AppendLine($"  tags in A that B lacks ({missing.Count}):");
            missing.Sort();
            foreach (var t in missing.Take(64)) s.AppendLine($"    {t:x12}");
            if (missing.Count > 64) s.AppendLine($"    ... and {missing.Count - 64} more");
        }
        return s.ToString();
    }
}
