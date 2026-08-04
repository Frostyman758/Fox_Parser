// mog verb: inspect, compare and repath a FOXMOTIONGRAPH
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.MotionGraph;

namespace MgsvModBldr.Tools.Cli;

internal static class MogCmd
{
    public static int Run(string[] args)
    {
        var files = new List<string>();
        string outPath = null, dictPath = null;
        bool diff = false, pool = false, gz2tpp = false, validate = false, graft = false;
        bool census = false;

        for (int i = 1; i < args.Length; i++)
        {
            if ((args[i] is "-o" or "--out") && i + 1 < args.Length) outPath = args[++i];
            else if (args[i] == "-d" && i + 1 < args.Length) dictPath = args[++i];
            else if (args[i] == "--diff") diff = true;
            else if (args[i] == "--pool") pool = true;
            else if (args[i] == "--validate") validate = true;
            else if (args[i] == "--repath-gz2tpp") gz2tpp = true;
            else if (args[i] == "--graft") graft = true;
            else if (args[i] == "--census") census = true;
            else files.Add(args[i]);
        }
        if (files.Count == 0) { Usage(); return 2; }
        foreach (var f in files)
            if (!File.Exists(f) && !Directory.Exists(f))
            { Console.Error.WriteLine($"FOXDIE: no such mog: {f}"); return 2; }

        dictPath ??= Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt");

        if (diff)
        {
            if (files.Count < 2) { Console.Error.WriteLine("FOXDIE: --diff needs two mogs"); return 2; }
            var a = MogFile.Read(File.ReadAllBytes(files[0]));
            var b = MogFile.Read(File.ReadAllBytes(files[1]));
            Console.Write(MogReport.Diff(a, b, files[0], files[1]));
            return 0;
        }

        if (graft)
        {
            if (files.Count < 2) { Console.Error.WriteLine("FOXDIE: --graft needs <host.mog> <donor.mog>"); return 2; }
            var doc = MogGraft.Run(File.ReadAllBytes(files[0]), File.ReadAllBytes(files[1]), out var rep);
            var built = MogBuilder.Build(doc);
            var res = MogValidate.Run(built);
            Console.WriteLine($"donor-only clips {rep.DonorOnlyClips:N0}   new states {rep.NewNodes:N0}"
                            + $"   new edges {rep.NewEdges:N0}   dropped {rep.DroppedEdges:N0}");
            Console.WriteLine($"tags {rep.HostTags} -> {rep.FinalTags}"
                            + $"   reachable states {rep.ReachableStates:N0}   playable clips {rep.PlayableClips:N0}"
                            + $"   dead ends {rep.DeadEnds:N0}   trap entries removed {rep.TrapEntriesRemoved:N0}");
            Console.WriteLine($"entry edges: dominating (score 4) {rep.EntriesDominating:N0}"
                            + $"   tying stock score {rep.EntriesContested:N0}"
                            + $"   unconditional dropped {rep.EntriesUnconditional:N0}");
            Console.WriteLine($"scoring traps (dominant internal cycle) {rep.ScoringTraps:N0}"
                            + $" -> broke {rep.TrapsBroken:N0} edges, {rep.ScoringTrapsLeft:N0} left"
                            + $"   no exit at all {rep.NoExitAtAll:N0}");
            Console.WriteLine("host nodes taking the most grafted entries:");
            foreach (var (node, n) in rep.EntriesPerHost.OrderByDescending(k => k.Value).Take(8))
                Console.WriteLine($"    host node {node}: {n} entries");
            Console.Write(MogSelect.Report(MogFile.Read(built), "  built"));
            if (!res.Ok)
            {
                Console.Error.WriteLine($"FOXDIE: grafted mog fails {res.Errors.Count} invariant check(s):");
                foreach (var e in res.Errors.Take(15)) Console.Error.WriteLine($"  {e}");
                return 1;
            }
            var op = outPath ?? files[0] + ".grafted.mog";
            File.WriteAllBytes(op, built);
            Console.WriteLine($"  -> {op}   (invariants ok)");
            return 0;
        }

        if (census)
        {
            var rows = new List<MogCensus.Row>();
            foreach (var f in files.SelectMany(x => Directory.Exists(x)
                         ? Directory.GetFiles(x, "*.mog", SearchOption.AllDirectories) : [x]).Order())
                try { rows.Add(MogCensus.Measure(File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f))); }
                catch (Exception e) { Console.Error.WriteLine($"  skipped {f}: {e.Message}"); }
            Console.Write(MogCensus.Table(rows));
            Console.WriteLine();
            Console.Write(MogCensus.Summary(rows, "corpus"));
            return 0;
        }

