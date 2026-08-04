// Fit the mirror correction on one clip pair, test it on the rest
// 04/08/2026
using System.Numerics;
using MgsvModBldr.Tools.Anim;
using MgsvModBldr.Tools.Index;

namespace MgsvModBldr.Tools.Cli;

// mirror <in.mtar> --fit [--frig f]
//
// TRAIN on one left/right twin pair, TEST on every other pair in the archive. A correction
// fitted from data is only meaningful if it transfers — if it beats the unmirrored baseline
// on pairs it never saw, it is the rig's real rest offset; if it only fits its training pair,
// the model is wrong and this says so instead of shipping a mirror that looks plausible.
internal static class MirrorFitCmd
{
    public static int Run(string mtarPath, List<(int, int)> pairs)
    {
        var file = File.ReadAllBytes(mtarPath);
        int count = (int)BitConverter.ToUInt32(file, 4);
        var dict = MtarGaniNames.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));

        // Every clip, by leaf name, with its data offset.
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            int e = 0x20 + i * 16;
            if (e + 16 > file.Length) break;
            if (!dict.TryGetValue(MtarGaniNames.NameHash(BitConverter.ToUInt64(file, e)), out var p)) continue;
            at[p[(p.LastIndexOf('/') + 1)..]] = (int)BitConverter.ToUInt32(file, e + 8);
        }

        // Twins that differ only by an lNN -> rNN direction token.
        var twins = new List<(string l, string r)>();
        foreach (var k in at.Keys)
        {
            var m = System.Text.RegularExpressions.Regex.Match(k, @"_l(\d+)_");
            if (!m.Success) continue;
            var r = System.Text.RegularExpressions.Regex.Replace(k, @"_l(\d+)_", "_r$1_");
            if (at.ContainsKey(r)) twins.Add((k, r));
        }
        if (twins.Count < 2) { Console.Error.WriteLine("FOXDIE: need at least two twin pairs to fit and test"); return 2; }
        twins.Sort((x, y) => string.CompareOrdinal(x.l, y.l));

        var (tl, tr) = twins[0];
        var src = GaniFile.DecodeV1Gani(file, at[tl]);
        var dst = GaniFile.DecodeV1Gani(file, at[tr]);
        var corr = MirrorSolveCmd.SolveCorrection(src, dst, pairs, Vector3.UnitX);
        Console.WriteLine($"trained on {tl} -> {tr}   ({corr.Count} unit corrections)");
        Console.WriteLine($"testing on the other {twins.Count - 1} pair(s)\n");
        Console.WriteLine("  pair                            fitted   baseline   verdict");
        Console.WriteLine("  --------------------------------------------------------------");

        int win = 0, tot = 0; double sf = 0, sb = 0;
        foreach (var (l, r) in twins)
        {
            var a = GaniFile.DecodeV1Gani(file, at[l]);
            var b = GaniFile.DecodeV1Gani(file, at[r]);
            double baseErr = MirrorSolveCmd.MeanError(a, b);

            var m2 = GaniFile.DecodeV1Gani(file, at[l]);
            MirrorSolveCmd.MirrorWith(m2, Vector3.UnitX, pairs, corr);
            double fitErr = MirrorSolveCmd.MeanError(m2, b);

            tot++; sf += fitErr; sb += baseErr;
            bool better = fitErr < baseErr;
            if (better) win++;
            if (tot <= 12)
                Console.WriteLine($"  {l,-30}{fitErr,8:F4}{baseErr,10:F4}   {(better ? "better" : "worse")}"
                                + (l == tl ? "   <- trained on this" : ""));
        }
        Console.WriteLine($"\n  fitted beat baseline on {win} of {tot}   mean fitted {sf / tot:F4}  mean baseline {sb / tot:F4}");
        Console.WriteLine(win > tot * 0.8 ? "  GENERALISES — the correction is the rig's real offset"
                                          : "  DOES NOT GENERALISE — model is wrong, do not ship this mirror");
        return 0;
    }
}
