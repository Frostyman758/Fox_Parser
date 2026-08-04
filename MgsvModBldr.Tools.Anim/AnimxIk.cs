// Engine two-bone IK, exact port
// Ported verbatim from FoxBrowser.Models.Anim 04/08/2026 — do not re-derive, copy.
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MgsvModBldr.Tools.Anim;

// Exact float32 port of the engine's two-bone IK (BN decomp, fox::anim):
//   • fox::anim::CalcIkTwoBone @0x1418fde30 — the shared solver every limb routes through
//   • RigTwoBoneDef::PoseToSkeletonTwoBone @0x1418fef90 — the leg driver (target clamp
//     to reach+5e-4, near-full-extension boneA stretch, pole-quat bend via double-cross)
// Same porting rules as FoxVectormath: every op in float32 in the game's order; rsqrtps
// via float.ReciprocalSqrtEstimate (same instruction), one Newton-Raphson refine
// (3 − (v·nr)·nr)·(0.5·nr). Kept in its own file so the proven base files stay untouched.
public static class AnimxIk
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float RSqrtEst(float x) => float.ReciprocalSqrtEstimate(x);

    // Vectormath quat multiply, exact per-lane grouping from the game's inlined SSE:
    //   x = ((R.w·L.x + L.w·R.x) + L.y·R.z) − L.z·R.y   (and cyclic)
    //   w = (L.w·R.w − L.z·R.z) − (L.x·R.x + L.y·R.y)
    internal static Quaternion QMul(Quaternion l, Quaternion r) => new(
        ((r.W * l.X + l.W * r.X) + l.Y * r.Z) - l.Z * r.Y,
        ((r.W * l.Y + l.W * r.Y) + l.Z * r.X) - l.X * r.Z,
        ((r.W * l.Z + l.W * r.Z) + l.X * r.Y) - l.Y * r.X,
        (l.W * r.W - l.Z * r.Z) - (l.X * r.X + l.Y * r.Y));

    // Vectormath rotate(q, v) — two-stage form as the leg driver's blocks compute it.
    internal static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        float tx = (q.W * v.X + q.Y * v.Z) - q.Z * v.Y;
        float ty = (q.W * v.Y + q.Z * v.X) - q.X * v.Z;
        float tz = (q.W * v.Z + q.X * v.Y) - q.Y * v.X;
        float tw = (q.X * v.X + q.Y * v.Y) + q.Z * v.Z;
        return new Vector3(
            ((tw * q.X + tx * q.W) - ty * q.Z) + tz * q.Y,
            ((tw * q.Y + ty * q.W) - tz * q.X) + tx * q.Z,
            ((tw * q.Z + tz * q.W) - tx * q.Y) + ty * q.X);
    }

    // rotate(q, unit-X) closed form, exact grouping from the leg driver's pole block
    // (2q computed as q+q; P = ((1−2y·y)−2z·z, 2z·w + x·2y, x·2z − 2y·w)).
    internal static Vector3 RotateUnitX(Quaternion q)
    {
        float y2 = q.Y + q.Y, z2 = q.Z + q.Z;
        return new Vector3(
            (1f - y2 * q.Y) - z2 * q.Z,
            z2 * q.W + q.X * y2,
            q.X * z2 - y2 * q.W);
    }

    // rotate(q, unit-Y) closed form, exact grouping from the AnimalLeg pole block
    // (P = (y·2x − 2z·w, (1 − 2z·z) − 2x·x, 2x·w + y·2z)).
    internal static Vector3 RotateUnitY(Quaternion q)
    {
        float x2 = q.X + q.X, z2 = q.Z + q.Z;
        return new Vector3(
            q.Y * x2 - z2 * q.W,
            (1f - z2 * q.Z) - x2 * q.X,
            x2 * q.W + q.Y * z2);
    }

    internal static Vector3 Cross(Vector3 a, Vector3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    // dot with the game's shuffle-sum order (z² + y²) + x².
    internal static float Dot3(Vector3 a, Vector3 b) => (a.Z * b.Z + a.Y * b.Y) + a.X * b.X;

    // Newton-Raphson-refined reciprocal sqrt: (3 − (v·nr)·nr)·(0.5·nr), nr = rsqrtps(v).
    internal static float NrRsqrt(float v)
    {
        float nr = RSqrtEst(v);
        return (3f - (v * nr) * nr) * (0.5f * nr);
    }

    // Vectormath Quat(const Matrix3&) — branchless SSE version collapsed to the selected
    // lane's scalar ops. cols are the images of the reference basis.
    internal static Quaternion QuatFromBasis(Vector3 col0, Vector3 col1, Vector3 col2)
    {
        float xx = col0.X, yx = col0.Y, zx = col0.Z;
        float xy = col1.X, yy = col1.Y, zy = col1.Z;
        float xz = col2.X, yz = col2.Y, zz = col2.Z;
        float trace = (yy + xx) + zz;
        bool YgtX = xx < yy, ZgtX = xx < zz, ZgtY = yy < zz;

        float sumX = yz + zy, sumY = zx + xz, sumZ = xy + yx;
        float difX = zy - yz, difY = xz - zx, difZ = yx - xy;

        int c = !(trace < 0f) ? 3 : (ZgtX && ZgtY) ? 2 : YgtX ? 1 : 0;
        float r = c switch
        {
            0 => ((xx - yy) - zz) + 1f,
            1 => ((yy - zz) - xx) + 1f,
            2 => ((zz - xx) - yy) + 1f,
            _ => trace + 1f,
        };
        float nr = RSqrtEst(r);
        float scale = ((3f - (nr * r) * nr) * (nr * 0.5f)) * 0.5f;
        return c switch
        {
            0 => new Quaternion(r * scale, sumZ * scale, sumY * scale, difX * scale),
            1 => new Quaternion(sumZ * scale, r * scale, sumX * scale, difY * scale),
            2 => new Quaternion(sumY * scale, sumX * scale, r * scale, difZ * scale),
            _ => new Quaternion(difX * scale, difY * scale, difZ * scale, r * scale),
        };
    }

    // one column of R = Σ solved_k ⊗ ref_k, game add order (crossTerm + unitTerm) + sideTerm.
    static Vector3 BasisCol(float u, float c, float s, Vector3 unitSolved, Vector3 crossBD, Vector3 crossSolved) => new(
        (c * crossBD.X + u * unitSolved.X) + s * crossSolved.X,
        (c * crossBD.Y + u * unitSolved.Y) + s * crossSolved.Y,
        (c * crossBD.Z + u * unitSolved.Z) + s * crossSolved.Z);

    // fox::anim::CalcIkTwoBone @0x1418fde30 — the engine's shared two-bone solver.
    //   A/B    = bind bone vectors (root→mid, mid→end), possibly pre-stretched
    //   side   = the rig unit's side axis (def+0x20 / .frig plane normal)
    //   bend   = unit bend direction ⟂ aim (from the pole quat, see SolveTwoBoneAtRoot)
    // Outputs the two bones' WORLD rotations as basis-change quats, chain frame → chain
    // frame (aim, plane normal, in-plane — both triples proper, decomp 4906-4986):
    //   {unitBone, side, cross(unitBone,side)} → {unitSolved, cross(bend,dir), cross(unitSolved, crossBD)}.
    internal static void CalcIkTwoBone(out Quaternion qUpper, out Quaternion qLower,
        Vector3 a, Vector3 b, Vector3 side, Vector3 root, Vector3 target, Vector3 bend)
    {
        Vector3 t = target - root;
        float a2 = Dot3(a, a), b2 = Dot3(b, b), c2 = Dot3(t, t);
        float la = MathF.Sqrt(a2), lb = MathF.Sqrt(b2), lc = MathF.Sqrt(c2);
        float ia = NrRsqrt(a2), ib = NrRsqrt(b2), ic = NrRsqrt(c2);
        Vector3 unitA = a * ia;
        Vector3 unitB = b * ib;
        Vector3 dir = t * ic;

        float along = ((a2 - b2) + c2) * (0.5f * ic);
        along = MathF.Max(along, 0f);
        if (!(lc < la + lb)) along = la;                  // unreachable → straight
        float h = MathF.Sqrt(MathF.Max(a2 - along * along, 0f));
        Vector3 midVec = new(
            along * dir.X + h * bend.X,
            along * dir.Y + h * bend.Y,
            along * dir.Z + h * bend.Z);
        Vector3 lowVec = t - midVec;
        Vector3 unitMid = midVec * NrRsqrt(Dot3(midVec, midVec));
        Vector3 unitLow = lowVec * NrRsqrt(Dot3(lowVec, lowVec));

        // ref components pair (unit→unitSolved, side→crossBD, cross→crossSolved) — engine
        // packs (unitA, side, crossA) per axis; swapping the last two reflects the basis.
        Vector3 crossBD = Cross(bend, dir);
        Vector3 crossA = Cross(unitA, side);
        Vector3 crossMid = Cross(unitMid, crossBD);
        qUpper = QuatFromBasis(
            BasisCol(unitA.X, side.X, crossA.X, unitMid, crossBD, crossMid),
            BasisCol(unitA.Y, side.Y, crossA.Y, unitMid, crossBD, crossMid),
            BasisCol(unitA.Z, side.Z, crossA.Z, unitMid, crossBD, crossMid));

        Vector3 crossB = Cross(unitB, side);
        Vector3 crossLow = Cross(unitLow, crossBD);
        qLower = QuatFromBasis(
            BasisCol(unitB.X, side.X, crossB.X, unitLow, crossBD, crossLow),
            BasisCol(unitB.Y, side.Y, crossB.Y, unitLow, crossBD, crossLow),
            BasisCol(unitB.Z, side.Z, crossB.Z, unitLow, crossBD, crossLow));
    }

    // RigTwoBoneDef::PoseToSkeletonTwoBone @0x1418fef90 from `tv = target − rootW` on —
    // the caller supplies the chain-root world position (the viewer's FK already placed it)
    // and the ABSOLUTE target point. Includes the driver's two edge behaviours exactly:
    //  • target clamped to length (|A|+|B|)+0.0005 when beyond reach
    //  • bone A stretched up to (effLen−|B|)+0.0005 near full extension (stretchy limb)
    // bend = normalize(cross(T, cross(rotate(parentRot·poleQuat, X), T))) — the pole quat
    // swings the rig's X axis; the double-cross projects it ⟂ to the aim.
    internal static void SolveTwoBoneAtRoot(out Quaternion qUpper, out Quaternion qLower, out Vector3 midW,
        Vector3 rootW, Vector3 boneA, Vector3 boneB, Vector3 side, Vector3 target,
        Quaternion parentRot, Quaternion poleQuat)
    {
        Quaternion q12 = QMul(parentRot, poleQuat);
        Vector3 p = RotateUnitX(q12);
        Vector3 tv = target - rootW;
        Vector3 bendRaw = Cross(tv, Cross(p, tv));
        Vector3 bend = bendRaw * NrRsqrt(Dot3(bendRaw, bendRaw));

        float la = MathF.Sqrt(Dot3(boneA, boneA));
        float lb = MathF.Sqrt(Dot3(boneB, boneB));
        float lt = MathF.Sqrt(Dot3(tv, tv));
        float reach = lb + la;
        float effLen = lt;
        Vector3 tvEff = tv;
        if (!(lt <= reach + 0.000500000024f))
        {
            float s = (reach + 0.000500000024f) / lt;
            tvEff = new Vector3(tv.X * s, tv.Y * s, tv.Z * s);
            effLen = reach;
        }
        Vector3 targetPoint = tvEff + rootW;
        Vector3 boneAEff = boneA;
        float minA = (effLen - lb) + 0.000500000024f;
        if (!(la >= minA))
        {
            float s = minA / la;
            boneAEff = new Vector3(boneA.X * s, boneA.Y * s, boneA.Z * s);
        }
        CalcIkTwoBone(out qUpper, out qLower, boneAEff, boneB, side, rootW, targetPoint, bend);
        midW = rootW + Rotate(qUpper, boneAEff);
    }

    // fox::anim::RigAnimalLegDef::PoseToSkeleton (dev decomp l.5736016) — the quadruped
    // 5-bone leg (chain idx0..idx4 = scapula, humerus, radius, cannon, hoof). Pose slots:
    //   slot0 = scapula MODEL rot   slot1 = bend-plane quat (its Y-image = plane normal)
    //   slot2 = cannon MODEL rot    slot3/4 = hoof target (UpdatePose writes ONE vector
    // channel into both, so the slot3≠slot4 scapula-slide branch is unreachable from anim
    // data — rot0 = slot0 verbatim). The hoof bone itself is NOT written (normal FK child
    // of the cannon). No reach clamp / stretch — raw CalcIkTwoBone.
    internal static void SolveAnimalLeg(
        out Quaternion rot0, out Quaternion qUpper, out Quaternion qLower,
        out Vector3 pos0, out Vector3 pos1, out Vector3 pos2, out Vector3 pos3,
        Vector3 parentPos, Quaternion parentRot,
        Vector3 bind0, Vector3 bind1, Vector3 bind2, Vector3 bind3, Vector3 bind4,
        Vector3 side, Vector3 targetHoof, Quaternion slot0, Quaternion slot1, Quaternion slot2)
    {
        pos0 = parentPos + Rotate(parentRot, bind0);
        Vector3 ankle = targetHoof - Rotate(slot2, bind4);   // back the hoof's bind offset out of the goal
        rot0 = slot0;
        pos1 = pos0 + Rotate(rot0, bind1);
        // bend = slot1's Y-image double-crossed ⟂ the chain aim — same construction as the
        // TwoBone driver's pole, but about unit-Y.
        Vector3 d = ankle - pos1;
        Vector3 bendRaw = Cross(d, Cross(RotateUnitY(slot1), d));
        Vector3 bend = bendRaw * NrRsqrt(Dot3(bendRaw, bendRaw));
        CalcIkTwoBone(out qUpper, out qLower, bind2, bind3, side, pos1, ankle, bend);
        pos2 = pos1 + Rotate(qUpper, bind2);
        pos3 = pos2 + Rotate(qLower, bind3);
    }
}
