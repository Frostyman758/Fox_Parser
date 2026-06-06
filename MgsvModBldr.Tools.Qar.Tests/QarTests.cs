using System.Diagnostics;
using MgsvModBldr.Tools.Qar;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Qar.Tests;

/// <summary>
/// QAR (.dat) gate, two real checks:
///   (A) EXTRACTION CORRECTNESS — every extracted file (name AND bytes)
///       must byte-match cap's datfpk reference extraction.
///   (B) PACK ROUND-TRIP — repack, re-extract, require byte-identical to
///       the first extraction.
/// </summary>
public sealed class QarTests : IToolTests
{
    public string Name => "qar";

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- QAR (.dat: extraction vs datfpk + pack round-trip) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string dat)
    {
        var work = MakeTmp("qar_rt_");
        try
        {
            var stageDat = Path.Combine(work, Path.GetFileName(dat));
            File.Copy(dat, stageDat, overwrite: true);

            // (A) Extract + compare to datfpk reference if present.
            var manifestPath = QarPacker.Unpack(stageDat);
            var extractDir   = Path.Combine(work, Path.GetFileNameWithoutExtension(dat) + "_dat");

            var refDir = ReferenceDirFor(dat);
            string note;
            if (refDir is not null)
            {
                var (rm, rd, rmiss) = ByteCompareTrees(refDir, extractDir);
                if (rd > 0 || rmiss > 0)
                    return (false, $"vs datfpk: {rd} differ, {rmiss} missing (of {rm + rd + rmiss})");
                note = $"{rm} files byte-match datfpk";
            }
            else
            {
                note = "no datfpk ref (round-trip only)";
            }

            // (B) Repack → re-extract → compare to first extraction.
            var repacked = Path.Combine(work, "repacked.dat");
            QarPacker.Pack(manifestPath, repacked);
            var reExtractManifest = QarPacker.Unpack(repacked);
            var reExtractDir = Path.Combine(work, "repacked_dat");

            var (m2, d2, miss2) = ByteCompareTrees(extractDir, reExtractDir);
            if (d2 > 0 || miss2 > 0)
                return (false, $"pack round-trip: {d2} differ, {miss2} missing (of {m2 + d2 + miss2})");

            return (true, $"{note}; round-trip {m2} ok");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    /// <summary>Locate a datfpk reference extraction for this .dat, if cached.</summary>
    private static string ReferenceDirFor(string dat)
    {
        var stem = Path.GetFileNameWithoutExtension(dat);
        var cand = Path.Combine(FixturesDir, "qar", stem + "_ref");
        return Directory.Exists(cand) ? cand : null;
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "qar");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.dat", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    /// <summary>
    /// Z:\master1 has video <c>e2*.dat</c> blobs alongside the real game
    /// archives. We harvest <c>data1.dat</c> only — smallest real QAR and
    /// the one modders care about — plus its datfpk reference extraction.
    /// </summary>
    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "qar");
        Directory.CreateDirectory(dst);
        try
        {
            var src = @"Z:\master1\data1.dat";
            if (!File.Exists(src)) { Console.WriteLine("  QAR: Z:\\master1\\data1.dat not present"); return; }
            var localDat = Path.Combine(dst, "data1.dat");
            File.Copy(src, localDat, overwrite: true);

            var datfpk = FindDatFpk();
            if (datfpk is not null)
            {
                var refDir = Path.Combine(dst, "data1_ref");
                if (Directory.Exists(refDir)) Directory.Delete(refDir, true);
                Directory.CreateDirectory(refDir);
                var p = new ProcessStartInfo(datfpk, $"\"{localDat}\" \"{refDir}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };
                using (var proc = Process.Start(p)) { proc?.WaitForExit(120000); }
                var n = Directory.Exists(refDir) ? Directory.EnumerateFiles(refDir, "*", SearchOption.AllDirectories).Count() : 0;
                Console.WriteLine($"  QAR: harvested data1.dat + datfpk reference ({n} files) to {dst}");
            }
            else
            {
                Console.WriteLine($"  QAR: harvested data1.dat to {dst} (no datfpk — reference comparison skipped)");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  QAR harvest failed: {ex.Message}"); }
    }
}
