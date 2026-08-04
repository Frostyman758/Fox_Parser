// .mog <-> .mog.xml
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace MgsvModBldr.Tools.MotionGraph;

// The XML carries the decoded structure PLUS the original file as a base64 image. Parts of a
// mog are still undecoded (node/blend/edge fields with no known meaning, inter-array padding),
// and writing from structure alone would drop them. Building starts from the image and
// overwrites what the XML describes, so an untouched round trip is byte-exact.
public static class MogXml
{
    static string Hex(int v) => "0x" + v.ToString("x", CultureInfo.InvariantCulture);
    static int ParseInt(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(s, CultureInfo.InvariantCulture);
    static string Join(IEnumerable<int> v) => string.Join(" ", v.Select(Hex));
    static string JoinU(IEnumerable<ushort> v) => string.Join(" ", v);
    static int[] SplitI(string s) =>
        string.IsNullOrWhiteSpace(s) ? [] : s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(ParseInt).ToArray();
    static ushort[] SplitU(string s) =>
        string.IsNullOrWhiteSpace(s) ? [] : s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => (ushort)ParseInt(x)).ToArray();

    public static void Write(MogFile m, List<MogPathPool.Slot> pool, string path)
    {
        var root = new XElement("mog",
            new XAttribute("animLayerCount", m.AnimLayerCount),
            new XAttribute("unknownD", m.UnknownD),
            new XAttribute("paramsRelated", m.ParamsRelated),
            new XAttribute("unknown10", m.Unknown10));

        foreach (var g in m.Graphs)
        {
            var ge = new XElement("graph",
                new XAttribute("index", g.Index),
                new XAttribute("at", Hex(g.At)),
                new XAttribute("animLayerCount", g.AnimLayerCount),
                new XAttribute("entryNodes", JoinU(g.EntryNodes)),
                new XAttribute("specialNodes", JoinU(g.SpecialNodes)));

            foreach (var n in g.Nodes)
            {
                var ne = new XElement("node",
                    new XAttribute("at", Hex(n.At)),
                    new XAttribute("type", n.Type),
                    new XAttribute("outEdges", string.Join(" ", n.OutEdgeIndices)),
                    new XAttribute("compTags", JoinU(n.CompTags)),
                    new XAttribute("name", n.NameTag.ToString("x12")),
                    new XAttribute("group", n.GroupTag.ToString("x12")),
                    new XAttribute("unk10", n.Unk10),
                    new XAttribute("adjacent", JoinU(n.Adjacent)));
                foreach (var bn in n.BlendNodes)
                    ne.Add(new XElement("blend",
                        new XAttribute("at", Hex(bn.At)),
                        new XAttribute("type", bn.Type),
                        new XAttribute("floatIndex", bn.FloatIndex),
                        new XAttribute("flags", bn.Flags),
                        new XAttribute("animAt", Hex(bn.AnimPathAt)),
                        new XAttribute("anim", bn.AnimId.ToString("x16"))));
                ge.Add(ne);
            }
            foreach (var e in g.Edges)
                ge.Add(new XElement("edge",
                    new XAttribute("at", Hex(e.At)),
                    new XAttribute("from", e.NodeA),
                    new XAttribute("to", e.NodeB),
                    new XAttribute("compTags", JoinU(e.CompTags)),
                    new XAttribute("requestTags", JoinU(e.RequestTags)),
                    new XAttribute("bytes", $"{e.B8} {e.B9} {e.BA} {e.BC}"),
                    new XAttribute("layers", JoinU(e.Layers))));
            root.Add(ge);
        }

        var tm = new XElement("tagMap");
        for (int i = 0; i < m.Tags.Length; i++)
            tm.Add(new XElement("tag", new XAttribute("index", i),
                                       new XAttribute("id", m.Tags[i].ToString("x12"))));
        root.Add(tm);

        var pe = new XElement("ganiPool");
        foreach (var s in pool)
            pe.Add(new XElement("gani", new XAttribute("at", Hex(s.At)),
                                        new XAttribute("id", s.Id.ToString("x16")),
                                        new XAttribute("refs", s.RefCount)));
        root.Add(pe);

        root.Add(new XElement("image", Convert.ToBase64String(m.Raw)));

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  " };
        using var w = XmlWriter.Create(path, settings);
        new XDocument(root).Save(w);
    }

    public sealed class Doc
    {
        public byte[] Image;
        public byte AnimLayerCount, UnknownD;
        public List<(int At, byte AnimLayerCount, ushort[] Entry, ushort[] Special,
                     List<NodeEdit> Nodes, List<EdgeEdit> Edges)> Graphs = [];
        public List<(int Index, ulong Id)> Tags = [];
        public List<(int At, ulong Id)> Pool = [];
    }
    public sealed class NodeEdit
    {
        public int At = -1; public byte Type; public int[] OutEdges = []; public ushort[] CompTags = [];
        public ulong NameTag, GroupTag;
        public ushort Unk10;
        public ushort[] Adjacent = [];
        public List<(int At, byte Type, byte FloatIndex, byte Flags, int AnimAt, ulong Anim)> Blends = [];
    }
    public sealed class EdgeEdit
    {
        public int At = -1, From, To; public ushort[] RequestTags = [], CompTags = [];
        public byte B8, B9, BA, BC; public ushort[] Layers = [];
    }

