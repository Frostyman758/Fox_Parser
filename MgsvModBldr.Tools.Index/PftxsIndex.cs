// Read a pftxs texture index without its blobs
namespace MgsvModBldr.Tools.Index;

/// <summary>
/// Lists the textures in a .pftxs without reading a single texture blob.
///
/// Unlike an fpk, a pftxs INTERLEAVES index and data: after the 32-byte file
/// header each group is [ "FTEX" | groupSize | groupHash | count | pad(12) ] then
/// count x [ pieceHash | offset | size ] then the blobs, and the next group starts
/// groupSize later. So the tables are scattered through the file and no prefix
/// reaches them all — this hops group to group with ranged reads instead, touching
/// only the headers and piece tables.
/// </summary>
public static class PftxsIndex
{
    private const uint MagicPftx = 0x58544650u;   // "PFTX"
    private const uint MagicFtex = 0x58455446u;   // "FTEX"
    private const int FileHeader = 32, GroupHeader = 32, PieceSize = 16;

    /// <summary>One texture piece: the .ftex itself or one of its .N.ftexs sidecars.</summary>
    public sealed record Piece(ulong GroupHash, ulong Hash, int Offset, int Size);

    /// <summary>
    /// Every piece in the pftxs, or null when the entry isn't a TPP pftxs (the GZ
    /// variant has a different layout). <paramref name="bytesRead"/> reports the
    /// index bytes actually decoded.
    /// </summary>
    public static List<Piece> Read(RangeReader read, long totalSize, out int bytesRead)
    {
        bytesRead = 0;
        long stored = totalSize;
        if (stored < FileHeader) return null;

        var head = read(0, FileHeader);
        if (head is null || head.Length < FileHeader) return null;
        if (BitConverter.ToUInt32(head, 0) != MagicPftx) return null;   // GZ pftxs, or not a pftxs
        bytesRead += head.Length;

        int groupCount = BitConverter.ToInt32(head, 24);
        if (groupCount < 0 || groupCount > 100_000) return null;

        var pieces = new List<Piece>();
        long gpos = FileHeader;
        for (int g = 0; g < groupCount; g++)
        {
            if (gpos + GroupHeader > stored) break;
            var gh = read(gpos, GroupHeader);
            if (gh is null || gh.Length < GroupHeader) break;
            bytesRead += gh.Length;
            if (BitConverter.ToUInt32(gh, 0) != MagicFtex) break;

            uint groupSize = BitConverter.ToUInt32(gh, 4);
            ulong groupHash = BitConverter.ToUInt64(gh, 8);
            int count = BitConverter.ToInt32(gh, 16);
            if (count < 0 || count > 100_000) break;

            if (count > 0)
            {
                int tableLen = count * PieceSize;
                var table = read(gpos + GroupHeader, tableLen);
                if (table is null || table.Length < tableLen) break;
                bytesRead += table.Length;
                for (int i = 0; i < count; i++)
                {
                    int at = i * PieceSize;
                    pieces.Add(new Piece(
                        groupHash,
                        BitConverter.ToUInt64(table, at),
                        BitConverter.ToInt32(table, at + 8),
                        BitConverter.ToInt32(table, at + 12)));
                }
            }

            if (groupSize == 0) break;          // malformed; don't spin
            gpos += groupSize;
        }
        return pieces;
    }
}
