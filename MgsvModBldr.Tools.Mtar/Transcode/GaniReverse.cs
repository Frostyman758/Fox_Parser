// Play a gani backwards: last frame to first
// 04/08/2026
using System;
using System.Collections.Generic;
using System.Numerics;

namespace MgsvModBldr.Tools.Mtar.Transcode
{
    /// <summary>
    /// Reverses a clip in place. It is a pure REORDER — keys keep their sizes and the frame
    /// deltas keep their values, the lists simply run backwards (d'_i = d_(n-i+1)), so the same
    /// multiset is written and every 8-bit delta still fits. Nothing about the layout, offsets
    /// or blob lengths moves.
    ///
    /// Keys are moved as RAW BITS, not re-quantised, so a reversal is lossless.
    ///
    /// The Diff types (QuatDiff / VectorDiff) are NOT running accumulators here: the sampler
    /// slerps/lerps QuatKeys[seg] -> QuatKeys[seg+1] straight out of the stream, so "diff"
    /// describes the packing, not a chain. Treating them as a chain compounded a clip's total
    /// turn into the reversal and put every 90/135/180-degree clip a half-turn out.
    ///
    /// The one value that is not just moved is a HERMITE tangent: it points along travel, so
    /// reversing the clip negates it. Key 0 stores no tangent, and the sampler reads the first
    /// segment as m0 == m1, so the last output key inherits key 1's.
    /// </summary>
    public static class GaniReverse
    {
        public static void Apply(V1Gani g)
        {
            foreach (var u in g.Units)
            {
                bool isStatic = (u.Flags & 0x4) != 0;
                bool hermite = (u.Flags & 0x2) != 0;
                if (isStatic) continue;                    // one key: nothing to reverse
                foreach (var s in u.Segments)
                {
                    if (!s.HasData) continue;
                    switch (s.Type)
                    {
                        case 0: case 5: ReverseQuat(s, g.FrameCount); break;
                        case 3: case 6: ReverseVector(s, g.FrameCount, hermite); break;
                    }
                }
            }
        }

        // ── quaternion streams: [3 x bits | 3 signs], later keys prefixed by an 8-bit delta ──

        private static void ReverseQuat(V1Segment s, int frameCount)
        {
            int bits = s.ComponentBitSize;
            if (bits <= 0) return;
            var blob = s.Blob;
            int key = 3 * bits + 3;

            var deltas = new List<int> { 0 };
            var raw = new List<ulong>();
            int bitPos = 0;
            raw.Add(ReadBits64(blob, bitPos, key));
            bitPos += key;
            int acc = 0;
            while (acc < frameCount)
            {
                if ((bitPos + 8 + key + 7) / 8 > blob.Length) return;
                int d = (int)ReadBits64(blob, bitPos, 8);
                bitPos += 8;
                acc += d;
                deltas.Add(d);
                raw.Add(ReadBits64(blob, bitPos, key));
                bitPos += key;
            }

            int n = raw.Count;
            if (n < 2) return;

            bitPos = 0;
            WriteBits64(blob, bitPos, key, raw[n - 1]);
            bitPos += key;
            for (int i = 1; i < n; i++)
            {
                WriteBits64(blob, bitPos, 8, (ulong)deltas[n - i]);
                bitPos += 8;
                WriteBits64(blob, bitPos, key, raw[n - 1 - i]);
                bitPos += key;
            }
        }

        // ── vector streams: byte-aligned, [comps], later keys [1-byte delta][comps](+tangent) ──

