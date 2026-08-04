// Prove GaniV2Read/Write lose nothing across a whole archive
// 04/08/2026
using System;
using System.IO;
using MgsvModBldr.Tools.Mtar.Mtar;
using MgsvModBldr.Tools.Mtar.Transcode;

namespace MgsvModBldr.Tools.Cli;

/// <summary>
/// Reads every clip of a v2 archive, re-emits it, and reads it back, asserting the decoded
/// structure is identical: unit names and flags, segment types and bit sizes, and every blob
/// byte. Byte-identity against Konami's own body is NOT the test — their writer starts blobs
/// 4-aligned where ours 16-aligns them, and blob order is not table order. Offsets are explicit
/// in the format, so both layouts are valid and only the CONTENT has to survive.
/// </summary>
internal static class V2RoundTripCmd
{
    public static int Run(string mtarPath)
    {
        var f2 = new MtarFile2();
        using var fs = File.OpenRead(mtarPath);
        f2.Read(fs);
        if (f2.trackInfo is null || f2.trackInfo.units.Count == 0)
        { Console.Error.WriteLine("FOXDIE: not a v2 mtar with a readable .trk"); return 2; }

        int pass = 0, fail = 0, unreadable = 0;
        long bytes = 0;
        foreach (var file in f2.files)
        {
            fs.Position = 0;
            var body = file.ReadData(fs);
            var a = GaniV2Read.Read(body, f2.trackInfo, f2.trackInfo.frameRate);
            if (a is null) { unreadable++; continue; }
            var b = GaniV2Read.Read(GaniV2.Write(a), f2.trackInfo, f2.trackInfo.frameRate);
            if (b is null) { fail++; Console.WriteLine($"  FAIL {file.name}: re-read returned null"); continue; }

            string why = Compare(a, b);
            if (why is null) { pass++; foreach (var s in a.Flat()) bytes += s.Blob.Length; }
            else { fail++; if (fail <= 10) Console.WriteLine($"  FAIL {file.name}: {why}"); }
        }
        Console.WriteLine($"v2 round trip: {pass:N0} pass / {fail:N0} fail / {unreadable:N0} unreadable" +
                          $"   ({bytes / 1024:N0} KB of blob compared)");
        return fail == 0 ? 0 : 1;
    }

    private static string Compare(V1Gani a, V1Gani b)
    {
        if (a.FrameCount != b.FrameCount) return $"frameCount {a.FrameCount} vs {b.FrameCount}";
        if (a.Units.Count != b.Units.Count) return $"unitCount {a.Units.Count} vs {b.Units.Count}";
        for (int u = 0; u < a.Units.Count; u++)
        {
            var ua = a.Units[u]; var ub = b.Units[u];
            if (ua.Name != ub.Name) return $"unit {u} name";
            if (ua.Flags != ub.Flags) return $"unit {u} flags {ua.Flags} vs {ub.Flags}";
            if (ua.Segments.Count != ub.Segments.Count) return $"unit {u} segment count";
            for (int s = 0; s < ua.Segments.Count; s++)
            {
                var sa = ua.Segments[s]; var sb = ub.Segments[s];
                if (sa.Type != sb.Type) return $"unit {u} seg {s} type";
                if (sa.ComponentBitSize != sb.ComponentBitSize) return $"unit {u} seg {s} bit size";
                if (sa.Blob.Length != sb.Blob.Length) return $"unit {u} seg {s} blob {sa.Blob.Length} vs {sb.Blob.Length} bytes";
                for (int i = 0; i < sa.Blob.Length; i++)
                    if (sa.Blob[i] != sb.Blob[i]) return $"unit {u} seg {s} blob byte {i}";
            }
        }
        return null;
    }
}
