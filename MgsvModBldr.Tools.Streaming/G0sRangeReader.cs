// Decode part of a .g0s blob without the rest
using System.Buffers.Binary;
using MgsvModBldr.Tools.G0s;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Reads plaintext[start, start+len) of a .g0s entry without decoding the whole
/// blob — the GZ counterpart of <see cref="QarRangeReader"/>.
///
/// Easier than the QAR case in one way and harder in another. Easier: a .g0s blob
/// is never zlib-compressed, so byte ranges map straight through and there is no
/// inflate to walk past. Harder: BOTH layers are position-dependent. The outer
/// pass seeds from the entry's own 16-byte-unit offset and steps one 8-byte block
/// at a time, so its keystream can be computed directly for any block — no winding
/// loop needed. The optional inner pass advances once per 4-byte word and has to be
/// wound forward with arithmetic (no IO, no decode).
///
/// Same deliberate clone policy as the QAR readers: MgsvModBldr.Tools.G0s is
/// byte-exact and untouched, and StreamingTests diffs this against its full decode.
/// </summary>
public static class G0sRangeReader
{
    private const uint InnerKeyMagic = 0xA0F8EFE6u;
    private const int InnerPrefix = 8;          // magic + key ahead of the inner payload

    /// <summary>Plaintext prefix, up to <paramref name="want"/> bytes.</summary>
    public static byte[] Read(G0sEntry e, Stream source, int want) => Read(e, source, 0, want);

    public static byte[] Read(G0sEntry e, Stream source, long start, int len)
    {
        if (len <= 0 || start < 0 || e.Size == 0) return Array.Empty<byte>();

        // Is there an inner pass? Block 0 decrypts on its own, so this costs 8 bytes.
        var first = RawOuter(e, source, 0, 8);
        bool inner = first.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(first) == InnerKeyMagic;
        uint innerKey = inner && first.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(4, 4)) : 0;

        int shift = inner ? InnerPrefix : 0;
        long plainLen = e.Size - shift;
        if (start >= plainLen) return Array.Empty<byte>();
        if (start + len > plainLen) len = (int)(plainLen - start);

        long rawStart = shift + start;
        long aligned = rawStart & ~7L;
        int skew = (int)(rawStart - aligned);
        int span = Align8(skew + len);
        if (aligned + span > e.Size) span = Align8((int)(e.Size - aligned));

        var buf = RawOuter(e, source, aligned, span);
        if (buf.Length == 0) return Array.Empty<byte>();

        if (inner)
        {
            // Inner payload starts at raw offset 8; wind one key step per 4-byte word.
            // It covers WHOLE words only — a trailing 1-3 bytes are left as the outer
            // pass left them, so the cipher must stop at the last complete word.
            long innerStart = aligned - InnerPrefix;
            if (innerStart < 0) return Array.Empty<byte>();
            long wordsEnd = (plainLen / 4) * 4;                  // last complete inner word
            int bound = (int)Math.Min(buf.Length, Math.Max(0, wordsEnd - innerStart));
            InnerFrom(buf, innerKey, innerStart / 4, bound);
        }

        int take = Math.Min(len, buf.Length - skew);
        return take <= 0 ? Array.Empty<byte>() : buf[skew..(skew + take)];
    }

    private static int Align8(int n) => (n + 7) & ~7;

    // Read `span` bytes at `aligned` within the blob and undo the outer pass.
    // A blob whose size isn't a multiple of 8 ends in a partial block that the
    // original ciphers with a SEPARATE formula, so the tail is handled explicitly
    // instead of being dropped or run through the block path.
    private static byte[] RawOuter(G0sEntry e, Stream source, long aligned, int span)
    {
        long remain = e.Size - aligned;
        if (remain <= 0) return Array.Empty<byte>();
        if (span > remain) span = (int)remain;
        if (span <= 0) return Array.Empty<byte>();

        source.Position = 16L * e.Offset + aligned;
        var buf = new byte[span];
        int n = 0;
        while (n < span)
        {
            int r = source.Read(buf, n, span - n);
            if (r == 0) break;
            n += r;
        }
        if (n == 0) return Array.Empty<byte>();
        if (n < span) buf = buf[..n];

        OuterFrom(buf, e.Offset, aligned / 8);

        // Trailing partial block of the WHOLE blob (only when this range reaches it).
        int tail = (int)(e.Size % 8);
        if (tail > 0 && aligned + buf.Length > e.Size - tail)
        {
            long tailStart = e.Size - tail;                       // absolute
            int at = (int)(tailStart - aligned);                  // index within buf
            if (at >= 0 && at < buf.Length)
                OuterTail(buf, at, Math.Min(tail, buf.Length - at), e.Offset, e.Size);
        }
        return buf;
    }

    // Mirrors the remainder loop of DeEncryptQar. Its seed depends on the block
    // count of the ENTIRE blob, which is known from the entry size.
    private static void OuterTail(byte[] data, int at, int count, uint entryOffset, uint size)
    {
        unchecked
        {
            uint v5 = 8 * ((size / 8) + 2 * entryOffset);
            uint v10 = 6339797 * v5;
            uint v11 = 0;
            for (int i = 0; i < count; i++)
            {
                ulong pair = ((ulong)v11 << 32) + v10;
                data[at + i] ^= (byte)(pair >> 16);
                v11 = (uint)((pair + 6339797) >> 32);
                v10 += 6339797;
            }
        }
    }

    // Mirrors G0sCrypto.DeEncryptQar for whole blocks, starting at block `blockIndex`.
    // The original walks `low` forward one block at a time from the entry's offset;
    // the same value is reachable directly, so a range costs no winding loop.
    private static void OuterFrom(byte[] data, uint entryOffset, long blockIndex)
    {
        unchecked
        {
            int low = (int)(101436752 * entryOffset + 12679594) + (int)(50718376L * blockIndex);
            int at = 0;
            for (int i = 0; i < data.Length / 8; i++)
            {
                data[at]     ^= (byte)(((ulong)low - 12679594) >> 16);
                data[at + 1] ^= (byte)(((ulong)low - 6339797) >> 16);
                data[at + 2] ^= (byte)((ulong)low >> 16);
                data[at + 3] ^= (byte)(((ulong)low + 6339797) >> 16);
                data[at + 4] ^= (byte)(((ulong)low + 12679594) >> 16);
                data[at + 5] ^= (byte)(((ulong)low + 19019391) >> 16);
                data[at + 6] ^= (byte)(((ulong)low + 25359188) >> 16);
                data[at + 7] ^= (byte)(((ulong)low + 31698985) >> 16);
                at += 8;
                low += 50718376;
            }
        }
    }

    // Mirrors G0sCrypto.DeEncrypt, wound `skipWords` 4-byte words forward first,
    // stopping at `end` so a trailing partial word is left untouched.
    private static void InnerFrom(byte[] data, uint key, long skipWords, int end)
    {
        unchecked
        {
            uint i = 69069 * key;
            uint v5 = key | ((key ^ 0xFFFFCDEC) << 16);
            for (long w = 0; w < skipWords; w++) v5 = 3 * (i + 23023 * v5);

            for (int p = 0; p + 4 <= end; p += 4)
            {
                uint x = v5 ^ BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p, 4), x);
                v5 = 3 * (i + 23023 * v5);
            }
        }
    }
}