        private static void ReverseVector(V1Segment s, int frameCount, bool hermite)
        {
            int sz = s.ComponentBitSize == 16 ? 2 : s.ComponentBitSize == 32 ? 4 : 0;
            if (sz == 0) return;
            var blob = s.Blob;
            int comps = 3, vlen = comps * sz, block = vlen * (hermite ? 2 : 1);

            var deltas = new List<int> { 0 };
            var vals = new List<byte[]>();
            var tans = new List<Vector3> { default };      // key0 has no stored tangent
            int off = 0;
            if (off + vlen > blob.Length) return;
            vals.Add(Slice(blob, off, vlen));
            off += vlen;
            int acc = 0;
            while (acc < frameCount)
            {
                if (off >= blob.Length) return;
                int d = blob[off++];
                acc += d;
                if (off + block > blob.Length) return;
                deltas.Add(d);
                vals.Add(Slice(blob, off, vlen));
                if (hermite) tans.Add(ReadVec(blob, off + vlen, sz));
                off += block;
            }

            int n = vals.Count;
            if (n < 2) return;
            if (hermite) tans[0] = tans[1];                // sampler reads m0 == m1 on segment 0

            off = 0;
            Buffer.BlockCopy(vals[n - 1], 0, blob, off, vlen);
            off += vlen;
            for (int i = 1; i < n; i++)
            {
                blob[off++] = (byte)deltas[n - i];
                Buffer.BlockCopy(vals[n - 1 - i], 0, blob, off, vlen);
                if (hermite) WriteVec(blob, off + vlen, sz, -tans[n - 1 - i]);
                off += block;
            }
        }

        // ── helpers ──

        private static byte[] Slice(byte[] b, int at, int len)
        {
            var o = new byte[len];
            Buffer.BlockCopy(b, at, o, 0, Math.Min(len, b.Length - at));
            return o;
        }

        private static Vector3 ReadVec(byte[] b, int at, int sz) => sz == 2
            ? new Vector3(Half(b, at), Half(b, at + 2), Half(b, at + 4))
            : new Vector3(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));

        private static void WriteVec(byte[] b, int at, int sz, Vector3 v)
        {
            if (at < 0 || at + 3 * sz > b.Length) return;
            if (sz == 2) { PutHalf(b, at, v.X); PutHalf(b, at + 2, v.Y); PutHalf(b, at + 4, v.Z); }
            else { BitConverter.GetBytes(v.X).CopyTo(b, at); BitConverter.GetBytes(v.Y).CopyTo(b, at + 4); BitConverter.GetBytes(v.Z).CopyTo(b, at + 8); }
        }

        /// <summary>AnimHalf -> float, matching AnimBitReader.ReadAnimHalf.</summary>
        private static float Half(byte[] b, int at)
        {
            ushort v = (ushort)(b[at] | b[at + 1] << 8);
            uint num = (uint)(v & 0x7C00);
            if (num > 0) num = (num + 0x1DC00) << 13;
            num |= ((uint)(v & 0x8000) << 16) | ((uint)(v & 0x3FF) << 13);
            return BitConverter.UInt32BitsToSingle(num);
        }

        /// <summary>Its inverse — the exponent bias runs the other way.</summary>
        private static void PutHalf(byte[] b, int at, float f)
        {
            uint n = BitConverter.SingleToUInt32Bits(f);
            uint sign = (n >> 16) & 0x8000;
            uint mant = (n >> 13) & 0x3FF;
            uint exp = ((n >> 13) - 0x1DC00) & 0x7C00;
            if ((n & 0x7F800000) == 0) exp = 0;                  // zero / denormal
            ushort v = (ushort)(sign | exp | mant);
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8);
        }

        // A quat key is up to 3*16+3 = 51 bits, so it does not fit the 32-bit helpers.

        private static ulong ReadBits64(byte[] buf, int bitPos, int bitSize)
        {
            ulong v = 0;
            for (int i = 0; i < bitSize; i++)
            {
                int p = bitPos + i, by = p >> 3;
                if (by >= buf.Length) break;
                if ((buf[by] >> (p & 7) & 1) != 0) v |= 1UL << i;
            }
            return v;
        }

        private static void WriteBits64(byte[] buf, int bitPos, int bitSize, ulong value)
        {
            for (int i = 0; i < bitSize; i++)
            {
                int p = bitPos + i, by = p >> 3, bit = p & 7;
                if (by >= buf.Length) break;
                if ((value >> i & 1) != 0) buf[by] |= (byte)(1 << bit);
                else buf[by] &= (byte)~(1 << bit);
            }
        }
    }
}
