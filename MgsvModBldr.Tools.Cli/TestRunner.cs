using System.Diagnostics;
using System.Security.Cryptography;
using MgsvModBldr.Core;
using MgsvModBldr.Tools.Fox;
using MgsvModBldr.Tools.Fsop;
using MgsvModBldr.Tools.Ftex;
using MgsvModBldr.Tools.Fpk;
using MgsvModBldr.Tools.Pftxs;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Tests;

/// <summary>
/// Automated regression runner for every ported tool. Each tool
/// declares a set of "real-file" tests that are evaluated against
/// fixtures cached on disk. The runner reports pass/fail per file,
/// counts per tool, and a summary line at the end.
///
/// <para>Test gate per tool is owned by the per-tool runner method —
/// FSOP requires byte-exact round-trip (its reference is
/// deterministic), Fox only checks recompile size + XML stability
/// (Atvaark's reference is lossy on the literal table, and matching
/// his lossy behaviour is the porting contract — see
/// MEMORY.md project_modbldr_toolkit).</para>
///
/// <para>Fixtures live under <see cref="FixturesDir"/>. The harvest
/// step walks <c>Z:\</c> for sample files, unpacking FPK/FPKD
/// archives via the user-configured datfpk path (read from
/// <see cref="BuildStateIo.DefaultPath"/> — same builder.xml the
/// modbldr GUI uses). Subsequent test runs read only from
/// fixtures, so the tests work without datfpk after the first
/// harvest.</para>
/// </summary>
public static class TestRunner
{
    private const string FixturesDir = @"C:\rsearch\test_fixtures";
    // Target N samples per supported extension so every FoxFile
    // codepath is exercised. Some extensions are rare (e.g.
    // .vfxlf) — best-effort.
    private const int    MaxFoxSamplesPerExt = 2;

    /// <summary>
    /// Run regression tests, optionally scoped to one tool by name
    /// (fsop|fox|ftex). With <paramref name="harvest"/> true, refresh
    /// fixtures first — also scoped to <paramref name="toolFilter"/>
    /// when set so users can iterate on a single tool without
    /// re-walking Z:\ for the others.
    /// </summary>
    public static int Run(bool harvest, string? toolFilter = null)
    {
        bool All(string n) => toolFilter is null || string.Equals(toolFilter, n, StringComparison.OrdinalIgnoreCase);

        if (harvest)
        {
            Console.WriteLine($"Harvesting fixtures{(toolFilter is null ? "" : $" ({toolFilter} only)")}...");
            if (All("fsop")) HarvestFsop();
            if (All("fox"))  HarvestFox();
            if (All("ftex")) HarvestFtex();
            if (All("qar"))  HarvestQar();
            if (All("fpk"))  HarvestFpk();
            if (All("pftxs")) HarvestPftxs();
            Console.WriteLine();
        }

        var t0 = Stopwatch.StartNew();
        int totalPass = 0, totalFail = 0;
        int p, f;

        if (All("fsop")) { (p, f) = RunFsop(); totalPass += p; totalFail += f; }
        if (All("fox"))  { (p, f) = RunFox();  totalPass += p; totalFail += f; }
        if (All("ftex")) { (p, f) = RunFtex(); totalPass += p; totalFail += f; }
        if (All("qar"))  { (p, f) = RunQar();  totalPass += p; totalFail += f; }
        if (All("fpk"))  { (p, f) = RunFpk();  totalPass += p; totalFail += f; }
        if (All("pftxs")){ (p, f) = RunPftxs(); totalPass += p; totalFail += f; }

        t0.Stop();
        Console.WriteLine();
        Console.WriteLine($"=== Summary: {totalPass} passed, {totalFail} failed ({t0.ElapsedMilliseconds} ms total) ===");
        return totalFail == 0 ? 0 : 1;
    }

    // ─── FSOP — byte-exact gate (reference is deterministic) ────────────

