// Based on datfpk cli/main.go ExtractQar/PackQar
using System.Text.Json;
using System.Text.Json.Serialization;
using MgsvModBldr.Core;

namespace MgsvModBldr.Tools.Qar;

public static class QarPacker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Unpack(string qarPath, string? outDir = null, string? dictPath = null)
    {
        var dict = QarDictionary.Load(dictPath);
        outDir ??= DefaultUnpackDir(qarPath);
        Directory.CreateDirectory(outDir);

        if (!IsQarArchive(qarPath))
            return ExtractStandalone(qarPath, outDir, dict);

        var qar = new QarFile();
        qar.ReadFrom(qarPath);
        using var input = File.OpenRead(qarPath);

        var manifest = new QarManifest
        {
            Flags   = qar.Flags,
            Version = qar.Version,
        };

        Parallel.ForEach(qar.Entries, entry =>
        {
            using var per = File.OpenRead(qarPath);
            entry.ReadData(per);

            string path = dict.Resolve(entry.Header.PathHash, out var resolved);
            entry.Header.FilePath = path;

            var rel = path.TrimStart('/').Replace('\\', '/');
            var outFile = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllBytes(outFile, entry.Data);

            lock (manifest)
            {
                manifest.Entries.Add(new QarManifestEntry
                {
                    FilePath   = path,
                    Compressed = entry.Header.Compressed,
                    MetaFlag   = entry.Header.MetaFlag,
                    Encryption = entry.DataHeader.EncryptionMagic,
                    Key        = entry.DataHeader.Key,
                    Hash       = resolved ? 0 : entry.Header.PathHash,
                });
            }
        });

        manifest.Entries.Sort((a, b) => string.CompareOrdinal(a.FilePath, b.FilePath));

        var manifestPath = qarPath + ".json";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts));
        return manifestPath;
    }

    public static string Pack(string manifestPath, string? outFile = null)
    {
        var manifest = JsonSerializer.Deserialize<QarManifest>(File.ReadAllText(manifestPath), JsonOpts)
                       ?? throw new InvalidDataException("Manifest deserialise failed.");

        outFile ??= StripJsonExtension(manifestPath);

        var baseDir = ContentDirFor(manifestPath);

        var qar = new QarFile { Flags = manifest.Flags, Version = manifest.Version };
        foreach (var e in manifest.Entries)
        {
            var entry = new QarEntry();
            entry.Header.FilePath           = e.FilePath;
            entry.Header.Compressed         = e.Compressed;
            entry.Header.Version            = manifest.Version;
            entry.Header.NameHashForPacking = e.Hash;
            entry.DataHeader.EncryptionMagic = e.Encryption;
            entry.DataHeader.Key             = e.Key;
            qar.Entries.Add(entry);
        }

        using var fs = File.Open(outFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        qar.Write(fs, baseDir);
        return outFile;
    }

    private static bool IsQarArchive(string path)
    {
        Span<byte> m = stackalloc byte[4];
        using var fs = File.OpenRead(path);
        int n = fs.Read(m);
        return n == 4 && m[0] == 0x53 && m[1] == 0x51 && m[2] == 0x41 && m[3] == 0x52;
    }

    private static string ExtractStandalone(string datPath, string outDir, QarDictionary dict)
    {
        var stem = Path.GetFileNameWithoutExtension(datPath);
        string relPath;
        bool resolved = false;

        if (ulong.TryParse(stem, System.Globalization.NumberStyles.HexNumber, null, out var pathHash))
        {
            relPath = dict.Resolve(pathHash, out resolved);
        }
        else
        {
            relPath = stem;
        }

        if (!resolved)
        {
            var ext = SniffExtension(datPath);
            relPath = stem + ext;
        }

        var rel = relPath.TrimStart('/').Replace('\\', '/');
        var outFile = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.Copy(datPath, outFile, overwrite: true);

        var manifest = new QarManifest
        {
            Flags = 0, Version = 0,
            Entries = { new QarManifestEntry { FilePath = relPath, Hash = resolved ? 0 : pathHash } },
        };
        var manifestPath = datPath + ".json";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts));
        return manifestPath;
    }

    private static string SniffExtension(string path)
    {
        Span<byte> m = stackalloc byte[16];
        using (var fs = File.OpenRead(path)) { _ = fs.Read(m); }

        if (m[0] == 0x30 && m[1] == 0x26 && m[2] == 0xB2 && m[3] == 0x75) return ".wmv";
        if (m[0] == (byte)'B' && m[1] == (byte)'I' && m[2] == (byte)'K') return ".bik";
        if (m[0] == (byte)'K' && m[1] == (byte)'B' && m[2] == (byte)'2') return ".bk2";
        if (m[0] == (byte)'R' && m[1] == (byte)'I' && m[2] == (byte)'F' && m[3] == (byte)'F') return ".riff";
        return ".dat";
    }

    private static string DefaultUnpackDir(string qarPath)
    {
        var stem = Path.GetFileNameWithoutExtension(qarPath);
        var ext  = Path.GetExtension(qarPath).TrimStart('.');
        var dir  = Path.GetDirectoryName(qarPath) ?? ".";
        return Path.Combine(dir, $"{stem}_{ext}");
    }

    private static string ContentDirFor(string manifestPath)
    {
        var name = Path.GetFileName(manifestPath);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext  = Path.GetExtension(name).TrimStart('.');
        var dir  = Path.GetDirectoryName(manifestPath) ?? ".";
        return Path.Combine(dir, $"{stem}_{ext}");
    }

    private static string StripJsonExtension(string p)
    {
        return p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p[..^5] : p + ".out";
    }
}
