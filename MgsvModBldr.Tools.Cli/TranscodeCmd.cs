// transcode verb: rebuild a v1 (GZ) mtar as a v2 (TPP) mtar
using System.Xml.Serialization;
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Mtar;
using MgsvModBldr.Tools.Mtar.Common;
using MgsvModBldr.Tools.Mtar.Mtar;
using MgsvModBldr.Tools.Mtar.Transcode;

namespace MgsvModBldr.Tools.Cli;

// transcode <in.mtar> --template <v2.mtar> -o <out.mtar> [--limit N]
//
// GZ ships its animations in type-1 (FoxData) mtars; TPP expects type-2. The two use the
// SAME keyframe encoding, so this rewraps rather than re-encodes — every blob is copied
// byte for byte. The template supplies the shared track layout (.trk), which a v2 mtar
// keeps once for all its ganis; we refuse to run unless the source's layout matches it.
internal static class TranscodeCmd
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Usage(); return 2; }
        string src = args[1], template = null, outPath = null;
        int limit = int.MaxValue;
        bool merge = false, over = false, noAdd = false;
        string addOnly = null;
        string graphPath = null;
        var mapArgs = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if ((args[i] is "--template" or "-t") && i + 1 < args.Length) template = args[++i];
            else if ((args[i] is "-o" or "--out") && i + 1 < args.Length) outPath = args[++i];
            else if (args[i] == "--limit" && i + 1 < args.Length) int.TryParse(args[++i], out limit);
            else if (args[i] == "--merge") merge = true;
            else if (args[i] == "--override") over = true;
            else if (args[i] == "--no-add") noAdd = true;
            else if (args[i] == "--add-only" && i + 1 < args.Length) addOnly = args[++i];
            else if (args[i] == "--graph" && i + 1 < args.Length) graphPath = args[++i];
            else if (args[i] == "--map" && i + 1 < args.Length) mapArgs.Add(args[++i]);
        }
        if (!File.Exists(src)) { Console.Error.WriteLine($"FOXDIE: no such mtar: {src}"); return 2; }
        if (template is null || !File.Exists(template)) { Usage(); return 2; }
        outPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(src)) ?? ".",
                                 Path.GetFileNameWithoutExtension(src) + "_v2.mtar");
        outPath = Path.GetFullPath(outPath);

        // ── the template: header fields + the shared track blob ──
        var tpl = new MtarFile2();
        using (var ts = File.OpenRead(template)) tpl.Read(ts);
        // The chain is decoded, so the layout comes from the model rather than a re-read.
        byte[] trk = tpl.TrackNodeBytes();
        var tplLayout = TrackLayout.FromTrk(trk);
        if (tplLayout is null) { Console.Error.WriteLine("FOXDIE: template has no readable .trk layout"); return 2; }

        // ── the source ganis ──
        var file = File.ReadAllBytes(src);
        if (MtarConverter.GetMtarType(src) != 1)
        { Console.Error.WriteLine("FOXDIE: source is already a type-2 mtar"); return 2; }

        int count = (int)BitConverter.ToUInt32(file, 4);
        var dict = MtarGaniNames.LoadDictionary(
            Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));

        // --map: GZ name -> TPP name, for clips the two games spell differently (GZ's heli
        // player set is snapsbh_*, TPP's is snaputh_*). Without it only identical names pair.
        // Each --map is a pair given inline ("gz=tpp", comma-separated for several) or the
        // path of a file holding one pair per line. Two clips do not need a file; a hundred do.
        // Pairs separate on '=', "->" or whitespace, '#' comments. Either side may be a full
        // path or a bare leaf; a leaf resolves against the TEMPLATE's own entries, so a mapped
        // clip can only ever land on a slot that really exists. One GZ clip listed against
        // several targets is written into each of them.
        Dictionary<string, List<string>> renames = null;
        if (mapArgs.Count > 0)
        {
            var mapLines = new List<string>();
            foreach (var m in mapArgs)
            {
                if (m.Contains('=') || m.Contains("->")) mapLines.AddRange(m.Split(',', StringSplitOptions.RemoveEmptyEntries));
                else if (File.Exists(m)) mapLines.AddRange(File.ReadAllLines(m));
                else { Console.Error.WriteLine($"FOXDIE: --map is neither a pair nor a file: {m}"); return 2; }
            }

            var tplByLeaf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in tpl.files) { var n = Norm(f.name); tplByLeaf.TryAdd(Leaf(n), n); }

            renames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in mapLines)
            {
                var line = raw.Split('#')[0].Replace("->", " ").Replace('=', ' ').Trim();
                if (line.Length == 0) continue;
                var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 2) { Console.Error.WriteLine($"FOXDIE: map line needs two names: {raw}"); return 2; }
                var to = Norm(p[1]);
                if (!to.Contains('/') && !tplByLeaf.TryGetValue(to, out to))
                { Console.Error.WriteLine($"FOXDIE: map target not in the template: {p[1]}"); return 2; }
                var key = Norm(p[0]);
                if (!renames.TryGetValue(key, out var list)) renames[key] = list = new List<string>();
                if (!list.Contains(to, StringComparer.OrdinalIgnoreCase)) list.Add(to);
            }
        }
        int renamed = 0;

        var names = new List<string>();
        var nameKeys = new List<ulong>();      // TPP-side name hash, for template lookups
        var bodies = new List<byte[]>();
        var events = new List<byte[]>();
        var motionPoints = new List<byte[]>();
        var mpParents = new List<List<(uint Mtp, uint Bone)>>();
        string sig = null;
        int unnamed = 0, skipped = 0;

        for (int i = 0; i < count && names.Count < limit; i++)
        {
            int at = 0x20 + i * 16;
            ulong hash = BitConverter.ToUInt64(file, at);
            int off = (int)BitConverter.ToUInt32(file, at + 8);
            int len = (int)BitConverter.ToUInt32(file, at + 12);

            var g = GaniV1.Read(file, off, len);
            if (g is null) { skipped++; continue; }

            sig ??= g.Signature();
            if (g.Signature() != sig)
            { Console.Error.WriteLine($"FOXDIE: gani {i} has a different track layout — one mtar cannot hold both"); return 2; }

            if (!dict.TryGetValue(MtarGaniNames.NameHash(hash), out var path))
            { path = $"_unnamed/{hash:x16}"; unnamed++; }

            // Renamed BEFORE anything else looks at the name, so the destination path, the
            // template lookups and the pair/mirror guards all see the TPP clip it becomes.
            // Full path first, then the leaf — GZ clip names are unique across the archive.
            var targets = new List<string> { path };
            if (renames is not null
                && (renames.TryGetValue(Norm(path), out var mapped)
                 || renames.TryGetValue(Leaf(Norm(path)), out mapped)))
            { targets = mapped; renamed += mapped.Count; }

            var body = GaniV2.Write(g);
            foreach (var target in targets)
            {
                // Keep the leading slash: NameResolver keys off "/Assets/", and the vendored
                // Export/Import build their paths by concatenation, so it round-trips as-is.
                var dictPath = target;
                names.Add(target.StartsWith('/') ? target : "/" + target);
                nameKeys.Add(MtarGaniNames.Hash(dictPath, MtarGaniNames.NameMask));
                bodies.Add(body);
                events.Add(g.Events);
                motionPoints.Add(g.MotionPoints);
                mpParents.Add(g.MotionPointParents);
            }
        }
        if (names.Count == 0) { Console.Error.WriteLine("FOXDIE: no ganis decoded"); return 2; }

        if (sig != tplLayout.Signature)
        {
            Console.Error.WriteLine("FOXDIE: source track layout does not match the template's .trk.");
            Console.Error.WriteLine($"  source  : {Short(sig)}");
            Console.Error.WriteLine($"  template: {Short(tplLayout.Signature)}");
            return 2;
        }

        // ── stage the unpacked v2 form, then let the proven packer build the container ──
        var dir = Path.GetDirectoryName(outPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(outPath);
        var stage = Path.Combine(dir, stem + "_mtar");
        Directory.CreateDirectory(stage);

        var ser = new XmlSerializer(typeof(ArchiveFile), new[] { typeof(MtarFile), typeof(MtarFile2) });
        var xmlPath = outPath + ".xml";
        MtarFile2 outFile;
        int added = 0, replaced = 0, kept = 0;

        if (merge)
        {
            // Unpack the template INTO the output slot: that lays down its ganis together
            // with their .mtp/.enchnk/.trk, so every animation we don't touch ships
            // exactly as TPP authored it. Ours are then added alongside.
            File.Copy(template, outPath, overwrite: true);
            MtarConverter.Unpack(outPath);
            using (var xi = File.OpenRead(xmlPath)) outFile = (MtarFile2)ser.Deserialize(xi);
            kept = outFile.files.Count;
        }
        else
        {
            outFile = new MtarFile2
            {
                name = Path.GetFileName(outPath),
                signature = tpl.signature,
                unitCount = tpl.unitCount, segmentCount = tpl.segmentCount,
                shaderNodeCount = tpl.shaderNodeCount, shaderUnitCount = tpl.shaderUnitCount,
                motionPointUnitCount = tpl.motionPointUnitCount, flags = tpl.flags,
            };
            outFile.commonInfo = tpl.commonInfo;
            outFile.trackInfo = tpl.trackInfo;
            outFile.motionPointUnits = tpl.motionPointUnits;
            outFile.skeletonList = tpl.skeletonList;
        }

        // --add-only: a whitelist of clips eligible to be ADDED. Replacements are unaffected.
        // Keeps always-resident archives from swallowing clips that already live elsewhere.
        HashSet<string> addable = null;
        if (addOnly is not null)
        {
            if (!File.Exists(addOnly))
            { Console.Error.WriteLine($"FOXDIE: no such list: {addOnly}"); return 2; }
            addable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(addOnly))
                if (line.Trim().Length > 0) addable.Add(Norm(line));
        }

        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in outFile.files) have.Add(f.name);

        // Which template clips carry motion-point (root trajectory) tracks, keyed by the
        // extension-stripped name hash so it does not depend on table ordering.
        var tplMotionPoints = new HashSet<ulong>();
        {
            var tb = File.ReadAllBytes(template);
            int tn = (int)BitConverter.ToUInt32(tb, 4);
            for (int i = 0; i < tn; i++)
            {
                int at = 0x20 + i * 32;
                if (BitConverter.ToUInt16(tb, at + 0x10) > 0)
                    tplMotionPoints.Add(MtarGaniNames.NameHash(BitConverter.ToUInt64(tb, at)));
            }
        }

        // Not optional: replacing half a pair, or a clip the graph mirrors, produces a build that
        // visibly switches between the two games mid-stride. Both guards always run.
        // Locomotion clips come in foot-phase pairs — the same motion starting on the left or the
        // right foot, and the graph alternates between them every step. Replacing one half when the
        // source has no counterpart for the other leaves the player switching between GZ and stock
        // motion as they walk. Skip those so a pair moves together or not at all.
        var sourceNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        int pairSkipped = 0;

        // A clip's partners: the other foot phase (trailing _l/_r) and the mirrored turn
        // (an lNN/rNN token). Naming is systematic — snapnon_s_wk_tnb_l vs _r, snapnon_c_l45_stp_l
        // vs r45 — so both are recoverable from the name.
        static List<string> Partners(string n)
        {
            var outp = new List<string>();
            if (n.Length > 2 && n[^2] == '_' && (n[^1] == 'l' || n[^1] == 'r'))
                outp.Add(n.Substring(0, n.Length - 1) + (n[^1] == 'l' ? 'r' : 'l'));
            var parts = n.Split('_');
            for (int k = 0; k < parts.Length; k++)
            {
                var q = parts[k];
                if (q.Length < 2 || (q[0] != 'l' && q[0] != 'r')) continue;
                bool digits = true;
                for (int c = 1; c < q.Length; c++) if (!char.IsDigit(q[c])) { digits = false; break; }
                if (!digits) continue;
                var save = parts[k];
                parts[k] = (q[0] == 'l' ? "r" : "l") + q.Substring(1);
                outp.Add(string.Join("_", parts));
                parts[k] = save;
            }
            return outp;
        }

        // Take a pair together or not at all, to a FIXPOINT: dropping one half can strand its
        // partner in another pair, so removals cascade. Nine clips across the whole player set.
        var replaceable = new HashSet<string>(
            have.Where(h => sourceNames.Contains(h)), StringComparer.OrdinalIgnoreCase);
        while (true)
            {
                var drop = replaceable
                    .Where(t => Partners(t).Any(s => have.Contains(s) && !replaceable.Contains(s)))
                    .ToList();
                if (drop.Count == 0) break;
                foreach (var t in drop) replaceable.Remove(t);
            }

        // Clips the motion graph plays MIRRORED. TPP covers both foot phases from one clip by
        // setting bit 0 of a blend leaf's Flags: MotionUtility::NormarizeEventName then swaps the
        // clip's MTEV_AG_SYNC_L/R through AnimEventMirrorMap, so the reflected pose comes with the
        // opposite foot plant. A donor clip authored without that in mind plays fine one way and
        // wrong the other — which is what "correct in one direction only" looks like.
        var mirrored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (graphPath is not null && File.Exists(graphPath))
        {
            var mog = MgsvModBldr.Tools.MotionGraph.MogFile.Read(File.ReadAllBytes(graphPath));
            var ids = new HashSet<ulong>();
            foreach (var g in mog.Graphs)
                foreach (var nd in g.Nodes)
                    foreach (var bn in nd.BlendNodes)
                        if (bn.AnimId != 0 && (bn.Flags & 1) != 0) ids.Add(bn.AnimId);
            foreach (var n in names)
                if (ids.Contains(MtarGaniNames.Hash(n, MtarGaniNames.NameMask)
                                 | ((ulong)MgsvModBldr.Tools.MotionGraph.MogPathPool.TppGaniExt << 51)))
                    mirrored.Add(n);
        }
        int mirrorSkipped = 0;

        int syncKept = 0, motionKept = 0, notListed = 0, mpDeclared = 0;
        for (int i = 0; i < names.Count; i++)
        {
            bool exists = have.Contains(names[i]);
            if (exists && !over) continue;                    // keep the template's version
            // Never turn a clip that HAS foot-plant events into one that hasn't: the motion
            // graph partitions locomotion by foot phase, and a clip with no MTEV_AG_SYNC_L/R
            // never transitions out — that is the movement lock. Keep TPP's whole clip.
            if (exists && !HasSync(events[i])
                       && HasSync(ReadOrEmpty(stage + Path.DirectorySeparatorChar + names[i] + ".enchnk")))
            { syncKept++; continue; }
            // Motion points are the root trajectory. GZ stores 27 motion-point units against
            // TPP's 8, so its tracks do not fit TPP's layout and this transcoder emits none —
            // replacing a clip that HAS them strips its root motion, and anything glued to a
            // surface (ladders, wall climbs) then slides. Keep TPP's clip.
            if (exists && motionPoints[i].Length == 0 && tplMotionPoints.Contains(nameKeys[i]))
            { motionKept++; continue; }
            // The entry table is hash-SORTED on write, so any entry we add interleaves and
            // shifts the index of the ones already there. Anything that addresses
            // animations positionally (the .mog motion graph) then points at the wrong
            // clip. --no-add keeps the table identical and swaps bodies in place.
            if (!exists && noAdd) continue;
            if (!exists && addable is not null && !addable.Contains(Norm(names[i]))) { notListed++; continue; }
            if (exists && !replaceable.Contains(names[i])) { pairSkipped++; continue; }
            if (mirrored.Contains(names[i])) { mirrorSkipped++; continue; }
            // Concatenate the way Import does — Path.Combine would discard `stage`
            // the moment the name starts with a slash.
            var dest = stage + Path.DirectorySeparatorChar + names[i] + ".gani";
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bodies[i]);
            // The event list travels WITH the animation: it carries the MTEV_AG_SYNC_L/R
            // foot-plant events the motion graph needs to resolve foot phase. Ship ours,
            // or drop the template's if this clip genuinely has none.
            var en = stage + Path.DirectorySeparatorChar + names[i] + ".enchnk";
            if (events[i].Length > 0) File.WriteAllBytes(en, events[i]);
            else if (File.Exists(en)) File.Delete(en);

            // .mtp IS the motion-point track data — the root trajectory. Deleting it was
            // what made ladder and wall-climb clips slide: nothing left to move the root.
            // Ship GZ's own tracks; only drop the template's when this clip truly has none.
            var ex = stage + Path.DirectorySeparatorChar + names[i] + ".mtp";
            // Every motion-point unit this clip animates has to be declared by the destination,
            // or AnimFile::GetMotionPointParent finds no parent for it. Konami's archives are
            // always self-consistent; merge ours in so they stay that way.
            if (motionPoints[i].Length > 0 && i < mpParents.Count && outFile.motionPointUnits is not null)
                foreach (var (mtp, bone) in mpParents[i])
                {
                    var key = MgsvModBldr.Tools.Mtar.Utility.StrCode32Names.Text(mtp);
                    if (outFile.motionPointUnits.units.Exists(u =>
                            string.Equals(u.name, key, StringComparison.OrdinalIgnoreCase))) continue;
                    outFile.motionPointUnits.units.Add(new MtarMotionPointUnit
                    { name = MgsvModBldr.Tools.Mtar.Utility.StrCode32Names.Text(mtp),
                      bone = MgsvModBldr.Tools.Mtar.Utility.StrCode32Names.Text(bone) });
                    mpDeclared++;
                }
            if (motionPoints[i].Length > 0) File.WriteAllBytes(ex, motionPoints[i]);
            else if (File.Exists(ex)) File.Delete(ex);

            if (exists) replaced++;
            else { outFile.files.Add(new MtarGaniFile2 { name = names[i] }); added++; }
        }
        kept -= replaced;

        using (var xo = File.Create(xmlPath)) ser.Serialize(xo, outFile);
        MtarConverter.Pack(xmlPath);
        int mpRaised = RaiseMotionPointUnitCount(outPath);

        Console.WriteLine($"Wrote {outFile.files.Count:N0} ganis   ({new FileInfo(outPath).Length:N0} bytes)");
        if (merge) Console.WriteLine($"  template ganis kept as-is : {kept:N0}");
        Console.WriteLine($"  added from GZ             : {added:N0}");
        if (replaced > 0) Console.WriteLine($"  replaced with GZ version  : {replaced:N0}");
        if (renamed > 0) Console.WriteLine($"  renamed via --map         : {renamed:N0}");
        if (merge && !over && names.Count - added > 0)
            Console.WriteLine($"  GZ versions NOT used (already present; pass --override): {names.Count - added:N0}");
        if (syncKept > 0) Console.WriteLine($"  kept TPP clip (GZ has no foot-sync): {syncKept:N0}");
        if (motionKept > 0) Console.WriteLine($"  kept TPP clip (has motion points)  : {motionKept:N0}");
        if (notListed > 0) Console.WriteLine($"  not in --add-only list      : {notListed:N0}");
        if (pairSkipped > 0) Console.WriteLine($"  skipped to keep left/right pairs together   : {pairSkipped:N0}");
        if (mirrorSkipped > 0) Console.WriteLine($"  skipped, the graph plays these MIRRORED     : {mirrorSkipped:N0}");
        if (mpDeclared > 0) Console.WriteLine($"  motion-point units added to the table       : {mpDeclared:N0}");
        if (mpRaised > 0) Console.WriteLine($"  motion-point unit budget raised to : {mpRaised}");
        if (skipped > 0) Console.WriteLine($"  skipped (no bone tracks)  : {skipped}");
        if (unnamed > 0) Console.WriteLine($"  unnamed (kept as hash)    : {unnamed}");
        Console.WriteLine($"  -> {outPath}");
        return 0;
    }

    // Header +0x10 is the largest motion-point unit count of any clip in the archive; the
    // engine sizes per-archive motion-point storage from it. GZ clips reach 27 units where TPP
    // tops out at 8, so leaving TPP's value in place lets a clip overrun that storage. Only
    // ever raise it — some archives legitimately store 0.
    private static int RaiseMotionPointUnitCount(string mtar)
    {
        var b = File.ReadAllBytes(mtar);
        int n = (int)BitConverter.ToUInt32(b, 4);
        ushort cur = BitConverter.ToUInt16(b, 0x10), max = 0;
        for (int i = 0; i < n; i++)
        {
            int at = 0x20 + i * 32;
            if (BitConverter.ToUInt16(b, at + 0x10) == 0) continue;
            int start = (int)BitConverter.ToUInt32(b, at + 8) + BitConverter.ToUInt16(b, at + 0xc) * 0x10;
            if (start < 0 || start + 4 > b.Length) continue;
            uint u = BitConverter.ToUInt32(b, start);
            if (u < 0x1000 && u > max) max = (ushort)u;
        }
        if (max <= cur) return 0;
        BitConverter.GetBytes(max).CopyTo(b, 0x10);
        File.WriteAllBytes(mtar, b);
        return max;
    }

    // Compare clip paths regardless of a leading slash or a .gani suffix.
    private static string Norm(string s)
    {
        s = s.Trim().Replace('\\', '/');
        if (s.EndsWith(".gani", StringComparison.OrdinalIgnoreCase)) s = s[..^5];
        return s.TrimStart('/');
    }

    private static string Leaf(string s) => s[(s.LastIndexOf('/') + 1)..];

    private static string Short(string s) => s.Length <= 96 ? s : s[..96] + "…";

    private static byte[] ReadOrEmpty(string p) => File.Exists(p) ? File.ReadAllBytes(p) : [];

    // MTEV_AG_SYNC_L / MTEV_AG_SYNC_R
    private static bool HasSync(byte[] b)
    {
        for (int i = 0; i + 4 <= b.Length; i++)
        {
            uint v = BitConverter.ToUInt32(b, i);
            if (v == 0x3450f814 || v == 0xd962d8ad) return true;
        }
        return false;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("usage: transcode <in.mtar> --template <v2.mtar> [-o <out.mtar>] [options]");
        Console.Error.WriteLine("  --merge      keep the template's own ganis and add the source's alongside");
        Console.Error.WriteLine("  --override   with --merge, also replace ganis the template already has");
        Console.Error.WriteLine("  --limit N    stop after N ganis");
        Console.Error.WriteLine("  --no-add     replace only; never add a clip the template lacks");
        Console.Error.WriteLine("  --add-only <file>  only add clips whose path is listed");
        Console.Error.WriteLine("  --graph <mog>      the motion graph, so clips it plays MIRRORED are left alone");
        Console.Error.WriteLine("  --map <gz>=<tpp>   rename a source clip onto a template slot; repeatable,");
        Console.Error.WriteLine("                     comma-separates, and takes a file of pairs instead");
    }
}
