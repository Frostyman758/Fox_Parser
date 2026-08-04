// Mirror a gani across a rig plane, in place
// 04/08/2026
using System;
using System.Numerics;
using System.Collections.Generic;

namespace MgsvModBldr.Tools.Mtar.Transcode
{
    /// <summary>
    /// Reflecting an animation needs NO re-encoding. Two properties of the format make it a
    /// bit edit:
    ///
    ///  1. A quat key is stored as [theta | x | y] magnitudes plus THREE SIGN BITS, with z
    ///     derived as (1-x)-y. The sign bits are read nowhere else, so flipping one negates
    ///     exactly one component (fox::anim::MT_GetQuatDataFromBuffer @0x14191ca80). Mirroring
    ///     across the plane whose normal is axis N is q -> negate the two components that are
    ///     NOT N, i.e. two bit flips.
    ///  2. A vector component is an AnimHalf or a float32, so negating one axis flips its sign
    ///     bit — one more bit.
    ///
    /// Both survive the differential types: f(q) = (x,-y,-z,w) satisfies f(a)f(b) = f(ab), so a
    /// QuatDiff's per-key deltas mirror independently, and VectorDiff is linear. Nothing is
    /// re-quantized, so mirroring twice restores the original bytes exactly.
    ///
    /// What this does NOT do is decide which units are a left/right pair — that is rig data,
    /// and it is the caller's to supply.
    /// </summary>
    public static class GaniMirror
    {
        /// <summary>Which axis the mirror plane's normal points along.</summary>
        public enum Axis { X = 0, Y = 1, Z = 2 }

        /// <summary>
        /// Pairs read from a rig's own `MirrorL` / `MirrorR` masks — the two the engine's
        /// PoseOperatorMirror uses. This is the authority; prefer it over the table below.
        /// Empty for a rig with no mirror masks, which is the honest answer for one that was
        /// never authored to mirror.
        /// </summary>
        public static List<(int, int)> PairsFromRig(MgsvModBldr.Tools.Anim.FrigFile frig)
            => frig?.MirrorPairs() ?? new List<(int, int)>();

        /// <summary>
        /// Fallback for the 18-unit human rig GZ and TPP share, by StrCode32 unit name, used
        /// when no rig is supplied. Originally inferred from layout shape; since CONFIRMED to
        /// match `human_finger.frig`'s MirrorL/MirrorR exactly (6/8, 7/9, 10/12, 11/13, 14/15,
        /// 16/17). It only fits that one rig — pass the .frig for anything else.
        /// </summary>
        public static readonly (uint, uint)[] HumanRigPairs =
        {
            (0xf288bffe, 0x7afa9000),   // arms
            (0x60ed6c59, 0xfd19f0f6),   // hands
            (0x5e9cf7e6, 0x72497575),   // legs
            (0x8a2ba763, 0xde542e60),   // feet
            (0x27351e54, 0xa03a6769),
            (0xea4d1b8b, 0x8daa7db8),   // finger sets
        };

        /// <summary>Resolve name-hash pairs to this gani's unit indices; unknown names drop out.</summary>
        public static List<(int, int)> PairIndices(V1Gani g, IEnumerable<(uint, uint)> pairs)
        {
            var idx = new Dictionary<uint, int>();
            for (int i = 0; i < g.Units.Count; i++) idx[g.Units[i].Name] = i;
            var outp = new List<(int, int)>();
            foreach (var (a, b) in pairs ?? HumanRigPairs)
                if (idx.TryGetValue(a, out var ia) && idx.TryGetValue(b, out var ib)) outp.Add((ia, ib));
            return outp;
        }

