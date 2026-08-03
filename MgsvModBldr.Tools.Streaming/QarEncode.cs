// Encode a file into a QAR block
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>Encode a modded file into a QAR on-disk block (compress + encrypt + md5).</summary>
public static class QarEncode
{
    public static byte[] EncodeBlock(string qarPath, byte[] data, bool compressed, uint version)
        => EncodeBlock(qarPath, Hashing.NameToHash(qarPath), data, compressed, version);

    /// <summary>
    /// Same, with an EXPLICIT hash — keeps the original entry's hash when the
    /// dictionary can't name it (hash-only entries would otherwise re-key).
    /// </summary>
    public static byte[] EncodeBlock(string qarPath, ulong hash, byte[] data, bool compressed, uint version)
    {
        var e = new QarEntry();
        e.Header.FilePath = qarPath;
        e.Header.Version = version;
        e.Header.Compressed = compressed;
        e.Header.PathHash = hash;
        e.Header.NameHashForPacking = hash;
        e.Data = data;
        e.Loaded = true;
        return e.Write(); // position-independent block, identical to what QarFile.Write would emit
    }

    /// <summary>Whether an entry should be zlib-compressed in a QAR (fpk/fpkd, matching SnakeBite).</summary>
    public static bool ShouldCompress(string qarPath) =>
        qarPath.EndsWith(".fpk", StringComparison.OrdinalIgnoreCase) ||
        qarPath.EndsWith(".fpkd", StringComparison.OrdinalIgnoreCase);
}
