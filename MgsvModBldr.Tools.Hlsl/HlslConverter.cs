using System.Text;
using System.Text.RegularExpressions;

namespace MgsvModBldr.Tools.Hlsl;

/// <summary>
/// Extracts the embedded HLSL source from an MGSV .fxc (DXBC) shader. The
/// source lives in the SDBG debug chunk as a single NUL-terminated string
/// (build path + preprocessed source with #line directives). It contains
/// the original comments — including Shift-JIS (cp932) Japanese — which are
/// preserved BYTE-FOR-BYTE (extract from the first #line to the NUL; NUL
/// never occurs inside the text, so non-ASCII bytes pass through intact).
///
/// Two modes:
///   <see cref="Unpack"/>      -> &lt;name&gt;.fxc.hlsl   (the preprocessed source blob)
///   <see cref="UnpackFiles"/> -> &lt;name&gt;_src/...     (the original .shdr/.h files,
///                                reconstructed from the #line directives)
///
/// Recompile (hlsl -> fxc) is a separate, non-byte-exact step (the
/// sanctioned exception) — not in this file.
/// </summary>
public static class HlslConverter
{
    private const int SdbgStringOffsetField = 20; // header int32 index -> string-heap offset

    /// <summary>
    /// The embedded preprocessed source as raw bytes (Shift-JIS-safe), or
    /// null if the .fxc has no embedded source (no SDBG / no #line).
    /// </summary>
    public static byte[] ExtractSourceBytes(byte[] fxc)
    {
        var dxbc = new DxbcFile(fxc);
        var sdbg = dxbc.Chunk("SDBG");
        if (sdbg is null) return null;

        var (off, size) = sdbg.Value;
        int payEnd = off + size;

        int searchFrom = off;
        int heapField = off + SdbgStringOffsetField * 4;
        if (heapField + 4 <= payEnd)
        {
            int rel = BitConverter.ToInt32(fxc, heapField);
            if (rel > 0 && off + rel < payEnd) searchFrom = off + rel;
        }

        int start = IndexOf(fxc, "#line ", searchFrom, payEnd);
        if (start < 0) start = IndexOf(fxc, "#line ", off, payEnd); // fallback: whole chunk
        if (start < 0) return null;

        // The source is one NUL-terminated string. NUL never appears inside
        // HLSL text (incl. Shift-JIS), so this captures it fully + intact.
        int end = start;
        while (end < payEnd && fxc[end] != 0) end++;

        var bytes = new byte[end - start];
        Buffer.BlockCopy(fxc, start, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Decompile a .fxc to <c>&lt;name&gt;.fxc.hlsl</c> (raw bytes; Japanese preserved). Null if no source.</summary>
    public static string Unpack(string fxcPath)
    {
        var src = ExtractSourceBytes(File.ReadAllBytes(fxcPath));
        if (src is null) return null;
        var outPath = fxcPath + ".hlsl";
        File.WriteAllBytes(outPath, src);
        return outPath;
    }

    /// <summary>
    /// Reconstruct the original per-file source (.shdr/.h) from the #line
    /// directives in the embedded blob. Returns (relativePath -> raw bytes),
    /// or null if no source. Comments (incl. Japanese) are preserved.
    /// </summary>
    public static Dictionary<string, byte[]> ExtractSourceFiles(byte[] fxc)
    {
        var src = ExtractSourceBytes(fxc);
        if (src is null) return null;

        // Split into lines on \n, keeping each line's raw bytes (incl. any \r).
        var lines = SplitLines(src);
        var lineRe = new Regex("^\\s*#line\\s+(\\d+)\\s+\"(.*)\"\\s*$");

        // file -> (lineNumber -> raw line bytes)
        var files = new Dictionary<string, SortedDictionary<int, byte[]>>(StringComparer.OrdinalIgnoreCase);
        string current = null;
        int curLine = 0;

        foreach (var line in lines)
        {
            var ascii = Encoding.Latin1.GetString(line);
            var m = lineRe.Match(ascii);
            if (m.Success)
            {
                curLine = int.Parse(m.Groups[1].Value);
                current = NormalizePath(m.Groups[2].Value);
                if (!files.ContainsKey(current)) files[current] = new SortedDictionary<int, byte[]>();
                continue; // the directive itself is a marker, not file content
            }
            if (current == null) continue;
            files[current][curLine] = line;
            curLine++;
        }

        // Materialise each file: lines 1..max joined by \n (gaps -> blank).
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, map) in files)
        {
            using var ms = new MemoryStream();
            int max = map.Count == 0 ? 0 : map.Keys.Max();
            for (int i = 1; i <= max; i++)
            {
                if (map.TryGetValue(i, out var lb)) ms.Write(lb, 0, lb.Length);
                if (i < max) ms.WriteByte((byte)'\n');
            }
            result[path] = ms.ToArray();
        }
        return result;
    }

    /// <summary>Decompile a .fxc into a <c>&lt;name&gt;_src/</c> folder of original .shdr/.h files. Returns the folder, or null.</summary>
    public static string UnpackFiles(string fxcPath)
    {
        var files = ExtractSourceFiles(File.ReadAllBytes(fxcPath));
        if (files is null) return null;
        var dir = Path.Combine(Path.GetDirectoryName(fxcPath) ?? ".",
                               Path.GetFileNameWithoutExtension(fxcPath) + "_src");
        Directory.CreateDirectory(dir);
        foreach (var (rel, bytes) in files)
        {
            var dst = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.WriteAllBytes(dst, bytes);
        }
        return dir;
    }

    // Normalise a #line path ("..\Gr\Dg\shader\../DgShaderDefine.h") into a
    // safe relative path under the output dir (no drive, no '..' escape).
    private static string NormalizePath(string p)
    {
        p = p.Replace('\\', '/');
        var parts = new List<string>();
        foreach (var seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        var rel = string.Join(Path.DirectorySeparatorChar, parts);
        return string.IsNullOrEmpty(rel) ? "unknown.hlsl" : rel;
    }

    private static List<byte[]> SplitLines(byte[] data)
    {
        var lines = new List<byte[]>();
        int start = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\n')
            {
                var len = i - start;
                var line = new byte[len];
                Buffer.BlockCopy(data, start, line, 0, len);
                lines.Add(line);
                start = i + 1;
            }
        }
        if (start < data.Length)
        {
            var len = data.Length - start;
            var line = new byte[len];
            Buffer.BlockCopy(data, start, line, 0, len);
            lines.Add(line);
        }
        return lines;
    }

    private static int IndexOf(byte[] hay, string needleAscii, int from, int to)
    {
        var n = Encoding.ASCII.GetBytes(needleAscii);
        int limit = Math.Min(to, hay.Length) - n.Length;
        for (int i = Math.Max(0, from); i <= limit; i++)
        {
            bool ok = true;
            for (int j = 0; j < n.Length; j++)
                if (hay[i + j] != n[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
