using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace MgsvModBldr.Tools.G0s;

/// <summary>
/// Unpack/repack for GZ QAR (.g0s). Unpack writes the decrypted plaintext
/// files into <c>&lt;name&gt;/</c> (byte-identical to GzsTool 0.2's output)
/// plus a JSON manifest; repack rebuilds a byte-exact .g0s — including
/// re-encrypting the inner-encrypted entries (which GzsTool 0.2 left as a
/// TODO and could not round-trip). The inner key + entry hash are preserved
/// in the manifest. I/O is parallel via positional reads/writes.
/// </summary>
public static class G0sPacker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Unpack(string g0sPath, string? outDir = null)
    {
        outDir ??= DefaultDir(g0sPath);
        Directory.CreateDirectory(outDir);

        G0sArchive arc;
        using (var fs = File.OpenRead(g0sPath))
            arc = G0sArchive.ReadIndex(fs);
        arc.Name = Path.GetFileName(g0sPath);

        using var handle = File.OpenHandle(g0sPath, FileMode.Open, FileAccess.Read,
                                           FileShare.Read, FileOptions.RandomAccess);

        Parallel.ForEach(arc.Entries, e =>
        {
            var raw = new byte[e.Size];
            ReadExactAt(handle, raw, 16L * e.Offset);
            var (data, key) = G0sArchive.Decrypt(raw, e.Offset);

            e.FileNameFound = G0sHash.TryResolve(e.Hash, out var filePath);
            e.FilePath = filePath;
            e.InnerKey = key;

            var dst = Path.Combine(outDir, G0sArchive.OnDiskRelPath(filePath));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, data);
        });

        var manifest = new G0sManifest
        {
            Name = arc.Name,
            Entries = arc.Entries.Select(e => new G0sManifestEntry
            {
                FilePath = e.FilePath,
                Hash     = e.Hash,
                Key      = e.InnerKey,
            }).ToList(),
        };
        var manifestPath = g0sPath + ".json";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts));
        return manifestPath;
    }

    public static string Pack(string manifestPath, string? outFile = null)
    {
        var manifest = JsonSerializer.Deserialize<G0sManifest>(File.ReadAllText(manifestPath), JsonOpts)
                       ?? throw new InvalidDataException("G0s manifest deserialise failed.");
        outFile ??= StripJson(manifestPath);
        var baseDir = ContentDirFor(manifestPath, manifest.Name);

        int n = manifest.Entries.Count;
        var path = new string[n];
        var hash = new ulong[n];
        var key = new uint?[n];
        var offset = new long[n];   // 16-byte units
        var size = new long[n];     // raw blob size

        // Lay out the data region from file sizes alone (deterministic),
        // so we can write all blobs in parallel.
        long pos = 0;
        for (int i = 0; i < n; i++)
        {
            var me = manifest.Entries[i];
            path[i] = Path.Combine(baseDir, G0sArchive.OnDiskRelPath(me.FilePath));
            hash[i] = me.Hash != 0 ? me.Hash : G0sHash.HashFileNameWithExtension(me.FilePath);
            key[i]  = me.Key;
            long plen = new FileInfo(path[i]).Length;
            size[i] = G0sArchive.BlobSize(plen, me.Key.HasValue);
            offset[i] = pos / 16;
            pos += size[i];
            pos = Align16(pos);
        }

        long entryBlockOffset = pos / 16;
        long tableStart = pos;
        pos += (long)n * 16;
        long sizeSumPos = pos;
        pos += 4;
        pos = Align16(pos);
        long footerPos = pos;
        pos += G0sArchive.FooterSize;
        long finalSize = pos;

        // Preallocate (zeros the alignment padding for free).
        using (var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write)) fs.SetLength(finalSize);

        using (var handle = File.OpenHandle(outFile, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Parallel.For(0, n, i =>
            {
                var plaintext = File.ReadAllBytes(path[i]);
                var blob = G0sArchive.Encrypt(plaintext, (uint)offset[i], key[i]);
                RandomAccess.Write(handle, blob, 16L * offset[i]);
            });

            // Entry table + sizeSum + footer (sequential tail).
            var table = new byte[(long)n * 16 <= int.MaxValue ? n * 16 : throw new InvalidDataException("Too many entries.")];
            ulong sizeSum = 0;
            for (int i = 0; i < n; i++)
            {
                var s = table.AsSpan(i * 16, 16);
                BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0, 8), hash[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8, 4), (uint)offset[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12, 4), (uint)size[i]);
                sizeSum += (uint)size[i];
            }
            RandomAccess.Write(handle, table, tableStart);

            Span<byte> sumb = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(sumb, (uint)sizeSum);
            RandomAccess.Write(handle, sumb, sizeSumPos);

            Span<byte> footer = stackalloc byte[G0sArchive.FooterSize];
            BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(0, 4), n);
            BinaryPrimitives.WriteUInt32LittleEndian(footer.Slice(4, 4), G0sArchive.FooterMagic1);
            BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(8, 4), (int)entryBlockOffset);
            BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(12, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(16, 4), G0sArchive.FooterSize);
            RandomAccess.Write(handle, footer, footerPos);
        }
        return outFile;
    }

    private static void ReadExactAt(SafeFileHandle handle, byte[] buf, long pos)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = RandomAccess.Read(handle, buf.AsSpan(read), pos + read);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static long Align16(long v) => (v + 15) & ~15L;

    private static string DefaultDir(string g0sPath) =>
        Path.Combine(Path.GetDirectoryName(g0sPath) ?? ".", Path.GetFileNameWithoutExtension(g0sPath));

    private static string ContentDirFor(string manifestPath, string archiveName)
    {
        var dir = Path.GetDirectoryName(manifestPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(
            string.IsNullOrEmpty(archiveName) ? StripJson(Path.GetFileName(manifestPath)) : archiveName);
        return Path.Combine(dir, stem);
    }

    private static string StripJson(string p) =>
        p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p[..^5] : p + ".out";
}

public sealed class G0sManifest
{
    [JsonPropertyName("type")]    public string Type { get; set; } = "g0s";
    [JsonPropertyName("name")]    public string Name { get; set; } = "";
    [JsonPropertyName("entries")] public List<G0sManifestEntry> Entries { get; set; } = new();
}

public sealed class G0sManifestEntry
{
    [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
    [JsonPropertyName("hash")]     public ulong Hash { get; set; }
    [JsonPropertyName("key")]      public uint? Key { get; set; }
}
