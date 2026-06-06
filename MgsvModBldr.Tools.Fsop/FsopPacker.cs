// Based on fsop_tool.py
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MgsvModBldr.Tools.Fsop;

public static class FsopPacker
{
    private const byte XorKey = 0x9C;
    private const string MetadataFileName = "metadata.json";
    private const string MetadataInfo =
        "Edit .fxc files freely. To add a shader: add entry with \"name\", \"vertex_shader_file\", \"pixel_shader_file\". Order matters for repacking.";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static bool _codePagesRegistered;
    private static void EnsureCodePages()
    {
        if (_codePagesRegistered) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _codePagesRegistered = true;
    }

    public static int Unpack(string fsopPath, string outputDir)
    {
        EnsureCodePages();
        Directory.CreateDirectory(outputDir);

        var data = File.ReadAllBytes(fsopPath);

        var entries = new List<ParsedEntry>(capacity: 64);
        int offset = 0;
        while (offset < data.Length)
        {
            int nameLen = data[offset++];
            var nameBytes = new byte[nameLen];
            Array.Copy(data, offset, nameBytes, 0, nameLen);
            offset += nameLen;

            int vsSize = BitConverter.ToInt32(data, offset);
            offset += 4;
            int vsOffset = offset;
            offset += vsSize;

            int psSize = BitConverter.ToInt32(data, offset);
            offset += 4;
            int psOffset = offset;
            offset += psSize;

            entries.Add(new ParsedEntry(nameBytes, vsOffset, vsSize, psOffset, psSize));
        }

        var shaders = new FsopShaderEntry[entries.Count];
        Parallel.For(0, entries.Count, i =>
        {
            var e = entries[i];
            var (rawName, encoding) = DecodeName(e.NameBytes);

            var vs = new byte[e.VsSize];
            Buffer.BlockCopy(data, e.VsOffset, vs, 0, e.VsSize);
            XorVectorized(vs);

            var ps = new byte[e.PsSize];
            Buffer.BlockCopy(data, e.PsOffset, ps, 0, e.PsSize);
            XorVectorized(ps);

            var safe = SanitizeFileName(rawName);
            var vsFile = $"{safe}_vs.fxc";
            var psFile = $"{safe}_ps.fxc";

            File.WriteAllBytes(Path.Combine(outputDir, vsFile), vs);
            File.WriteAllBytes(Path.Combine(outputDir, psFile), ps);

            shaders[i] = new FsopShaderEntry
            {
                Name             = rawName,
                Encoding         = encoding,
                VertexShaderFile = vsFile,
                PixelShaderFile  = psFile,
            };
        });

        var meta = new FsopMetadata { Shaders = shaders.ToList(), Info = MetadataInfo };
        File.WriteAllText(
            Path.Combine(outputDir, MetadataFileName),
            JsonSerializer.Serialize(meta, JsonOpts));
        return shaders.Length;
    }

