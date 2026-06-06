using System.Diagnostics;
using MgsvModBldr.Tools.Translation;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Translation.Tests;

/// <summary>
/// Subp (.subp subtitle pack) gate — byte-exact parity with Atvaark's
/// SubpTool (the reference tool), on BOTH directions:
///   (A) my XML byte-matches SubpTool's XML (cached <c>&lt;name&gt;.subp.ref.xml</c>).
///   (B) my repack byte-matches SubpTool's repack (cached <c>&lt;name&gt;.subp.ref.repack</c>).
///
/// NOTE: the contract is parity with the REFERENCE TOOL, not with the
/// original game file. SubpTool is lossy vs the game file — it
/// normalises the file magic (0x0413 → 0x0113) on repack — so a
/// round-trip against the original would (correctly) differ. Matching
/// SubpTool's lossy behaviour is the porting contract (same as Fox's
/// reconstructed literal table). Default codec is ISO-8859-1, which is
/// binary-safe, so the gate holds for every language.
/// </summary>
public sealed class SubpTests : IToolTests
{
    public string Name => "subp";
    private const int MaxSubpSamples = 12;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Subp (xml + repack byte-match SubpTool) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string subp)
    {
        var work = MakeTmp("subp_rt_");
        try
        {
            var srcDir = Path.GetDirectoryName(subp) ?? ".";
            var staged = Path.Combine(work, Path.GetFileName(subp));
            File.Copy(subp, staged, overwrite: true);

            // Decompile: <staged>.subp -> <staged>.subp.xml
            var myXml = SubpConverter.Unpack(staged);
            var myXmlBytes = File.ReadAllBytes(myXml);

            // (A) my XML must byte-match the SubpTool reference XML.
            var refXml = Path.Combine(srcDir, Path.GetFileName(subp) + ".ref.xml");
            string noteA;
            if (File.Exists(refXml))
            {
                if (!FilesEqual(myXml, refXml))
                    return (false, "xml differs from SubpTool reference");
                noteA = "xml matches SubpTool";
            }
            else noteA = "no ref xml";

            // (B) my repack must byte-match the SubpTool reference repack.
            var repacked  = SubpConverter.Pack(myXml); // writes <work>/<name>.subp
            var refRepack = Path.Combine(srcDir, Path.GetFileName(subp) + ".ref.repack");
            string noteB;
            if (File.Exists(refRepack))
            {
                if (!FilesEqual(repacked, refRepack))
                    return (false, "repack differs from SubpTool reference");
                noteB = "repack matches SubpTool";
            }
            else
            {
                // Fallback (no reference cached): round-trip XML stability.
                var reXml = SubpConverter.Unpack(repacked); // overwrites myXml path
                if (!File.ReadAllBytes(reXml).AsSpan().SequenceEqual(myXmlBytes))
                    return (false, "round-trip xml not stable");
                noteB = "round-trip xml-stable (no ref repack)";
            }

            return (true, $"{noteA}; {noteB}");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "subp");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.subp", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    /// <summary>
    /// Subp files live LOOSE on Z:\ under
    /// <c>Z:\tpp\release\ui\Subtitles\subp\</c>. Picks a spread across
    /// language folders (different encodings) and sizes, each into a
    /// hash-keyed bucket (names like common_str.subp collide across
    /// languages). For each, caches the SubpTool reference XML + repack.
    /// </summary>
    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "subp");
        Directory.CreateDirectory(dst);
        try
        {
            var pool = EnumerateSafe(@"Z:\tpp\release\ui\Subtitles\subp", "*.subp").ToList();
            if (pool.Count == 0) { Console.WriteLine("  Subp: no .subp under Z:\\tpp\\release\\ui\\Subtitles\\subp"); return; }

            var rng = new Random();
            // Up to 2 per language folder for codec spread, capped.
            var picks = pool.OrderBy(_ => rng.Next())
                            .GroupBy(f => Path.GetDirectoryName(f))
                            .SelectMany(g => g.Take(2))
                            .Take(MaxSubpSamples)
                            .ToList();

            int copied = 0;
            foreach (var src in picks)
            {
                var bucket = Path.Combine(dst, ShortHash(src));
                Directory.CreateDirectory(bucket);
                var local = Path.Combine(bucket, Path.GetFileName(src));
                File.Copy(src, local, overwrite: true);
                GenerateReference(local);
                copied++;
            }
            Console.WriteLine($"  Subp: harvested {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Subp harvest failed: {ex.Message}"); }
    }

    /// <summary>
    /// Run SubpToolRef (built from Atvaark's SubpTool source) to cache
    /// both reference artifacts next to the sample:
    ///   <c>&lt;name&gt;.subp.ref.xml</c>    (unpack of the original .subp)
    ///   <c>&lt;name&gt;.subp.ref.repack</c> (repack of that XML — SubpTool's
    ///                                        own round-trip output)
    /// Locate the oracle via the SUBPREF env var (path to SubpTool.dll)
    /// or the default build path. Best-effort: if absent, the gate falls
    /// back to round-trip XML stability.
    /// </summary>
    private static void GenerateReference(string stagedSubp)
    {
        var refDll = Environment.GetEnvironmentVariable("SUBPREF");
        if (string.IsNullOrWhiteSpace(refDll) || !File.Exists(refDll))
            refDll = @"C:\rsearch\subpref\bin\Release\net10.0\SubpTool.dll";
        if (!File.Exists(refDll)) return;

        var dir  = Path.GetDirectoryName(stagedSubp)!;
        var stem = Path.GetFileNameWithoutExtension(stagedSubp);
        try
        {
            // 1) Reference unpack: SubpTool writes <stem>.xml next to input.
            RunRef(refDll, stagedSubp);
            var producedXml = Path.Combine(dir, stem + ".xml");
            var refXml = Path.Combine(dir, Path.GetFileName(stagedSubp) + ".ref.xml");
            if (!File.Exists(producedXml)) return;
            File.Move(producedXml, refXml, overwrite: true);

            // 2) Reference repack: pack the ref XML in a temp dir (so we
            //    don't clobber the original .subp sample), cache the result.
            var tmp = MakeTmp("subp_ref_");
            try
            {
                var tmpXml = Path.Combine(tmp, stem + ".xml");
                File.Copy(refXml, tmpXml, overwrite: true);
                RunRef(refDll, tmpXml);
                var producedSubp = Path.Combine(tmp, stem + ".subp");
                if (File.Exists(producedSubp))
                    File.Move(producedSubp, Path.Combine(dir, Path.GetFileName(stagedSubp) + ".ref.repack"), overwrite: true);
            }
            finally { TryDelete(tmp); }
        }
        catch { /* reference is best-effort */ }
    }

    private static void RunRef(string refDll, string arg)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{refDll}\" \"{arg}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(30000);
    }
}
