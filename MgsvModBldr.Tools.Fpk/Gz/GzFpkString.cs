// GZ (Ground Zeroes) fpk path string — SEPARATE from the TPP FpkString so the
// proven TPP path is never touched. Ported from GzsTool 0.2 (Fpk/FpkString.cs +
// Utility/Hashing.cs ResolveString / TryGetFileNameFromMd5Hash).
//
// In a GZ fpk the stored string is usually an opaque placeholder, not the path.
// Resolution: if the raw bytes' MD5 equals the entry's stored hash, the raw
// string IS the path; otherwise look the hash up in fpk_dictionary.txt, and
// failing that synthesise "<md5hex><ext>". Read-only (browse/extract only).
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MgsvModBldr.Tools.Fpk.Gz;

public sealed class GzFpkString
{
    public const int HeaderSize = 16;

    public uint   Offset  { get; private set; }
    public uint   Length  { get; private set; }
    public byte[] RawData { get; private set; } = System.Array.Empty<byte>();
    public string Path    { get; private set; } = "";   // resolved display path
    public bool   Resolved { get; private set; }

    public void Read(Stream r)
    {
        Span<byte> b = stackalloc byte[HeaderSize];
        ReadExact(r, b);
        Offset = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4));
        Length = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(8, 4));

        long cur = r.Position;
        r.Seek(Offset, SeekOrigin.Begin);
        RawData = new byte[Length];
        ReadExact(r, RawData);
        r.Seek(cur, SeekOrigin.Begin);
    }

    public void Resolve(byte[] md5Hash)
    {
        // Latin1 is a lossless 1:1 byte<->char mapping, so the raw bytes survive
        // unchanged (UTF8 would mangle the placeholder bytes).
        string rawText = Encoding.Latin1.GetString(RawData);

        if (md5Hash is { Length: 16 } && !AllZero(md5Hash))
        {
            if (MD5.HashData(RawData).AsSpan().SequenceEqual(md5Hash))
            {
                Path = rawText; Resolved = true; return;          // raw string IS the path
            }
            if (FpkDictionary.TryResolve(md5Hash, out var real))
            {
                Path = real; Resolved = true; return;             // resolved via fpk_dictionary
            }
            Path = Convert.ToHexString(md5Hash).ToLowerInvariant() + ExtensionOf(rawText);
            Resolved = false;                                      // synthesised <md5hex><ext>
            return;
        }
        Path = rawText; Resolved = true;                          // no usable hash: trust raw
    }

    private static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name.Substring(dot) : "";
    }

    private static bool AllZero(byte[] b)
    {
        foreach (var x in b) if (x != 0) return false;
        return true;
    }

    internal static void ReadExact(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length)
        {
            int r = s.Read(buf.Slice(n));
            if (r == 0) throw new EndOfStreamException();
            n += r;
        }
    }
}
