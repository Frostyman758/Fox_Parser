// Quantise a quaternion back into a gani keyframe stream
// 04/08/2026
using System.Numerics;

namespace MgsvModBldr.Tools.Anim;

// The inverse of AnimBitReader.ReadQuat / FoxVectormath.DequantQuat.
//
// A key is [theta | x | y] magnitudes at bitSize each, plus three sign bits, with z derived as
// (1-x)-y. So the stored triple is L1-normalised (x+y+z = 1) and the decoder L2-normalises it
// to recover the unit axis; theta is the half-angle scaled to the field, and w is not stored.
//
// Writing back into the SAME bit positions keeps every key's size and frame delta intact, so a
// clip can be re-quantised in place without touching its layout, offsets or blob lengths.
public static class AnimBitWriter
{
    /// <summary>Overwrite bitSize bits at bitPos, LE, matching AnimBitReader.ReadBits.</summary>
    public static void WriteBits(byte[] buf, int bitPos, int bitSize, uint value)
    {
        for (int i = 0; i < bitSize; i++)
        {
            int p = bitPos + i;
            int by = p >> 3, bit = p & 7;
            if (by >= buf.Length) return;
            uint b = (value >> i) & 1u;
            buf[by] = (byte)(b != 0 ? buf[by] | (1 << bit) : buf[by] & ~(1 << bit));
        }
    }

    /// <summary>
    /// Quantise a rotation to (theta, x, y, signs). The axis magnitudes are stored L1-normalised
    /// because the decoder rebuilds z as (1-x)-y; the sign bits carry each component's sign, so
    /// a reflection that only negates components is a pure bit flip and needs no re-quantising —
    /// this is for transforms that genuinely move a key, like a roll about the bend plane.
    /// </summary>
    public static (uint a, uint b, uint c, uint signs) QuantQuat(Quaternion q, int bitSize)
    {
        // w is recovered as cos(halfTheta) and is never stored, so the hemisphere must be
        // positive or the angle comes back wrong.
        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        q = Quaternion.Normalize(q);

        int maskI = (1 << bitSize) - 1;
        float fmask = maskI;

        float w = Math.Clamp(q.W, -1f, 1f);
        double halfTheta = Math.Acos(w);
        double ft = halfTheta / (Math.PI * 0.5);            // DequantQuat: halfTheta = ft*Pi*0.5
        uint a = (uint)Math.Clamp(Math.Round(ft * fmask), 0, maskI);

        double sin = Math.Sqrt(Math.Max(0, 1.0 - (double)w * w));
        double ax = Math.Abs(q.X), ay = Math.Abs(q.Y), az = Math.Abs(q.Z);
        double s = ax + ay + az;
        if (s < 1e-9 || sin < 1e-9) { ax = 1; ay = 0; az = 0; s = 1; }

        uint b = (uint)Math.Clamp(Math.Round(ax / s * fmask), 0, maskI);
        uint c = (uint)Math.Clamp(Math.Round(ay / s * fmask), 0, maskI);

        uint signs = 0;
        if (q.X < 0) signs |= 1;
        if (q.Y < 0) signs |= 2;
        if (q.Z < 0) signs |= 4;
        return (a, b, c, signs);
    }

    /// <summary>Write a quantised key at bitPos: three magnitudes then three sign bits.</summary>
    public static void WriteQuat(byte[] buf, int bitPos, int bitSize, Quaternion q)
    {
        var (a, b, c, signs) = QuantQuat(q, bitSize);
        WriteBits(buf, bitPos, bitSize, a);
        WriteBits(buf, bitPos + bitSize, bitSize, b);
        WriteBits(buf, bitPos + 2 * bitSize, bitSize, c);
        WriteBits(buf, bitPos + 3 * bitSize, 3, signs);
    }
}
