// Streaming tool regression gate
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
        return RunParallel(samples, Gate);
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
