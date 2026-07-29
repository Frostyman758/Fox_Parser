// Hlsl tool regression gate
using MgsvModBldr.Tools.Hlsl;
using MgsvModBldr.Tools.Fsop;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Hlsl.Tests;

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
            var withSrc = new List<(string path, byte[] src)>();
            foreach (var fxc in fxcs)
            {
                var src = HlslConverter.ExtractSourceBytes(File.ReadAllBytes(fxc));
                if (src is null) { noSrc++; continue; } // shader has no embedded debug source (legit)
                // Latin1 keeps the bytes 1:1 so Shift-JIS Japanese comments
                // don't break these ASCII substring checks.
                var ascii = System.Text.Encoding.Latin1.GetString(src);
                if (src.Length > 50 && ascii.Contains("#line") &&
                    (ascii.Contains("return") || ascii.Contains("void ") || ascii.Contains("float") || ascii.Contains("struct")))
                { valid++; withSrc.Add((fxc, src)); }
                else bad++;
            }

            // Fail only on truncated/garbage extractions; no-source shaders
            // legitimately exist (compiled without embedded debug source).
            if (bad > 0)
                return (false, $"{valid}/{fxcs.Count} valid, {bad} MALFORMED ({noSrc} no-source)");

            // Recompile validation (Windows): a sample of the extracted sources
            // must compile back to valid SM5 DXBC via D3DCompile. This proves
            // the decompile->recompile round-trip works (functional, not
            // byte-exact — the sanctioned exception).
            string recNote = "";
            if (HlslCompiler.IsAvailable && withSrc.Count > 0)
            {
                int recOk = 0, recFail = 0; string firstErr = null;
                foreach (var (path, src) in withSrc.Take(12))
                {
                    bool ps = Path.GetFileNameWithoutExtension(path).EndsWith("_ps", StringComparison.OrdinalIgnoreCase);
                    var entry = ps ? "ps_main" : "vs_main";
                    var target = ps ? "ps_5_0" : "vs_5_0";
                    try
                    {
                        var dxbc = HlslCompiler.Compile(src, Path.GetFileName(path), entry, target, 0);
                        if (dxbc.Length >= 4 && dxbc[0] == (byte)'D' && dxbc[1] == (byte)'X') recOk++;
                        else { recFail++; firstErr ??= Path.GetFileName(path) + ": not DXBC"; }
                    }
                    catch (Exception ex) { recFail++; firstErr ??= Path.GetFileName(path) + ": " + ex.Message; }
                }
                if (recFail > 0)
                    return (false, $"{valid}/{fxcs.Count} extracted; recompile {recOk} ok, {recFail} FAIL ({firstErr})");
                recNote = $"; recompiled {recOk}/{Math.Min(12, withSrc.Count)} ok";
            }

            return (true, $"{valid}/{fxcs.Count} -> valid HLSL" + (noSrc > 0 ? $" ({noSrc} no-source)" : "") + recNote);
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