        /// <summary>
        /// Mirror every segment of <paramref name="g"/> in place, then swap the units listed in
        /// <paramref name="pairs"/> (index pairs; units not listed stay put, which is right for
        /// the centre line — root, spine, head).
        /// </summary>
        /// <summary>
        /// Mirror, then carry the IK BEND PLANE across with the data.
        ///
        /// A limb's roll reference is `chain_plane_normal`, fixed per unit in the .frig — the
        /// left arm's is (0,-1,0) and the right arm's (0,+1,0), and both legs' are (1,0,0). An
        /// X-mirror leaves Y alone and flips X, so after the left/right swap EVERY arm and leg
        /// lands on a unit whose normal is the wrong sign, and the solve rolls the bone 180°.
        /// The clip is correct and the limb still renders inside-out.
        ///
        /// The rig cannot be changed per clip, so the compensation goes where we are already
        /// authoring: the unit's POLE channel — the last rotation channel of an IK unit — gets
        /// the same 180° roll, cancelling the flipped reference. That rotation PERMUTES
        /// quaternion components, so unlike the reflection it cannot be a sign-bit flip; those
        /// keys are decoded, rotated and re-quantised in place (AnimBitWriter), which keeps
        /// every key size and frame delta byte-identical.
        /// </summary>
        /// <summary>Which IK units get the bend-plane compensation.</summary>
        public enum Comp { None, Arms, All, ArmsAndFlipLegs, LegBend }

        public static void Apply(V1Gani g, Axis axis, IEnumerable<(int, int)> pairs,
                                 MgsvModBldr.Tools.Anim.FrigFile frig)
            => Apply(g, axis, pairs, frig, Comp.LegBend);

        /// <summary>
        /// Arms and legs mismatch for DIFFERENT reasons and may not want the same fix. The two
        /// arm units carry OPPOSITE normals ((0,-1,0) and (0,+1,0)) which an X-mirror leaves
        /// alone, so the destination is simply the wrong sign. Both leg units carry the SAME
        /// normal (1,0,0), which the mirror itself flips. Same symptom, different cause — hence
        /// the switch rather than one rule applied to both.
        /// </summary>
        public static void Apply(V1Gani g, Axis axis, IEnumerable<(int, int)> pairs,
                                 MgsvModBldr.Tools.Anim.FrigFile frig, Comp comp)
        {
            Apply(g, axis, pairs);
            if (frig is null || pairs is null || comp == Comp.None) return;

            var n = axis == Axis.Y ? Vector3.UnitY : axis == Axis.Z ? Vector3.UnitZ : Vector3.UnitX;
            foreach (var (a, b) in pairs)
                foreach (var (from, to) in new[] { (a, b), (b, a) })
                {
                    if (from < 0 || to < 0 || from >= frig.Units.Count || to >= frig.Units.Count) continue;
                    var src = frig.Units[from].PlaneNormal;
                    var dst = frig.Units[to].PlaneNormal;
                    if (src.LengthSquared() < 1e-6f || dst.LengthSquared() < 1e-6f) continue;
                    // Where the mirror of the source's plane already equals the destination's,
                    // the solve is consistent and nothing is owed.
                    bool isLeg = frig.Units[to].Type != MgsvModBldr.Tools.Anim.FrigFile.RigUnitType.Arm;
                    if (isLeg && comp == Comp.Arms) continue;
                    // LegBend: the knee direction is pole*UnitX, and f(q)(X) = -reflect(bendDir)
                    // — the mirror NEGATES it, so the knee bends inward. Rolling about the plane
                    // normal cannot fix that for a leg because the normal IS X and the roll
                    // leaves X fixed. Post-multiplying by 180 deg about Y maps X -> -X, which is
                    // exactly the missing negation.
                    if (isLeg && comp == Comp.LegBend)
                    {
                        RollPolePost(g, to, Vector3.UnitY);
                        continue;
                    }
                    var want = src - 2f * Vector3.Dot(src, n) * n;
                    if (Vector3.Dot(Vector3.Normalize(want), Vector3.Normalize(dst)) > 0.999f) continue;
                    // ArmsAndFlipLegs rolls the legs the other way round the same axis.
                    RollPole(g, to, isLeg && comp == Comp.ArmsAndFlipLegs ? -dst : dst);
                }
        }

