// graft one motion graph's states into another, anchored on shared animations
namespace MgsvModBldr.Tools.MotionGraph;

// Replicates the donor's own nodes and edges for animations the host does not already play,
// anchoring them wherever both graphs play the same clip. Donor ids must already be in the
// host's id space (see MogPathPool.Rewrite / --repath-gz2tpp).
public static class MogGraft
{
    public sealed class Report
    {
        public int NewNodes, NewEdges, DroppedEdges, HostTags, FinalTags;
        public int ReachableStates, PlayableClips, DonorOnlyClips, DeadEnds, TrapEntriesRemoved;
        public int EntriesDominating, EntriesContested, EntriesUnconditional;
        public int ScoringTraps, NoExitAtAll, TrapsBroken, ScoringTrapsLeft;
        public Dictionary<int, int> EntriesPerHost = [];   // host NODE INDEX -> entries gained
    }

    // one grafted edge, in host index space
    sealed class Graft
    {
        public int From, To;
        public List<ulong> Comp = [], Req = [];
        public MogEdge Src;
    }

    // One policy, the only one that survived testing: donor conditions stay faithful, entries are
    // scored to outrank stock, exits keep the donor's live-tag conditions, and both topological and
    // scoring traps are broken. The tuning knobs that produced the racing, looping and input-locking
    // builds are gone — see MOG_FORMAT.md.
    public static MogXml.Doc Run(byte[] hostRaw, byte[] donorRaw, out Report rep)
    {
        rep = new Report();
        var host = MogFile.Read(hostRaw);
        var donor = MogFile.Read(donorRaw);
        var doc = MogXml.ToDoc(host, hostRaw);
        if (host.Graphs.Count == 0 || donor.Graphs.Count == 0) return doc;

        var hg = host.Graphs[0];
        var dg = donor.Graphs[0];

        // clips the donor has and the host's pool never mentions
        var hostPool = new HashSet<ulong>(MogPathPool.Find(hostRaw).Select(s => s.Id));
        var donorOnly = new HashSet<ulong>(
            MogPathPool.Find(donorRaw).Select(s => s.Id).Where(id => !hostPool.Contains(id)));
        rep.DonorOnlyClips = donorOnly.Count;

        var animToHosts = new Dictionary<ulong, List<int>>();
        for (int j = 0; j < hg.Nodes.Count; j++)
            foreach (var bn in hg.Nodes[j].BlendNodes)
                if (bn.AnimId != 0)
                    (animToHosts.TryGetValue(bn.AnimId, out var l) ? l : animToHosts[bn.AnimId] = []).Add(j);

        // Donor node -> the host node it has the MOST animations in common with. Taking the first
        // host that shared any one clip made whichever state happened to play a common animation a
        // magnet: 35 grafted entries landed on a single host node, so every one of them competed
        // there. Counting the overlap picks the state that actually corresponds; ties go to the
        // lowest index so the result is deterministic.
        var anchor = new Dictionary<int, int>();
        for (int j = 0; j < dg.Nodes.Count; j++)
        {
            var votes = new Dictionary<int, int>();
            foreach (var bn in dg.Nodes[j].BlendNodes)
                if (bn.AnimId != 0 && animToHosts.TryGetValue(bn.AnimId, out var hosts))
                    foreach (int h in hosts.Distinct()) votes[h] = votes.GetValueOrDefault(h) + 1;
            if (votes.Count == 0) continue;
            int best = -1, bestN = 0;
            foreach (var (h, n) in votes)
                if (n > bestN || (n == bestN && h < best)) { best = h; bestN = n; }
            anchor[j] = best;
        }

        // Seed: donor nodes playing a donor-only clip that do not already anchor to a host state.
        var need = new HashSet<int>();
        for (int j = 0; j < dg.Nodes.Count; j++)
            if (!anchor.ContainsKey(j) && dg.Nodes[j].BlendNodes.Any(x => donorOnly.Contains(x.AnimId)))
                need.Add(j);

        // Close the subgraph. A seed state's real exit often leads to a donor node that plays
        // nothing new — a pure transition state. Skip it and the exit is dropped, stranding the
        // player. So pull in every unanchored node the seeds can reach, until every edge out of
        // the set lands on a node that exists here: another new one, or an anchored host state.
        var frontier = new Queue<int>(need);
        var outOf = new Dictionary<int, List<MogEdge>>();
        foreach (var e in dg.Edges)
            if (e.NodeA >= 0) (outOf.TryGetValue(e.NodeA, out var l) ? l : outOf[e.NodeA] = []).Add(e);
        while (frontier.Count > 0)
        {
            int j = frontier.Dequeue();
            if (!outOf.TryGetValue(j, out var outs)) continue;
            foreach (var e in outs)
            {
                if (e.NodeB < 0 || anchor.ContainsKey(e.NodeB) || need.Contains(e.NodeB)) continue;
                need.Add(e.NodeB);
                frontier.Enqueue(e.NodeB);
            }
        }

        var newIndex = new Dictionary<int, int>();
        foreach (int j in need.OrderBy(x => x))
        {
            newIndex[j] = hg.Nodes.Count + newIndex.Count;
        }

        var hostVocab = new HashSet<ulong>(host.Tags);
        ulong TagOf(MogFile m, ushort i) => i < m.Tags.Length ? m.Tags[i] : 0;

        // Every edge touching a new state — in AND out. Entry edges alone strand the player:
        // it reaches a grafted state and, with no route back into the host graph, input locks
        // and the state never ends.
        int Map(int dj) => newIndex.TryGetValue(dj, out int x) ? x
                         : anchor.TryGetValue(dj, out int a) ? a : -1;
        // The best score a stock edge already offers at each host node. SelectMoveEdge keeps the
        // highest-scoring candidate and a strictly higher score WIPES the rest, so this is the
        // bar a grafted entry has to clear to take precedence there.
        var hostBest = new byte[hg.Nodes.Count];
        foreach (var e in hg.Edges)
            if (e.NodeA >= 0 && e.NodeA < hostBest.Length)
                hostBest[e.NodeA] = Math.Max(hostBest[e.NodeA], MogSelect.Score(e));

        var grafted = new List<Graft>();
        foreach (var e in dg.Edges)
        {
            if (e.NodeA < 0 || e.NodeB < 0) continue;
            bool touchesNew = newIndex.ContainsKey(e.NodeA) || newIndex.ContainsKey(e.NodeB);
            if (!touchesNew) continue;
            int from = Map(e.NodeA), to = Map(e.NodeB);
            if (from < 0 || to < 0) continue;

            // CompTags and RequestTags are DIFFERENT engine checks — CompTag tests the path's own
            // node set, CheckRequestTagsEdge tests the control's live tag set — so folding them
            // into one set, as this used to, misstates every condition it touches.
            var comp = e.CompTags.Select(i => TagOf(donor, i)).Distinct().ToList();
            var req = e.RequestTags.Select(i => TagOf(donor, i)).Distinct().ToList();
            bool isEntry = newIndex.ContainsKey(e.NodeB) && !newIndex.ContainsKey(e.NodeA);
            bool isExit = newIndex.ContainsKey(e.NodeA) && !newIndex.ContainsKey(e.NodeB);

            {
                // Conditions stay faithful. Filtering one down to the host's shared vocabulary
                // BROADENS it — the edge then fires in contexts the donor never meant, and since
                // a conditional edge outscores a stock unconditional one it wins there outright.
                // That is the race. A tag the host never sets simply leaves the edge dormant and
                // stock behaviour carries on: wrong-but-quiet beats wrong-and-winning.
                if (isExit)
                {
                    // GZ drives transitions with RequestTags against its LIVE tag set — 1,595 of
                    // its edges — which is exactly how a state stops looping on itself: the live
                    // condition outranks the comp-gated internal edges. Keep that, filtered to the
                    // shared vocabulary. Broadening is only dangerous on an ENTRY; a broadened exit
                    // just leaves more eagerly, which lands back in stock behaviour.
                    comp.Clear();
                    req = req.Where(hostVocab.Contains).ToList();
                }
                else if (isEntry)
                {
                    // Outrank whatever the host already offers here. Copying the shared-vocabulary
                    // part of the condition into RequestTags lifts the score to 4, above the 2 a
                    // stock edge can reach, and NARROWS the edge — both checks must now pass —
                    // rather than widening it. Winning the score is not enough on its own; the
                    // condition has to stay honest or we just lose more often in more places.
                    foreach (var t in comp) if (hostVocab.Contains(t) && !req.Contains(t)) req.Add(t);
                    if (comp.Count == 0 && req.Count == 0)
                    { rep.EntriesUnconditional++; rep.DroppedEdges++; continue; }
                    if (MogSelect.Beats(MogSelect.Score(comp.Count, req.Count), hostBest[from]))
                        rep.EntriesDominating++;
                    else rep.EntriesContested++;
                }
            }
            grafted.Add(new Graft { From = from, To = to, Comp = comp, Req = req, Src = e });
        }

        // Only let the player into a state they can leave. A grafted state that cannot reach
        // the host graph again is a trap: the player enters and input locks, because the graph
        // has nowhere valid to go. Escape is transitive — a state whose exits all lead to other
        // trapped states is itself a trap.
        {
            // Only an UNCONDITIONAL edge counts as a way out. A conditional exit is not an
            // exit: if its tags never become true in that context the state is trapped in
            // practice, even though a path exists on paper. This is what "turn, turn, turn,
            // then stuck" was — each turn walked deeper until every remaining exit was gated.
            var succ = new Dictionary<int, List<int>>();
            foreach (var x in grafted)
            {
                if (x.Comp.Count + x.Req.Count > 0) continue;
                (succ.TryGetValue(x.From, out var l) ? l : succ[x.From] = []).Add(x.To);
            }
            var escapes = new Dictionary<int, bool>();
            bool Escapes(int x, HashSet<int> path)
            {
                if (x < hg.Nodes.Count) return true;
                if (escapes.TryGetValue(x, out bool k)) return k;
                if (!path.Add(x)) return false;              // cycle among grafted states
                bool r = succ.TryGetValue(x, out var outs) && outs.Any(y => Escapes(y, path));
                path.Remove(x);
                escapes[x] = r;
                return r;
            }
            int before = grafted.Count;
            grafted.RemoveAll(x => x.From < hg.Nodes.Count && !Escapes(x.To, []));
            // An UNCONDITIONAL entry edge is always available, so it competes with the host's
            // own transitions and can win — which is why the first step went wrong every time.
            // A grafted state should only ever be entered in a context that actually calls for
            // it, so entry requires a real condition. Exits stay unconditional on purpose.
            grafted.RemoveAll(x => x.From < hg.Nodes.Count && x.To >= hg.Nodes.Count
                                   && x.Comp.Count + x.Req.Count == 0);
            rep.TrapEntriesRemoved = before - grafted.Count;
        }
        // Must run before the tag merge and edge emission — it only ever CLEARS conditions, so it
        // cannot introduce a tag, but the demotions have to be in place before the edges are written.
        ScoringTraps(grafted, hg.Nodes.Count, rep);

        // merge tags, keeping the map sorted — FindTagIndex and the CompTag merge-intersect
        // both depend on it — then rewrite every existing index through the shift
        var used = new SortedSet<ulong>(host.Tags);
        foreach (var j in newIndex.Keys)
            foreach (var t in dg.Nodes[j].CompTags.Select(i => TagOf(donor, i))) used.Add(t);
        foreach (var g in grafted) { foreach (var t in g.Comp) used.Add(t); foreach (var t in g.Req) used.Add(t); }
        var tags = used.ToList();
        var idx = tags.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => (ushort)x.i);
        var remap = host.Tags.Select(t => idx[t]).ToArray();
        rep.HostTags = host.Tags.Length;
        rep.FinalTags = tags.Count;

