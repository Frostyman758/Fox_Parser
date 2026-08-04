// Read the mirror transform off a real left/right pair
// 04/08/2026
using System.Numerics;
using MgsvModBldr.Tools.Anim;
using MgsvModBldr.Tools.Index;

namespace MgsvModBldr.Tools.Cli;

// mirror <in.mtar> --diff <leftClip> <rightClip>
//
// Konami authored both sides of these, so the relationship between them IS the transform, and
// it can be read off rather than assumed. For every unit this asks two questions:
//   does unit U in the right clip match unit U in the left one? (side-independent)
//   does it match the left clip's PARTNER unit, and under which reflection?
// Reported as a 3D distance per unit, with the reflection that fits best. If the answer is a
// plane, one column wins everywhere; if it is not, that shows too — and either way it comes
// from the game's own data instead of from a formula I picked.
internal static class MirrorDiffCmd
{
    private static readonly (string name, Vector3 n)[] Planes =
    {
        ("x", Vector3.UnitX), ("y", Vector3.UnitY), ("z", Vector3.UnitZ),
    };

    public static int Run(string mtarPath, string leftName, string rightName, List<(int, int)> pairs)
    {
        var file = File.ReadAllBytes(mtarPath);
        int count = (int)BitConverter.ToUInt32(file, 4);
        var dict = MtarGaniNames.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));

        int la = -1, ra = -1;
        for (int i = 0; i < count; i++)
        {
            int e = 0x20 + i * 16;
            if (e + 16 > file.Length) break;
            if (!dict.TryGetValue(MtarGaniNames.NameHash(BitConverter.ToUInt64(file, e)), out var p)) continue;
            var leaf = p[(p.LastIndexOf('/') + 1)..];
            if (leaf.Equals(leftName, StringComparison.OrdinalIgnoreCase)) la = (int)BitConverter.ToUInt32(file, e + 8);
            else if (leaf.Equals(rightName, StringComparison.OrdinalIgnoreCase)) ra = (int)BitConverter.ToUInt32(file, e + 8);
        }
        if (la < 0 || ra < 0) { Console.Error.WriteLine("FOXDIE: one of those clips is not in this mtar"); return 2; }

        var L = GaniFile.DecodeV1Gani(file, la);
        var R = GaniFile.DecodeV1Gani(file, ra);
        Console.WriteLine($"{leftName} ({L.FrameCount}f)  vs  {rightName} ({R.FrameCount}f)\n");
        Console.WriteLine("  unit                              same    partner   +mirror x   +mirror y   +mirror z");
        Console.WriteLine("  ------------------------------------------------------------------------------------");

        // These are WORLD orientations, so a clip that faces the other way differs everywhere by
        // its root yaw. Divide the root out first or a perfect mirror still reads as wrong.
        var rootL = RootChannel(L);
        var rootR = RootChannel(R);

        for (int u = 0; u < L.Tracks.Count && u < R.Tracks.Count; u++)
        {
            int partner = u;
            if (pairs is not null)
                foreach (var (a, b) in pairs) { if (a == u) partner = b; else if (b == u) partner = a; }

            double same = Dist(L.Tracks[u], R.Tracks[u], L, R, null, rootL, rootR);
            double part = Dist(L.Tracks[partner], R.Tracks[u], L, R, null, rootL, rootR);
            var mir = new double[Planes.Length];
            for (int p = 0; p < Planes.Length; p++)
                mir[p] = Dist(L.Tracks[partner], R.Tracks[u], L, R, Planes[p].n, rootL, rootR);

            Console.WriteLine($"  {u,2} {Name(L.Tracks.Count, u),-28}{same,8:F4}{part,10:F4}"
                            + $"{mir[0],12:F4}{mir[1],12:F4}{mir[2],12:F4}");
        }
        Console.WriteLine("\n  lowest column per row is the transform the game itself used.");
        return 0;
    }

    /// <summary>
    /// Mean 3D distance between two units' motion, optionally reflecting the first across a
    /// plane. Orientations become three unit-length points; a reflection is compared through
    /// the mirror (R'(Mv) = M R(v)) because reflecting reverses handedness.
    /// </summary>
    /// <summary>The clip's root rotation channel — unit 0, the trajectory.</summary>
    private static GaniChannel RootChannel(GaniAnimation g)
    {
        if (g.Tracks.Count == 0) return null;
        foreach (var c in g.Tracks[0].Channels) if (c.IsRot) return c;
        return null;
    }

    private static double Dist(GaniTrack a, GaniTrack b, GaniAnimation ca, GaniAnimation cb, Vector3? n,
                               GaniChannel rootA, GaniChannel rootB)
    {
        double sum = 0; int cnt = 0;
        for (int s = 0; s <= 24; s++)
        {
            float t = s / 24f;
            for (int c = 0; c < a.Channels.Count && c < b.Channels.Count; c++)
            {
                var x = a.Channels[c]; var y = b.Channels[c];
                if (x.IsRot != y.IsRot) continue;
                if (x.IsRot)
                {
                    var qa = x.SampleRot(t * ca.FrameCount);
                    var qb = y.SampleRot(t * cb.FrameCount);
                    // Root-relative: strip each clip's own facing before comparing.
                    if (rootA is not null) qa = Quaternion.Conjugate(rootA.SampleRot(t * ca.FrameCount)) * qa;
                    if (rootB is not null) qb = Quaternion.Conjugate(rootB.SampleRot(t * cb.FrameCount)) * qb;
                    foreach (var e in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    {
                        Vector3 pa = n is null
                            ? Vector3.Transform(e, qa)
                            : Refl(Vector3.Transform(e, qa), n.Value);
                        Vector3 pb = n is null
                            ? Vector3.Transform(e, qb)
                            : Vector3.Transform(Refl(e, n.Value), qb);
                        sum += (pa - pb).Length(); cnt++;
                    }
                }
                else
                {
                    var va = x.SampleVec(t * ca.FrameCount);
                    var vb = y.SampleVec(t * cb.FrameCount);
                    if (rootA is not null) va = Vector3.Transform(va, Quaternion.Conjugate(rootA.SampleRot(t * ca.FrameCount)));
                    if (rootB is not null) vb = Vector3.Transform(vb, Quaternion.Conjugate(rootB.SampleRot(t * cb.FrameCount)));
                    if (n is not null) va = Refl(va, n.Value);
                    sum += (va - vb).Length(); cnt++;
                }
            }
        }
        return cnt == 0 ? 0 : sum / cnt;
    }

    private static Vector3 Refl(Vector3 v, Vector3 n) => v - 2f * Vector3.Dot(v, n) * n;

    private static string Name(int trackCount, int unit)
    {
        var rig = FrigBones.ForUnitCount(trackCount);
        if (rig is null || unit >= rig.UnitBones.Length || rig.UnitBones[unit].Length == 0) return "";
        return MgsvModBldr.Tools.Mtar.Utility.StrCode32Names.Text(rig.UnitBones[unit][0]);
    }
}
