// Does the bit-level mirror equal the decoded-domain one?
// 04/08/2026
using System.Numerics;
using MgsvModBldr.Tools.Anim;
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Mtar.Transcode;

namespace MgsvModBldr.Tools.Cli;

// mirror <in.mtar> --bitcheck <clip>
//
// There are two mirrors in this codebase and only one of them ships. GaniMirror flips SIGN BITS
// in the quantised stream and is what writes the mtar; the harness reflects DECODED quaternions
// and is what every "EXACT" result so far was measured on. If they disagree, the file is wrong
// while the measurement says it is right — which is exactly the symptom.
//
// Run with NO unit swap: a swap moves data between units, but each segment writes back to its
// own recorded offset, so only the reflection half can be compared this way. That is the half
// under suspicion.
internal static class MirrorBitCheckCmd
{
    public static int Run(string mtarPath, string clipName, GaniMirror.Axis axis)
    {
        var file = File.ReadAllBytes(mtarPath);
        int count = (int)BitConverter.ToUInt32(file, 4);
        var dict = MtarGaniNames.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));

        int at = -1, len = 0;
        for (int i = 0; i < count; i++)
        {
            int e = 0x20 + i * 16;
            if (e + 16 > file.Length) break;
            if (!dict.TryGetValue(MtarGaniNames.NameHash(BitConverter.ToUInt64(file, e)), out var p)) continue;
            if (p[(p.LastIndexOf('/') + 1)..].Equals(clipName, StringComparison.OrdinalIgnoreCase))
            { at = (int)BitConverter.ToUInt32(file, e + 8); len = (int)BitConverter.ToUInt32(file, e + 12); break; }
        }
        if (at < 0) { Console.Error.WriteLine($"FOXDIE: no clip named {clipName}"); return 2; }

        // A: reflect the DECODED values (what the harness has been measuring).
        var wanted = GaniFile.DecodeV1Gani(file, at);
        MirrorSolveCmd.MirrorWith(wanted, AxisVec(axis), new List<(int, int)>(), null);

        // B: reflect the BITS, write them back, decode (what actually ships in the mtar).
        var work = (byte[])file.Clone();
        var g = GaniV1.Read(work, at, len);
        GaniMirror.Apply(g, axis, new List<(int, int)>());
        foreach (var s in g.Flat())
            if (s.HasData && s.BlobStart >= 0 && s.BlobStart + s.Blob.Length <= work.Length)
                Buffer.BlockCopy(s.Blob, 0, work, s.BlobStart, s.Blob.Length);
        var got = GaniFile.DecodeV1Gani(work, at);

        Console.WriteLine($"{clipName}   axis {axis}, no swap — bit mirror vs decoded mirror\n");
        Console.WriteLine("  unit  rot worst   pos worst");
        Console.WriteLine("  ------------------------------");
        double allRot = 0, allPos = 0;
        for (int u = 0; u < wanted.Tracks.Count && u < got.Tracks.Count; u++)
        {
            double wr = 0, wp = 0;
            var A = wanted.Tracks[u]; var B = got.Tracks[u];
            for (int c = 0; c < A.Channels.Count && c < B.Channels.Count; c++)
            {
                var x = A.Channels[c]; var y = B.Channels[c];
                if (x.IsRot != y.IsRot) continue;
                for (int s = 0; s <= 24; s++)
                {
                    float fr = s / 24f * wanted.FrameCount;
                    if (x.IsRot)
                    {
                        var qa = x.SampleRot(fr); var qb = y.SampleRot(fr);
                        foreach (var e in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                            wr = Math.Max(wr, (Vector3.Transform(e, qa) - Vector3.Transform(e, qb)).Length());
                    }
                    else wp = Math.Max(wp, (x.SampleVec(fr) - y.SampleVec(fr)).Length());
                }
            }
            allRot = Math.Max(allRot, wr); allPos = Math.Max(allPos, wp);
            Console.WriteLine($"  {u,3} {wr,10:F5}{wp,12:F5}{(wr > 1e-3 || wp > 1e-3 ? "   <-- DIFFERS" : "")}");
        }
        Console.WriteLine($"\n  worst rotation {allRot:F5}, worst position {allPos:F5}");
        Console.WriteLine(allRot < 1e-3 && allPos < 1e-3
            ? "  the two mirrors AGREE — the shipped bits match the verified maths"
            : "  THEY DISAGREE — the mtar does not contain what the harness verified");
        return 0;
    }

    private static Vector3 AxisVec(GaniMirror.Axis a) =>
        a == GaniMirror.Axis.Y ? Vector3.UnitY : a == GaniMirror.Axis.Z ? Vector3.UnitZ : Vector3.UnitX;
}
