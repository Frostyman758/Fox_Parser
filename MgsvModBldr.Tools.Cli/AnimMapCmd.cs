// Pair two characters' clips by name, for cross-character ports
// 04/08/2026
using System;
using System.Collections.Generic;
using System.IO;
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Mtar;
using MgsvModBldr.Tools.Mtar.Mtar;

namespace MgsvModBldr.Tools.Cli;

/// <summary>
/// Two characters animate the same rig but do not share a clip vocabulary — Snake's heli
/// boarding is snaputh_*, Quiet's is qui0uth_*, and past the prefix their motion names diverge
/// too (Snake's set is ten times larger and far more granular). Exact-name pairing finds almost
/// nothing, so clips are matched on their TOKENS instead.
///
/// A name is prefix + underscore-separated tokens. The prefix identifies the character and is
/// dropped; what remains is the motion vocabulary both sides really do share — stance (s/q/c/p),
/// verb (idl/wk/rn/dsh/jmp/dam/rdy), angle (l90/r180), phase (st/lp/ed) and foot (_l/_r).
/// Score is a weighted token overlap, and the STANCE token must agree — a standing clip dropped
/// into a prone slot is not a port, it is a bug.
/// </summary>
internal static class AnimMapCmd
{
    private static readonly string[] Stances = { "s", "q", "c", "p" };

    public static int Run(string[] args)
    {
        if (args.Length < 3) { Usage(); return 2; }
        var srcPaths = new List<string>();
        var dstPaths = new List<string>();
        string outPath = null;
        double min = 0.55;
        string srcPrefix = null;
        bool listing = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--to" && i + 1 < args.Length) { listing = true; dstPaths.Add(args[++i]); }
            else if ((args[i] is "-o" or "--out") && i + 1 < args.Length) outPath = args[++i];
            else if (args[i] == "--min" && i + 1 < args.Length) double.TryParse(args[++i], out min);
            else if (args[i] == "--src-prefix" && i + 1 < args.Length) srcPrefix = args[++i];
            else if (args[i] == "--list") listing = true;
            else if (listing && dstPaths.Count > 0) dstPaths.Add(args[i]);
            else srcPaths.Add(args[i]);
        }
        if (srcPaths.Count == 0 || dstPaths.Count == 0) { Usage(); return 2; }

        var srcNames = new List<string>();
        foreach (var p in srcPaths) srcNames.AddRange(Names(p));
        // A character archive also ships clips that are not the character's own — shared
        // enemy-interaction and weapon sets. Porting those is not what "use her animations" means.
        if (srcPrefix is not null)
            srcNames.RemoveAll(n => !n.StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase));
        var dstNames = new List<string>();
        foreach (var p in dstPaths) dstNames.AddRange(Names(p));
        if (srcNames.Count == 0 || dstNames.Count == 0)
        { Console.Error.WriteLine("FOXDIE: no named clips on one side — is the mtar dictionary present?"); return 2; }

        // Index the source once: stance -> clips, so each target only scores its own stance.
        var byStance = new Dictionary<string, List<(string name, List<string> tok)>>();
        foreach (var n in srcNames)
        {
            var t = Tokens(n);
            var st = Stance(t) ?? "?";
            if (!byStance.TryGetValue(st, out var l)) byStance[st] = l = new List<(string, List<string>)>();
            l.Add((n, t));
        }

        var lines = new List<string>();
        int matched = 0;
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dstNames)
        {
            var dt = Tokens(d);
            var st = Stance(dt) ?? "?";
            if (!byStance.TryGetValue(st, out var cands)) continue;
            string best = null; double bestScore = 0;
            foreach (var (sn, stk) in cands)
            {
                double sc = Score(stk, dt);
                if (sc > bestScore) { bestScore = sc; best = sn; }
            }
            if (best is null || bestScore < min) continue;
            lines.Add($"{best} = {d}    # {bestScore:0.00}");
            used[best] = used.TryGetValue(best, out var c) ? c + 1 : 1;
            matched++;
        }
        lines.Sort(StringComparer.OrdinalIgnoreCase);

        if (outPath is not null) File.WriteAllLines(outPath, lines);
        else foreach (var l in lines) Console.WriteLine(l);

        Console.Error.WriteLine($"source clips {srcNames.Count:N0}  target slots {dstNames.Count:N0}  "
                              + $"matched {matched:N0} at >= {min:0.00}  ({used.Count} distinct source clips used)");
        return 0;
    }

    private static List<string> Names(string mtarPath)
    {
        var outp = new List<string>();
        if (!File.Exists(mtarPath)) return outp;
        var dict = MtarGaniNames.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));
        if (MtarConverter.GetMtarType(mtarPath) == 1)
        {
            var file = File.ReadAllBytes(mtarPath);
            int count = (int)BitConverter.ToUInt32(file, 4);
            for (int i = 0; i < count; i++)
            {
                int at = 0x20 + i * 16;
                if (at + 16 > file.Length) break;
                if (dict.TryGetValue(MtarGaniNames.NameHash(BitConverter.ToUInt64(file, at)), out var p))
                    outp.Add(p[(p.LastIndexOf('/') + 1)..]);
            }
            return outp;
        }
        var f2 = new MtarFile2();
        using var fs = File.OpenRead(mtarPath);
        f2.Read(fs);
        foreach (var f in f2.files)
            if (dict.TryGetValue(MtarGaniNames.NameHash(f.hash), out var p))
                outp.Add(p[(p.LastIndexOf('/') + 1)..]);
        return outp;
    }

    /// <summary>Everything after the character prefix, which is the first token.</summary>
    private static List<string> Tokens(string name)
    {
        var parts = name.Split('_');
        var outp = new List<string>();
        for (int i = 1; i < parts.Length; i++) if (parts[i].Length > 0) outp.Add(parts[i]);
        return outp;
    }

    private static string Stance(List<string> t)
    {
        foreach (var s in Stances) if (t.Count > 0 && t[0] == s) return s;
        return null;
    }

    /// <summary>
    /// Weighted overlap. A token shared by both sides counts once; tokens carrying real motion
    /// meaning (a verb or an angle) count double, so "run at 90 degrees" beats a pair that only
    /// agrees on filler. Normalised by the LONGER side so a two-token clip cannot score 1.00
    /// against a ten-token slot just by being a subset of it.
    /// </summary>
    private static double Score(List<string> a, List<string> b)
    {
        var pool = new List<string>(b);
        double hit = 0, total = 0;
        foreach (var t in a)
        {
            double w = Weight(t);
            total += w;
            int at = pool.IndexOf(t);
            if (at >= 0) { hit += w; pool.RemoveAt(at); }
        }
        foreach (var t in b) total += Weight(t);
        return total <= 0 ? 0 : 2.0 * hit / total;
    }

    private static double Weight(string t)
    {
        switch (t)
        {
            case "idl": case "wk": case "wlk": case "rn": case "run": case "dsh": case "jmp":
            case "dam": case "dwn": case "die": case "rdy": case "rld": case "fre": case "aim":
                return 2.0;
        }
        if (t.Length > 1 && (t[0] == 'l' || t[0] == 'r'))
        {
            bool digits = true;
            for (int i = 1; i < t.Length; i++) if (!char.IsDigit(t[i])) { digits = false; break; }
            if (digits) return 2.0;                       // an angle is load-bearing
        }
        return 1.0;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("usage: animmap <source.mtar>... --to <target.mtar>... [-o map.txt] [--min 0.55]");
        Console.Error.WriteLine("  pairs two characters' clips by token similarity; writes a `transcode --map` file");
    }
}
