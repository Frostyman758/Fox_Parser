using MgsvModBldr.Tools.Hlsl;
using MgsvModBldr.Tools.Fsop;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Hlsl.Tests;

/// <summary>
/// HLSL extractor gate. This tool is the sanctioned NON-byte-exact tool
/// (shader recompile can't reproduce the original DXBC), so the gate is a
/// validity smoke test, not a byte-exact round-trip: unpack a real fsop,
/// and confirm EVERY .fxc yields non-empty, plausibly-valid preprocessed
/// HLSL (has #line directives + actual shader code). Samples: the loose
/// FxShaders fsop on Z:\ (or harvested fsop fixtures).
/// </summary>
public sealed class HlslTests : IToolTests
{
    public string Name => "hlsl";

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Hlsl (.fxc -> HLSL source extraction; validity smoke test) ---");
        var fsops = DiscoverFsops();
        if (fsops.Count == 0)
        {
            Console.WriteLine("  (no fsop fixtures; run fsop --harvest or attach Z:\\)");
            return (0, 0);
        }
        return RunParallel(fsops, GateFsop);
    }

    private static (bool ok, string note) GateFsop(string fsop)
    {
        var work = MakeTmp("hlsl_");
        try
        {
            var unpackDir = Path.Combine(work, "u");
            FsopPacker.Unpack(fsop, unpackDir);
            var fxcs = Directory.EnumerateFiles(unpackDir, "*.fxc", SearchOption.AllDirectories).ToList();
            if (fxcs.Count == 0) return (true, "no .fxc in fsop");

            int valid = 0, noSrc = 0, bad = 0;
            foreach (var fxc in fxcs)
            {
                var src = HlslConverter.ExtractSourceBytes(File.ReadAllBytes(fxc));
                if (src is null) { noSrc++; continue; } // shader has no embedded debug source (legit)
                // Latin1 keeps the bytes 1:1 so Shift-JIS Japanese comments
                // don't break these ASCII substring checks.
                var ascii = System.Text.Encoding.Latin1.GetString(src);
                if (src.Length > 50 && ascii.Contains("#line") &&
                    (ascii.Contains("return") || ascii.Contains("void ") || ascii.Contains("float") || ascii.Contains("struct")))
                    valid++;
                else bad++;
            }

            // Fail only on truncated/garbage extractions; no-source shaders
            // legitimately exist (compiled without embedded debug source).
            if (bad > 0)
                return (false, $"{valid}/{fxcs.Count} valid, {bad} MALFORMED ({noSrc} no-source)");
            return (true, $"{valid}/{fxcs.Count} -> valid HLSL" + (noSrc > 0 ? $" ({noSrc} no-source)" : ""));
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverFsops()
    {
        var hits = new List<string>();
        TryAdd(hits, @"Z:\shaders\dx11\FxShaders_dx11.fsop");
        var dir = Path.Combine(FixturesDir, "fsop");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.fsop", SearchOption.AllDirectories))
                if (!hits.Contains(f)) hits.Add(f);
        return hits;
    }

    public void Harvest()
    {
        // Reuses the fsop fixtures/Z:\ shaders; nothing tool-specific to harvest.
    }
}
