using System.Diagnostics;
using MgsvModBldr.Tools.Fox;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Fox.Tests;

/// <summary>
/// Fox gate: Atvaark-equivalent (lossy reference). Checks recompile
/// SIZE matches the original and the XML is STABLE across a second
/// round-trip (Atvaark reconstructs the string-literal table, so a
/// byte-exact round-trip is impossible — matching his behaviour is the
/// porting contract).
/// </summary>
public sealed class FoxTests : IToolTests
{
    public string Name => "fox";

    // Target N samples per supported extension so every FoxFile
    // codepath is exercised. Some extensions are rare (e.g. .vfxlf).
    private const int MaxFoxSamplesPerExt = 2;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Fox (Atvaark-equivalent: size + XML-stable) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    /// <summary>
    /// Fox verification: decompile → recompile → decompile → check
    /// (a) recompile output is the same SIZE as the original and
    /// (b) the XML is STABLE across the second round-trip.
    /// </summary>
    private static (bool ok, string note) TryRoundtrip(string fox)
    {
        var work = MakeTmp("fox_rt_");
        try
        {
            var xml1     = Path.Combine(work, Path.GetFileName(fox) + ".xml");
            var bin1     = Path.Combine(work, Path.GetFileName(fox));
            var xml2     = Path.Combine(work, Path.GetFileName(fox) + ".second.xml");

            FoxPacker.Decompile(fox, xml1);
            FoxPacker.Compile(xml1, bin1);

            var origSize = new FileInfo(fox).Length;
            var binSize  = new FileInfo(bin1).Length;
            if (origSize != binSize)
                return (false, $"size mismatch (orig={origSize}, recompiled={binSize})");

            FoxPacker.Decompile(bin1, xml2);
            if (!XmlEqualIgnoringTimestamp(xml1, xml2))
            {
                var saved = Path.Combine(Path.GetTempPath(), "fox_unstable_" + Path.GetFileNameWithoutExtension(fox));
                Directory.CreateDirectory(saved);
                File.Copy(xml1, Path.Combine(saved, "xml1.xml"), overwrite: true);
                File.Copy(xml2, Path.Combine(saved, "xml2.xml"), overwrite: true);
                return (false, $"XML not stable across second round-trip — saved to {saved}");
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "fox");
        if (!Directory.Exists(dir)) return new();
        // Pick up EVERY supported extension, not just .fox2 — they all
        // flow through the same FoxFile reader but each has distinct
        // schemas and only sample coverage tells us the port handles them.
        return FoxPacker.DecompilableExtensions
            .SelectMany(ext => Directory.EnumerateFiles(dir, "*" + ext, SearchOption.AllDirectories))
            .OrderBy(f => Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => new FileInfo(f).Length)
            .ToList();
    }

    /// <summary>
    /// Whole-XML comparison with one known noise field masked out: the
    /// <c>originalVersion</c> attribute on the <c>&lt;fox&gt;</c> root,
    /// which Atvaark's FoxFile constructor initialises to
    /// <see cref="DateTime.Now"/> rather than reading from the binary.
    /// Any other difference still fails.
    /// </summary>
    private static bool XmlEqualIgnoringTimestamp(string aPath, string bPath)
    {
        var a = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(aPath), " originalVersion=\"[^\"]*\"", "");
        var b = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(bPath), " originalVersion=\"[^\"]*\"", "");
        return a == b;
    }

    /// <summary>
    /// Fox samples live INSIDE FPK/FPKD archives, so we need datfpk to
    /// extract them. Walks a wide slice of Z:\tpp\release\pack and
    /// harvests up to MaxFoxSamplesPerExt samples of every supported
    /// extension.
    /// </summary>
    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "fox");
        Directory.CreateDirectory(dst);

        var datfpk = FindDatFpk();
        if (datfpk is null)
        {
            Console.WriteLine("  Fox harvest skipped: datfpk path not set. Configure it in modbldr Settings or set the DATFPK env var.");
            return;
        }

        try
        {
            var pool = EnumerateSafe(@"Z:\tpp\release\pack", "*.fpkd")
                       .Concat(EnumerateSafe(@"Z:\tpp\release\pack", "*.fpk"))
                       .Where(f => new FileInfo(f).Length > 1000)
                       .ToList();
            if (pool.Count == 0) { Console.WriteLine("  Fox: no FPK/FPKD files found under Z:\\tpp\\release\\pack"); return; }

            var rng = new Random();
            var picks = pool.OrderBy(_ => rng.Next()).Take(400).ToList();

            var quota = FoxPacker.DecompilableExtensions
                .ToDictionary(e => e, _ => MaxFoxSamplesPerExt, StringComparer.OrdinalIgnoreCase);
            var tmp = MakeTmp("fox_harvest_");
            int totalCopied = 0;

            try
            {
                foreach (var src in picks)
                {
                    if (quota.Values.All(v => v <= 0)) break;

                    var cp = Path.Combine(tmp, Path.GetFileName(src));
                    File.Copy(src, cp, overwrite: true);
                    var p = new ProcessStartInfo(datfpk, $"\"{cp}\" \"{tmp}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    };
                    using (var proc = Process.Start(p)) { proc?.WaitForExit(15000); }

                    foreach (var f in EnumerateSafe(tmp, "*"))
                    {
                        var ext = Path.GetExtension(f);
                        if (!quota.TryGetValue(ext, out var remaining) || remaining <= 0) continue;

                        var subdir = Path.Combine(dst, ext.TrimStart('.'));
                        Directory.CreateDirectory(subdir);
                        var into = Path.Combine(subdir, Path.GetFileName(f));
                        if (File.Exists(into))
                        {
                            var stem = Path.GetFileNameWithoutExtension(f);
                            var h    = ShortHash(f);
                            into = Path.Combine(subdir, $"{stem}_{h}{ext}");
                        }
                        File.Copy(f, into, overwrite: true);
                        quota[ext] = remaining - 1;
                        totalCopied++;
                    }
                }
            }
            finally { TryDelete(tmp); }

            var got      = quota.Where(kv => kv.Value < MaxFoxSamplesPerExt)
                                 .Select(kv => $"{kv.Key}({MaxFoxSamplesPerExt - kv.Value})");
            var missing  = quota.Where(kv => kv.Value == MaxFoxSamplesPerExt)
                                 .Select(kv => kv.Key);
            Console.WriteLine($"  Fox: harvested {totalCopied} sample(s) to {dst}");
            Console.WriteLine($"        covered: {string.Join(" ", got)}");
            if (missing.Any())
                Console.WriteLine($"        no samples found: {string.Join(" ", missing)}");
        }
        catch (Exception ex) { Console.WriteLine($"  Fox harvest failed: {ex.Message}"); }
    }
}
