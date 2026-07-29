// Fox archive format detection by content magic
namespace MgsvModBldr.Tools.Browse;

// Detection is CONTENT-based (magic bytes), never extension-based, so a
// generic .dat (save game, other app's data) is never mistaken for a Fox
// archive. Fox_shellext's include/foxmagic.h mirrors these — keep in step.

// Content format of a Fox Engine file, identified by its magic signature.
internal enum FoxFormat
{
    Unknown = 0,
    Qar,   // QAR archive       — ".dat"/".qar"   magic "SQAR"
    Fpk,   // Fox Package       — ".fpk"          magic "foxfpk\0win"
    Fpkd,  // Fox Package Data  — ".fpkd"         magic "foxfpkdwin"
    Pftxs, // Packed Fox Texture— ".pftxs"        magic "PFTX" (+ "TEXL")
    G0s,   // GZ QAR archive    — ".g0s"          footer 0x71610000 (no header)
    Sbp,   // Sound Bank Package— ".sbp"          magic "SBPL"
    Stp,   // Streamed Package  — ".stp"          magic "STPL"
    Sab,   // Streamed Animation— ".sab"          magic "SAL3"
    Fsop,  // Fox Shader Pack   — ".fsop"         NO magic (structural detect)
    Mtar,  // Motion Archive    — ".mtar"         NO magic (extension detect)
}

internal static class FoxFormats
{
    // Bytes we need to look at to recognise every magic above (longest = 10,
    // rounded up). Callers should hand Detect() at least this many bytes.
    public const int SniffBytes = 16;

    private static ReadOnlySpan<byte> Sqar => "SQAR"u8;          // QAR
    private static ReadOnlySpan<byte> FoxFpk => "foxfpk"u8;      // FPK family prefix
    private static ReadOnlySpan<byte> Pftx => "PFTX"u8;          // PFTXS
    private static ReadOnlySpan<byte> Sbpl => "SBPL"u8;          // Sound Bank Package
    private static ReadOnlySpan<byte> Stpl => "STPL"u8;          // Streamed Package
    private static ReadOnlySpan<byte> Sal3 => "SAL3"u8;          // Streamed Animation (.sab)

    // Identify a file from the first bytes of its content.
    public static FoxFormat Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[..4].SequenceEqual(Sqar))
            return FoxFormat.Qar;

        if (head.Length >= 10 && head[..6].SequenceEqual(FoxFpk))
            // "foxfpk\0win" => Fpk, "foxfpkdwin" => Fpkd. Byte 6 disambiguates.
            return head[6] == (byte)'d' ? FoxFormat.Fpkd : FoxFormat.Fpk;

        if (head.Length >= 4 && head[..4].SequenceEqual(Pftx))
            return FoxFormat.Pftxs;

        if (head.Length >= 4 && head[..4].SequenceEqual(Sbpl)) return FoxFormat.Sbp;
        if (head.Length >= 4 && head[..4].SequenceEqual(Stpl)) return FoxFormat.Stp;
        if (head.Length >= 4 && head[..4].SequenceEqual(Sal3)) return FoxFormat.Sab;

        return FoxFormat.Unknown;
    }

    // Every format we detect is a browsable container; non-container Fox
    // formats (fmdl, fox2, lba, …) never get a magic here.
    public static bool IsContainer(FoxFormat f) => f != FoxFormat.Unknown;

    // .g0s has no header — identify by its 20-byte footer (count|0x71610000|
    // offset|0|20). The extension is overloaded (data_00.g0s is a WMV), so the
    // footer check is what tells a real GZ archive apart.
    public const int FooterBytes = MgsvModBldr.Tools.G0s.G0sArchive.FooterSize;   // 20

    public static bool IsG0sFooter(ReadOnlySpan<byte> tail)
    {
        if (tail.Length < FooterBytes) return false;
        uint magic      = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.Slice(4, 4));
        int  footerSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tail.Slice(16, 4));
        return magic == MgsvModBldr.Tools.G0s.G0sArchive.FooterMagic1
            && footerSize == MgsvModBldr.Tools.G0s.G0sArchive.FooterSize;
    }

    // .fsop is a headerless [nameLen|name|vsSize|vs|psSize|ps] stream; accept
    // only if it parses cleanly to EOF (a genuine content check).
    public static bool IsFsop(ReadOnlySpan<byte> data) => MgsvModBldr.Tools.Fsop.FsopFile.LooksLikeFsop(data);

    // Top-level container extensions the VFS can browse (association list).
    public static readonly string[] TopLevelExtensions =
        { ".dat", ".qar", ".g0s", ".fpk", ".fpkd", ".pftxs", ".sbp", ".stp", ".sab", ".fsop", ".mtar" };

    // Nested entries we offer to drill into. Flagged by name as a cheap hint;
    // the actual open re-confirms by magic (OpenNestedBytes), so a mislabelled
    // entry simply fails to open rather than corrupting anything.
    private static readonly HashSet<string> NestedContainerExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".fpk", ".fpkd", ".pftxs", ".dat", ".qar", ".g0s", ".sbp", ".stp", ".sab", ".fsop", ".mtar" };

    public static bool IsNestedContainer(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 && NestedContainerExts.Contains(name[dot..]);
    }

    // Nested containers inside a GZ archive (.g0s): same set — GZ fpk/fpkd and
    // GZ pftxs have dedicated GZ readers, and sbp/stp/sab/fsop/mtar are
    // format-identical inside a .g0s (routed by magic in OpenNestedBytes).
    public static bool IsGzNestedContainer(string name) => IsNestedContainer(name);
}