        /// <summary>Post-multiply the pole by 180° about an axis: q -> q * r, which rotates the
        /// pole's OWN axes rather than the world, so pole*UnitX flips when the axis is not X.</summary>
        private static void RollPolePost(V1Gani g, int unit, Vector3 axis)
        {
            if (unit < 0 || unit >= g.Units.Count) return;
            var u = g.Units[unit];
            V1Segment pole = null;
            foreach (var s in u.Segments) if (s.Type is 0 or 5 && s.HasData) pole = s;
            if (pole is null) return;
            RotateQuatKeys(pole, (u.Flags & 0x4) != 0, g.FrameCount,
                           Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI), post: true);
        }

        /// <summary>Rotate a unit's pole channel (its LAST rotation segment) 180° about <paramref name="axis"/>.</summary>
        private static void RollPole(V1Gani g, int unit, Vector3 axis)
        {
            if (unit < 0 || unit >= g.Units.Count) return;
            var u = g.Units[unit];
            V1Segment pole = null;
            foreach (var s in u.Segments) if (s.Type is 0 or 5 && s.HasData) pole = s;   // last quat segment
            if (pole is null) return;

            var roll = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
            RotateQuatKeys(pole, (u.Flags & 0x4) != 0, g.FrameCount, roll);
        }

        /// <summary>Decode, rotate and re-quantise every key of a quat segment, in place.</summary>
        private static void RotateQuatKeys(V1Segment s, bool isStatic, int frameCount, Quaternion by, bool post = false)
        {
            int bits = s.ComponentBitSize;
            if (bits <= 0) return;
            var blob = s.Blob;
            int bitPos = 0;

            void One()
            {
                uint a = Read(blob, bitPos, bits);
                uint b = Read(blob, bitPos + bits, bits);
                uint c = Read(blob, bitPos + 2 * bits, bits);
                uint sg = Read(blob, bitPos + 3 * bits, 3);
                var q = MgsvModBldr.Tools.Anim.FoxVectormath.DequantQuat(a, b, c, sg, bits);
                MgsvModBldr.Tools.Anim.AnimBitWriter.WriteQuat(blob, bitPos, bits, Quaternion.Normalize(post ? q * by : by * q));
            }

            One();
            bitPos += 3 * bits + 3;
            if (isStatic) return;
            int acc = 0;
            while (acc < frameCount)
            {
                if ((bitPos + 8 + 3 * bits + 3 + 7) / 8 > blob.Length) return;
                acc += (int)Read(blob, bitPos, 8);
                bitPos += 8;
                One();
                bitPos += 3 * bits + 3;
            }
        }

        private static uint Read(byte[] buf, int bitPos, int bitSize) => ReadBits(buf, bitPos, bitSize);

        public static void Apply(V1Gani g, Axis axis, IEnumerable<(int, int)> pairs)
        {
            foreach (var u in g.Units)
                foreach (var s in u.Segments)
                    if (s.HasData) MirrorSegment(s, g, u, axis);

            if (pairs is null) return;
            foreach (var (a, b) in pairs)
            {
                if (a < 0 || b < 0 || a >= g.Units.Count || b >= g.Units.Count || a == b) continue;
                (g.Units[a].Segments, g.Units[b].Segments) = (g.Units[b].Segments, g.Units[a].Segments);
                (g.Units[a].Flags, g.Units[b].Flags) = (g.Units[b].Flags, g.Units[a].Flags);
            }
        }

