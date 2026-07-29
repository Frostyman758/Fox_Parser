// hash/unhash commands, all engine hash variants
using MgsvModBldr.Tools.GameHashing;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Cli;

// foxhash — forward + reverse game-hash utility.
//
//   tools.exe hash <string...>            -> every hash variant of each string
//   tools.exe unhash <hex...> [-d file]   -> reverse-lookup hashes against dict/*.txt
//
// Variants covered (all engine-confirmed):
//   PathCode64  QAR/PathServer file-path hash: 51-bit base (CityHash of the
//               extensionless path) | 13-bit extension code << 51; bit 50 is
//               the user-flag. GameHash.PathCode.
//   StrCode64   Fox StringId — 48-bit CityHash StrCode. GameHash.StringId.
//   StrCode32   low 32 bits of StrCode64 — Lua GameObject command ids, labels.
//   FNV1_32     FNV-1 32-bit — spch/rdf voice ids. The spch/rdf tooling
//               lowercases before hashing; `hash` prints both when they differ.
//
// unhash hashes every dictionary line under all variants and reports which
// algorithm + dictionary produced each hit, so a bare hash from a decompile,
// a disassembly immediate, or an archive listing can be named without knowing
// which id space it came from. A PathCode base hit resolves the target's own
// extension code, so the reported path carries the right extension even when
// the dictionary line is extensionless.
public static class FoxHashCmd
{
    public static int Hash(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("usage: hash <string> [...]   (prints PathCode64 / StrCode64 / StrCode32 / FNV1_32)");
            return 2;
        }
        foreach (var s in inputs)
        {
            ulong pc  = GameHash.PathCode(s);
            ulong sid = GameHash.StringId(s);
            uint  s32 = (uint)sid;
            uint  fnv = MgsvModBldr.Tools.Spch.HashManager.FNV1Hash32Str(s);
            Console.WriteLine(s);
            Console.WriteLine($"  PathCode64  {pc:x16}  (QAR/PathServer path hash)");
            Console.WriteLine($"  StrCode64   {sid:x12}      (Fox StringId, 48-bit)");
            Console.WriteLine($"  StrCode32   {s32:x8}          (Lua GameObject cmd / label id)");
            Console.WriteLine($"  FNV1_32     {fnv:x8}          (spch/rdf voice id)");
            var lower = s.ToLowerInvariant();
            if (lower != s)
                Console.WriteLine($"  FNV1_32     {MgsvModBldr.Tools.Spch.HashManager.FNV1Hash32Str(lower):x8}          (lowercased, spch/rdf convention)");
        }
        return 0;
    }

    public static int Unhash(string[] args)
    {
        var targets    = new List<(ulong value, string display)>();
        var extraDicts = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "-d" or "--dict")
            {
                if (i + 1 >= args.Length) { Console.Error.WriteLine("FOXDIE: -d needs a file argument."); return 2; }
                extraDicts.Add(args[++i]);
                continue;
            }
            var t = args[i].Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            if (!ulong.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v))
            {
                Console.Error.WriteLine($"FOXDIE: not a hex hash: {args[i]}");
                return 2;
            }
            targets.Add((v, args[i]));
        }
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("usage: unhash <hex-hash> [...] [-d extra_dictionary.txt]");
            return 2;
        }

        var files = DictFiles(extraDicts);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("FOXDIE: no dictionaries found (expected dict/*.txt next to the exe).");
            return 2;
        }

        // (target, algorithm) -> first "string  (dictionary)" hit. Kept per
        // algorithm so collisions across id spaces stay visible.
        var found = new Dictionary<(ulong, string), string>();
        long tested = 0;
        void Add(ulong t, string algo, string str, string dict)
            => found.TryAdd((t, algo), $"\"{str}\"  ({dict})");

        foreach (var f in files)
        {
            var fname = Path.GetFileName(f);
            foreach (var raw in File.ReadLines(f))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                tested++;
                ulong pc  = GameHash.PathCode(line);
                ulong sid = GameHash.StringId(line);
                uint  s32 = (uint)sid;
                uint  fnv = MgsvModBldr.Tools.Spch.HashManager.FNV1Hash32Str(line);
                foreach (var (t, _) in targets)
                {
                    if (t == pc)
                        Add(t, "PathCode64", line, fname);
                    else if (t > uint.MaxValue &&
                             (t & GameHash.PATH_CODE_BASE_MASK) == (pc & GameHash.PATH_CODE_BASE_MASK))
                    {
                        var ext = QarDictionary.ExtensionFor(GameHash.ExtCodeOf(t));
                        Add(t, "PathCode64 base", line + (ext ?? $"  [ext code {GameHash.ExtCodeOf(t):x} unknown]"), fname);
                    }
                    if (t == sid) Add(t, "StrCode64", line, fname);
                    if (t <= uint.MaxValue)
                    {
                        if ((uint)t == s32) Add(t, "StrCode32", line, fname);
                        if ((uint)t == fnv) Add(t, "FNV1_32", line, fname);
                    }
                }
            }
        }

        int misses = 0;
        foreach (var (t, disp) in targets)
        {
            Console.WriteLine(disp);
            var hits = found.Where(kv => kv.Key.Item1 == t).ToList();
            if (hits.Count == 0)
            {
                Console.WriteLine("  no match");
                misses++;
            }
            else
                foreach (var kv in hits)
                    Console.WriteLine($"  {kv.Key.Item2,-15} {kv.Value}");
        }
        Console.WriteLine($"\nsearched {tested:N0} strings across {files.Count} dictionaries");
        return misses == 0 ? 0 : 1;
    }

    private static List<string> DictFiles(IEnumerable<string> extras)
    {
        var files = new List<string>();
        foreach (var dir in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dict"),
            Path.Combine(Directory.GetCurrentDirectory(), "dict"),
        })
            if (Directory.Exists(dir))
            {
                files.AddRange(Directory.GetFiles(dir, "*.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                break;
            }
        foreach (var e in extras)
        {
            if (!File.Exists(e)) Console.Error.WriteLine($"FOXDIE: extra dictionary not found, skipping: {e}");
            else files.Add(e);
        }
        return files;
    }
}