    private static (int pass, int fail) RunFsop()
    {
        Console.WriteLine("--- FSOP (byte-exact gate) ---");
        var samples = DiscoverFsopSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, s =>
        {
            var ok = RoundtripFsopExact(s);
            return (ok, "");
        });
    }

    private static bool RoundtripFsopExact(string fsop)
    {
        var work = MakeTmp("fsop_rt_");
        try
        {
            var unpackDir = Path.Combine(work, "unpacked");
            var repacked  = Path.Combine(work, Path.GetFileName(fsop));
            FsopPacker.Unpack(fsop, unpackDir);
            FsopPacker.Pack(unpackDir, repacked);
            return Sha256(File.ReadAllBytes(fsop)) == Sha256(File.ReadAllBytes(repacked));
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverFsopSamples()
    {
        var hits = new List<string>();
        // Real-file path: any .fsop directly accessible from Z:\.
        // FSOPs aren't usually packed inside FPKs so we can hit Z:\
        // straight — no fixture harvest needed for this tool.
        TryAdd(hits, @"Z:\shaders\dx11\FxShaders_dx11.fsop");
        // Plus anything previously harvested.
        var dir = Path.Combine(FixturesDir, "fsop");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.fsop", SearchOption.AllDirectories))
                hits.Add(f);
        return hits;
    }

    // ─── Fox — Atvaark-equivalent gate (lossy, size + stability only) ───

    private static (int pass, int fail) RunFox()
    {
        Console.WriteLine();
        Console.WriteLine("--- Fox (Atvaark-equivalent: size + XML-stable) ---");
        var samples = DiscoverFoxSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryFoxRoundtrip);
    }

    /// <summary>
    /// Fox verification: decompile → recompile → decompile → check
    /// (a) recompile output is the same SIZE as the original (proves
    /// we wrote the right bytes count, not garbage) and (b) the XML
    /// is STABLE across the second round-trip (proves the decompile
    /// is deterministic and the compile produces an equivalent binary).
    /// </summary>
    private static (bool ok, string note) TryFoxRoundtrip(string fox)
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
        finally { /* leave work dir on failure for diff */ if (true) TryDelete(work); }
    }

    private static List<string> DiscoverFoxSamples()
    {
        var dir = Path.Combine(FixturesDir, "fox");
        if (!Directory.Exists(dir)) return new();
        // Pick up EVERY supported extension, not just .fox2 — they
        // all flow through the same FoxFile reader but each has
        // distinct schemas and only sample coverage tells us
        // whether the port handles them.
        return FoxPacker.DecompilableExtensions
            .SelectMany(ext => Directory.EnumerateFiles(dir, "*" + ext, SearchOption.AllDirectories))
            // Group by extension first so the report is grouped, but
            // within an extension order by size for predictable runs.
            .OrderBy(f => Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => new FileInfo(f).Length)
            .ToList();
    }

    // ─── Harvest ────────────────────────────────────────────────────────

    private static void HarvestAll()
    {
        HarvestFsop();
        HarvestFox();
        HarvestFtex();
    }

    /// <summary>
    /// FSOPs sit loose on disk under <c>Z:\shaders\**</c> — copy a
    /// handful into the fixtures dir so the test runner has work to
    /// do even if Z:\ is detached.
    /// </summary>
    private static void HarvestFsop()
    {
        var dst = Path.Combine(FixturesDir, "fsop");
        Directory.CreateDirectory(dst);
        try
        {
            int copied = 0;
            foreach (var f in EnumerateSafe(@"Z:\shaders", "*.fsop"))
            {
                if (copied >= 3) break;
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
                copied++;
            }
            Console.WriteLine($"  FSOP: copied {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  FSOP harvest failed: {ex.Message}"); }
    }

    /// <summary>
    /// Fox samples live INSIDE FPK/FPKD archives, so we need datfpk
    /// to extract them. Walks a wide slice of Z:\tpp\release\pack —
    /// different content trees host different extensions (mission2
    /// has .fox2/.evf, chara has .clo/.sim/.parts/.ph, level_asset
    /// has .lad, weapon has .veh, etc) — and harvests up to
    /// MaxFoxSamplesPerExt samples of every supported extension.
    /// Stops walking once every extension has its quota OR the pool
    /// is exhausted.
    /// </summary>
    private static void HarvestFox()
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
            // FPK = asset packs, FPKD = definition packs. Both can
            // host any of the 16 decompilable extensions depending
            // on what tree they came from.
            var pool = EnumerateSafe(@"Z:\tpp\release\pack", "*.fpkd")
                       .Concat(EnumerateSafe(@"Z:\tpp\release\pack", "*.fpk"))
                       .Where(f => new FileInfo(f).Length > 1000)
                       .ToList();
            if (pool.Count == 0) { Console.WriteLine("  Fox: no FPK/FPKD files found under Z:\\tpp\\release\\pack"); return; }

            // Shuffle so we don't deterministically miss the same
            // archives every run, but cap the walk so harvest stays
            // under a few minutes even on cold Z:\.
            var rng = new Random();
            var picks = pool.OrderBy(_ => rng.Next()).Take(400).ToList();

            // Per-extension quota tracking. Stop walking archives once
            // every extension is satisfied.
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

                        // Collision-safe filename + group by extension
                        // in subdirs so it's obvious at a glance what's
                        // there.
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

            // Coverage report so it's obvious which extensions had
            // no samples available in the walked archives.
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

    private static string? FindDatFpk()
    {
        // Honour an explicit env override first (CI / quick swap).
        var env = Environment.GetEnvironmentVariable("DATFPK");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        // Fall back to whatever the builder GUI is configured with.
        try
        {
            var state = new BuildState();
            BuildStateIo.Load(state, BuildStateIo.DefaultPath());
            if (!string.IsNullOrWhiteSpace(state.DatFpk) && File.Exists(state.DatFpk)) return state.DatFpk;
        }
        catch { /* ignore */ }
        return null;
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static void TryAdd(List<string> list, string path)
    {
        if (File.Exists(path)) list.Add(path);
    }

    private static IEnumerable<string> EnumerateSafe(string root, string pattern)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> seq;
        try { seq = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in seq) yield return f;
    }

    private static string MakeTmp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* leave it */ }
    }

    private static bool FilesEqual(string a, string b)
    {
        var fa = File.ReadAllBytes(a);
        var fb = File.ReadAllBytes(b);
        if (fa.Length != fb.Length) return false;
        for (int i = 0; i < fa.Length; i++) if (fa[i] != fb[i]) return false;
        return true;
    }

    /// <summary>
    /// Whole-XML comparison for Fox stability gate, with one known
    /// noise field masked out: the <c>originalVersion</c> attribute on
    /// the <c>&lt;fox&gt;</c> root, which Atvaark's FoxFile constructor
    /// initialises to <see cref="DateTime.Now"/> rather than reading
    /// it from the binary. Two decompiles a second apart therefore
    /// differ by this attribute alone — that's wire-compat behaviour,
    /// not a semantic divergence. Any other difference still fails.
    /// </summary>
    private static bool XmlEqualIgnoringTimestamp(string aPath, string bPath)
    {
        var a = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(aPath), " originalVersion=\"[^\"]*\"", "");
        var b = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(bPath), " originalVersion=\"[^\"]*\"", "");
        return a == b;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string ShortHash(string s) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..8];

    private static string Size(string path)
    {
        long n = new FileInfo(path).Length;
        if (n >= 1 << 20) return $"{n / (double)(1 << 20):F1} MB";
        if (n >= 1 << 10) return $"{n / (double)(1 << 10):F1} KB";
        return $"{n} B";
    }

    // ─── Ftex ───────────────────────────────────────────────────────────
    // Gate is NOT byte-exact on the .ftex side: SharpZipLib (Atvaark's
    // dep) and System.IO.Compression.ZLibStream (ours) both produce
    // valid deflate streams but with different byte layouts. Game
    // accepts both. The meaningful gate is "decompressed pixel bytes
    // round-trip equal" — verified by re-unpacking the recompressed
    // ftex and comparing the resulting .dds against the first .dds.

    private static (int pass, int fail) RunFtex()
    {
        Console.WriteLine();
        Console.WriteLine("--- Ftex (dds vs FtexTool reference + round-trip) ---");
        var samples = DiscoverFtexSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryFtexRoundtrip);
    }

    /// <summary>
    /// Per-file gate runner. Independent test cases (each round-trip
    /// has its own scratch dir, no shared state) so Parallel.ForEach
    /// is safe. We collect results into a per-sample array indexed by
    /// position so the printed output is in stable input order, not
    /// completion order — keeps logs diff-friendly across runs.
    /// </summary>
    private static (int pass, int fail) RunParallel(
        List<string> samples,
        Func<string, (bool ok, string note)> gate)
    {
        var results = new (bool ok, string note)[samples.Count];
        Parallel.For(0, samples.Count, i =>
        {
            results[i] = gate(samples[i]);
        });

        int pass = 0, fail = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var (ok, note) = results[i];
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {Path.GetFileName(samples[i])} ({Size(samples[i])}) {note}");
            if (ok) pass++; else fail++;
        }
        return (pass, fail);
    }

    private static (bool ok, string note) TryFtexRoundtrip(string ftex)
    {
        var work = MakeTmp("ftex_rt_");
        try
        {
            // Stage: copy the .ftex + every sibling .ftexs into work.
            var srcDir = Path.GetDirectoryName(ftex) ?? ".";
            var stem   = Path.GetFileNameWithoutExtension(ftex);
            foreach (var sibling in Directory.EnumerateFiles(srcDir, stem + ".*"))
                File.Copy(sibling, Path.Combine(work, Path.GetFileName(sibling)), overwrite: true);

            var staged = Path.Combine(work, Path.GetFileName(ftex));
            var dds1   = FtexPacker.Unpack(staged);

            // (A) ftex -> dds must byte-match the FtexTool reference DDS
            // cached next to the fixture (<stem>.ref.dds). This is the
            // gold-standard gate — a pure round-trip would NOT catch a
            // wrong-but-stable DDS (e.g. scrambled mipmaps).
            var refDds = Path.Combine(srcDir, stem + ".ref.dds");
            string note;
            if (File.Exists(refDds))
            {
                if (!FilesEqual(dds1, refDds))
                    return (false, "dds differs from FtexTool reference");
                note = "dds byte-matches FtexTool";
            }
            else note = "no FtexTool ref (round-trip only)";

            // (B) round-trip stability: dds -> ftex -> dds reproduces dds1.
            var ftex2 = FtexPacker.Pack(dds1);
            var dds2  = FtexPacker.Unpack(ftex2);
            if (!FilesEqual(dds1, dds2))
                return (false, "dds not stable across round-trip");

            return (true, note + "; round-trip ok");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverFtexSamples()
    {
        var dir = Path.Combine(FixturesDir, "ftex");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.ftex", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    /// <summary>
    /// Ftex samples (.ftex + .N.ftexs sidecars) live LOOSE under the
    /// asset trees on Z:\ (e.g. <c>Z:\tpp\release\weapon\.../Pictures/#windx11/</c>),
    /// not inside FPKs. Walks those trees directly — no datfpk needed.
    /// Picks samples with varying sidecar counts (1, 2, 3+ .ftexs files)
    /// so we exercise different mipmap-chain shapes.
    /// </summary>
    private const int MaxFtexSamples = 8;

    private static void HarvestFtex()
    {
        var dst = Path.Combine(FixturesDir, "ftex");
        Directory.CreateDirectory(dst);

        try
        {
            // #windx11 dirs hold the dx11-cooked .ftex+.ftexs sets.
            // Walking Z:\tpp\release for any .ftex catches them across
            // weapon/chara/buddy/mecha/environ/etc.
            var pool = EnumerateSafe(@"Z:\tpp\release", "*.ftex").Take(2000).ToList();
            if (pool.Count == 0)
            {
                Console.WriteLine("  Ftex: no .ftex files found under Z:\\tpp\\release");
                return;
            }

            // Group by sibling-count so the picks cover different
            // mipmap-chain shapes (some textures have one .ftexs
            // sidecar, some have six). Quota per bucket so we don't
            // just grab eight 1-sidecar textures.
            var rng = new Random();
            var byShape = pool.OrderBy(_ => rng.Next())
                              .GroupBy(f => CountSiblings(f))
                              .OrderBy(g => g.Key);

            int copied = 0;
            int perBucket = Math.Max(1, MaxFtexSamples / 3);
            foreach (var group in byShape)
            {
                int taken = 0;
                foreach (var ftex in group)
                {
                    if (copied >= MaxFtexSamples) break;
                    if (taken >= perBucket) break;
                    var stem   = Path.GetFileNameWithoutExtension(ftex);
                    var srcDir = Path.GetDirectoryName(ftex) ?? ".";

                    // Dedicated subdir keyed by short hash — names
                    // like tr00_leve0_def_c00_bsm collide across
                    // weapon variants.
                    var bucket = Path.Combine(dst, ShortHash(ftex));
                    Directory.CreateDirectory(bucket);
                    foreach (var sibling in Directory.EnumerateFiles(srcDir, stem + ".*"))
                        File.Copy(sibling, Path.Combine(bucket, Path.GetFileName(sibling)), overwrite: true);

                    // Produce the FtexTool reference DDS for byte-compare
                    // gating. Needs FtexToolRef (built from Atvaark's
                    // source) — path via FTEXREF env or the default build
                    // location. If absent, gate falls back to round-trip.
                    GenerateFtexReference(Path.Combine(bucket, stem + ".ftex"), stem);

                    copied++;
                    taken++;
                }
                if (copied >= MaxFtexSamples) break;
            }

            Console.WriteLine($"  Ftex: harvested {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Ftex harvest failed: {ex.Message}"); }
    }

    private static int CountSiblings(string ftex)
    {
        var stem = Path.GetFileNameWithoutExtension(ftex);
        var dir  = Path.GetDirectoryName(ftex) ?? ".";
        try { return Directory.EnumerateFiles(dir, stem + ".*.ftexs").Count(); }
        catch { return 0; }
    }

    /// <summary>
    /// Run FtexToolRef (Atvaark's original) on a staged .ftex to
    /// produce <c>&lt;stem&gt;.ref.dds</c> — the byte-compare reference
    /// for the Ftex gate. FtexToolRef is a self-contained build of
    /// Atvaark's FtexTool with SharpZipLib; locate it via the FTEXREF
    /// env var (path to FtexToolRef.dll) or the default build path.
    /// </summary>
    private static void GenerateFtexReference(string stagedFtex, string stem)
    {
        var refDll = Environment.GetEnvironmentVariable("FTEXREF");
        if (string.IsNullOrWhiteSpace(refDll) || !File.Exists(refDll))
            refDll = @"C:\rsearch\ftexref\bin\Release\net8.0\FtexToolRef.dll";
        if (!File.Exists(refDll)) return;

        var dir = Path.GetDirectoryName(stagedFtex)!;
        try
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{refDll}\" \"{stagedFtex}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using (var proc = Process.Start(psi)) { proc?.WaitForExit(30000); }
            var produced = Path.Combine(dir, stem + ".dds");
            var refDds   = Path.Combine(dir, stem + ".ref.dds");
            if (File.Exists(produced)) File.Move(produced, refDds, overwrite: true);
        }
        catch { /* reference is best-effort; gate falls back to round-trip */ }
    }

    // ─── QAR (.dat / .qar) ──────────────────────────────────────────────
    // Two real gates, no hand-waving:
    //   (A) EXTRACTION CORRECTNESS — every extracted file (name AND
    //       bytes) must match cap's datfpk reference extraction at
    //       <fixtures>/qar/<stem>_ref/. This is the gold standard:
    //       if even one byte differs, we'd corrupt a game file.
    //   (B) PACK ROUND-TRIP — repack the extracted tree, re-extract
    //       the repacked archive, and require the re-extraction to be
    //       byte-identical to the first extraction. Proves the pack
    //       path preserves every entry's data losslessly.

    private static (int pass, int fail) RunQar()
    {
        Console.WriteLine();
        Console.WriteLine("--- QAR (.dat: extraction vs datfpk + pack round-trip) ---");
        var samples = DiscoverQarSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryQarRoundtrip);
    }

    private static (bool ok, string note) TryQarRoundtrip(string dat)
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
    private static string? ReferenceDirFor(string dat)
    {
        var stem = Path.GetFileNameWithoutExtension(dat);
        // Convention: <fixtures>/qar/<stem>_ref/  (a datfpk extraction).
        var cand = Path.Combine(FixturesDir, "qar", stem + "_ref");
        return Directory.Exists(cand) ? cand : null;
    }

    // ─── FPK / FPKD ─────────────────────────────────────────────────────
    // Same gates as QAR: (A) extraction byte-matches datfpk reference,
    // (B) pack round-trip (repack -> re-extract -> byte-identical).
    // Fixtures: <fixtures>/fpk/<name> (the archive) +
    // <fixtures>/fpk/<name>_ref/ (datfpk extraction).

    private static (int pass, int fail) RunFpk()
    {
        Console.WriteLine();
        Console.WriteLine("--- FPK/FPKD (extraction vs datfpk + pack round-trip) ---");
        var dir = Path.Combine(FixturesDir, "fpk");
        if (!Directory.Exists(dir)) { Console.WriteLine("  (no fixtures; run with --harvest)"); return (0, 0); }
        var samples = Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".fpk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fpkd", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => new FileInfo(f).Length).ToList();
        if (samples.Count == 0) { Console.WriteLine("  (no fixtures; run with --harvest)"); return (0, 0); }
        return RunParallel(samples, TryFpkRoundtrip);
    }

    private static (bool ok, string note) TryFpkRoundtrip(string archive)
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

    // ─── PFTXS ──────────────────────────────────────────────────────────
    // Gates: (A) extraction byte-matches GzsTool reference,
    // (B) pack round-trip (repack -> re-extract -> byte-identical).

    private static (int pass, int fail) RunPftxs()
    {
        Console.WriteLine();
        Console.WriteLine("--- PFTXS (extraction vs GzsTool + pack round-trip) ---");
        var dir = Path.Combine(FixturesDir, "pftxs");
        if (!Directory.Exists(dir)) { Console.WriteLine("  (no fixtures; run with --harvest)"); return (0, 0); }
        var samples = Directory.EnumerateFiles(dir, "*.pftxs").OrderBy(f => new FileInfo(f).Length).ToList();
        if (samples.Count == 0) { Console.WriteLine("  (no fixtures; run with --harvest)"); return (0, 0); }
        return RunParallel(samples, TryPftxsRoundtrip);
    }

    private static (bool ok, string note) TryPftxsRoundtrip(string archive)
    {
        var work = MakeTmp("pftxs_rt_");
        try
        {
            var staged = Path.Combine(work, Path.GetFileName(archive));
            File.Copy(archive, staged, overwrite: true);

            var manifestPath = PftxsPacker.Unpack(staged);
            var stem = Path.GetFileNameWithoutExtension(archive);
            var extractDir = Path.Combine(work, stem + "_pftxs");

            var refDir = Path.Combine(FixturesDir, "pftxs", Path.GetFileName(archive) + "_ref");
            string note;
            if (Directory.Exists(refDir))
            {
                // Compare by CONTENT, not path. PFTXS is a pure
                // byte-range container (no codec), so extraction is
                // definitionally correct; only NAMING can differ.
                // GzsTool names unresolved entries <baseHex>.ext, which
                // TRUNCATES the hash and can collide — two distinct
                // entries overwrite to one file, silently dropping data
                // (seen on ba01: 200 entries -> GzsTool writes 193).
                // Our full-hash names never collide, so we're a superset.
                // Gate: we must contain everything GzsTool produced
                // (onlyRef == 0); extra blobs we kept that GzsTool
                // dropped (onlyMine) are us being MORE complete.
                var (same, onlyRef, onlyMine) = ContentSetCompare(refDir, extractDir);
                if (onlyRef > 0)
                    return (false, $"vs GzsTool content: missing {onlyRef} GzsTool produced (of {same + onlyRef})");
                note = onlyMine > 0
                    ? $"{same} content-match GzsTool (+{onlyMine} entries GzsTool dropped to name-collision)"
                    : $"{same} files content-match GzsTool";
            }
            else note = "no GzsTool ref (round-trip only)";

            var repacked = Path.Combine(work, "repacked.pftxs");
            PftxsPacker.Pack(manifestPath, repacked);
            var reManifest = PftxsPacker.Unpack(repacked);
            var reDir = Path.Combine(work, "repacked_pftxs");
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

    private static void HarvestPftxs()
    {
        var dst = Path.Combine(FixturesDir, "pftxs");
        Directory.CreateDirectory(dst);
        var gz = FindGzsTool();
        try
        {
            var rng = new Random();
            var picks = EnumerateSafe(@"Z:\tpp\release\pack", "*.pftxs")
                .Where(f => new FileInfo(f).Length is > 2000 and < (20L << 20))
                .OrderBy(_ => rng.Next()).Take(5).ToList();
            int n = 0;
            foreach (var src in picks)
            {
                var local = Path.Combine(dst, Path.GetFileName(src));
                File.Copy(src, local, overwrite: true);
                if (gz is not null)
                {
                    var stem = Path.GetFileNameWithoutExtension(src);
                    var gzOut = Path.Combine(dst, stem + "_pftxs");
                    if (Directory.Exists(gzOut)) Directory.Delete(gzOut, true);
                    var psi = new ProcessStartInfo(gz, $"\"{local}\"")
                    {
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false, CreateNoWindow = true,
                    };
                    using (var proc = Process.Start(psi)) { proc?.WaitForExit(120000); }
                    var refDir = local + "_ref";
                    if (Directory.Exists(refDir)) Directory.Delete(refDir, true);
                    if (Directory.Exists(gzOut)) Directory.Move(gzOut, refDir);
                    // GzsTool also drops a .xml next to the archive; ignore it.
                }
                n++;
            }
            Console.WriteLine($"  PFTXS: harvested {n} archive(s){(gz is null ? " (no GzsTool ref)" : " + GzsTool references")} to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  PFTXS harvest failed: {ex.Message}"); }
    }

    private static string? FindGzsTool()
    {
        var env = Environment.GetEnvironmentVariable("GZSTOOL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var def = @"C:\rsearch\gzstool\GzsTool.exe";
        return File.Exists(def) ? def : null;
    }

    private static void HarvestFpk()
    {
        var dst = Path.Combine(FixturesDir, "fpk");
        Directory.CreateDirectory(dst);
        var datfpk = FindDatFpk();
        if (datfpk is null) { Console.WriteLine("  FPK harvest skipped: datfpk path not set."); return; }

        try
        {
            // Mix of FPK (asset) + FPKD (definition). Both live under
            // Z:\tpp\release\pack.
            var rng = new Random();
            var fpks  = EnumerateSafe(@"Z:\tpp\release\pack", "*.fpk").Where(f => new FileInfo(f).Length is > 2000 and < (20L << 20)).OrderBy(_ => rng.Next()).Take(3);
            var fpkds = EnumerateSafe(@"Z:\tpp\release\pack", "*.fpkd").Where(f => new FileInfo(f).Length is > 5000 and < (20L << 20)).OrderBy(_ => rng.Next()).Take(3);

            // Plus datfpk's own testdata archives — known to contain
            // ENCRYPTED entries (title.fpkd, EQP_WP_SP_SLD_BASE.fpkd),
            // which is the only reliable way to exercise FpkCrypto.
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

                // datfpk IGNORES the output-dir arg for fpk(d) and
                // extracts to <inputdir>/<stem>_<ext>/ next to the
                // input. Run it, then move that output to the _ref
                // location the gate expects.
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

    /// <summary>
    /// Recursively byte-compare two extracted trees. Returns
    /// (matched, differing, missingInB). Files present only in B are
    /// ignored (datfpk and we should produce the same set; a B-extra
    /// would show as missing the other direction in practice).
    /// </summary>
    private static (int matched, int differing, int missingInB) ByteCompareTrees(string a, string b)
    {
        int matched = 0, differing = 0, missing = 0;
        foreach (var fa in Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories))
        {
            var rel = fa.Substring(a.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fb = Path.Combine(b, rel);
            if (!File.Exists(fb)) { missing++; continue; }
            if (FilesEqual(fa, fb)) matched++; else differing++;
        }
        return (matched, differing, missing);
    }

    /// <summary>
    /// Compare two trees by file-content multiset (SHA256), ignoring
    /// names/paths. Returns (sharedCount, onlyInA, onlyInB). Used where
    /// two tools extract identical bytes but name unresolved entries
    /// differently.
    /// </summary>
    private static (int shared, int onlyA, int onlyB) ContentSetCompare(string a, string b)
    {
        static Dictionary<string, int> Hashes(string root)
        {
            var d = new Dictionary<string, int>();
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)));
                d[h] = d.TryGetValue(h, out var c) ? c + 1 : 1;
            }
            return d;
        }
        var ha = Hashes(a);
        var hb = Hashes(b);
        int shared = 0, onlyA = 0, onlyB = 0;
        foreach (var (h, ca) in ha)
        {
            int cb = hb.TryGetValue(h, out var v) ? v : 0;
            shared += Math.Min(ca, cb);
            if (ca > cb) onlyA += ca - cb;
        }
        foreach (var (h, cb) in hb)
        {
            int ca = ha.TryGetValue(h, out var v) ? v : 0;
            if (cb > ca) onlyB += cb - ca;
        }
        return (shared, onlyA, onlyB);
    }

    private static List<string> DiscoverQarSamples()
    {
        var dir = Path.Combine(FixturesDir, "qar");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.dat", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    /// <summary>
    /// Z:\master1 has video <c>e2*.dat</c> blobs alongside the real
    /// game archives — the e2*.dat aren't QAR format, just video
    /// streams that happen to share the extension. The actual QAR
    /// archives are <c>data1.dat</c> (~283 MB, the main game data),
    /// plus <c>chunk*.dat</c> / <c>texture*.dat</c> (1+ GB each).
    /// We harvest <c>data1.dat</c> only — smallest real QAR and the
    /// one modders actually care about.
    /// </summary>
    private static void HarvestQar()
    {
        var dst = Path.Combine(FixturesDir, "qar");
        Directory.CreateDirectory(dst);
        try
        {
            var src = @"Z:\master1\data1.dat";
            if (!File.Exists(src)) { Console.WriteLine("  QAR: Z:\\master1\\data1.dat not present"); return; }
            var localDat = Path.Combine(dst, "data1.dat");
            File.Copy(src, localDat, overwrite: true);

            // Generate the datfpk reference extraction so the gate can
            // byte-compare against it. Without datfpk, the gate falls
            // back to pack round-trip only (still a real gate, just
            // not validated against the reference tool).
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
