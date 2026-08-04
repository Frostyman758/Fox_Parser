// engine edge-selection rules: how the graph picks one out-edge
namespace MgsvModBldr.Tools.MotionGraph;

// MotionGraphControlBuiltin::CanMoveState scores every candidate out-edge and SelectMoveEdge keeps
// the best. A strictly higher score WIPES the winner found so far, so score is absolute priority,
// not a hint: a conditional edge beats an unconditional one outright whenever its condition holds.
// Equal scores tiebreak on tag COUNT (more wins); an exact tie goes to whichever edge comes first
// in the node's out-edge list.
public static class MogSelect
{
    // SelectMoveEdge returns out of its loop once the out-edge index reaches this, so edges past
    // it are never evaluated at all. Control createContext byte 2, 100 when absent.
    public const int MaxOutEdgesEvaluated = 100;

    public const byte ScorePlain = 1, ScoreComp = 2, ScoreRequest = 3, ScoreBoth = 4;

    // CanMoveState: bVar2 = 1, then 2 if CompTagCount, then 3 (or 4 with comp) if RequestTagCount.
    public static byte Score(int compTags, int requestTags) =>
        requestTags != 0 ? (compTags != 0 ? ScoreBoth : ScoreRequest)
                         : (compTags != 0 ? ScoreComp : ScorePlain);

    public static byte Score(MogEdge e) => Score(e.CompTags.Length, e.RequestTags.Length);

    // The count SelectMoveEdge compares when two candidates tie on score.
    public static int TieBreak(byte score, int compTags, int requestTags) =>
        score >= ScoreRequest ? requestTags : score == ScoreComp ? compTags : 0;

    // Beating a stock edge means outscoring it — no tag state can rescue the loser.
    public static bool Beats(byte mine, byte theirs) => mine > theirs;

    public static string Report(MogFile m, string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{path}");
        foreach (var g in m.Graphs)
        {
            var byScore = new int[5];
            foreach (var e in g.Edges) byScore[Score(e)]++;
            int maxOut = 0, overCap = 0;
            foreach (var n in g.Nodes)
            {
                maxOut = Math.Max(maxOut, n.OutEdgeIndices.Length);
                if (n.OutEdgeIndices.Length > MaxOutEdgesEvaluated) overCap++;
            }
            sb.AppendLine($"  graph{g.Index}: {g.Nodes.Count:N0} nodes  {g.Edges.Count:N0} edges");
            sb.AppendLine($"    edge scores  1 plain {byScore[1]:N0}   2 comp {byScore[2]:N0}"
                        + $"   3 request {byScore[3]:N0}   4 both {byScore[4]:N0}");
            sb.AppendLine($"    max out-edges on one node {maxOut}"
                        + $"   over the {MaxOutEdgesEvaluated}-edge evaluation cap: {overCap}");
            // At equal score the tiebreak is tag COUNT, so this is the bar an authored edge has to
            // clear to win at a node where stock already offers a conditional edge.
            var comp = g.Edges.Where(e => e.CompTags.Length > 0).Select(e => e.CompTags.Length).ToList();
            if (comp.Count > 0)
                sb.AppendLine($"    comp-tag count on the {comp.Count:N0} conditional edges:"
                            + $" min {comp.Min()}  max {comp.Max()}  mean {comp.Average():0.00}");
            var deg = g.Nodes.Select(n => n.OutEdgeIndices.Length).Where(d => d > 0).OrderBy(d => d).ToList();
            if (deg.Count > 0)
                sb.AppendLine($"    out-degree of the {deg.Count:N0} nodes that have edges:"
                            + $" median {deg[deg.Count / 2]}   p90 {deg[deg.Count * 9 / 10]}"
                            + $"   p99 {deg[Math.Min(deg.Count - 1, deg.Count * 99 / 100)]}"
                            + $"   top {string.Join(",", deg.AsEnumerable().Reverse().Take(6))}");
        }
        return sb.ToString();
    }
}
