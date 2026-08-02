// GZ obfuscated /as/ path decode (embedded PathId)
// 02/08/2026
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.G0s;

// PC GZ ships texture refs (fova/uif) as junk strings hiding the real 64-bit
// PathId after a 0x07 marker: 8 b64 chars = 48-bit hash (LE), 3 chars = type
// bits; the textual extension after the first '.' re-derives the type bits
// (engine FUN_14002def0 / FUN_14002d6a0 / FUN_14002d940 / FUN_140029440).
// Alphabet = A-Z a-z 0-9 _ - (0..63) plus aliases 0x0b->0 ('A'), 0x09->10 ('K').
// Everything else in the string is length-preserving filler.
public static class GzAsPath
{
    public static bool TryDecodeId(ReadOnlySpan<char> s, out ulong pathId)
    {
        pathId = 0;
        int m = s.IndexOf('\a');                 // 0x07 marker
        if (m < 0 || m + 12 > s.Length) return false;
        Span<byte> b = stackalloc byte[8];
        if (!Quantum4(s.Slice(m + 1, 4), b) ||
            !Quantum4(s.Slice(m + 5, 4), b.Slice(3)) ||
            !Quantum3(s.Slice(m + 9, 3), b.Slice(6))) return false;
        pathId = BinaryPrimitives.ReadUInt64LittleEndian(b);

        // textual extension wins over the embedded type bits (engine order)
        int dot = s.Slice(m).IndexOf('.');
        int t = dot < 0 ? 0 : ExtTypeId(s.Slice(m + dot));
        if (t > 0) pathId = pathId & G0sHash.HashMask | (ulong)t << 52;
        return true;
    }

    // decoded id -> dictionary path when known, else "<hex48><ext>"
    public static bool TryResolve(string s, out ulong pathId, out string path)
    {
        path = "";
        if (!TryDecodeId(s, out pathId)) return false;
        G0sHash.TryResolve(pathId, out path);
        return true;
    }

    private static int ExtTypeId(ReadOnlySpan<char> ext)
    {
        var exts = G0sHash.TypeExtensions;
        for (int id = 1; id < exts.Length; id++)
            if (ext.Equals(exts[id], StringComparison.OrdinalIgnoreCase)) return id;
        return 0;
    }

    private static int Val(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '_' => 62,
        '-' => 63,
        '\v' => 0,
        '\t' => 10,
        _ => -1,
    };

    private static bool Quantum4(ReadOnlySpan<char> s, Span<byte> o)
    {
        int a = Val(s[0]), b = Val(s[1]), c = Val(s[2]), d = Val(s[3]);
        if ((a | b | c | d) < 0) return false;
        o[0] = (byte)(a << 2 | b >> 4);
        o[1] = (byte)(b << 4 | c >> 2);
        o[2] = (byte)(c << 6 | d);
        return true;
    }

    private static bool Quantum3(ReadOnlySpan<char> s, Span<byte> o)
    {
        int a = Val(s[0]), b = Val(s[1]), c = Val(s[2]);
        if ((a | b | c) < 0) return false;
        o[0] = (byte)(a << 2 | b >> 4);
        o[1] = (byte)(b << 4 | c >> 2);
        return true;
    }
}
