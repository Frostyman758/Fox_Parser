// Track a unit's position through a clip, original vs mirrored
// 04/08/2026
using System.Numerics;
using MgsvModBldr.Tools.Anim;
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Mtar.Transcode;

namespace MgsvModBldr.Tools.Cli;

// mirror <in.mtar> --track <clip>
//
// Follows every unit that carries a POSITION channel — the arm and leg units store their IK
// effector as a world position, so a hand or foot can be followed through the clip with no
// skeleton, no bind pose and no FK.
//
// This is the spatial test the mirror actually has to pass: reflecting an animation must
// reflect each tracked point, so a point at (x, y, z) becomes (-x, y, z) frame for frame, and
// a clip that starts and ends in the same place must still start and end there. It needs no
// twin clip to compare against, which matters because the game ships none — its lNN/rNN pairs
// differ by ~0.04 and are the same body motion, not mirror images.
internal static class MirrorTrackCmd
{
    public static int Run(string mtarPath, string clipName, List<(int, int)> pairs, GaniMirror.Axis axis)
    {
        var file = File.ReadAllBytes(mtarPath);
        int count = (int)BitConverter.ToUInt32(file, 4);
        var dict = MtarGaniNames.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));

        int at = -1;
        for (int i = 0; i < count; i++)
        {
            int e = 0x20 + i * 16;
            if (e + 16 > file.Length) break;
            if (!dict.TryGetValue(MtarGaniNames.NameHash(BitConverter.ToUInt64(file, e)), out var p)) continue;
            if (p[(p.LastIndexOf('/') + 1)..].Equals(clipName, StringComparison.OrdinalIgnoreCase))
            { at = (int)BitConverter.ToUInt32(file, e + 8); break; }
        }
        if (at < 0) { Console.Error.WriteLine($"FOXDIE: no clip named {clipName}"); return 2; }

        var orig = GaniFile.DecodeV1Gani(file, at);
        var mirr = GaniFile.DecodeV1Gani(file, at);
        MirrorSolveCmd.MirrorWith(mirr, AxisVec(axis), pairs, null);

        Console.WriteLine($"{clipName}   {orig.FrameCount} frames, mirror axis {axis}\n");

        for (int u = 0; u < orig.Tracks.Count; u++)
        {
            var to = orig.Tracks[u];
            var tm = mirr.Tracks[u];
            int posCh = -1;
            for (int c = 0; c < to.Channels.Count; c++) if (!to.Channels[c].IsRot) { posCh = c; break; }
            Console.WriteLine($"  unit {u,2}  {to.NameHash32:x8}   {FrigName(orig.Tracks.Count, u)}");
            if (posCh < 0) { RotationCheck(orig, mirr, u, pairs, AxisVec(axis)); continue; }
            Console.WriteLine("     frame            original                        mirrored");
            foreach (var f in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                float fr = f * orig.FrameCount;
                var a = to.Channels[posCh].SampleVec(fr);
                var b = posCh < tm.Channels.Count ? tm.Channels[posCh].SampleVec(fr) : default;
                Console.WriteLine($"     {fr,6:F1}   ({a.X,8:F4},{a.Y,8:F4},{a.Z,8:F4})   ({b.X,8:F4},{b.Y,8:F4},{b.Z,8:F4})");
            }
            RotationCheck(orig, mirr, u, pairs, AxisVec(axis));

            // Start and end must survive a mirror: a clip that returns to its origin still must.
            var s0 = to.Channels[posCh].SampleVec(0);
            var e0 = to.Channels[posCh].SampleVec(orig.FrameCount);
            var s1 = tm.Channels[posCh].SampleVec(0);
            var e1 = tm.Channels[posCh].SampleVec(orig.FrameCount);
            Console.WriteLine($"     start->end  original {(e0 - s0).Length(),7:F4}   mirrored {(e1 - s1).Length(),7:F4}"
                            + $"   (must match; reflection preserves distance)\n");
        }
        return 0;
    }

    /// <summary>
    /// The rotation half of the same spatial test. A reflection reverses handedness, so a
    /// mirrored rotation is not "the rotated vector, reflected" — it is R'(Mv) = M R(v). Feed
    /// the mirrored rotation a MIRRORED input vector and the answer must be the mirror of what
    /// the original produced. Compared against the unit's PAIR, since a mirror also swaps sides.
    /// </summary>
    private static void RotationCheck(GaniAnimation orig, GaniAnimation mirr, int unit,
                                      List<(int, int)> pairs, Vector3 n)
    {
        int partner = unit;
        if (pairs is not null)
            foreach (var (a, b) in pairs) { if (a == unit) partner = b; else if (b == unit) partner = a; }

        var src = orig.Tracks[partner];      // the side whose motion lands here after the swap
        var dst = mirr.Tracks[unit];
        int rc = -1;
        for (int c = 0; c < src.Channels.Count && c < dst.Channels.Count; c++)
            if (src.Channels[c].IsRot && dst.Channels[c].IsRot) { rc = c; break; }
        if (rc < 0) return;

        double worst = 0;
        for (int s = 0; s <= 16; s++)
        {
            float fr = s / 16f * orig.FrameCount;
            var qo = src.Channels[rc].SampleRot(fr);
            var qm = dst.Channels[rc].SampleRot(fr);
            foreach (var e in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                var expect = Reflect(Vector3.Transform(e, qo), n);   // M R(v)
                var got = Vector3.Transform(Reflect(e, n), qm);      // R'(Mv)
                double d = (expect - got).Length();
                if (d > worst) worst = d;
            }
        }
        Console.WriteLine($"     rotation vs unit {partner,2}: worst |R'(Mv) - M R(v)| = {worst,8:F5}"
                        + (worst < 1e-3 ? "   EXACT" : "   MISMATCH"));
    }

    private static Vector3 Reflect(Vector3 v, Vector3 n) => v - 2f * Vector3.Dot(v, n) * n;

    private static Vector3 AxisVec(GaniMirror.Axis a) =>
        a == GaniMirror.Axis.Y ? Vector3.UnitY : a == GaniMirror.Axis.Z ? Vector3.UnitZ : Vector3.UnitX;

    /// <summary>Name a unit from the built-in rig tables, by track count.</summary>
    private static string FrigName(int trackCount, int unit)
    {
        var rig = FrigBones.ForUnitCount(trackCount);
        if (rig is null || unit >= rig.UnitBones.Length) return "";
        var b = rig.UnitBones[unit];
        if (b.Length == 0) return "(no bones)";
        var names = new List<string>();
        foreach (var h in b)
        {
            var s = MgsvModBldr.Tools.Mtar.Utility.StrCode32Names.Text(h);
            names.Add(s);
            if (names.Count == 3) break;
        }
        return string.Join(", ", names);
    }
}
