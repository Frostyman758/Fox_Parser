// Gani track bit reader
// Ported verbatim from FoxBrowser.Models.Anim 04/08/2026 — do not re-derive, copy.
using System.Numerics;

namespace MgsvModBldr.Tools.Anim;

// Segment value type (gani SegmentType enum; the runtime dispatches on TrackData byte 6 & 0xF).
public enum GaniSegmentType { Quat = 0, Float = 1, Vector2 = 2, Vector3 = 3, Vector4 = 4, QuatDiff = 5, VectorDiff = 6 }

// One decoded segment: keyframe deltas + typed values, produced with the game's exact
// decode math (see FoxVectormath). Deltas are RELATIVE (Deltas[0] == 0); callers
// accumulate them into absolute frames.
public sealed class GaniSegKeys
{
    public GaniSegmentType Type;
    public bool IsHermite;
    public int[] Deltas = Array.Empty<int>();
    public Quaternion[] Quats = Array.Empty<Quaternion>();   // QUAT / QUAT_DIFF (raw — NO hemisphere fix; the game corrects at slerp time)
    public Vector3[] Vecs = Array.Empty<Vector3>();          // VECTOR3 / VECTOR_DIFF values
    public Vector3[] Tans = Array.Empty<Vector3>();          // hermite tangents; Tans[0] is unset (stream stores none for key0)
    public int Count => Deltas.Length;
}

// Bit/byte-level decoder for Fox Engine gani animation streams — a 1:1 port of the
// decode paths in Tpp_main_win64 (BN decomp):
//  • quat streams:  fox::anim::MT_GetQuatDataFromBuffer @0x14191ca80, driven by
//    DataQuatSegment::InitNextQuatDataHead/SetNextQuatDataCore. The stream is addressed
//    as 16-bit little-endian words + a bit offset; layout = [key0 quat][8-bit delta,
//    quat]… where a quat costs 3*bitSize + 3 bits (theta, x, y, then 3 sign bits).
//  • vector streams: DataVectorSegment::InitVectorLinear* — byte-aligned; layout =
//    [key0 comps][delta u8, comps]…, each comp an AnimHalf (16) or float32 (32).
//  • hermite streams: DataVectorSegmentHermite::InitVectorHermiteControl — like vector
//    but every key AFTER the first carries [delta u8][value comps][tangent comps].
// The game reads these via (u16 word index, bit offset) pairs; since all vector pieces
// are byte-sized that collapses to a plain little-endian byte stream, identical to the
// straight byte reads used here. AnimHalf matches the game's inline expansion
// ((exp+0x1DC00)<<13 form) bit-for-bit.
public static class AnimBitReader
{
    // LE bitstream read (≤ 32 bits). Matches the game's word-wise
    // ((w0 | w1<<16 | w2<<32) >> bitOffset) & mask — LE words == LE byte bit order.
    public static uint ReadBits(byte[] buf, ref int bitPos, int bitSize)
    {
        if (bitSize == 0) return 0;
        int bytePos = bitPos >> 3;
        int bitOffset = bitPos & 7;
        int totalBytes = (bitOffset + bitSize + 7) >> 3;
        ulong raw = 0;
        for (int i = 0; i < totalBytes; i++) raw |= (ulong)buf[bytePos + i] << (8 * i);
        bitPos += bitSize;
        ulong mask = (1UL << bitSize) - 1;
        return (uint)((raw >> bitOffset) & mask);
    }

    // One packed quat: theta, x, y (bitSize each) + 3 sign bits, dequantized with the
    // game's exact float32 math (MT_GetQuatDataFromBuffer). The decomp's sign-extend +
    // mod-2^bits dance is a verified no-op — components enter the float convert as the
    // raw unsigned values.
    public static Quaternion ReadQuat(byte[] buf, ref int bitPos, int bitSize)
    {
        uint a = ReadBits(buf, ref bitPos, bitSize);
        uint b = ReadBits(buf, ref bitPos, bitSize);
        uint c = ReadBits(buf, ref bitPos, bitSize);
        uint signs = ReadBits(buf, ref bitPos, 3);
        return FoxVectormath.DequantQuat(a, b, c, signs, bitSize);
    }

