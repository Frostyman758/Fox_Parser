using System.Diagnostics;
using System.Security.Cryptography;
using MgsvModBldr.Tools.Fox;
using MgsvModBldr.Tools.Fsop;
using MgsvModBldr.Tools.Ftex;
using MgsvModBldr.Tools.Qar;
using MgsvModBldr.Tools.Translation;
using MgsvModBldr.Tools.Twpf;
using MgsvModBldr.Tools.Mtar;
using MgsvModBldr.Tools.Spch;
using MgsvModBldr.Tools.Tcvp;
using MgsvModBldr.Tools.Rdf;
using MgsvModBldr.Tools.Fv2;
using MgsvModBldr.Tools.Hlsl;
using MgsvModBldr.Tools.Stp;
using MgsvModBldr.Tools.Tests;

namespace MgsvModBldr.Tools.Cli;

// modbldr-tools wrapper DLL — single-arg CLI dispatch for the ported
// tools. The thin modbldr-tools(.exe) launcher just forwards argv here.
// Mirrors the drag-on-exe convention of the reference tools:
//
//   tools.exe <file.fox2>      -> file.fox2.xml          (decompile)
//   tools.exe <file.fox2.xml>  -> file.fox2              (compile)
//   tools.exe <file.fsop>      -> <basename>_unpacked/   (unpack)
//   tools.exe <unpacked_dir>   -> <basename>.fsop        (pack)
//   tools.exe test             -> run automated regression on cached fixtures
//   tools.exe test --harvest   -> refresh fixtures from Z:\ (uses datfpk)
//
// Optional --roundtrip flag does <op>-><inverse op> and SHA256-checks
// the result against the original. PASS only expected for tools whose
// reference is deterministic (FSOP); lossy ports (Fox) FAIL by design.
// Use `test` for the real per-tool gate.
public static class Cli
{
    public static int Run(string[] args)
    {
        // No args: open the interactive fox shell (one living process whose
        // fox recolours in place per command, instead of a fresh logo each
        // time). When piped/redirected there's no TTY — just print the
        // splash and exit so scripts stay clean.
        if (args.Length == 0)
        {
            if (FoxLogo.CanGoInteractive) return Interactive();
            FoxLogo.Show(FoxLogo.DefaultColor);
            return 0;
        }

        // --help: just the list of supported file types.
        if (args[0] is "--help" or "-h" or "-?" or "/?" or "help")
        {
            PrintSupportedTypes();
            return 0;
        }

        // Debug: print game PathCode for a path (compare against archive hashes).
        if (args[0] == "pathcode")
        {
            foreach (var p in args.Skip(1))
                Console.WriteLine($"{MgsvModBldr.Tools.GameHashing.GameHash.PathCode(p):x16}  {p}");
            return 0;
        }
        if (args[0] == "stringid")
        {
            foreach (var p in args.Skip(1))
                Console.WriteLine($"{MgsvModBldr.Tools.GameHashing.GameHash.StringId(p):x16}  {p}");
            return 0;
        }
        // buildmgsv <sourceDir> <out.mgsv> — exercise the full ModBuilder
        // pipeline with the in-process managed FPK archiver (no datfpk).
        if (args[0] == "buildmgsv")
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: buildmgsv <sourceDir> <out.mgsv>"); return 2; }
            var meta = MgsvModBldr.Core.ModMetadata.Load(Path.Combine(args[1], "metadata.xml"));
            var builder = new MgsvModBldr.Core.ModBuilder
            {
                FpkArchiver = new MgsvModBldr.Tools.Fpk.ManagedFpkArchiver(),
                Log = Console.WriteLine,
            };
            builder.Build(args[1], meta, args[2]);
            return 0;
        }

        // `test` is a subcommand, not a file path — handle before file checks.
        //   test                  -> run everything
        //   test <tool>           -> run just that tool (fsop|fox|ftex)
        //   test [<tool>] --harvest -> refresh fixtures first (scope honours <tool> too)
        if (args[0] == "test")
        {
            bool harvest = args.Contains("--harvest") || args.Contains("-h");
            // Anything other than the flags is treated as a tool filter.
            var filter = args.Skip(1)
                             .FirstOrDefault(a => !a.StartsWith("--") && a != "-h");
            return TestRunner.Run(harvest, filter);
        }