        doc.Tags = tags.Select((t, i) => (i, t)).ToList();
        foreach (var gd in doc.Graphs)
        {
            foreach (var nd in gd.Nodes) nd.CompTags = Remap(nd.CompTags, remap);
            foreach (var ed in gd.Edges)
            { ed.CompTags = Remap(ed.CompTags, remap); ed.RequestTags = Remap(ed.RequestTags, remap); }
        }

        var g0 = doc.Graphs[0];
        foreach (var (dj, _) in newIndex.OrderBy(kv => kv.Value))
        {
            var src = dg.Nodes[dj];
            var n = new MogXml.NodeEdit
            {
                At = -1,
                Type = src.Type,
                NameTag = src.NameTag,
                GroupTag = src.GroupTag,
                Unk10 = src.Unk10,
                CompTags = Sorted(src.CompTags.Select(i => idx.GetValueOrDefault(TagOf(donor, i), ushort.MaxValue))
                                              .Where(x => x != ushort.MaxValue)),
            };
            foreach (var bn in src.BlendNodes)
                n.Blends.Add((-1, bn.Type, bn.FloatIndex, bn.Flags, -1, bn.AnimId));
            g0.Nodes.Add(n);
        }
        rep.NewNodes = newIndex.Count;

        int firstEdge = g0.Edges.Count;
        foreach (var g in grafted)
        {
            g0.Edges.Add(new MogXml.EdgeEdit
            {
                At = -1,
                From = g.From,
                To = g.To,
                CompTags = Sorted(g.Comp.Select(t => idx[t])),
                RequestTags = Sorted(g.Req.Select(t => idx[t])),
                Layers = [],
            });
            var owner = g0.Nodes[g.From];
            owner.OutEdges = [.. owner.OutEdges, firstEdge + rep.NewEdges];
            rep.NewEdges++;
        }

