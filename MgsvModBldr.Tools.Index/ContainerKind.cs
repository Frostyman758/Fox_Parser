// Identify a container by magic, not by name
namespace MgsvModBldr.Tools.Index;

public enum Container
{
    Unknown,
    Fpk, Fpkd,          // TPP "foxfpk\0win" / "foxfpkdwin"
    GzFpk, GzFpkd,      // GZ  "foxfpk\0ste" / "foxfpkdste"
    Pftxs, GzPftxs,     // "PFTX"; the GZ variant has float 1.0 at +4
    Sbp,                // "SBPL"
    Mtar,               // no magic — only accepted when the caller says so
}

/// <summary>
/// Reads <paramref name="len"/> bytes at <paramref name="offset"/> within a
/// container. Returns fewer bytes (or null) when the range isn't available.
/// </summary>
public delegate byte[] RangeReader(long offset, int len);

/// <summary>
/// What kind of container is this, judged by its first bytes.
///
/// Matters because archive entries are keyed by HASH: when the dictionary can't
/// name an entry there is no ".fpk" to match on, and a name-based check silently
/// skips it. Sixteen bytes settle it regardless of whether the name resolved.
/// </summary>
public static class ContainerKind
{
    public const int SniffBytes = 16;

    public static Container Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 10 && head[0] == 'f' && head[1] == 'o' && head[2] == 'x'
            && head[3] == 'f' && head[4] == 'p' && head[5] == 'k')
        {
            bool d = head[6] == (byte)'d';
            int p = d ? 7 : 7;                      // platform tag starts at 7 either way
            bool ste = head[p] == 's' && head[p + 1] == 't' && head[p + 2] == 'e';
            bool win = head[p] == 'w' && head[p + 1] == 'i' && head[p + 2] == 'n';
            if (ste) return d ? Container.GzFpkd : Container.GzFpk;
            if (win) return d ? Container.Fpkd : Container.Fpk;
        }

        if (head.Length >= 8 && head[0] == 'P' && head[1] == 'F' && head[2] == 'T' && head[3] == 'X')
        {
            // GZ pftxs carries the float 1.0 where TPP carries other header data.
            uint at4 = (uint)(head[4] | head[5] << 8 | head[6] << 16 | head[7] << 24);
            return at4 == 0x3F800000u ? Container.GzPftxs : Container.Pftxs;
        }

        if (head.Length >= 4 && head[0] == 'S' && head[1] == 'B' && head[2] == 'P' && head[3] == 'L')
            return Container.Sbp;

        return Container.Unknown;
    }

    /// <summary>Sniff through a range reader — costs one 16-byte read.</summary>
    public static Container Detect(RangeReader read)
    {
        var head = read(0, SniffBytes);
        return head is null ? Container.Unknown : Detect(head);
    }

    public static bool IsPack(Container c) =>
        c is Container.Fpk or Container.Fpkd or Container.GzFpk or Container.GzFpkd;
}
