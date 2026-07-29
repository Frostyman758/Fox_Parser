// Fpk tool regression gate
using System.Diagnostics;
using MgsvModBldr.Tools.Fpk;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Fpk.Tests;

public sealed class FpkTests : IToolTests
{
    public string Name => "fpk";

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- FPK/FPKD (extraction vs datfpk + pack round-trip) ---");
        var dir = Path.Combine(FixturesDir, "fpk");
        if (!Directory.Exists(dir)) { Console.WriteLine("  (no fixtures; run with --harvest)"); return (0, 0); }
        var samples = Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".fpk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fpkd", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => new FileInfo(f).Length).ToList();
        if (samples.Count == 0) { Console.WriteLine("  (no fixtures; run with --harvest)"); return (0, 0); }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string archive)
    {
        var work = MakeTmp("fpk_rt_");
        try
        {
            var staged = Path.Combine(work, Path.GetFileName(archive));
            File.Copy(archive, staged, overwrite: true);

            var manifestPath = FpkPacker.Unpack(staged);
            var stem = Path.GetFileNameWithoutExtension(archive);
            var ext  = Path.GetExtension(archive).TrimStart('.');
            var extractDir = Path.Combine(work, $"{stem}_{ext}");

            // (A) compare against datfpk reference if present.
            var refDir = Path.Combine(FixturesDir, "fpk", Path.GetFileName(archive) + "_ref");
            string note;
            if (Directory.Exists(refDir))
            {
                var (rm, rd, rmiss) = ByteCompareTrees(refDir, extractDir);
                if (rd > 0 || rmiss > 0)
                    return (false, $"vs datfpk: {rd} differ, {rmiss} missing (of {rm + rd + rmiss})");
                note = $"{rm} files byte-match datfpk";
            }
            else note = "no datfpk ref (round-trip only)";

            // (B) pack round-trip.
            var repacked = Path.Combine(work, "repacked" + Path.GetExtension(archive));
            FpkPacker.Pack(manifestPath, repacked);
            var reManifest = FpkPacker.Unpack(repacked);
            var reDir = Path.Combine(work, $"repacked_{ext}");
            var (m2, d2, miss2) = ByteCompareTrees(extractDir, reDir);
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

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "fpk");
        Directory.CreateDirectory(dst);
        var datfpk = FindDatFpk();
        if (datfpk is null) { Console.WriteLine("  FPK harvest skipped: datfpk path not set."); return; }

        try
        {
            var rng = new Random();
            var fpks  = EnumerateSafe(@"Z:\tpp\release\pack", "*.fpk").Where(f => new FileInfo(f).Length is > 2000 and < (20L << 20)).OrderBy(_ => rng.Next()).Take(3);
            var fpkds = EnumerateSafe(@"Z:\tpp\release\pack", "*.fpkd").Where(f => new FileInfo(f).Length is > 5000 and < (20L << 20)).OrderBy(_ => rng.Next()).Take(3);

            // Plus datfpk's own testdata archives — known to contain
            // ENCRYPTED entries (title.fpkd, EQP_WP_SP_SLD_BASE.fpkd).
            var testdata = new[]
            {
                @"C:\Users\Blue\Downloads\datfpk-master\datfpk-master\fpk\testdata\title.fpkd",
                @"C:\Users\Blue\Downloads\datfpk-master\datfpk-master\fpk\testdata\EQP_WP_SP_SLD_BASE.fpkd",
            }.Where(File.Exists);

            var picks = fpks.Concat(fpkds).Concat(testdata).Distinct().Take(8).ToList();
            int n = 0;
            foreach (var src in picks)
            {
                var local = Path.Combine(dst, Path.GetFileName(src));
                File.Copy(src, local, overwrite: true);

                var stem = Path.GetFileNameWithoutExtension(src);
                var ext  = Path.GetExtension(src).TrimStart('.');
                var datfpkOut = Path.Combine(dst, $"{stem}_{ext}");
                if (Directory.Exists(datfpkOut)) Directory.Delete(datfpkOut, true);

                var p = new ProcessStartInfo(datfpk, $"\"{local}\"")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using (var proc = Process.Start(p)) { proc?.WaitForExit(60000); }

                var refDir = local + "_ref";
                if (Directory.Exists(refDir)) Directory.Delete(refDir, true);
                if (Directory.Exists(datfpkOut)) Directory.Move(datfpkOut, refDir);
                n++;
            }
            Console.WriteLine($"  FPK: harvested {n} archive(s) + datfpk references to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  FPK harvest failed: {ex.Message}"); }
    }
}