        private static void MirrorSegment(V1Segment s, V1Gani g, V1Unit u, Axis axis)
        {
            bool isStatic = (u.Flags & 0x4) != 0;
            bool hermite = (u.Flags & 0x2) != 0;
            switch (s.Type)
            {
                case 0:                        // Quat
                case 5:                        // QuatDiff
                    MirrorQuats(s.Blob, s.ComponentBitSize, isStatic, g.FrameCount, axis);
                    break;
                case 3:                        // Vector3
                case 6:                        // VectorDiff
                    MirrorVectors(s.Blob, 3, s.ComponentBitSize, isStatic, hermite, g.FrameCount, (int)axis);
                    break;
                // FLOAT / VECTOR2 / VECTOR4 are aux/shader channels, not bone transforms.
            }
        }

        /// <summary>
        /// Walk the quat bitstream exactly as the decoder does — key0 is [3 x bits][3 signs],
        /// every later key is [8-bit frame delta][3 x bits][3 signs] — and flip the two sign
        /// bits for the components perpendicular to the mirror normal.
        /// </summary>
        private static void MirrorQuats(byte[] blob, int bits, bool isStatic, int frameCount, Axis axis)
        {
            if (bits <= 0) return;
            int keep = (int)axis;                       // the component the mirror preserves
            int bitPos = 0;
            FlipSigns(blob, bitPos + 3 * bits, keep);
            bitPos += 3 * bits + 3;
            if (isStatic) return;

            int acc = 0;
            while (acc < frameCount)
            {
                if ((bitPos + 8 + 3 * bits + 3 + 7) / 8 > blob.Length) return;   // truncated
                acc += (int)ReadBits(blob, bitPos, 8);
                bitPos += 8;
                FlipSigns(blob, bitPos + 3 * bits, keep);
                bitPos += 3 * bits + 3;
            }
        }

        /// <summary>Flip the two of the three sign bits that are not <paramref name="keep"/>.</summary>
        private static void FlipSigns(byte[] blob, int signBitPos, int keep)
        {
            for (int c = 0; c < 3; c++)
            {
                if (c == keep) continue;
                int p = signBitPos + c;
                int by = p >> 3;
                if (by >= blob.Length) return;
                blob[by] ^= (byte)(1 << (p & 7));
            }
        }

        private static uint ReadBits(byte[] buf, int bitPos, int bitSize)
        {
            int bytePos = bitPos >> 3, bitOffset = bitPos & 7;
            int total = (bitOffset + bitSize + 7) >> 3;
            ulong raw = 0;
            for (int i = 0; i < total && bytePos + i < buf.Length; i++) raw |= (ulong)buf[bytePos + i] << (8 * i);
            return (uint)((raw >> bitOffset) & ((1UL << bitSize) - 1));
        }

        /// <summary>
        /// Vector streams are byte-aligned: key0 is the components, every later key is
        /// [1-byte delta][components] plus, when the unit is hermite, a second set of
        /// components for the tangent — which mirrors the same way.
        /// </summary>
        private static void MirrorVectors(byte[] blob, int comps, int bits, bool isStatic, bool hermite, int frameCount, int axis)
        {
            int sz = bits == 16 ? 2 : bits == 32 ? 4 : 0;
            if (sz == 0) return;
            int off = 0;
            NegateComp(blob, off, axis, sz);
            off += comps * sz;
            if (isStatic) return;

            int acc = 0;
            while (acc < frameCount)
            {
                if (off >= blob.Length) return;
                acc += blob[off++];
                if (off + comps * sz > blob.Length) return;
                NegateComp(blob, off, axis, sz);
                off += comps * sz;
                if (hermite)
                {
                    if (off + comps * sz > blob.Length) return;
                    NegateComp(blob, off, axis, sz);
                    off += comps * sz;
                }
            }
        }

        /// <summary>Flip the sign bit of one component — AnimHalf 0x8000, float32 0x80000000.</summary>
        private static void NegateComp(byte[] blob, int baseOff, int comp, int sz)
        {
            int at = baseOff + comp * sz + sz - 1;      // little-endian: sign is the top byte
            if (at >= 0 && at < blob.Length) blob[at] ^= 0x80;
        }
    }
}
