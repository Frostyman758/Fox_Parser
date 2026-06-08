using Microsoft.Win32.SafeHandles;
using MgsvModBldr.Tools.G0s;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;

namespace MgsvModBldr.Tools.G0s.Tests;

/// <summary>
/// G0s (GZ QAR) gate. Two checks:
///  (A) REAL data — sample the smallest entries of data_02.g0s, decrypt them
///      and confirm they byte-match the reference unpacked dir GzsTool 0.2
///      produced (validates the outer+inner decryption AND name resolution),
///      then re-encrypt each and confirm it reproduces the original on-disk
///      bytes (byte-exact round-trip, incl. the inner layer GzsTool 0.2 could
///      not). Skipped if the GZ install isn't present.
///  (B) SYNTHETIC — a small archive (with a forced inner-encrypted entry)
///      packed and unpacked by us must round-trip byte-exact. Always runs.
/// </summary>
public sealed class G0sTests : IToolTests
{
    public string Name => "g0s";

    private static readonly string GzDir =
        @"C:\Program Files (x86)\Steam\steamapps\common\Metal Gear Solid Ground Zeroes\GzsTool.v0.2\GzsTool v0.2";
    private const int RealSampleCount = 40;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- G0s (GZ QAR: decrypt==reference dir + byte-exact round-trip) ---");
        int pass = 0, fail = 0;

        var (rp, rf) = RealDataGate();
        pass += rp; fail += rf;

        var (sp, sf) = SyntheticGate();
        pass += sp; fail += sf;

        // Opt-in full round-trip vs the real game file (heavy: ~1.6 GB).
        if (Environment.GetEnvironmentVariable("G0S_FULL") == "1")
        {
            var (fp, ff) = FullRoundTripGate();
            pass += fp; fail += ff;
        }