        // reachability over edges the host can actually satisfy
        var adj = new Dictionary<int, List<int>>();
        foreach (var (i, g) in grafted.Select((g, i) => (i, g)))
            (adj.TryGetValue(g.From, out var l) ? l : adj[g.From] = []).Add(g.To);
        var seen = new HashSet<int>();
        var stack = new Stack<int>(grafted.Where(g => g.From < hg.Nodes.Count).Select(g => g.To));
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            if (!seen.Add(x)) continue;
            if (adj.TryGetValue(x, out var nx)) foreach (var y in nx) stack.Push(y);
        }
        rep.ReachableStates = seen.Count;
        var inv = newIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        var playable = new HashSet<ulong>();
        foreach (var x in seen)
            if (inv.TryGetValue(x, out int dj))
                foreach (var bn in dg.Nodes[dj].BlendNodes)
                    if (donorOnly.Contains(bn.AnimId)) playable.Add(bn.AnimId);
        rep.PlayableClips = playable.Count;
        var withExit = new HashSet<int>(grafted.Select(x => x.From));
        rep.DeadEnds = newIndex.Values.Count(v => !withExit.Contains(v));

        foreach (var g in grafted)
            if (g.From < hg.Nodes.Count && g.To >= hg.Nodes.Count)
                rep.EntriesPerHost[g.From] = rep.EntriesPerHost.GetValueOrDefault(g.From) + 1;
        return doc;
    }

    // A grafted state can only be LEFT by an edge that outscores every edge staying inside, because
    // SelectMoveEdge takes the highest score and a higher one wipes the rest. An exit that ends up
    // unconditional is score 1 — the lowest there is — so it loses to any satisfiable internal
    // edge. Where those dominant internal edges form a cycle the graph never hands control back:
    // escape exists on paper and can never win. That is a scoring trap, distinct from the
    // topological traps, and it is the crouch-loop seen in-game.
    //
    // Clearing the condition on the edge that CLOSES each cycle drops it to score 1, level with the
    // exit, so the loop can end. Nothing else in the donor's topology is touched, and no edge is
    // invented. Repeat to a fixpoint because demoting one edge can expose another cycle.
    static void ScoringTraps(List<Graft> grafted, int hostCount, Report rep)
    {
        rep.ScoringTraps = Cycles(grafted, hostCount, out int noExit).Count;
        rep.NoExitAtAll = noExit;
        for (int pass = 0; pass < 32; pass++)
        {
            var closing = Cycles(grafted, hostCount, out _);
            if (closing.Count == 0) break;
            foreach (int i in closing)
            { grafted[i].Comp.Clear(); grafted[i].Req.Clear(); rep.TrapsBroken++; }
        }
        rep.ScoringTrapsLeft = Cycles(grafted, hostCount, out _).Count;
    }

    // Edges that close a cycle among internal edges scoring above 1.
    //
    // The escape route out of the donor subgraph is unconditional — score 1 — whether it leaves
    // directly or hops through other grafted states first, so ANY internal edge at score 2 or more
    // outranks it. Cycles among score-1 edges are left alone: they are what stock TPP's own
    // locomotion looks like, and with nothing outscoring the exit the graph can still leave.
    // Comparing against a per-state "best exit" instead was wrong — a state with no exit scores 0,
    // which made even a demoted edge look dominant and the fixpoint never converged.
    static List<int> Cycles(List<Graft> grafted, int hostCount, out int noExit)
    {
        var hasExit = new HashSet<int>();
        foreach (var x in grafted)
            if (x.From >= hostCount && x.To < hostCount) hasExit.Add(x.From);
        var dom = new Dictionary<int, List<(int To, int Idx)>>();
        for (int i = 0; i < grafted.Count; i++)
        {
            var x = grafted[i];
            if (x.From < hostCount || x.To < hostCount) continue;
            if (MogSelect.Score(x.Comp.Count, x.Req.Count) < MogSelect.ScoreComp) continue;
            (dom.TryGetValue(x.From, out var l) ? l : dom[x.From] = []).Add((x.To, i));
        }
        noExit = dom.Keys.Count(x => !hasExit.Contains(x));
        var colour = new Dictionary<int, int>();          // 1 = on the stack, 2 = finished
        var closing = new List<int>();
        void Visit(int n)
        {
            colour[n] = 1;
            foreach (var (y, idx) in dom.GetValueOrDefault(n) ?? [])
            {
                int c = colour.GetValueOrDefault(y);
                if (c == 1) closing.Add(idx);
                else if (c == 0) Visit(y);
            }
            colour[n] = 2;
        }
        foreach (int r in dom.Keys) if (colour.GetValueOrDefault(r) == 0) Visit(r);
        return closing;
    }

    static ushort[] Remap(ushort[] v, ushort[] map) =>
        [.. v.Select(x => x < map.Length ? map[x] : x).Order()];
    static ushort[] Sorted(IEnumerable<ushort> v) => [.. new SortedSet<ushort>(v)];
    static byte Allowed(byte v, byte[] ok) => ok.Contains(v) ? v : (byte)0;
}
