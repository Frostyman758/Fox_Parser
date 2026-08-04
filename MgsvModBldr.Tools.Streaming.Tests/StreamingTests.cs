// Streaming tool regression gate
using MgsvModBldr.Tools.G0s;
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Qar;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;

namespace MgsvModBldr.Tools.Streaming.Tests;

// Two things must hold for the splice engine:
//   1. a NO-OP splice preserves every entry (hash set + decoded bytes),
//   2. a REPLACE swaps exactly one entry and leaves the rest untouched.
// Plus: ArchiveStream must hand back the same bytes as a plain decode.
// Archives are copied to temp first — the game files are never written to.
public sealed class StreamingTests : IToolTests
{
    public string Name => "streaming";

    private static readonly string[] SampleDirs =
    {
        @"Z:\master",
        @"C:\Program Files (x86)\Steam\steamapps\common\MGS_TPP - Copy\master",
    };
    private const int MaxSamples = 2;
    private const long MaxSampleBytes = 400L << 20;  // splices copy the whole archive
    private const int ProbeEntries = 24;             // entries decoded per gate

    public void Harvest() { }

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Streaming (splice preserves entries; stream == decode) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no .dat fixtures found; attach Z:\\ or a packed master\\ copy)");
            return (0, 0);
        }
        var (p, f) = RunParallel(samples, Gate);
        // The read-only gates never copy the archive, so they can use the big chunk
        // archives the splice gate has to skip — which is where the real packs are.
        var readSamples = DiscoverReadSamples();
        var (p2, f2) = RunParallel(readSamples, PrefixGate);
        var (p3, f3) = RunParallel(readSamples, PackIndexGate);
        var (p4, f4) = RunParallel(readSamples, PftxsIndexGate);
        var (p5, f5) = RunParallel(readSamples, SbpIndexGate);
        var (p6, f6) = RunParallel(readSamples, NestedMtarGate);
        var (p7, f7) = RunParallel(DiscoverG0s(), G0sRangeGate);
        var (p8, f8) = RunParallel(DiscoverG0s(), GzContainerGate);
        return (p + p2 + p3 + p4 + p5 + p6 + p7 + p8, f + f2 + f3 + f4 + f5 + f6 + f7 + f8);
    }

    // GZ containers, found by MAGIC rather than by name. This is the non-dehashed
    // case in its purest form: a .g0s entry carries only a hash, so unless the
    // dictionary resolves it there is no ".fpk" to match on — but 16 bytes settle it.
    // Each hit is then indexed and checked against the reference reader's full parse.
    private static (bool ok, string note) GzContainerGate(string g0s)
    {
        try
        {
            var gr = new GzReader(g0s);
            using var fs = File.OpenRead(g0s);
            int fpks = 0, tex = 0, unnamed = 0;
            long indexBytes = 0, fullBytes = 0;

            foreach (var e in gr.Entries)
            {
                if (fpks >= 12 && tex >= 12) break;
                if (e.Size < ContainerKind.SniffBytes) continue;

                var src = RangeSources.ForG0s(e, fs);
                var kind = ContainerKind.Detect(src);
                if (kind is not (Container.GzFpk or Container.GzFpkd or Container.GzPftxs)) continue;
                if (!G0sHash.TryResolve(e.Hash, out _)) unnamed++;

                long plain = RangeSources.PlainSize(e, fs);
                var full = gr.ReadDecoded(e);
                fullBytes += full.Length;

                if (kind is Container.GzFpk or Container.GzFpkd)
                {
                    if (fpks >= 12) continue;
                    var idx = GzFpkIndex.Read(src, plain, out int used);
                    if (idx is null) return (false, $"{e.Hash:x16}: GZ fpk index would not parse");
                    indexBytes += used;

                    var reference = MgsvModBldr.Tools.Fpk.Gz.GzFpkFile.Read(new MemoryStream(full, false));
                    if (idx.Count != reference.Entries.Count)
                        return (false, $"{e.Hash:x16}: {idx.Count} entries vs {reference.Entries.Count}");
                    fpks++;
                }
                else
                {
                    if (tex >= 12) continue;
                    var idx = GzPftxsIndex.Read(src, plain, out int used);
                    if (idx is null) return (false, $"{e.Hash:x16}: GZ pftxs index would not parse");
                    indexBytes += used;

                    // NB: the reference reader's Files list is the FLATTENED .ftex +
                    // .ftexs expansion, not the index — so compare against a walk of
                    // the same index layout over the fully decoded buffer instead.
                    var want = GzPftxsIndex.Read(RangeSources.ForBytes(full), full.Length, out _);
                    if (want is null) return (false, $"{e.Hash:x16}: full-buffer walk rejected a GZ pftxs");
                    if (idx.Count != want.Count)
                        return (false, $"{e.Hash:x16}: {idx.Count} textures vs {want.Count}");
                    for (int k = 0; k < idx.Count; k++)
                        if (idx[k].Name != want[k].Name || idx[k].Size != want[k].Size)
                            return (false, $"{e.Hash:x16}: texture {k} differs");
                    tex++;
                }
            }

            if (fpks + tex == 0) return (true, "no GZ fpk/pftxs containers in this archive");
            double ratio = fullBytes == 0 ? 1 : (double)indexBytes / fullBytes;
            return (true, $"{fpks} GZ fpk + {tex} GZ pftxs found BY MAGIC ({unnamed} unnamed by dictionary): "
                        + $"counts match reference; {indexBytes / 1024} KB index vs {fullBytes / 1024} KB full ({ratio:P2})");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static readonly string[] G0sDirs =
    {
        @"C:\Program Files (x86)\Steam\steamapps\common\Metal Gear Solid Ground Zeroes",
    };

    private static List<string> DiscoverG0s()
    {
        var found = new List<string>();
        foreach (var dir in G0sDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in EnumerateSafe(dir, "*.g0s"))
                if (ArchiveFormat.Detect(f) == FoxArchiveKind.G0s) found.Add(f);   // data_00 is a .wmv
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Both .g0s cipher layers are position-dependent, so ranges are the risky case:
    // the outer keystream is computed straight from the block index, the inner one is
    // wound forward. Prefixes AND mid-blob ranges must match the full decode.
    private static (bool ok, string note) G0sRangeGate(string g0s)
    {
        try
        {
            var gr = new GzReader(g0s);
            using var fs = File.OpenRead(g0s);
            int checkedCount = 0, innerCount = 0;

            var all = gr.Entries;
            int step = Math.Max(1, all.Count / 40);
            for (int i = 0; i < all.Count && checkedCount < 40; i += step)
            {
                var e = all[i];
                var full = gr.ReadDecoded(e);
                if (full.Length < 64) continue;
                if (e.Size != full.Length) innerCount++;          // 8-byte inner prefix stripped

                foreach (var (start, len) in new (long, int)[]
                         { (0, 8), (0, 1024), (16, 64), (full.Length / 2, 256), (full.Length - 32, 32) })
                {
                    if (start < 0 || start + len > full.Length) continue;
                    var got = G0sRangeReader.Read(e, fs, start, len);
                    if (got.Length != len)
                        return (false, $"{e.Hash:x16}: range({start},{len}) gave {got.Length}");
                    if (!got.AsSpan().SequenceEqual(full.AsSpan((int)start, len)))
                    {
                        int at = 0;
                        while (at < len && got[at] == full[start + at]) at++;
                        return (false, $"{e.Hash:x16} [size={e.Size} plain={full.Length}]: range({start},{len}) diverges at {at}");
                    }
                }
                checkedCount++;
            }
            return checkedCount == 0
                ? (false, "no entries could be decoded")
                : (true, $"range == full decode on {checkedCount} entries ({innerCount} inner-ciphered)");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // .sbp states its own index size in the header, so one prefix read covers it.
    private static (bool ok, string note) SbpIndexGate(string dat)
    {
        try
        {
            var qr = new QarReader(dat);
            using var fs = File.OpenRead(dat);
            var banks = Containers(qr, ".sbp");
            if (banks.Count == 0) return (true, "no sbp entries in this archive");

            long fullBytes = 0, headBytes = 0, slots = 0;
            foreach (var e in banks)
            {
                var full = qr.ReadDecoded(e);
                fullBytes += full.Length;
                var want = WalkSbp(full);

                var idx = SbpIndex.Read(RangeSources.ForQar(e, fs), RangeSources.PlainSize(e), out int used);
                if ((idx is null) != (want is null))
                    return (false, $"{e.Header.PathHash:x16}: ranged/full disagree on whether this is an sbp");
                if (idx is null) continue;
                headBytes += used;
                if (idx.Count != want.Count)
                    return (false, $"{e.Header.PathHash:x16}: {idx.Count} slots vs {want.Count}");
                for (int k = 0; k < want.Count; k++)
                    if (idx[k].Offset != want[k].off || idx[k].Size != want[k].size || idx[k].Magic != want[k].magic)
                        return (false, $"{e.Header.PathHash:x16}: slot {k} differs");
                slots += idx.Count;
            }
            double ratio = fullBytes == 0 ? 1 : (double)headBytes / fullBytes;
            return (true, $"{banks.Count} sbp / {slots} slots: identical; "
                        + $"{headBytes / 1024} KB read vs {fullBytes / 1024} KB full ({ratio:P2} of the bytes)");
        }
        catch (Exception ex) { return (false, $"exception: {ex.GetType().Name}: {ex.Message}"); }
    }

    // .mtar is never a top-level dat entry — it lives inside an .fpk. This walks the
    // whole stack: fpk index by prefix, then the mtar's table by ranged read inside
    // that pack, and checks it against the pack decoded in full.
    private static (bool ok, string note) NestedMtarGate(string dat)
    {
        try
        {
            var qr = new QarReader(dat);
            using var fs = File.OpenRead(dat);
            int packsSeen = 0, mtars = 0, ganis = 0;

            foreach (var e in Containers(qr, ".fpk", 600))
            {
                if (mtars >= 8) break;
                var idx = FpkIndex.Read(RangeSources.ForQar(e, fs), RangeSources.PlainSize(e), out _);
                if (idx is null) continue;
                var inner = idx.FirstOrDefault(x => x.Path.EndsWith(".mtar", StringComparison.OrdinalIgnoreCase));
                if (inner is null) continue;
                packsSeen++;

                var packBytes = qr.ReadDecoded(e);            // reference: whole pack
                if (inner.DataOffset + inner.DataSize > packBytes.Length) continue;
                var mtarBytes = new byte[inner.DataSize];
                Array.Copy(packBytes, inner.DataOffset, mtarBytes, 0, inner.DataSize);

                var want = MtarIndex.Read(RangeSources.ForBytes(mtarBytes), mtarBytes.Length, out _);
                var got = MtarIndex.Read(
                    RangeSources.Slice(RangeSources.ForQar(e, fs), inner.DataOffset, inner.DataSize),
                    inner.DataSize, out _);

                if ((got is null) != (want is null))
                    return (false, $"{inner.Path}: ranged and in-memory walks disagree on format");
                if (got is null) continue;
                if (got.Count != want.Count)
                    return (false, $"{inner.Path}: {got.Count} ganis vs {want.Count}");
                for (int k = 0; k < want.Count; k++)
                    if (got[k] != want[k])
                        return (false, $"{inner.Path}: gani {k} differs ({got[k]} vs {want[k]})");
                mtars++; ganis += got.Count;
            }
            return mtars == 0
                ? (true, "no .mtar found inside the sampled packs")
                : (true, $"{mtars} nested mtar / {ganis} ganis via fpk-index + ranged read: identical");
        }
        catch (Exception ex) { return (false, $"exception: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static byte[] Slice(byte[] b, long off, int len)
    {
        if (off < 0 || off + len > b.Length) return null;
        var o = new byte[len];
        Array.Copy(b, off, o, 0, len);
        return o;
    }

    private static List<QarEntry> Containers(QarReader qr, string ext, int cap = PackSamples)
    {
        var dict = QarDictionary.Load();
        var found = new List<QarEntry>();
        foreach (var e in qr.Entries)
        {
            var n = e.Header.FilePath;
            if (string.IsNullOrEmpty(n)) { var r = dict.Resolve(e.Header.PathHash, out bool ok); if (ok) n = r; }
            if (!string.IsNullOrEmpty(n) && n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) found.Add(e);
            if (found.Count >= cap) break;
        }
        return found;
    }

    private static List<(string magic, uint off, int size)> WalkSbp(byte[] p)
    {
        if (p.Length < 8 || BitConverter.ToUInt32(p, 0) != 0x4C504253u) return null;
        int count = p[4];
        var outp = new List<(string, uint, int)>(count);
        for (int i = 0; i < count; i++)
        {
            int at = 8 + i * 12;
            if (at + 12 > p.Length) break;
            outp.Add((System.Text.Encoding.ASCII.GetString(p, at, 4).TrimEnd('\0'),
                      BitConverter.ToUInt32(p, at + 4), BitConverter.ToInt32(p, at + 8)));
        }
        return outp;
    }

    // A pftxs interleaves its group tables with the texture blobs, so PftxsIndex
    // hops between them with ranged reads. Same contract as the pack gate: the
    // pieces it finds must match a walk of the fully decoded entry, exactly.
    private static (bool ok, string note) PftxsIndexGate(string dat)
    {
        try
        {
            var qr = new QarReader(dat);
            var dict = QarDictionary.Load();
            using var fs = File.OpenRead(dat);

            var texPacks = new List<QarEntry>();
            foreach (var e in qr.Entries)
            {
                var n = e.Header.FilePath;
                if (string.IsNullOrEmpty(n)) { var r = dict.Resolve(e.Header.PathHash, out bool ok2); if (ok2) n = r; }
                if (!string.IsNullOrEmpty(n) && n.EndsWith(".pftxs", StringComparison.OrdinalIgnoreCase))
                    texPacks.Add(e);
                if (texPacks.Count >= PackSamples) break;
            }
            if (texPacks.Count == 0) return (true, "no pftxs entries in this archive");

            long fullBytes = 0, headBytes = 0, pieces = 0;
            long allocFull = GC.GetAllocatedBytesForCurrentThread();
            var swFull = System.Diagnostics.Stopwatch.StartNew();
            var reference = new List<List<(ulong hash, int size)>>(texPacks.Count);
            foreach (var e in texPacks)
            {
                var bytes = qr.ReadDecoded(e);
                fullBytes += bytes.Length;
                reference.Add(WalkPftxs(bytes));
            }
            swFull.Stop();
            allocFull = GC.GetAllocatedBytesForCurrentThread() - allocFull;

            long allocHead = GC.GetAllocatedBytesForCurrentThread();
            var swHead = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < texPacks.Count; i++)
            {
                var idx = PftxsIndex.Read(RangeSources.ForQar(texPacks[i], fs), RangeSources.PlainSize(texPacks[i]), out int used);
                var want = reference[i];
                if (idx is null)
                {
                    if (want is null) continue;             // both say "not a TPP pftxs"
                    return (false, $"{texPacks[i].Header.PathHash:x16}: ranged walk rejected an entry the full walk parsed");
                }
                if (want is null) return (false, $"{texPacks[i].Header.PathHash:x16}: ranged walk parsed a non-pftxs");
                headBytes += used;
                if (idx.Count != want.Count)
                    return (false, $"{texPacks[i].Header.PathHash:x16}: {idx.Count} pieces vs {want.Count}");
                for (int k = 0; k < want.Count; k++)
                    if (idx[k].Hash != want[k].hash || idx[k].Size != want[k].size)
                        return (false, $"{texPacks[i].Header.PathHash:x16}: piece {k} {idx[k].Hash:x16}/{idx[k].Size} != {want[k].hash:x16}/{want[k].size}");
                pieces += idx.Count;
            }
            swHead.Stop();
            allocHead = GC.GetAllocatedBytesForCurrentThread() - allocHead;

            double ratio = fullBytes == 0 ? 1 : (double)headBytes / fullBytes;
            return (true, $"{texPacks.Count} pftxs / {pieces} pieces: identical; "
                        + $"{swHead.ElapsedMilliseconds} ms / {headBytes / 1024} KB read / {allocHead / 1024} KB alloc vs "
                        + $"{swFull.ElapsedMilliseconds} ms / {fullBytes / 1024} KB read / {allocFull / 1024} KB alloc "
                        + $"({ratio:P2} of the bytes, {(allocFull == 0 ? 1 : (double)allocHead / allocFull):P2} of the allocation)");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Reference walk over the fully decoded pftxs — what the ranged reader must match.
    private static List<(ulong hash, int size)> WalkPftxs(byte[] p)
    {
        if (p.Length < 32 || BitConverter.ToUInt32(p, 0) != 0x58544650u) return null;
        var outp = new List<(ulong, int)>();
        int groupCount = BitConverter.ToInt32(p, 24);
        long gpos = 32;
        for (int g = 0; g < groupCount; g++)
        {
            if (gpos + 32 > p.Length) break;
            if (BitConverter.ToUInt32(p, (int)gpos) != 0x58455446u) break;
            uint groupSize = BitConverter.ToUInt32(p, (int)gpos + 4);
            int count = BitConverter.ToInt32(p, (int)gpos + 16);
            for (int i = 0; i < count; i++)
            {
                int at = (int)gpos + 32 + i * 16;
                if (at + 16 > p.Length) return outp;
                outp.Add((BitConverter.ToUInt64(p, at), BitConverter.ToInt32(p, at + 12)));
            }
            if (groupSize == 0) break;
            gpos += groupSize;
        }
        return outp;
    }

    // The actual use case: read a nested pack's index without inflating its payload.
    // Correctness = the prefix yields the SAME fpk entry list as the full decode.
    // Also reports what that saves, which is the whole point of the exercise.
    private static (bool ok, string note) PackIndexGate(string dat)
    {
        try
        {
            var qr = new QarReader(dat);
            var dict = QarDictionary.Load();
            using var fs = File.OpenRead(dat);

            var packs = new List<QarEntry>();
            foreach (var e in qr.Entries)
            {
                var n = e.Header.FilePath;
                if (string.IsNullOrEmpty(n)) { var r = dict.Resolve(e.Header.PathHash, out bool ok2); if (ok2) n = r; }
                if (string.IsNullOrEmpty(n)) continue;
                if (n.EndsWith(".fpk", StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith(".fpkd", StringComparison.OrdinalIgnoreCase)) packs.Add(e);
                if (packs.Count >= PackSamples) break;
            }
            if (packs.Count == 0) return (true, "no fpk entries in this archive");

            long fullBytes = 0, headBytes = 0;
            long allocFull = GC.GetAllocatedBytesForCurrentThread();
            var swFull = System.Diagnostics.Stopwatch.StartNew();
            var fullIndex = new List<List<(string path, uint size)>>(packs.Count);
            foreach (var e in packs)
            {
                var bytes = qr.ReadDecoded(e);
                fullBytes += bytes.Length;
                fullIndex.Add(ListFpk(bytes));
            }
            swFull.Stop();
            allocFull = GC.GetAllocatedBytesForCurrentThread() - allocFull;

            long allocHead = GC.GetAllocatedBytesForCurrentThread();
            var swHead = System.Diagnostics.Stopwatch.StartNew();
            int widened = 0;
            var reasons = new Dictionary<string, int>();
            for (int i = 0; i < packs.Count; i++)
            {
                var src = RangeSources.ForQar(packs[i], fs);
                var idx = FpkIndex.Read(src, RangeSources.PlainSize(packs[i]), out int used);
                bool exact = idx is not null; var why = exact ? "Exact" : "Failed";
                if (idx is null)
                    return (false, $"pack {packs[i].Header.PathHash:x16}: index would not parse ({why})");
                headBytes += used;
                reasons[why] = reasons.GetValueOrDefault(why) + 1;
                if (!exact) widened++;

                var want = fullIndex[i];
                if (idx.Count != want.Count)
                    return (false, $"pack {packs[i].Header.PathHash:x16}: index has {idx.Count} entries, full has {want.Count}");
                for (int k = 0; k < want.Count; k++)
                    if (idx[k].Path != want[k].path || idx[k].DataSize != want[k].size)
                        return (false, $"pack {packs[i].Header.PathHash:x16}: entry {k} '{idx[k].Path}'/{idx[k].DataSize} != '{want[k].path}'/{want[k].size}");
            }
            swHead.Stop();
            allocHead = GC.GetAllocatedBytesForCurrentThread() - allocHead;

            double ratio = fullBytes == 0 ? 1 : (double)headBytes / fullBytes;
            var breakdown = string.Join(" ", reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
            return (true, $"{packs.Count} packs [{breakdown}]: index identical; "
                        + $"{swHead.ElapsedMilliseconds} ms / {headBytes / 1024} KB read / {allocHead / 1024} KB alloc vs "
                        + $"{swFull.ElapsedMilliseconds} ms / {fullBytes / 1024} KB read / {allocFull / 1024} KB alloc "
                        + $"({ratio:P1} of the bytes, {(allocFull == 0 ? 1 : (double)allocHead / allocFull):P1} of the allocation)");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private const int PackSamples = 40;

    // Reference list via the eager FpkFile — the thing PackIndex must agree with.
    private static List<(string path, uint size)> ListFpk(byte[] bytes)
    {
        var f = new MgsvModBldr.Tools.Fpk.FpkFile();
        using var ms = new MemoryStream(bytes, writable: false);
        f.Read(ms);
        return f.Entries.Select(x => (x.FilePath.Data, x.DataSize)).ToList();
    }

    // QarPrefixReader duplicates Tools.Qar's ciphers on purpose (that library is
    // byte-exact and every other tool sits on it, so it isn't edited to add a
    // partial read). This gate is what makes the clone safe: for real entries —
    // compressed, encrypted, plain — the prefix must equal the first n bytes the
    // untouched QarEntry.ReadData() produces. Drift fails here.
    private static (bool ok, string note) PrefixGate(string dat)
    {
        try
        {
            var qr = new QarReader(dat);
            using var fs = File.OpenRead(dat);
            int checkedCount = 0, compressed = 0, encrypted = 0;

            foreach (var e in Sample(qr))
            {
                var full = qr.ReadDecoded(e);
                if (full.Length == 0) continue;
                foreach (int want in new[] { 8, 1024, 96 * 1024 })
                {
                    int expect = Math.Min(want, full.Length);
                    var head = QarPrefixReader.Read(e, fs, want);
                    string what = $"{e.Header.PathHash:x16} [comp={e.Header.Compressed} enc=0x{e.DataHeader.EncryptionMagic:x} "
                                + $"u={e.Header.UncompressedSize} c={e.Header.CompressedSize} full={full.Length}]";
                    if (head.Length < expect)
                        return (false, $"{what}: prefix({want}) gave {head.Length}, expected {expect}");
                    if (!head.AsSpan(0, expect).SequenceEqual(full.AsSpan(0, expect)))
                    {
                        int at = 0;
                        while (at < expect && head[at] == full[at]) at++;
                        return (false, $"{what}: prefix({want}) diverges at byte {at}");
                    }
                }
                if (e.Header.Compressed) compressed++;
                if (e.DataHeader.EncryptionMagic > 0) encrypted++;
                checkedCount++;
            }
            return checkedCount == 0
                ? (false, "no entries could be decoded")
                : (true, $"prefix == ReadData on {checkedCount} entries ({compressed} compressed, {encrypted} encrypted)");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private const int PrefixSamples = 40;
    private const int PerKind = 12;

    // A spread across the archive, PLUS entries deliberately drawn from each
    // decode path — compressed and encrypted — so a cipher the sweep happened to
    // miss can't sit untested. Retail archives are mostly plain, so the encrypted
    // path only gets covered when an archive actually has one.
    private static IEnumerable<QarEntry> Sample(QarReader qr)
    {
        var all = qr.Entries;
        var picked = new HashSet<ulong>();

        int step = Math.Max(1, all.Count / PrefixSamples);
        for (int i = 0, taken = 0; i < all.Count && taken < PrefixSamples; i += step, taken++)
            if (picked.Add(all[i].Header.PathHash)) yield return all[i];

        int enc = 0, comp = 0;
        foreach (var e in all)
        {
            if (enc >= PerKind && comp >= PerKind) break;
            bool wantEnc = e.DataHeader.EncryptionMagic > 0 && enc < PerKind;
            bool wantComp = e.Header.Compressed && comp < PerKind;
            if (!wantEnc && !wantComp) continue;
            if (!picked.Add(e.Header.PathHash)) continue;
            if (wantEnc) enc++;
            if (wantComp) comp++;
            yield return e;
        }
    }

    private static (bool ok, string note) Gate(string dat)
    {
        var work = MakeTmp("splice_");
        try
        {
            var staged = Path.Combine(work, Path.GetFileName(dat));
            File.Copy(dat, staged, overwrite: true);

            var before = new QarReader(staged);
            var probe = Probe(before);
            var want = new Dictionary<ulong, string>();
            foreach (var h in probe) want[h] = Sha256(before.ReadDecoded(before.Find(h)));

            // 1. no-op splice: same hashes, same bytes.
            QarSplice.Apply(staged, before, new Dictionary<ulong, byte[]>());
            var after = new QarReader(staged);
            if (after.Entries.Count != before.Entries.Count)
                return (false, $"no-op splice changed the entry count ({before.Entries.Count} -> {after.Entries.Count})");
            foreach (var h in probe)
                if (Sha256(after.ReadDecoded(after.Find(h))) != want[h])
                    return (false, $"no-op splice altered entry {h:x16}");

            // 2. stream one entry == decode it.
            ulong first = probe[0];
            if (Sha256(ArchiveStream.Read(staged, first)) != want[first])
                return (false, $"ArchiveStream.Read differs from decode for {first:x16}");

            // 3. replace one entry: it reads back new, the others read back old.
            var payload = System.Text.Encoding.ASCII.GetBytes("streaming-gate payload\n");
            var target = after.Find(first);
            var block = QarEncode.EncodeBlock(
                string.IsNullOrEmpty(target.Header.FilePath) ? $"/{first:x16}.bin" : target.Header.FilePath,
                first, payload, target.Header.Compressed, after.Version);
            QarSplice.Apply(staged, after, new Dictionary<ulong, byte[]> { [first] = block });

            var final = new QarReader(staged);
            if (final.Entries.Count != before.Entries.Count)
                return (false, "replace changed the entry count");
            if (Sha256(final.ReadDecoded(final.Find(first))) != Sha256(payload))
                return (false, "replaced entry did not read back as the new bytes");
            for (int i = 1; i < probe.Count; i++)
                if (Sha256(final.ReadDecoded(final.Find(probe[i]))) != want[probe[i]])
                    return (false, $"replace disturbed entry {probe[i]:x16}");

            return (true, $"{before.Entries.Count:N0} entries, {probe.Count} probed: no-op + replace clean");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    // Spread the probe across the archive so head, middle and tail blocks are covered.
    private static List<ulong> Probe(QarReader qr)
    {
        var all = qr.Entries;
        var picked = new List<ulong>();
        int step = Math.Max(1, all.Count / ProbeEntries);
        for (int i = 0; i < all.Count && picked.Count < ProbeEntries; i += step)
            picked.Add(all[i].Header.PathHash);
        if (all.Count > 0) picked.Add(all[^1].Header.PathHash);
        return picked;
    }

    // Read-only sampling. Size alone is a bad spread: the biggest archives are
    // textures and one chunk, which between them hold no .mtar at all. These named
    // archives cover the container types that matter (mtar lives in chunk0/data1/00),
    // and the largest archive is added for the volume case.
    private static readonly string[] PreferredReads = { "chunk0.dat", "data1.dat", "00.dat" };

    private static List<string> DiscoverReadSamples()
    {
        var all = new List<string>();
        foreach (var dir in SampleDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in EnumerateSafe(dir, "*.dat"))
                if (new FileInfo(f).Length > 0 && ArchiveStream.IsQar(f)) all.Add(f);
            if (all.Count > 0) break;
        }

        var picked = new List<string>();
        foreach (var name in PreferredReads)
        {
            var hit = all.FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) picked.Add(hit);
        }
        all.Sort((a, b) => new FileInfo(b).Length.CompareTo(new FileInfo(a).Length));
        foreach (var f in all)
        {
            if (picked.Count >= ReadSamples) break;
            if (!picked.Contains(f, StringComparer.OrdinalIgnoreCase)) picked.Add(f);
        }
        return picked;
    }

    private const int ReadSamples = 4;

    private static List<string> DiscoverSamples()
    {
        var found = new List<string>();
        foreach (var dir in SampleDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in EnumerateSafe(dir, "*.dat"))
            {
                var len = new FileInfo(f).Length;
                if (len is 0 or > MaxSampleBytes) continue;
                if (!ArchiveStream.IsQar(f)) continue;   // master\ also holds the .wmv movies as .dat
                found.Add(f);
            }
            if (found.Count > 0) break;      // one source is enough
        }
        found.Sort((a, b) => new FileInfo(a).Length.CompareTo(new FileInfo(b).Length));
        return found.Count > MaxSamples ? found.GetRange(0, MaxSamples) : found;
    }
}
