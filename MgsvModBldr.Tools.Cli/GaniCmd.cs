// ganis verb: name the animations inside an mtar
using MgsvModBldr.Tools.GameHashing;
using MgsvModBldr.Tools.Index;

namespace MgsvModBldr.Tools.Cli;

// ganis <file.mtar> [-d <dictionary.txt>]
//
// Lists an mtar's animations with their real paths. Works for GZ as well as TPP:
// the lookup key is the entry hash masked to 50 bits, so it is independent of the
// extension code (TPP tags a gani 8074, GZ tags it 22).
internal static class GaniCmd
{
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("usage: ganis <file.mtar> [-d <dictionary.txt>] [--mtp-names]");
            return 2;
        }
        var mtar = args[1];

        // --mtp-names: harvest the literal MTP_* strings the archive carries and hash them
        // forward. The engine has no reader for GZ's name table, so its layout cannot be checked
        // against anything — but a string plus its own StrCode32 needs no layout at all, and the
        // result is verifiable: hash the name, compare to the hash the archive actually uses.
        if (Array.IndexOf(args, "--mtp-names") > 0)
        {
            var raw = File.ReadAllBytes(mtar);
            var names = new SortedDictionary<uint, string>();
            for (int i = 0; i + 4 < raw.Length; i++)
            {
                if (raw[i] != 'M' || raw[i + 1] != 'T' || raw[i + 2] != 'P' || raw[i + 3] != '_') continue;
                int e = i;
                while (e < raw.Length && (char.IsLetterOrDigit((char)raw[e]) || raw[e] == '_')) e++;
                var nm = System.Text.Encoding.ASCII.GetString(raw, i, e - i);
                names[(uint)GameHash.StringId(nm)] = nm;
                i = e;
            }
            foreach (var kv in names) Console.WriteLine($"{kv.Key:x8}	{kv.Value}");
            Console.Error.WriteLine($"{names.Count} motion-point names");
            return 0;
        }
        string dictPath = null;
        for (int i = 2; i < args.Length - 1; i++)
            if (args[i] is "-d" or "--dict") dictPath = args[i + 1];
        dictPath ??= Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt");

        if (args.Contains("--probehash")) return ProbeHash(mtar, dictPath);
        if (args.Contains("--probe")) return Probe(mtar, dictPath);

        var map = MtarGaniNames.LoadDictionary(dictPath);
        if (map.Count == 0)
        {
            Console.Error.WriteLine($"FOXDIE: no gani names loaded from {dictPath}");
            return 2;
        }

        var b = File.ReadAllBytes(mtar);
        if (b.Length < 0x20) { Console.Error.WriteLine("FOXDIE: not an mtar"); return 2; }
        uint count = BitConverter.ToUInt32(b, 4);
        int stride = EntryStride(b);
        if (count == 0 || 0x20 + (long)count * stride > b.Length)
        { Console.Error.WriteLine($"FOXDIE: entry table doesn't fit ({count} entries)"); return 2; }

        int named = 0;
        var exts = new SortedDictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            ulong h = BitConverter.ToUInt64(b, 0x20 + i * stride);
            bool isGz = MtarGaniNames.IsGzLayout(h);
            int ext = isGz ? MtarGaniNames.GzTypeId(h) : MtarGaniNames.ExtensionCode(h);
            exts[ext] = exts.GetValueOrDefault(ext) + 1;

            var key = MtarGaniNames.NameHash(h);
            if (map.TryGetValue(key, out var path)) { Console.WriteLine($"{path}.gani"); named++; }
            else Console.WriteLine($"({key:x}).gani");
        }
        Console.Error.WriteLine($"{named} of {count} named ({(double)named / count:P1}) · "
                              + $"ext codes: {string.Join(", ", exts.Select(kv => $"{kv.Key}x{kv.Value}"))} · "
                              + $"dictionary {map.Count:N0} names");
        return named > 0 ? 0 : 1;
    }

    // Try a range of path NORMALISATIONS against the mtar's hashes and report which
    // (if any) produce hits. The hash function is verified against TPP at 100%, so a
    // normalisation that matches is the right one — this is a search for how GZ
    // spells its paths, not for the algorithm.
    private static int Probe(string mtar, string dictPath)
    {
        var b = File.ReadAllBytes(mtar);
        uint count = BitConverter.ToUInt32(b, 4);
        int stride = EntryStride(b);
        var targets = new HashSet<ulong>();
        bool gz = false;
        for (int i = 0; i < count && 0x20 + (long)i * stride + 8 <= b.Length; i++)
        {
            ulong h = BitConverter.ToUInt64(b, 0x20 + i * stride);
            gz |= MtarGaniNames.IsGzLayout(h);
            targets.Add(MtarGaniNames.NameHash(h));
        }
        // GZ and TPP hash names with DIFFERENT functions, not just different masks.
        Func<string, ulong> hash = gz ? MtarGaniNames.GzHash
                                      : p => MtarGaniNames.Hash(p, MtarGaniNames.NameMask);

        var lines = File.ReadAllLines(dictPath).Where(l => l.Length > 0).ToArray();
        Console.WriteLine($"{targets.Count:N0} distinct hashes vs {lines.Length:N0} dictionary paths"
                        + $"  [{(gz ? "GZ 16/48" : "TPP 13/50")} split]\n");

        // Each variant rewrites a dictionary path; MtarGaniNames.Hash then strips
        // "/Assets/" and the extension itself, so variants work in that space.
        var variants = new (string name, Func<string, string> f)[]
        {
            ("as-is",                 p => p),
            ("lowercased",            p => p.ToLowerInvariant()),
            ("tpp -> gz",             p => p.Replace("/tpp/", "/gz/")),
            ("SI_game -> SI_demo",    p => p.Replace("SI_game", "SI_demo")),
            ("leaf only",             p => p[(p.LastIndexOf('/') + 1)..]),
            ("gz_ on leaf",           p => Leaf(p, n => "gz_" + n)),
            ("_gz on leaf",           p => Leaf(p, n => n + "_gz")),
            ("gz prefix on path",     p => "gz_" + p.TrimStart('/')),
            ("no Assets strip",       p => "/Assets" + p),          // double so strip leaves one
            ("motion/ dropped",       p => p.Replace("/motion/", "/")),
            ("fani -> gani",          p => p.Replace("/fani/", "/gani/")),
            ("bodies -> skl/bodies",  p => p.Replace("/bodies/", "/skl/bodies/")),
            // Hash() strips ONE extension, so doubling leaves one in place — this is
            // how we test "GZ hashes the path WITH .gani still on it".
            ("keep .gani",            p => p + ".gani.gani"),
            ("keep .gani, no strip",  p => "/Assets" + p + ".gani.gani"),
            ("backslashes",           p => p.Replace('/', '\\') + ".x"),
            ("trailing slash gone",   p => p.TrimEnd('/') + ".x"),
            // GZ spells its paths /as/… where TPP says /Assets/… — the reason the
            // /as/ decoder exists. Hash() strips one /Assets/, so double where needed.
            ("/as/ form",             p => "/Assets" + AsForm(p)),
            ("/as/ form, no slash",   p => "/Assets" + AsForm(p).TrimStart('/')),
            ("/as/ + .gani",          p => "/Assets" + AsForm(p) + ".gani.gani"),
            ("as/ bare",              p => "/Assets/as/" + Stripped(p)),
        };

        foreach (var (name, f) in variants)
        {
            int hits = 0;
            string example = null;
            foreach (var line in lines)
            {
                string cand;
                try { cand = f(line); } catch { continue; }
                if (targets.Contains(hash(cand))) { hits++; example ??= cand; }
            }
            Console.WriteLine($"  {name,-22} {hits,6} hit(s){(example is null ? "" : $"   e.g. {example}")}");
        }
        return 0;
    }

    // Probe the HASH FUNCTION rather than the path spelling. Used once the string
    // variants are exhausted: if GZ's mtar hashes aren't TPP's masked
    // CityHash64WithSeeds at all, no amount of renaming will ever land.
    private static int ProbeHash(string mtar, string dictPath)
    {
        var b = File.ReadAllBytes(mtar);
        uint count = BitConverter.ToUInt32(b, 4);
        int stride = EntryStride(b);
        var targets = new HashSet<ulong>();
        bool gz = false;
        for (int i = 0; i < count && 0x20 + (long)i * stride + 8 <= b.Length; i++)
        {
            ulong h = BitConverter.ToUInt64(b, 0x20 + i * stride);
            gz |= MtarGaniNames.IsGzLayout(h);
            targets.Add(MtarGaniNames.NameHash(h));
        }
        ulong mask = gz ? MtarGaniNames.GzNameMask : MtarGaniNames.NameMask;

        var lines = File.ReadAllLines(dictPath).Where(l => l.Length > 0).ToArray();
        Console.WriteLine($"{targets.Count:N0} hashes vs {lines.Length:N0} paths  "
                        + $"[{(gz ? "GZ 16/48" : "TPP 13/50")} split]\n");

        static string Strip(string p)
        {
            var s = p.Replace('\\', '/');
            int dot = s.LastIndexOf('.'), sl = s.LastIndexOf('/');
            if (dot > sl) s = s[..dot];
            foreach (var pre in new[] { "/Assets/", "Assets/" })
                if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { s = s[pre.Length..]; break; }
            return s;
        }
        static ulong Seeded(string t, bool reversed)
        {
            var sb = new byte[sizeof(ulong)];
            if (reversed) for (int i = t.Length - 1, j = 0; i >= 0 && j < 8; i--, j++) sb[j] = (byte)t[i];
            else for (int i = Math.Max(0, t.Length - 8), j = 0; i < t.Length && j < 8; i++, j++) sb[j] = (byte)t[i];
            return MgsvModBldr.Tools.GameHashing.GameCityHash.CityHash64WithSeeds(
                t.AsSpan(), 0x9ae16a3b2f90404f, BitConverter.ToUInt64(sb, 0));
        }

        var fns = new (string name, Func<string, ulong> f)[]
        {
            ("cityhash seeds, reversed", p => Seeded(Strip(p), true)),
            ("cityhash seeds, forward",  p => Seeded(Strip(p), false)),
            ("cityhash seeds, seed1=0",  p => MgsvModBldr.Tools.GameHashing.GameCityHash
                                                .CityHash64WithSeeds(Strip(p).AsSpan(), 0x9ae16a3b2f90404f, 0)),
            ("GameHash.PathCode",        p => MgsvModBldr.Tools.GameHashing.GameHash.PathCode(Strip(p).AsSpan())),
            ("GameHash.PathCode +ext",   p => MgsvModBldr.Tools.GameHashing.GameHash.PathCode(p.AsSpan())),
            ("GameHash.StringId",        p => MgsvModBldr.Tools.GameHashing.GameHash.StringId(Strip(p).AsSpan())),
            ("GameHash.StringId leaf",   p => MgsvModBldr.Tools.GameHashing.GameHash.StringId(
                                                Strip(p)[(Strip(p).LastIndexOf('/') + 1)..].AsSpan())),
        };

        foreach (var (name, f) in fns)
        {
            int hits = 0; string example = null;
            foreach (var line in lines)
            {
                ulong h;
                try { h = f(line) & mask; } catch { continue; }
                if (targets.Contains(h)) { hits++; example ??= line; }
            }
            Console.WriteLine($"  {name,-26} {hits,6} hit(s){(example is null ? "" : $"   e.g. {example}")}");
        }
        return 0;
    }

    /// <summary>
    /// Entry-table stride. A type-1 mtar keeps 16-byte entries (hash/offset/size); a
    /// type-2 keeps 32 (it adds size2, an exchunk and an endchunk). Type is told by the
    /// first entry's payload magic, exactly as MtarConverter.GetMtarType does it —
    /// reading a type-2 table at 16 bytes yields twice as many "entries", every second
    /// one being the back half of a real record.
    /// </summary>
    private static int EntryStride(byte[] b)
    {
        if (b.Length < 0x2C) return 32;
        uint firstOffset = BitConverter.ToUInt32(b, 0x28);
        if (firstOffset + 4 > (uint)b.Length) return 32;
        return BitConverter.ToUInt32(b, (int)firstOffset) == 0x0BFCA2D2u ? 16 : 32;
    }

    /// <summary>"/Assets/tpp/motion/x" -> "tpp/motion/x".</summary>
    private static string Stripped(string p)
    {
        var s = p.Replace('\\', '/');
        foreach (var pre in new[] { "/Assets/", "Assets/" })
            if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return s[pre.Length..];
        return s.TrimStart('/');
    }

    /// <summary>"/Assets/tpp/motion/x" -> "/as/tpp/motion/x".</summary>
    private static string AsForm(string p) => "/as/" + Stripped(p);

    private static string Leaf(string p, Func<string, string> f)
    {
        int i = p.LastIndexOf('/');
        return i < 0 ? f(p) : p[..(i + 1)] + f(p[(i + 1)..]);
    }
}