    // AnimHalf → float32, identical to the game's inline expansion (and to the previous
    // implementation): sign<<16, (exp+0x1DC00)<<13 when exp≠0, mantissa<<13.
    public static float ReadAnimHalf(byte[] buf, ref int offset)
    {
        ushort value = (ushort)(buf[offset] | buf[offset + 1] << 8);
        offset += 2;
        uint num = (uint)(value & 0x7C00);
        if (num > 0) num = (num + 0x1DC00) << 13;
        num |= ((uint)(value & 0x8000) << 16) | ((uint)(value & 0x3FF) << 13);
        return BitConverter.UInt32BitsToSingle(num);
    }

    public static float ReadF32(byte[] buf, ref int offset)
    {
        float v = BitConverter.ToSingle(buf, offset);
        offset += 4;
        return v;
    }

    private static Vector3 ReadVec3(byte[] buf, ref int offset, int componentBits)
    {
        if (componentBits == 0) return default;
        if (componentBits == 16)
            return new Vector3(ReadAnimHalf(buf, ref offset), ReadAnimHalf(buf, ref offset), ReadAnimHalf(buf, ref offset));
        return new Vector3(ReadF32(buf, ref offset), ReadF32(buf, ref offset), ReadF32(buf, ref offset));
    }

    private static void SkipComps(byte[] buf, ref int offset, int comps, int componentBits)
        => offset += comps * (componentBits == 16 ? 2 : 4);

    private static int CompCount(GaniSegmentType type) => type switch
    {
        GaniSegmentType.Float => 1,
        GaniSegmentType.Vector2 => 2,
        GaniSegmentType.Vector3 or GaniSegmentType.VectorDiff => 3,
        GaniSegmentType.Vector4 => 4,
        _ => 3,
    };

    // Decode a whole segment's keyframes. `dataOffset` is the ABSOLUTE byte offset of
    // the blob. Static segments hold only key0. Non-static keys are read until the
    // accumulated delta reaches frameCount, mirroring the game's play-to-total loop.
    public static GaniSegKeys DecodeSegment(byte[] data, int dataOffset, GaniSegmentType type,
        int componentBits, bool isStatic, bool hermite, int frameCount)
    {
        var seg = new GaniSegKeys { Type = type, IsHermite = hermite };

        if (type is GaniSegmentType.Quat or GaniSegmentType.QuatDiff)
        {
            var deltas = new List<int> { 0 };
            var quats = new List<Quaternion>();
            int bitPos = dataOffset * 8;
            quats.Add(ReadQuat(data, ref bitPos, componentBits));
            if (!isStatic)
            {
                int acc = 0;
                while (acc < frameCount)
                {
                    int delta = (int)ReadBits(data, ref bitPos, 8);
                    acc += delta;
                    quats.Add(ReadQuat(data, ref bitPos, componentBits));
                    deltas.Add(delta);
                }
            }
            seg.Deltas = deltas.ToArray();
            seg.Quats = quats.ToArray();
            return seg;
        }

        // vector-family segments: byte stream.
        int comps = CompCount(type);
        int off = dataOffset;
        var vDeltas = new List<int> { 0 };
        var vecs = new List<Vector3>();
        var tans = hermite ? new List<Vector3> { default } : null;   // key0 has no stored tangent

        vecs.Add(comps == 3 ? ReadVec3(data, ref off, componentBits) : ReadOther(data, ref off, comps, componentBits));
        if (!isStatic)
        {
            int acc = 0;
            while (acc < frameCount)
            {
                int delta = data[off++];
                acc += delta;
                vecs.Add(comps == 3 ? ReadVec3(data, ref off, componentBits) : ReadOther(data, ref off, comps, componentBits));
                if (hermite)
                    tans!.Add(comps == 3 ? ReadVec3(data, ref off, componentBits) : ReadOther(data, ref off, comps, componentBits));
                vDeltas.Add(delta);
            }
        }
        seg.Deltas = vDeltas.ToArray();
        seg.Vecs = vecs.ToArray();
        if (hermite) seg.Tans = tans!.ToArray();
        return seg;
    }

    // FLOAT / VECTOR2 / VECTOR4 are aux/shader channels, not bone transforms; decode
    // the first up-to-3 components into a Vector3 so callers can still inspect them.
    private static Vector3 ReadOther(byte[] buf, ref int offset, int comps, int componentBits)
    {
        Span<float> v = stackalloc float[4];
        for (int i = 0; i < comps; i++)
            v[i] = componentBits == 16 ? ReadAnimHalf(buf, ref offset) : componentBits == 0 ? 0f : ReadF32(buf, ref offset);
        return new Vector3(v[0], v[1], v[2]);
    }
}
