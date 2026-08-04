// fsop shader list without the DXBC blobs
namespace MgsvModBldr.Tools.Index;

/// <summary>
/// Lists the shaders in a .fsop without reading a blob.
///
/// The awkward one: no magic, and sizes are interleaved with data —
///   byte nameLen | name | int32 vsSize | vs | int32 psSize | ps
/// repeated to EOF. So there's no index to prefix-read and no header to sniff;
/// the structure has to be walked, hopping over each shader blob. Each step reads
/// only the name and the two size fields, which is a handful of bytes per shader
/// against blobs that are usually kilobytes.
///
/// Because it has no magic, a caller must decide by other means that a blob is an
/// fsop — a successful walk that consumes the whole thing is itself good evidence,
/// which is what <see cref="LooksLikeFsop"/> reports.
/// </summary>
public static class FsopIndex
{
    public sealed record Shader(string Name, long VsOffset, int VsSize, long PsOffset, int PsSize);

    /// <summary>Walked cleanly to the end, with at least one shader.</summary>
    public static bool LooksLikeFsop(RangeReader read, long totalSize)
        => Read(read, totalSize, out _, out bool clean) is { Count: > 0 } && clean;

    public static List<Shader> Read(RangeReader read, long totalSize, out int bytesRead)
        => Read(read, totalSize, out bytesRead, out _);

    public static List<Shader> Read(RangeReader read, long totalSize, out int bytesRead, out bool consumedAll)
    {
        bytesRead = 0;
        consumedAll = false;
        var list = new List<Shader>();
        long o = 0;

        while (o < totalSize)
        {
            var lenByte = read(o, 1);
            if (lenByte is null || lenByte.Length < 1) break;
            bytesRead += 1;
            int nameLen = lenByte[0];
            o += 1;
            if (nameLen == 0 || o + nameLen + 4 > totalSize) break;

            var nameBuf = read(o, nameLen);
            if (nameBuf is null || nameBuf.Length < nameLen) break;
            bytesRead += nameBuf.Length;
            o += nameLen;

            var sz = read(o, 4);
            if (sz is null || sz.Length < 4) break;
            bytesRead += 4;
            int vsSize = BitConverter.ToInt32(sz, 0);
            o += 4;
            if (vsSize < 0 || o + vsSize + 4 > totalSize) break;
            long vsOff = o;
            o += vsSize;                                     // hop the blob, never read it

            sz = read(o, 4);
            if (sz is null || sz.Length < 4) break;
            bytesRead += 4;
            int psSize = BitConverter.ToInt32(sz, 0);
            o += 4;
            if (psSize < 0 || o + psSize > totalSize) break;
            long psOff = o;
            o += psSize;

            list.Add(new Shader(DecodeName(nameBuf), vsOff, vsSize, psOff, psSize));
        }

        consumedAll = o == totalSize && list.Count > 0;
        return list;
    }

    // Matches Browse's LazyFsopReader so listings agree character for character.
    private static string DecodeName(byte[] data)
    {
        var s = System.Text.Encoding.Latin1.GetString(data).TrimEnd('\0').Trim();
        foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
            s = s.Replace(c, '_');
        return s.Length == 0 ? "unnamed" : s;
    }
}
