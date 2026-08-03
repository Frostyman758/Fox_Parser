// Tell archives apart by magic, not by extension
using System.Buffers.Binary;
using MgsvModBldr.Tools.G0s;

namespace MgsvModBldr.Tools.Streaming;

public enum FoxArchiveKind
{
    Unknown,
    Qar,    // TPP .dat/.qar — "SQAR"
    G0s,    // GZ .g0s — no magic, footer-keyed
    Wmv,    // pre-rendered movie wearing an archive extension
}

/// <summary>
/// The game ships non-archives under archive extensions: TPP's five cutscene
/// movies are .wmv renamed to &lt;PathCode64&gt;.dat in master\ (registered in
/// foxfs.dat's safiles list), and GZ's data_00.g0s is the same trick. Opening
/// one as a QAR or a .g0s throws something that reads like a corrupt archive,
/// so callers should ask here first and skip or report properly.
/// </summary>
public static class ArchiveFormat
{
    // ASF_Header_Object GUID 75B22630-668E-11CF-A6D9-00AA0062CE6C, little-endian
    // on disk — the container every .wmv starts with.
    private static ReadOnlySpan<byte> AsfHeaderGuid =>
    [
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C,
    ];

    public static FoxArchiveKind Detect(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[16];
            if (fs.Length < 16 || fs.Read(head) != 16) return FoxArchiveKind.Unknown;

            if (head[..4].SequenceEqual(QarFormat.Magic)) return FoxArchiveKind.Qar;
            if (head.SequenceEqual(AsfHeaderGuid)) return FoxArchiveKind.Wmv;

            // A .g0s is encrypted from byte 0 — the only tell is the footer.
            if (fs.Length >= G0sArchive.FooterSize)
            {
                Span<byte> tail = stackalloc byte[4];
                fs.Seek(-4, SeekOrigin.End);
                if (fs.Read(tail) == 4 &&
                    BinaryPrimitives.ReadInt32LittleEndian(tail) == G0sArchive.FooterSize)
                    return FoxArchiveKind.G0s;
            }
            return FoxArchiveKind.Unknown;
        }
        catch { return FoxArchiveKind.Unknown; }
    }

    /// <summary>True for the formats this library can index and splice.</summary>
    public static bool IsArchive(FoxArchiveKind k) => k is FoxArchiveKind.Qar or FoxArchiveKind.G0s;

    public static string Describe(string path, FoxArchiveKind k) => k switch
    {
        FoxArchiveKind.Qar => "TPP QAR archive",
        FoxArchiveKind.G0s => "GZ G0s archive",
        FoxArchiveKind.Wmv => $"{Path.GetFileName(path)} is a .wmv movie with an archive extension "
                            + "(see foxfs.dat safiles), not an archive",
        _ => $"{Path.GetFileName(path)} is not a QAR or G0s archive",
    };
}