        return (pass, fail);
    }

    // ── (A) real data_02.g0s sample vs reference dir + per-entry round-trip ──
    private static (int, int) RealDataGate()
    {
        var g0s = Path.Combine(GzDir, "data_02.g0s");
        var refDir = Path.Combine(GzDir, "data_02");
        if (!File.Exists(g0s) || !Directory.Exists(refDir))
        {
            Console.WriteLine("  [skip] GZ install not found (data_02.g0s + data_02/)");
            return (0, 0);
        }

        G0sArchive arc;
        using (var fs = File.OpenRead(g0s)) arc = G0sArchive.ReadIndex(fs);

        using var handle = File.OpenHandle(g0s, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

        // Flag inner-encrypted entries (cheap 8-byte peek), so the sample can
        // exercise the real inner cipher, not just the smallest plain entries.
        var innerFlag = new System.Collections.Concurrent.ConcurrentBag<G0sEntry>();
        Parallel.ForEach(arc.Entries.Where(e => e.Size >= 8), e =>
        {
            var b = new byte[8];
            ReadExactAt(handle, b, 16L * e.Offset);
            if (G0sArchive.PeekIsInner(b, e.Offset)) innerFlag.Add(e);
        });

        var sample = arc.Entries.OrderBy(e => e.Size).Take(RealSampleCount)
            .Concat(innerFlag.OrderBy(e => e.Size).Take(12))
            .Distinct().ToList();

        int ok = 0, bad = 0; string firstErr = null; int inner = 0, missing = 0;

        foreach (var e in sample)
        {
            try
            {
                var raw = new byte[e.Size];
                ReadExactAt(handle, raw, 16L * e.Offset);
                var original = (byte[])raw.Clone();

                var (data, key) = G0sArchive.Decrypt(raw, e.Offset);
                if (key.HasValue) inner++;

                G0sHash.TryResolve(e.Hash, out var filePath);
                var refFile = Path.Combine(refDir, G0sArchive.OnDiskRelPath(filePath));
                if (!File.Exists(refFile)) { missing++; continue; } // name resolved differently; skip

                if (!data.AsSpan().SequenceEqual(File.ReadAllBytes(refFile)))
                { bad++; firstErr ??= $"decrypt mismatch vs ref: {filePath}"; continue; }

                var blob = G0sArchive.Encrypt(data, e.Offset, key);
                if (!blob.AsSpan().SequenceEqual(original))
                { bad++; firstErr ??= $"re-encrypt mismatch: {filePath}"; continue; }

                ok++;
            }
            catch (Exception ex) { bad++; firstErr ??= ex.Message; }
        }

        bool pass = bad == 0 && ok > 0;
        Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] data_02.g0s sample: {ok}/{sample.Count} decrypt+roundtrip ok " +
                          $"({inner} inner-encrypted, {missing} name-unresolved){(firstErr != null ? " :: " + firstErr : "")}");
        return pass ? (1, 0) : (0, 1);
    }

    // ── (C, opt-in) full unpack vs reference dir + repack vs game file ──────
    private static (int, int) FullRoundTripGate()
    {
        var g0s = Path.Combine(GzDir, "data_02.g0s");
        var refDir = Path.Combine(GzDir, "data_02");
        if (!File.Exists(g0s) || !Directory.Exists(refDir))
        { Console.WriteLine("  [skip] full: GZ install not found"); return (0, 0); }

        var work = MakeTmp("g0s_full_");
        try
        {
            var staged = Path.Combine(work, "data_02.g0s");
            Console.WriteLine("  full: staging 1.6 GB copy ...");
            File.Copy(g0s, staged);

            Console.WriteLine("  full: unpacking ...");
            var manifest = G0sPacker.Unpack(staged);          // work/data_02/ + work/data_02.g0s.json
            var ourDir = Path.Combine(work, "data_02");

            var (m1, d1, miss1) = ByteCompareTrees(refDir, ourDir);
            var (_, _, miss2) = ByteCompareTrees(ourDir, refDir);
            if (d1 > 0 || miss1 > 0 || miss2 > 0)
            {
                Console.WriteLine($"  [FAIL] full: unpack vs ref dir (matched={m1} differing={d1} onlyInRef={miss1} onlyInOurs={miss2})");
                return (0, 1);
            }

            Console.WriteLine($"  full: {m1} files match reference dir; repacking ...");
            var repacked = G0sPacker.Pack(manifest, Path.Combine(work, "repacked.g0s"));
            bool exact = FilesEqual(repacked, g0s);
            Console.WriteLine($"  [{(exact ? "PASS" : "FAIL")}] full: unpack == {m1} ref files; repack {(exact ? "BYTE-EXACT" : "DIFFERS")} vs game file");
            return exact ? (1, 0) : (0, 1);
        }
        catch (Exception ex) { Console.WriteLine($"  [FAIL] full: {ex.GetType().Name}: {ex.Message}"); return (0, 1); }
        finally { TryDelete(work); }
    }

    // ── (B) synthetic small archive, byte-exact self round-trip ─────────────
    private static (int, int) SyntheticGate()
    {
        var work = MakeTmp("g0s_syn_");
        try
        {
            var contentA = Path.Combine(work, "arc");
            Directory.CreateDirectory(Path.Combine(contentA, "Fox", "Scripts"));
            var rng = new Random(1234);
            var files = new (string rel, byte[] data, uint? key)[]
            {
                ("Fox/Scripts/a.lua", RandomBytes(rng, 1500), null),
                ("Fox/Scripts/b.lua", RandomBytes(rng, 64),   0xDEADBEEF), // inner-encrypted
                ("deadbeefcafe.lua",  RandomBytes(rng, 4096), null),
                ("Fox/Scripts/c.fox2", RandomBytes(rng, 333), 0x12345678), // inner-encrypted, odd length
            };
            foreach (var f in files)
            {
                var p = Path.Combine(contentA, G0sArchive.OnDiskRelPath(f.rel));
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, f.data);
            }

            // Author a manifest next to the content dir (named arc.g0s.json).
            var manifest = new G0sManifest
            {
                Name = "arc.g0s",
                Entries = files.Select(f => new G0sManifestEntry
                {
                    FilePath = f.rel.Contains('/') ? "/" + f.rel : f.rel,
                    Hash = G0sHash.HashFileNameWithExtension((f.rel.Contains('/') ? "/" + f.rel : f.rel)),
                    Key = f.key,
                }).ToList(),
            };
            var manifestPath = Path.Combine(work, "arc.g0s.json");
            File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest));

            // pack -> g0s, unpack -> dir2 + manifest2, repack -> g0s2; compare.
            var g0s = G0sPacker.Pack(manifestPath);                 // work/arc.g0s
            var manifest2 = G0sPacker.Unpack(g0s);                  // work/arc/ + work/arc.g0s.json (overwrites)
            // content must survive
            var (matched, differing, missing) = ByteCompareTrees(contentA, Path.Combine(work, "arc"));
            if (differing > 0 || missing > 0)
            { Console.WriteLine($"  [FAIL] synthetic: unpack content mismatch (diff={differing} missing={missing})"); return (0, 1); }

            var g0s2 = G0sPacker.Pack(manifest2, Path.Combine(work, "arc2.g0s"));
            if (!FilesEqual(g0s, g0s2))
            { Console.WriteLine("  [FAIL] synthetic: repack not byte-exact"); return (0, 1); }

            Console.WriteLine($"  [PASS] synthetic {files.Length}-entry archive (2 inner-encrypted): round-trip byte-exact");
            return (1, 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] synthetic: {ex.GetType().Name}: {ex.Message}");
            return (0, 1);
        }
        finally { TryDelete(work); }
    }

    private static byte[] RandomBytes(Random rng, int n) { var b = new byte[n]; rng.NextBytes(b); return b; }

    private static void ReadExactAt(SafeFileHandle h, byte[] buf, long pos)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = RandomAccess.Read(h, buf.AsSpan(read), pos + read);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public void Harvest() { /* uses the live GZ install + synthetic data; nothing to cache */ }
}
