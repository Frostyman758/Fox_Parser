// GZ (Ground Zeroes) Fox Package reader — SEPARATE from the TPP FpkFile, so the
// byte-exact TPP packer/reader and its tests are never affected. Read-only: the
// shell-extension bridge uses this to browse/extract GZ fpk(d) found inside a
// .g0s. Ported from GzsTool 0.2 (Fpk/FpkFile.cs).
//
// The body layout matches TPP (48-byte header, 48-byte entry records, 16-byte
// references). The two differences that make GZ its own reader: the magic's
// platform tag is "ste" not "win", and entry path strings are MD5-resolved
// (see GzFpkString). References are not surfaced when browsing.
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Fpk.Gz;

public sealed class GzFpkFile
{
    public const int HeaderSize = 48;

    public List<GzFpkEntry> Entries { get; } = new();

    // foxfpk\0ste (fpk) / foxfpkdste (fpkd) — "foxfpk" + type byte + "ste".
    public static bool IsGzMagic(ReadOnlySpan<byte> head)
        => head.Length >= 10
        && head[0] == 0x66 && head[1] == 0x6f && head[2] == 0x78 && head[3] == 0x66
        && head[4] == 0x70 && head[5] == 0x6b
        && head[7] == 0x73 && head[8] == 0x74 && head[9] == 0x65; // "ste"

    public static GzFpkFile Read(Stream r)
    {
        var f = new GzFpkFile();
        Span<byte> hdr = stackalloc byte[HeaderSize];
        GzFpkString.ReadExact(r, hdr);

        if (!IsGzMagic(hdr))
            throw new InvalidDataException("not a GZ (ste) fpk");

        // hdr[36..40] = entry count, hdr[40..44] = reference count (skipped).
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(36, 4));

        for (int i = 0; i < entryCount; i++)
        {
            var e = new GzFpkEntry();
            e.Read(r);
            f.Entries.Add(e);
        }
        return f;
    }
}
