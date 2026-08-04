// Prove a reversed clip is the original run backwards
// 04/08/2026
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using MgsvModBldr.Tools.Anim;
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Mtar.Transcode;

namespace MgsvModBldr.Tools.Cli;

/// <summary>
/// Decodes a clip, reverses it, decodes again, and asks the only question that matters:
/// does frame t of the reversed clip hold the pose the original held at FrameCount - t?
/// Measured as an angle per rotation channel, so quantisation shows up as tenths of a degree
/// and a wrong reversal shows up as tens.
/// </summary>
internal static class MirrorRevCmd
{
    public static int Run(string mtarPath, string clipName)
    {
        if (clipName == "*") return All(mtarPath);
        return One(mtarPath, clipName, true);
    }

    /// <summary>Every clip in the archive, so one lucky pass cannot pose as a proof.</summary>
    private static int All(string mtarPath)
    {
        var file = File.ReadAllBytes(mtarPath);
        int count = (int)BitConverter.ToUInt32(file, 4);
        var dict = MtarGaniNames.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));
        int pass = 0, fail = 0, skip = 0;
        double worst = 0; string worstClip = "";
        for (int i = 0; i < count; i++)
        {
            int at = 0x20 + i * 16;
            if (at + 16 > file.Length) break;
            if (!dict.TryGetValue(MtarGaniNames.NameHash(BitConverter.ToUInt64(file, at)), out var path)) { skip++; continue; }
            var leaf = path[(path.LastIndexOf('/') + 1)..];
            var r = Measure(file, (int)BitConverter.ToUInt32(file, at + 8), (int)BitConverter.ToUInt32(file, at + 12));
            if (r is null) { skip++; continue; }
            if (r.Value.worst > worst) { worst = r.Value.worst; worstClip = leaf; }
            if (r.Value.worst < 2.0) pass++; else { fail++; Console.WriteLine($"  FAIL {leaf,-40} worst {r.Value.worst:0.000}deg"); }
        }
        Console.WriteLine($"reverse check: {pass:N0} pass / {fail:N0} fail / {skip:N0} skipped");
        Console.WriteLine($"  worst across the archive: {worst:0.0000}deg  ({worstClip})");
        return fail == 0 ? 0 : 1;
    }

    private static int One(string mtarPath, string clipName, bool verbose)
    {
        var file = File.ReadAllBytes(mtarPath);
        int count = (int)BitConverter.ToUInt32(file, 4);
        var dict = MtarGaniNames.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));

        int at0 = -1, len = 0;
        for (int i = 0; i < count; i++)
        {
            int at = 0x20 + i * 16;
            if (at + 16 > file.Length) break;
            if (!dict.TryGetValue(MtarGaniNames.NameHash(BitConverter.ToUInt64(file, at)), out var path)) continue;
            if (!path[(path.LastIndexOf('/') + 1)..].Equals(clipName, StringComparison.OrdinalIgnoreCase)) continue;
            at0 = (int)BitConverter.ToUInt32(file, at + 8);
            len = (int)BitConverter.ToUInt32(file, at + 12);
            break;
        }
        if (at0 < 0) { Console.Error.WriteLine($"FOXDIE: no clip named {clipName}"); return 2; }
        var m = Measure(file, at0, len);
        if (m is null) { Console.Error.WriteLine("FOXDIE: clip has no rotation channels to compare"); return 2; }
        var (mean, worst, frames, tracks, channels, worstAt) = m.Value;
        Console.WriteLine($"{clipName}: {frames}f, {tracks} tracks, {channels} rot channels");
        Console.WriteLine($"  reversed[t] vs original[end-t]   mean {mean:0.0000}deg   worst {worst:0.0000}deg");
        Console.WriteLine($"  worst at {worstAt}");
        Console.WriteLine(worst < 2.0 ? "  PASS - the clip plays backwards" : "  FAIL - not a time reversal");
        return worst < 2.0 ? 0 : 1;
    }

    private static (double mean, double worst, int frames, int tracks, int channels, string worstAt)?
        Measure(byte[] file, int at0, int len)
    {
        var fwd = GaniFile.DecodeV1Gani(file, at0);
        if (fwd is null) return null;

        // Reverse writes into each segment's own blob, and with no unit swap involved the blobs
        // sit where they started — so patching them back in place gives a readable clip.
        var copy = (byte[])file.Clone();
        var g = GaniV1.Read(copy, at0, len);
        if (g is null) return null;
        GaniReverse.Apply(g);
        foreach (var u in g.Units)
            foreach (var s in u.Segments)
                if (s.HasData && s.Blob is not null && s.BlobStart >= 0 && s.BlobStart + s.Blob.Length <= copy.Length)
                    Buffer.BlockCopy(s.Blob, 0, copy, s.BlobStart, s.Blob.Length);
        var rev = GaniFile.DecodeV1Gani(copy, at0);

        var byName = new Dictionary<uint, GaniTrack>();
        foreach (var t in rev.Tracks) byName.TryAdd(t.NameHash32, t);

        double worst = 0, sum = 0; int n = 0, channels = 0;
        string worstAt = "";
        for (int s = 0; s <= 64; s++)
        {
            float u = s / 64f;
            float ta = u * fwd.FrameCount, tb = (1f - u) * rev.FrameCount;
            foreach (var track in fwd.Tracks)
            {
                if (!byName.TryGetValue(track.NameHash32, out var tr)) continue;
                for (int c = 0; c < track.Channels.Count && c < tr.Channels.Count; c++)
                {
                    if (!track.Channels[c].IsRot || !tr.Channels[c].IsRot) continue;
                    var qa = Quaternion.Normalize(track.Channels[c].SampleRot(ta));
                    var qb = Quaternion.Normalize(tr.Channels[c].SampleRot(tb));
                    double dot = Math.Abs((double)qa.X * qb.X + (double)qa.Y * qb.Y +
                                          (double)qa.Z * qb.Z + (double)qa.W * qb.W);
                    double deg = 2.0 * Math.Acos(Math.Clamp(dot, -1, 1)) * 180.0 / Math.PI;
                    sum += deg; n++;
                    if (deg > worst) { worst = deg; worstAt = $"unit {track.NameHash32:x8} ch{c} @{u:0.00}"; }
                    if (s == 0) channels++;
                }
            }
        }
        if (n == 0) return null;
        return (sum / n, worst, fwd.FrameCount, fwd.Tracks.Count, channels, worstAt);
    }
}
