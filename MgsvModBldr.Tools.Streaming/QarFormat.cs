// QAR container constants + section-table codec
using System.Buffers.Binary;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// QAR container layout constants + the section-table codec, expressed on
/// the Qar tool's PUBLIC surface (QarFile.DecryptSectionList). The header XOR keys
/// are the standard public QAR format values (identical in datfpk / GzsTool /
/// this toolset) — not fox-proprietary crypto.
/// </summary>
public static class QarFormat
{
    public static readonly byte[] Magic = { 0x53, 0x51, 0x41, 0x52 }; // "SQAR"

    public static readonly uint[] XorTable =
    {
        0x41441043u, 0x11C22050u, 0xD05608C3u, 0x532C7319u,
    };

    public const int HeaderSize = 32;      // SQAR header
    public const int EntryHeaderSize = 32; // per-entry header
    public const int BlockSize = 8;        // bytes per section-table entry

    public static int Shift(uint flags) => (flags & 0x800) > 0 ? 12 : 10;

    public static long AlignUp(long pos, int alignment)
    {
        long rem = pos % alignment;
        return rem == 0 ? pos : pos + (alignment - rem);
    }

    /// <summary>Encrypt the section table (re-uses QarFile's public DecryptSectionList).</summary>
    public static byte[] EncryptSections(ulong[] sections, uint version)
    {
        var blob = new byte[sections.Length * BlockSize];
        for (int i = 0; i < sections.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(i * 8, 8), sections[i]);

        var enc = QarFile.DecryptSectionList((uint)sections.Length, blob, version, encrypt: true);
        for (int i = 0; i < enc.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(i * 8, 8), enc[i]);
        return blob;
    }
}
