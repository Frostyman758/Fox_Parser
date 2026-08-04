// mirror verb: reflect gani clips across a rig plane
// 04/08/2026
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Mtar;
using MgsvModBldr.Tools.Mtar.Transcode;

namespace MgsvModBldr.Tools.Cli;

// mirror <in.mtar> [options]
//
// Reflects clips across a rig plane. The keyframe format makes this a bit edit, not a
// re-encode (see GaniMirror), so it is lossless and mirroring twice restores the source
// bytes — which --selftest asserts.
internal static class MirrorCmd
{
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1])) { Usage(); return 2; }
        string src = args[1], outPath = null, clip = null, asName = null, frigPath = null, solveL = null, solveR = null, revClip = null;
        bool v2rt = false;
        var axis = GaniMirror.Axis.X;
        bool all = false, selftest = false, fit = false;
        string trackClip = null, diffL = null, diffR = null, bitClip = null;
        var extraPairs = new List<(uint, uint)>();
        for (int i = 2; i < args.Length; i++)
        {
            if ((args[i] is "-o" or "--out") && i + 1 < args.Length) outPath = args[++i];
            else if (args[i] == "--clip" && i + 1 < args.Length) clip = args[++i];
            else if (args[i] == "--as" && i + 1 < args.Length) asName = args[++i];
            else if (args[i] == "--all") all = true;
            else if (args[i] == "--selftest") selftest = true;
            else if (args[i] == "--fit") fit = true;
            else if (args[i] == "--track" && i + 1 < args.Length) trackClip = args[++i];
            else if (args[i] == "--diff" && i + 2 < args.Length) { diffL = args[++i]; diffR = args[++i]; }
            else if (args[i] == "--bitcheck" && i + 1 < args.Length) bitClip = args[++i];
            else if (args[i] == "--axis" && i + 1 < args.Length)
                axis = args[++i].ToLowerInvariant() switch { "y" => GaniMirror.Axis.Y, "z" => GaniMirror.Axis.Z, _ => GaniMirror.Axis.X };
            else if (args[i] == "--frig" && i + 1 < args.Length) frigPath = args[++i];
            else if (args[i] == "--solve" && i + 2 < args.Length) { solveL = args[++i]; solveR = args[++i]; }
            else if (args[i] == "--revtest" && i + 1 < args.Length) revClip = args[++i];
            else if (args[i] == "--v2roundtrip") v2rt = true;
            else if (args[i] == "--pair" && i + 1 < args.Length)
            {
                var p = args[++i].Split('=', ',');
                if (p.Length != 2
                    || !uint.TryParse(p[0], System.Globalization.NumberStyles.HexNumber, null, out var a)
                    || !uint.TryParse(p[1], System.Globalization.NumberStyles.HexNumber, null, out var b))
                { Console.Error.WriteLine($"FOXDIE: --pair wants two hex unit names, e.g. --pair f288bffe=7afa9000"); return 2; }
                extraPairs.Add((a, b));
            }
        }

        if (v2rt) return V2RoundTripCmd.Run(src);   // v2 by definition — runs before the v1 gate

        var file = File.ReadAllBytes(src);
        if (MtarConverter.GetMtarType(src) != 1)
        { Console.Error.WriteLine("FOXDIE: mirror reads a type-1 (v1) mtar — that is the format that carries each clip's own layout"); return 2; }

        int count = (int)BitConverter.ToUInt32(file, 4);
        var dict = MtarGaniNames.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));

        // A rig's own MirrorL/MirrorR masks beat any table we could carry.
        List<(int, int)> rigPairs = null;
        if (frigPath is not null)
        {
            if (!File.Exists(frigPath)) { Console.Error.WriteLine($"FOXDIE: no such frig: {frigPath}"); return 2; }
            var frig = MgsvModBldr.Tools.Anim.FrigFile.TryParse(File.ReadAllBytes(frigPath));
            if (frig is null) { Console.Error.WriteLine($"FOXDIE: not a readable frig: {frigPath}"); return 2; }
            rigPairs = GaniMirror.PairsFromRig(frig);
            for (int ui = 0; ui < frig.Units.Count; ui++)
            {
                var ru = frig.Units[ui];
                if (ru.PlaneNormal.LengthSquared() < 1e-6f) continue;
                Console.Error.WriteLine($"  unit {ui,2} type {ru.Type,-22} chain_plane_normal "
                    + $"({ru.PlaneNormal.X,7:F3},{ru.PlaneNormal.Y,7:F3},{ru.PlaneNormal.Z,7:F3})");
            }
            Console.Error.WriteLine($"rig {Path.GetFileName(frigPath)}: {frig.RigUnitCount} units, {frig.Masks.Count} masks, "
                                  + (rigPairs.Count > 0
                                     ? $"MirrorL/R gives {rigPairs.Count} pair(s): {string.Join(" ", rigPairs.ConvertAll(p => $"{p.Item1}<->{p.Item2}"))}"
                                     : "NO MirrorL/MirrorR masks — this rig was not authored to mirror"));
        }

        if (bitClip is not null) return MirrorBitCheckCmd.Run(src, bitClip, axis);
        if (diffL is not null) return MirrorDiffCmd.Run(src, diffL, diffR, rigPairs ?? GaniMirror.PairsFromRig(null));
        if (trackClip is not null) return MirrorTrackCmd.Run(src, trackClip, rigPairs ?? GaniMirror.PairsFromRig(null), axis);
        if (fit) return MirrorFitCmd.Run(src, rigPairs ?? GaniMirror.PairsFromRig(null));
        if (revClip is not null) return MirrorRevCmd.Run(src, revClip);
        if (solveL is not null) return MirrorSolveCmd.Run(src, solveL, solveR, rigPairs);
        if (selftest) return Selftest(file, count, axis, extraPairs, rigPairs);

        if (!all && (clip is null || asName is null)) { Usage(); return 2; }
        Console.Error.WriteLine("FOXDIE: writing mirrored clips into an archive is not wired yet — --selftest proves the transform; use `transcode --map-mirror` to place one.");
        return 2;
    }

    /// <summary>
    /// The proof: mirroring is its own inverse. Every clip is mirrored twice and compared to
    /// the source blob byte for byte. A re-encoding implementation could not pass this.
    /// </summary>
    private static int Selftest(byte[] file, int count, GaniMirror.Axis axis, List<(uint, uint)> extraPairs, List<(int, int)> rigPairs)
    {
        int clips = 0, segs = 0, bytes = 0, bad = 0, changed = 0;
        for (int i = 0; i < count; i++)
        {
            int at = 0x20 + i * 16;
            if (at + 16 > file.Length) break;
            int off = (int)BitConverter.ToUInt32(file, at + 8);
            int len = (int)BitConverter.ToUInt32(file, at + 12);
            var g = GaniV1.Read(file, off, len);
            if (g is null) continue;
            clips++;

            var original = new List<byte[]>();
            foreach (var s in g.Flat()) original.Add((byte[])s.Blob.Clone());

            var pairs = rigPairs ?? PairIndices(g, extraPairs);
            GaniMirror.Apply(g, axis, pairs);

            // After ONE mirror the bytes must differ, or nothing happened.
            int k = 0;
            foreach (var s in g.Flat()) { if (!Same(s.Blob, original[k])) changed++; k++; }

            GaniMirror.Apply(g, axis, pairs);
            k = 0;
            foreach (var s in g.Flat())
            {
                segs++; bytes += s.Blob.Length;
                if (!Same(s.Blob, original[k])) bad++;
                k++;
            }
        }
        Console.WriteLine($"clips {clips}  segments {segs}  blob bytes {bytes:N0}");
        Console.WriteLine($"  segments changed by one mirror : {changed:N0}");
        Console.WriteLine($"  segments not restored by two   : {bad:N0}");
        Console.WriteLine(bad == 0 && changed > 0 ? "INVOLUTION OK — mirror is lossless" : "INVOLUTION FAILED");
        return bad == 0 && changed > 0 ? 0 : 1;
    }

    private static List<(int, int)> PairIndices(V1Gani g, List<(uint, uint)> extra)
        => GaniMirror.PairIndices(g, extra.Count > 0 ? extra : null);

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("usage: mirror <in.mtar> [options]");
        Console.Error.WriteLine("  --selftest        mirror every clip twice and prove the bytes come back");
        Console.Error.WriteLine("  --solve <L> <R>   measure each axis against the game's own twin clip, in 3D");
        Console.Error.WriteLine("  --axis x|y|z      plane normal; the component kept (default x)");
        Console.Error.WriteLine("  --frig <file>     take the left/right pairing from the rig's own MirrorL/MirrorR masks");
        Console.Error.WriteLine("  --pair <a>=<b>    swap these two rig units, hex StrCode32 names; repeatable");
        Console.Error.WriteLine("  --clip <name> --as <name>   mirror one clip under a new name");
    }
}
