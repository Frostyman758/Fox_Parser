// census a corpus of mogs — one row per file, to compare how graphs are authored
namespace MgsvModBldr.Tools.MotionGraph;

// Single-file reports answer "what does this graph do"; a census answers "what do these AUTHORS
// do", which is what tells you whether a construct is idiomatic or something you invented.
public static class MogCensus
{
    public sealed class Row
    {
        public string Name = "";
        public int Graphs, Nodes, Edges, Tags, EntryNodes;
        public int S1, S2, S3, S4, MaxComp, MaxReq, MaxOut;
        public int TypeWildcard, TypeEnterable, TypeTerminal, NodesWithTags;
        public double CondPct => Edges == 0 ? 0 : 100.0 * (S2 + S3 + S4) / Edges;
        public double EdgesPerNode => Nodes == 0 ? 0 : (double)Edges / Nodes;
    }

    public static Row Measure(byte[] raw, string name)
    {
        var m = MogFile.Read(raw);
        var r = new Row { Name = name, Graphs = m.Graphs.Count, Tags = m.Tags.Length };
        foreach (var g in m.Graphs)
        {
            r.Nodes += g.Nodes.Count;
            r.Edges += g.Edges.Count;
            r.EntryNodes += g.EntryNodes.Length;
            foreach (var e in g.Edges)
            {
                switch (MogSelect.Score(e))
                {
                    case MogSelect.ScorePlain: r.S1++; break;
                    case MogSelect.ScoreComp: r.S2++; break;
                    case MogSelect.ScoreRequest: r.S3++; break;
                    default: r.S4++; break;
                }
                r.MaxComp = Math.Max(r.MaxComp, e.CompTags.Length);
                r.MaxReq = Math.Max(r.MaxReq, e.RequestTags.Length);
            }
            foreach (var n in g.Nodes)
            {
                r.MaxOut = Math.Max(r.MaxOut, n.OutEdgeIndices.Length);
                if (n.CompTags.Length > 0) r.NodesWithTags++;
                if (n.Type == 1) r.TypeWildcard++;
                else if (n.Type is 2 or 7) r.TypeEnterable++;
                else if (n.Type == 4) r.TypeTerminal++;
            }
        }
        return r;
    }

    public static string Table(List<Row> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("name\tgraphs\tnodes\tedges\te/n\ttags\tentry\tnodesTagged"
                    + "\ts1\ts2\ts3\ts4\tcond%\tmaxComp\tmaxReq\tmaxOut\twild\tenterable\tterminal");
        foreach (var r in rows)
            sb.AppendLine($"{r.Name}\t{r.Graphs}\t{r.Nodes}\t{r.Edges}\t{r.EdgesPerNode:0.00}\t{r.Tags}"
                        + $"\t{r.EntryNodes}\t{r.NodesWithTags}\t{r.S1}\t{r.S2}\t{r.S3}\t{r.S4}"
                        + $"\t{r.CondPct:0.0}\t{r.MaxComp}\t{r.MaxReq}\t{r.MaxOut}"
                        + $"\t{r.TypeWildcard}\t{r.TypeEnterable}\t{r.TypeTerminal}");
        return sb.ToString();
    }

    // What the corpus agrees on. A construct absent from every shipped file is one the engine may
    // support but the game never feeds — which is exactly the trap an authored graph falls into.
    public static string Summary(List<Row> rows, string label)
    {
        if (rows.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        int edges = rows.Sum(r => r.Edges), nodes = rows.Sum(r => r.Nodes);
        int s1 = rows.Sum(r => r.S1), s2 = rows.Sum(r => r.S2);
        int s3 = rows.Sum(r => r.S3), s4 = rows.Sum(r => r.S4);
        sb.AppendLine($"{label}: {rows.Count} files   {nodes:N0} nodes   {edges:N0} edges"
                    + $"   {(double)edges / Math.Max(1, nodes):0.00} edges/node");
        sb.AppendLine($"  scores  1 plain {s1:N0}   2 comp {s2:N0}   3 request {s3:N0}   4 both {s4:N0}"
                    + $"   conditional {(edges == 0 ? 0 : 100.0 * (s2 + s3 + s4) / edges):0.0}%");
        sb.AppendLine($"  files using comp tags {rows.Count(r => r.S2 + r.S4 > 0)}/{rows.Count}"
                    + $"   using request tags {rows.Count(r => r.S3 + r.S4 > 0)}/{rows.Count}");
        sb.AppendLine($"  max comp tags on one edge {rows.Max(r => r.MaxComp)}"
                    + $"   max request tags {rows.Max(r => r.MaxReq)}"
                    + $"   max out-edges {rows.Max(r => r.MaxOut)}");
        return sb.ToString();
    }
}