        var raw = File.ReadAllBytes(files[0]);
        if (validate)
        {
            var vr = MogValidate.Run(raw);
            if (vr.Ok) { Console.WriteLine($"{files[0]}: all invariants hold"); return 0; }
            Console.WriteLine($"{files[0]}: {vr.Errors.Count} problem(s)");
            foreach (var e in vr.Errors) Console.WriteLine($"  {e}");
            return 1;
        }
        var mog = MogFile.Read(raw);
        var slots = MogPathPool.Find(raw);


        if (gz2tpp) return Repath(raw, slots, dictPath, outPath ?? files[0] + ".tpp.mog");

        if (pool)
        {
            var dict = LoadDict(dictPath);
            int named = 0;
            foreach (var s in slots)
            {
                dict.TryGetValue(MtarGaniNames.NameHash(s.Id), out var name);
                if (name is not null) named++;
                Console.WriteLine($"0x{s.At:x8}  {s.Id:x16}  refs={s.RefCount,-4} {name ?? "?"}");
            }
            Console.WriteLine($"{named:N0} of {slots.Count:N0} named");
            return 0;
        }

        Console.Write(MogReport.Dump(mog, files[0], slots));
        return 0;
    }

    // GZ ids are hash48 | (11<<52); TPP wants PathCode64 with the .gani ext at bit 51.
    // Same width, so the rewrite is in place and no offset in the file moves.
    private static int Repath(byte[] raw, List<MogPathPool.Slot> slots, string dictPath, string outPath)
    {
        var dict = LoadDict(dictPath);
        var map = new Dictionary<ulong, ulong>();
        int unnamed = 0;
        foreach (var s in slots)
        {
            if (!MtarGaniNames.IsGzLayout(s.Id)) continue;
            if (!dict.TryGetValue(MtarGaniNames.NameHash(s.Id), out var name)) { unnamed++; continue; }
            map[s.Id] = MtarGaniNames.Hash(name, MtarGaniNames.NameMask)
                        | ((ulong)MogPathPool.TppGaniExt << 51);
        }
        int changed = MogPathPool.Rewrite(raw, map, out int untouched);
        File.WriteAllBytes(outPath, raw);
        Console.WriteLine($"Repathed {changed:N0} pool ids GZ -> TPP");
        if (unnamed > 0) Console.WriteLine($"  unnamed, left as GZ ids : {unnamed:N0}");
        if (untouched > 0) Console.WriteLine($"  not GZ / unmapped       : {untouched:N0}");
        Console.WriteLine($"  -> {outPath}");
        return 0;
    }

    private static Dictionary<ulong, string> LoadDict(string p)
        => File.Exists(p) ? MtarGaniNames.LoadDictionary(p) : [];

    private static void Usage()
    {
        Console.Error.WriteLine("usage: mog <file.mog> [options]");
        Console.Error.WriteLine("  (no option)          structure report");
        Console.Error.WriteLine("  --pool [-d dict]     list the .gani PathId pool, resolved to names");
        Console.Error.WriteLine("  --validate           check every structural invariant");
        Console.Error.WriteLine("  <dir|files> --census one row per mog: how the corpus is authored");
        Console.Error.WriteLine("  <a> <b> --diff       compare two graphs (tags, nodes, layers)");
        Console.Error.WriteLine("  --repath-gz2tpp -o   rewrite GZ pool ids to TPP PathCode64 in place");
        Console.Error.WriteLine("  <host> <donor> --graft -o <out.mog>");
        Console.Error.WriteLine("                       graft the donor's states for clips the host lacks");
    }
}