    public static int Pack(string inputDir, string outputFile)
    {
        EnsureCodePages();
        var metaPath = Path.Combine(inputDir, MetadataFileName);
        if (!File.Exists(metaPath))
            throw new FileNotFoundException("metadata.json not found — required to maintain shader order and names.", metaPath);

        var meta = JsonSerializer.Deserialize<FsopMetadata>(File.ReadAllText(metaPath), JsonOpts)
                   ?? throw new InvalidDataException("metadata.json failed to deserialise.");

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in meta.Shaders)
        {
            known.Add(s.VertexShaderFile);
            known.Add(s.PixelShaderFile);
        }
        var newPairs = DiscoverNewPairs(inputDir, known);
        if (newPairs.Count > 0)
        {
            meta.Shaders.AddRange(newPairs);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOpts));
        }

        var chunks = new byte[meta.Shaders.Count][];
        Parallel.For(0, meta.Shaders.Count, i =>
        {
            var s = meta.Shaders[i];
            var vsPath = Path.Combine(inputDir, s.VertexShaderFile);
            var psPath = Path.Combine(inputDir, s.PixelShaderFile);
            if (!File.Exists(vsPath) || !File.Exists(psPath)) return;
            chunks[i] = EncodeEntry(s, vsPath, psPath);
        });

        long total = 0;
        foreach (var c in chunks) if (c is not null) total += c.Length;

        using var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None,
                                      bufferSize: 1 << 16, useAsync: false);
        int packed = 0;
        foreach (var c in chunks)
        {
            if (c is null) continue;
            fs.Write(c, 0, c.Length);
            packed++;
        }
        return packed;
    }

    private static byte[] EncodeEntry(FsopShaderEntry s, string vsPath, string psPath)
    {
        var name = s.Name;
        if (!name.EndsWith('\0')) name += '\0';

        var enc = ResolveEncoding(s.Encoding);
        byte[] nameBytes;
        try { nameBytes = enc.GetBytes(name); }
        catch
        {
            try { nameBytes = Encoding.GetEncoding("shift_jis").GetBytes(name); }
            catch
            {
                try { nameBytes = Encoding.UTF8.GetBytes(name); }
                catch { nameBytes = Encoding.Latin1.GetBytes(name); }
            }
        }
        if (nameBytes.Length > byte.MaxValue)
            throw new InvalidDataException($"Shader name encodes to {nameBytes.Length} bytes; FSOP name-length is a single byte.");

        var vs = File.ReadAllBytes(vsPath);
        var ps = File.ReadAllBytes(psPath);
        XorVectorized(vs);
        XorVectorized(ps);

        int len = 1 + nameBytes.Length + 4 + vs.Length + 4 + ps.Length;
        var buf = new byte[len];
        int o = 0;
        buf[o++] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, buf, o, nameBytes.Length); o += nameBytes.Length;
        BitConverter.GetBytes(vs.Length).CopyTo(buf, o); o += 4;
        Buffer.BlockCopy(vs, 0, buf, o, vs.Length); o += vs.Length;
        BitConverter.GetBytes(ps.Length).CopyTo(buf, o); o += 4;
        Buffer.BlockCopy(ps, 0, buf, o, ps.Length);
        return buf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void XorVectorized(byte[] data)
    {
        var span = data.AsSpan();
        int i = 0;
        if (Vector.IsHardwareAccelerated && span.Length >= Vector<byte>.Count)
        {
            var key = new Vector<byte>(XorKey);
            int upper = span.Length - (span.Length % Vector<byte>.Count);
            for (; i < upper; i += Vector<byte>.Count)
            {
                var v = new Vector<byte>(span.Slice(i, Vector<byte>.Count));
                (v ^ key).CopyTo(span.Slice(i, Vector<byte>.Count));
            }
        }
        for (; i < span.Length; i++) span[i] ^= XorKey;
    }

    private static (string Raw, string Encoding) DecodeName(byte[] bytes)
    {
        try { return (Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes), "shift-jis"); }
        catch { /* try next */ }
        try { return (Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes), "utf-8"); }
        catch { /* try next */ }
        return (Encoding.Latin1.GetString(bytes), "latin-1");
    }

    private static Encoding ResolveEncoding(string name) => (name ?? "shift-jis").ToLowerInvariant() switch
    {
        "shift-jis" or "shift_jis" or "sjis"  => Encoding.GetEncoding("shift_jis"),
        "utf-8"     or "utf8"                 => Encoding.UTF8,
        "latin-1"   or "latin1" or "iso-8859-1" => Encoding.Latin1,
        "ascii"                               => Encoding.ASCII,
        _                                     => Encoding.GetEncoding("shift_jis"),
    };

    private static string SanitizeFileName(string name)
    {
        var s = name.Replace("\0", "").Trim();
        foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
            s = s.Replace(c, '_');
        return s.Length == 0 ? "unnamed" : s;
    }

    private static List<FsopShaderEntry> DiscoverNewPairs(string inputDir, HashSet<string> known)
    {
        var all = new HashSet<string>(
            Directory.EnumerateFiles(inputDir, "*.fxc").Select(Path.GetFileName).Where(n => n is not null)!,
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<FsopShaderEntry>();
        foreach (var f in all)
        {
            if (f is null) continue;
            if (known.Contains(f) || seen.Contains(f)) continue;
            if (!f.EndsWith("_vs.fxc", StringComparison.OrdinalIgnoreCase)) continue;
            var baseName = f[..^"_vs.fxc".Length];
            var ps = baseName + "_ps.fxc";
            if (!all.Contains(ps) || known.Contains(ps)) continue;

            found.Add(new FsopShaderEntry
            {
                Name             = baseName + "\0",
                Encoding         = DetectEncoding(baseName),
                VertexShaderFile = f,
                PixelShaderFile  = ps,
            });
            seen.Add(f); seen.Add(ps);
        }
        return found;
    }

    private static string DetectEncoding(string text)
    {
        try
        {
            var enc = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            enc.GetBytes(text);
            return "ascii";
        }
        catch { /* not pure ASCII */ }

        try
        {
            var enc = Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var sjis = enc.GetBytes(text);
            if (enc.GetString(sjis) == text)
            {
                try
                {
                    var utf8 = Encoding.UTF8.GetBytes(text);
                    if (utf8.Length <= sjis.Length) return "utf-8";
                }
                catch { /* utf-8 fall-through */ }
                return "shift-jis";
            }
        }
        catch { /* not representable */ }

        try { _ = Encoding.UTF8.GetBytes(text); return "utf-8"; }
        catch { return "latin-1"; }
    }

    private readonly record struct ParsedEntry(byte[] NameBytes, int VsOffset, int VsSize, int PsOffset, int PsSize);
}