        // A file/folder (incl. drag-and-drop onto the exe): one-shot convert,
        // drawing the fox out once in the tool's colour.
        return ExecuteFileOp(args, LogoMode.Animate);
    }

    private enum LogoMode { None, Animate, Repaint }

    /// <summary>
    /// Parse the conversion flags + positional input and dispatch. The fox is
    /// drawn out (one-shot) or recoloured in place (interactive) per
    /// <paramref name="logo"/>. Errors surface as FOXDIE.
    /// </summary>
    private static int ExecuteFileOp(string[] args, LogoMode logo)
    {
        bool roundtrip = false, hlslFiles = false, stpGz = false;
        var positional = new List<string>();
        foreach (var a in args)
        {
            if (a == "--roundtrip" || a == "-r") roundtrip = true;
            else if (a == "-files" || a == "-src") hlslFiles = true;
            else if (a == "-gz") stpGz = true; // .stp/.sab GZ version (reference defaults TPP)
            else positional.Add(a);
        }

        if (positional.Count == 0)
        {
            PrintSupportedTypes();
            return 2;
        }

        var input = positional[0];
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            Console.Error.WriteLine($"FOXDIE: input does not exist: {input}");
            return 2;
        }

        var color = FoxLogo.ColorFor(ToolNameFor(input));
        switch (logo)
        {
            case LogoMode.Animate: FoxLogo.DrawOut(color); break; // one-shot flourish
            case LogoMode.Repaint: FoxLogo.Repaint(color); break; // interactive in-place recolour
        }

        try
        {
            return Dispatch(input, roundtrip, hlslFiles, stpGz);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FOXDIE: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// The interactive fox shell. Draws the fox once, then loops reading a
    /// path (or a quoted path dropped into the window) per line; each command
    /// recolours the SAME fox in place and prints its result below. Type
    /// help / clear / exit. Plain commands only — no flags juggling.
    /// </summary>
    private static int Interactive()
    {
        try { Console.Clear(); } catch { /* some hosts disallow */ }
        FoxLogo.DrawOut(FoxLogo.DefaultColor); // animated first draw
        Console.WriteLine("  Drop a file here or type a path to convert.   ( help · clear · exit )");

        while (true)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = FoxLogo.DefaultColor;
            Console.Write("  fox> ");
            Console.ForegroundColor = prev;

            string line;
            try { line = Console.ReadLine(); }
            catch { break; }
            if (line is null) break;            // EOF (Ctrl+Z)
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line is "exit" or "quit" or ":q" or "q") break;
            if (line is "help" or "--help" or "-h" or "?")
            {
                FoxLogo.Repaint(FoxLogo.DefaultColor);
                PrintSupportedTypes();
                continue;
            }
            if (line is "clear" or "cls")
            {
                FoxLogo.Repaint(FoxLogo.DefaultColor);
                continue;
            }

            ExecuteFileOp(Tokenize(line), LogoMode.Repaint);
        }

        Console.ResetColor();
        try { Console.Clear(); } catch { /* ignore */ }
        return 0;
    }

    /// <summary>Split a shell line into tokens, honouring "double quotes" (paths with spaces / drag-drop).</summary>
    private static string[] Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens.ToArray();
    }

    /// <summary>
    /// Map an input path to its tool key (for the splash colour). Mirrors
    /// the dispatch routing; falls back to the default colour when unknown.
    /// </summary>
    private static string ToolNameFor(string input)
    {
        if (Directory.Exists(input))
        {
            var t = input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (t.EndsWith("_stp", StringComparison.OrdinalIgnoreCase) ||
                t.EndsWith("_sab", StringComparison.OrdinalIgnoreCase)) return "stp";
            return "fsop";
        }

        var name = Path.GetFileName(input);
        bool Has(string suffix) => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        var ext = Path.GetExtension(input).ToLowerInvariant();

        if (ext == ".xml")
        {
            if (Has(".subp.xml")) return "subp";
            if (Has(".twpf.xml")) return "twpf";
            if (Has(".ffnt.xml")) return "ffnt";
            if (Has(".lng.xml") || Has(".lng2.xml")) return "lng";
            if (Has(".mtar.xml")) return "mtar";
            if (Has(".spch.xml")) return "spch";
            if (Has(".tcvp.xml")) return "tcvp";
            if (Has(".rdf.xml")) return "rdf";
            if (Has(".fv2.xml")) return "fv2";
            return "fox";
        }
        if (ext == ".json")
        {
            if (Has(".pftxs.json")) return "pftxs";
            if (Has(".fpk.json") || Has(".fpkd.json")) return "fpk";
            if (Has(".dat.json") || Has(".qar.json")) return "qar";
            if (Has(".sbp.json")) return "sbp";
            return "fox";
        }
        return ext switch
        {
            ".subp" => "subp", ".twpf" => "twpf", ".ffnt" => "ffnt",
            ".lng" or ".lng2" => "lng", ".mtar" => "mtar", ".spch" => "spch",
            ".tcvp" => "tcvp", ".rdf" => "rdf", ".fv2" => "fv2",
            ".fxc" or ".hlsl" => "hlsl", ".fsop" => "fsop",
            ".ftex" or ".dds" or ".ftexs" => "ftex",
            ".pftxs" => "pftxs", ".fpk" or ".fpkd" => "fpk",
            ".dat" or ".qar" => "qar", ".sbp" => "sbp",
            ".stp" or ".sab" => "stp",
            _ when FoxPacker.DecompilableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) => "fox",
            _ => null,
        };
    }

    private static void PrintSupportedTypes()
    {
        Console.WriteLine("Supported file types:");
        Console.WriteLine("  .fox2 .bnd .clo .des .evf .fsd .lad .parts .ph .phsd .sdf .sim .tgt .vdp .veh .vfxlf");
        Console.WriteLine("                Fox data            <-> .xml");
        Console.WriteLine("  .fsop         shader package       <-> folder");
        Console.WriteLine("  .ftex / .dds  texture              .ftex <-> .dds");
        Console.WriteLine("  .fpk / .fpkd  file package         <-> .fpk.json + folder");
        Console.WriteLine("  .dat / .qar   archive              <-> .json + folder");
        Console.WriteLine("  .pftxs        packed textures      <-> .pftxs.json + folder");
        Console.WriteLine("  .subp         subtitles            <-> .subp.xml");
        Console.WriteLine("  .ffnt         font                 <-> .ffnt.xml + .png");
        Console.WriteLine("  .lng / .lng2  language             <-> .lng.xml");
        Console.WriteLine("  .twpf         weather params       <-> .twpf.xml");
        Console.WriteLine("  .mtar         motion archive       <-> .mtar.xml + folder");
        Console.WriteLine("  .spch         speech               <-> .spch.xml");
        Console.WriteLine("  .tcvp         cover-point locators <-> .tcvp.xml");
        Console.WriteLine("  .rdf          radio dialogue       <-> .rdf.xml");
        Console.WriteLine("  .fv2          vfx                  <-> .fv2.xml");
        Console.WriteLine("  .fxc          DXBC shader          -> .hlsl source (.hlsl -> .fxc)");
        Console.WriteLine("  .sbp          sound bank package   <-> .sbp.json + folder");
        Console.WriteLine("  .stp / .sab   streamed audio/anim  <-> folder");
    }

    private static int Dispatch(string input, bool roundtrip, bool hlslFiles = false, bool stpGz = false)
    {
        if (Directory.Exists(input))
        {
            // _stp / _sab folders repack via the Stp tool; everything else
            // is an FSOP unpacked-folder.
            var trimmed = input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.EndsWith("_stp", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith("_sab", StringComparison.OrdinalIgnoreCase))
            {
                var p = StpPacker.Pack(input, stpGz ? StpVersion.GZ : StpVersion.TPP);
                Console.WriteLine($"Packed    {input} -> {p}");
                return 0;
            }
            return roundtrip ? RoundtripFsop(input) : PackFsop(input);
        }

        var ext = Path.GetExtension(input).ToLowerInvariant();
        if (ext == ".xml")
        {
            // Format-suffixed companions (like .fpk.json) — route before Fox's bare .xml.
            if (input.EndsWith(".subp.xml", StringComparison.OrdinalIgnoreCase))
            { var p = SubpConverter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            if (input.EndsWith(".twpf.xml", StringComparison.OrdinalIgnoreCase))
            { var p = TwpfConverter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            if (input.EndsWith(".ffnt.xml", StringComparison.OrdinalIgnoreCase))
            { var p = FfntConverter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            if (input.EndsWith(".lng.xml", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".lng2.xml", StringComparison.OrdinalIgnoreCase))
            { var p = LangConverter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            if (input.EndsWith(".mtar.xml", StringComparison.OrdinalIgnoreCase))
            { var p = MtarConverter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            if (input.EndsWith(".spch.xml", StringComparison.OrdinalIgnoreCase))
            { var p = SpchConverter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            if (input.EndsWith(".tcvp.xml", StringComparison.OrdinalIgnoreCase))
            { var p = TcvpConverter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            if (input.EndsWith(".rdf.xml", StringComparison.OrdinalIgnoreCase))
            { var p = RdfConverter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            if (input.EndsWith(".fv2.xml", StringComparison.OrdinalIgnoreCase))
            { var p = Fv2Converter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            return roundtrip ? RoundtripFoxFromXml(input) : CompileFox(input);
        }

        if (ext == ".subp") { var p = SubpConverter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".twpf") { var p = TwpfConverter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".ffnt") { var p = FfntConverter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".lng" || ext == ".lng2") { var p = LangConverter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".mtar") { var p = MtarConverter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".spch") { var p = SpchConverter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".tcvp") { var p = TcvpConverter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".rdf") { var p = RdfConverter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".fv2") { var p = Fv2Converter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".fxc")
        {
            // Default: extract the preprocessed .hlsl. -files: reconstruct the
            // original .shdr/.h source files into <name>_src/.
            var p = hlslFiles ? HlslConverter.UnpackFiles(input) : HlslConverter.Unpack(input);
            if (p is null) { Console.Error.WriteLine($"FOXDIE: no embedded HLSL source in {input} (no SDBG chunk)."); return 2; }
            Console.WriteLine($"Extracted {input} -> {p}");
            return 0;
        }
        if (ext == ".hlsl")
        {
            var p = HlslConverter.Recompile(input);
            Console.WriteLine($"Compiled  {input} -> {p}");
            return 0;
        }

        if (FoxPacker.DecompilableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return roundtrip ? RoundtripFox(input) : DecompileFox(input);

        if (ext == ".fsop")
            return roundtrip ? RoundtripFsopFromFile(input) : UnpackFsop(input);

        if (ext == ".ftex") { var p = FtexPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".dds")  { var p = FtexPacker.Pack(input);   Console.WriteLine($"Packed    {input} -> {p}"); return 0; }

        if (ext == ".ftexs")
        {
            // .ftexs are mipmap sidecars of a .ftex — you unpack the
            // .ftex (which pulls in its sidecars automatically), not the
            // sidecar directly.
            var stem = Path.GetFileNameWithoutExtension(input);
            // foo.2.ftexs -> base is "foo" (strip the .N too)
            var dotIdx = stem.IndexOf('.');
            if (dotIdx > 0) stem = stem[..dotIdx];
            var ftex = Path.Combine(Path.GetDirectoryName(input) ?? ".", stem + ".ftex");
            Console.Error.WriteLine($"FOXDIE: .ftexs is a mipmap sidecar, not a standalone file.");
            Console.Error.WriteLine($"Unpack its .ftex instead:  modbldr-tools \"{ftex}\"");
            return 2;
        }

        if (ext == ".pftxs") { var p = MgsvModBldr.Tools.Pftxs.PftxsPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".json" && input.EndsWith(".pftxs.json", StringComparison.OrdinalIgnoreCase))
        { var p = MgsvModBldr.Tools.Pftxs.PftxsPacker.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }

        if (ext == ".fpk" || ext == ".fpkd") { var p = MgsvModBldr.Tools.Fpk.FpkPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".json" && (input.EndsWith(".fpk.json", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".fpkd.json", StringComparison.OrdinalIgnoreCase)))
        { var p = MgsvModBldr.Tools.Fpk.FpkPacker.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }

        if (ext == ".stp" || ext == ".sab") { var p = StpPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }

        if (ext == ".sbp") { var p = MgsvModBldr.Tools.Sbp.SbpPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".json" && input.EndsWith(".sbp.json", StringComparison.OrdinalIgnoreCase))
        { var p = MgsvModBldr.Tools.Sbp.SbpPacker.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }

        if (ext == ".dat" || ext == ".qar") { var p = QarPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".json" && (input.EndsWith(".dat.json", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".qar.json", StringComparison.OrdinalIgnoreCase)))
        { var p = QarPacker.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }

        Console.Error.WriteLine($"FOXDIE: unknown extension '{ext}'. See --help.");
        return 2;
    }

    // ─── Direct (one-shot) ops ──────────────────────────────────────────────

    private static int DecompileFox(string fox)
    {
        var sw = Stopwatch.StartNew();
        var outPath = FoxPacker.Decompile(fox);
        sw.Stop();
        Console.WriteLine($"Decompiled {fox} -> {outPath} ({sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    private static int CompileFox(string xml)
    {
        var sw = Stopwatch.StartNew();
        var outPath = FoxPacker.Compile(xml);
        sw.Stop();
        Console.WriteLine($"Compiled   {xml} -> {outPath} ({sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    private static int UnpackFsop(string fsop)
    {
        var dir = Path.Combine(Path.GetDirectoryName(fsop) ?? ".", Path.GetFileNameWithoutExtension(fsop) + "_unpacked");
        var sw = Stopwatch.StartNew();
        int n = FsopPacker.Unpack(fsop, dir);
        sw.Stop();
        Console.WriteLine($"Unpacked   {fsop} -> {dir} ({n} shaders, {sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    private static int PackFsop(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        if (name.EndsWith("_unpacked")) name = name[..^"_unpacked".Length];
        var outPath = Path.Combine(Path.GetDirectoryName(dir) ?? ".", name + ".fsop");
        var sw = Stopwatch.StartNew();
        int n = FsopPacker.Pack(dir, outPath);
        sw.Stop();
        Console.WriteLine($"Packed     {dir} -> {outPath} ({n} shaders, {sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    // ─── Round-trip regression gates ────────────────────────────────────────

    private static int RoundtripFox(string fox)
    {
        var work = MakeTmpDir("fox_rt_");
        var xml      = Path.Combine(work, Path.GetFileName(fox) + ".xml");
        var repacked = Path.Combine(work, Path.GetFileName(fox));
        var original = File.ReadAllBytes(fox);
        Console.WriteLine($"Input: {fox} ({original.Length:N0} bytes)");
        FoxPacker.Decompile(fox, xml);
        FoxPacker.Compile(xml, repacked);
        return CompareAndReport(original, File.ReadAllBytes(repacked), work);
    }

    private static int RoundtripFoxFromXml(string xml)
    {
        // For symmetry: compile XML -> decompile -> compare XML strings.
        var work     = MakeTmpDir("fox_rt_");
        var binPath  = Path.Combine(work, Path.GetFileNameWithoutExtension(xml));
        var xml2     = Path.Combine(work, Path.GetFileName(xml));
        FoxPacker.Compile(xml, binPath);
        FoxPacker.Decompile(binPath, xml2);
        var a = File.ReadAllText(xml);
        var b = File.ReadAllText(xml2);
        if (a == b) { Console.WriteLine("PASS: XML->bin->XML stable."); return 0; }
        Console.Error.WriteLine($"FAIL: XML differs. See {work}");
        return 1;
    }

    private static int RoundtripFsopFromFile(string fsop)
    {
        var work      = MakeTmpDir("fsop_rt_");
        var unpackDir = Path.Combine(work, "unpacked");
        var repacked  = Path.Combine(work, Path.GetFileName(fsop));
        var original  = File.ReadAllBytes(fsop);
        Console.WriteLine($"Input: {fsop} ({original.Length:N0} bytes)");
        FsopPacker.Unpack(fsop, unpackDir);
        FsopPacker.Pack(unpackDir, repacked);
        return CompareAndReport(original, File.ReadAllBytes(repacked), work);
    }

    private static int RoundtripFsop(string dir)
    {
        // Pack the folder, then unpack the result, then pack again, then
        // compare the two packs. Sanity check for hand-authored folders.
        var work    = MakeTmpDir("fsop_rt_");
        var a       = Path.Combine(work, "first.fsop");
        var unpack2 = Path.Combine(work, "unpacked2");
        var b       = Path.Combine(work, "second.fsop");
        FsopPacker.Pack(dir, a);
        FsopPacker.Unpack(a, unpack2);
        FsopPacker.Pack(unpack2, b);
        return CompareAndReport(File.ReadAllBytes(a), File.ReadAllBytes(b), work);
    }

    private static int CompareAndReport(byte[] original, byte[] repacked, string workDirOnFail)
    {
        if (original.Length != repacked.Length)
        {
            Console.Error.WriteLine($"FAIL: size differs (orig={original.Length}, repacked={repacked.Length})");
            Console.Error.WriteLine($"  See {workDirOnFail}");
            return 1;
        }
        var origHash = Convert.ToHexString(SHA256.HashData(original));
        var rpkdHash = Convert.ToHexString(SHA256.HashData(repacked));
        Console.WriteLine($"Original  SHA : {origHash}");
        Console.WriteLine($"Repacked  SHA : {rpkdHash}");
        if (origHash != rpkdHash)
        {
            int firstDiff = -1;
            for (int i = 0; i < original.Length; i++)
                if (original[i] != repacked[i]) { firstDiff = i; break; }
            Console.Error.WriteLine($"FAIL: hashes differ. First diff at 0x{firstDiff:X} (orig=0x{original[firstDiff]:X2}, repacked=0x{repacked[firstDiff]:X2})");
            Console.Error.WriteLine($"  See {workDirOnFail}");
            return 1;
        }
        Console.WriteLine("PASS: byte-exact.");
        return 0;
    }

    private static string MakeTmpDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }
}
