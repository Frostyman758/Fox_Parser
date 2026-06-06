using System.Diagnostics;
using System.IO.Compression;
using MgsvModBldr.Tools.Translation;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Translation.Tests;

/// <summary>
/// Lang (.lng/.lng2) gate — byte-exact parity with Atvaark's LangTool
/// (the reference), on BOTH directions:
///   (A) my XML byte-matches LangTool's XML (cached <c>&lt;name&gt;.lng.ref.xml</c>).
///   (B) my repack byte-matches LangTool's repack (cached <c>&lt;name&gt;.lng.ref.repack</c>).
/// Like subp, LangTool is lossy vs the game file (it always pads on
/// align and normalises the version), so the contract is reference-parity.
///
/// .lng files are not loose on Z:\ (they live in archives); supply
/// samples via the LNG_SAMPLES_ZIP env (a zip of .lng/.lng2 files) and
/// re-harvest. Reference oracle: LangToolRef (env LNGREF).
/// </summary>
public sealed class LangTests : IToolTests
{
    public string Name => "lng";

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Lang (xml + repack byte-match LangTool) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; set LNG_SAMPLES_ZIP and run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string lng)
    {
        var work = MakeTmp("lng_rt_");
        try
        {
            var srcDir = Path.GetDirectoryName(lng) ?? ".";
            var staged = Path.Combine(work, Path.GetFileName(lng));
            File.Copy(lng, staged, overwrite: true);

            var myXml = LangConverter.Unpack(staged);
            var myXmlBytes = File.ReadAllBytes(myXml);

            var refXml = Path.Combine(srcDir, Path.GetFileName(lng) + ".ref.xml");
            string noteA;
            if (File.Exists(refXml))
            {
                if (!FilesEqual(myXml, refXml)) return (false, "xml differs from LangTool reference");
                noteA = "xml matches LangTool";
            }
            else noteA = "no ref xml";

            var repacked = LangConverter.Pack(myXml);
            var refRepack = Path.Combine(srcDir, Path.GetFileName(lng) + ".ref.repack");
            string noteB;
            if (File.Exists(refRepack))
            {
                if (!FilesEqual(repacked, refRepack)) return (false, "repack differs from LangTool reference");
                noteB = "repack matches LangTool";
            }
            else
            {
                var reXml = LangConverter.Unpack(repacked); // overwrites myXml path
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
        var dir = Path.Combine(FixturesDir, "lng");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.lng", SearchOption.AllDirectories)
                        .Concat(Directory.EnumerateFiles(dir, "*.lng2", SearchOption.AllDirectories))
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    /// <summary>
    /// Harvest .lng/.lng2 from a zip given by LNG_SAMPLES_ZIP into
    /// hash-keyed buckets, caching the LangTool reference XML + repack.
    /// </summary>
    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "lng");
        Directory.CreateDirectory(dst);

        var zipPath = Environment.GetEnvironmentVariable("LNG_SAMPLES_ZIP");
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            Console.WriteLine("  Lng: no LNG_SAMPLES_ZIP set (.lng not loose on Z:\\)");
            return;
        }
        try
        {
            int copied = 0;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.EndsWith(".lng", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith(".lng2", StringComparison.OrdinalIgnoreCase)) continue;
                    var bucket = Path.Combine(dst, ShortHash(entry.FullName));
                    Directory.CreateDirectory(bucket);
                    var local = Path.Combine(bucket, name);
                    entry.ExtractToFile(local, overwrite: true);
                    GenerateReference(local);
                    copied++;
                }
            }
            Console.WriteLine($"  Lng: harvested {copied} sample(s) from {Path.GetFileName(zipPath)} to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Lng harvest failed: {ex.Message}"); }
    }

    private static void GenerateReference(string stagedLng)
    {
        var refDll = Environment.GetEnvironmentVariable("LNGREF");
        if (string.IsNullOrWhiteSpace(refDll) || !File.Exists(refDll))
            refDll = @"C:\rsearch\langref\bin\Release\net10.0\LangTool.dll";
        if (!File.Exists(refDll)) return;

        var dir = Path.GetDirectoryName(stagedLng)!;
        try
        {
            RunRef(refDll, stagedLng);                              // -> <name>.lng.xml
            var producedXml = stagedLng + ".xml";
            var refXml = Path.Combine(dir, Path.GetFileName(stagedLng) + ".ref.xml");
            if (!File.Exists(producedXml)) return;
            File.Move(producedXml, refXml, overwrite: true);

            var tmp = MakeTmp("lng_ref_");
            try
            {
                var tmpXml = Path.Combine(tmp, Path.GetFileName(stagedLng) + ".xml");
                File.Copy(refXml, tmpXml, overwrite: true);
                RunRef(refDll, tmpXml);                              // -> <name>.lng
                var producedLng = Path.Combine(tmp, Path.GetFileName(stagedLng));
                if (File.Exists(producedLng))
                    File.Move(producedLng, Path.Combine(dir, Path.GetFileName(stagedLng) + ".ref.repack"), overwrite: true);
            }
            finally { TryDelete(tmp); }
        }
        catch { /* best-effort */ }
    }

    private static void RunRef(string refDll, string arg)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{refDll}\" \"{arg}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(arg),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(30000);
    }
}