    static byte Byte(XElement e, int i)
    {
        var v = (e.Attribute("bytes")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return i < v.Length ? byte.Parse(v[i]) : (byte)0;
    }

    // a struct with no "at" is newly authored — the builder allocates it
    static int OptAt(XElement e) => e.Attribute("at") is { } a ? ParseInt(a.Value) : -1;

    // Mirror a parsed file into the edit model, so callers can mutate a graph in memory
    // without a round trip through XML.
    public static Doc ToDoc(MogFile m, byte[] raw)
    {
        var d = new Doc
        {
            Image = raw,
            AnimLayerCount = m.AnimLayerCount,
            UnknownD = m.UnknownD,
            Tags = [.. m.Tags.Select((t, i) => (i, t))],
        };
        foreach (var g in m.Graphs)
        {
            var nodes = new List<NodeEdit>();
            foreach (var n in g.Nodes)
            {
                var ne = new NodeEdit
                {
                    At = n.At, Type = n.Type, OutEdges = n.OutEdgeIndices, CompTags = n.CompTags,
                    NameTag = n.NameTag, GroupTag = n.GroupTag, Unk10 = n.Unk10, Adjacent = n.Adjacent,
                };
                foreach (var b in n.BlendNodes)
                    ne.Blends.Add((b.At, b.Type, b.FloatIndex, b.Flags, b.AnimPathAt, b.AnimId));
                nodes.Add(ne);
            }
            var edges = g.Edges.Select(e => new EdgeEdit
            {
                At = e.At, From = e.NodeA, To = e.NodeB,
                CompTags = e.CompTags, RequestTags = e.RequestTags,
                B8 = e.B8, B9 = e.B9, BA = e.BA, BC = e.BC, Layers = e.Layers,
            }).ToList();
            d.Graphs.Add((g.At, g.AnimLayerCount, g.EntryNodes, g.SpecialNodes, nodes, edges));
        }
        return d;
    }

    public static Doc Read(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new InvalidDataException("empty mog xml");
        var d = new Doc
        {
            Image = Convert.FromBase64String(root.Element("image")?.Value?.Trim()
                    ?? throw new InvalidDataException("mog xml has no <image>")),
            AnimLayerCount = byte.Parse(root.Attribute("animLayerCount")!.Value),
            UnknownD = byte.Parse(root.Attribute("unknownD")!.Value),
        };
        foreach (var ge in root.Elements("graph"))
        {
            var nodes = new List<NodeEdit>();
            foreach (var ne in ge.Elements("node"))
            {
                var n = new NodeEdit
                {
                    At = OptAt(ne),
                    Type = byte.Parse(ne.Attribute("type")!.Value),
                    OutEdges = SplitI(ne.Attribute("outEdges")?.Value ?? ""),
                    CompTags = SplitU(ne.Attribute("compTags")?.Value ?? ""),
                    NameTag = ne.Attribute("name") is { } nt
                              ? ulong.Parse(nt.Value, NumberStyles.HexNumber) : 0,
                    GroupTag = ne.Attribute("group") is { } gt
                              ? ulong.Parse(gt.Value, NumberStyles.HexNumber) : 0,
                    Unk10 = ne.Attribute("unk10") is { } uk ? ushort.Parse(uk.Value) : (ushort)0,
                    Adjacent = SplitU(ne.Attribute("adjacent")?.Value ?? ""),
                };
                foreach (var be in ne.Elements("blend"))
                    n.Blends.Add((OptAt(be),
                                  byte.Parse(be.Attribute("type")!.Value),
                                  byte.Parse(be.Attribute("floatIndex")!.Value),
                                  byte.Parse(be.Attribute("flags")!.Value),
                                  be.Attribute("animAt") is { } aa ? ParseInt(aa.Value) : -1,
                                  ulong.Parse(be.Attribute("anim")!.Value, NumberStyles.HexNumber)));
                nodes.Add(n);
            }
            var edges = ge.Elements("edge").Select(ee => new EdgeEdit
            {
                At = OptAt(ee),
                From = int.Parse(ee.Attribute("from")!.Value),
                To = int.Parse(ee.Attribute("to")!.Value),
                CompTags = SplitU(ee.Attribute("compTags")?.Value ?? ""),
                RequestTags = SplitU(ee.Attribute("requestTags")?.Value ?? ""),
                Layers = SplitU(ee.Attribute("layers")?.Value ?? ""),
                B8 = Byte(ee, 0), B9 = Byte(ee, 1), BA = Byte(ee, 2), BC = Byte(ee, 3),
            }).ToList();
            d.Graphs.Add((ParseInt(ge.Attribute("at")!.Value),
                          byte.Parse(ge.Attribute("animLayerCount")!.Value),
                          SplitU(ge.Attribute("entryNodes")?.Value ?? ""),
                          SplitU(ge.Attribute("specialNodes")?.Value ?? ""),
                          nodes, edges));
        }
        foreach (var t in root.Element("tagMap")?.Elements("tag") ?? [])
            d.Tags.Add((int.Parse(t.Attribute("index")!.Value),
                        ulong.Parse(t.Attribute("id")!.Value, NumberStyles.HexNumber)));
        foreach (var p in root.Element("ganiPool")?.Elements("gani") ?? [])
            d.Pool.Add((ParseInt(p.Attribute("at")!.Value),
                        ulong.Parse(p.Attribute("id")!.Value, NumberStyles.HexNumber)));
        return d;
    }
}
