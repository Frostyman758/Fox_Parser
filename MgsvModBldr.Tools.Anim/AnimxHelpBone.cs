// fox::animx help-bone operators, exact port
// Ported verbatim from FoxBrowser.Models.Anim 04/08/2026 — do not re-derive, copy.
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MgsvModBldr.Tools.Anim;

// Exact float32 port of the fox::animx help-bone operator suite from Tpp_main_win64
// (BN Mapped-MLIL-SSA dumps, see HELPBONE_DECODE.md for the per-op derivation):
//   HelpBone::ExecCore dispatch @0x141921fd0 + the fox::animx::*Operator functions
//   in AnimxHelpBone.obj, and the x0bda11d9 helper lambdas (GetLocalRot, Rotation,
//   SlerpWithNormalize, SetResult, SetRotX/Y/Z).
// Every op is kept in float32 in the game's order like FoxVectormath. IEEE add/mul
// commutativity is the only liberty taken (a+b == b+a bit-exactly).
public static class AnimxHelpBone
{
    // sinf4/cosf4 minimax constants (same table FoxVectormath.SinF4 uses)
    const float TwoOverPi = 0.63661977236f;                   // [0x141e0bd80]
    const float Kc1 = 1.57079625129f;                         // [0x141e0bda0] 0x3FC90FDA
    const float Kc2 = 7.54978995489e-8f;                      // [0x141e0bd40]
    const float Cc0 = -0.0013602249f, Cc1 = 0.0416566950f, Cc2 = -0.4999990225f;
    const float Sc0 = -0.0001950727f, Sc1 = 0.0083320758f, Sc2 = -0.1666665247f;
    internal const float HalfPiF = 1.57079637f;               // [0x14292085c] 0x3FC90FDB (÷ divisor)
    internal const float InvPiF = 0.318309873f;               // [0x142920864] 0x3EA2F983
    internal const float TwoOverPiSwell = 0.636619747f;       // Swell's inline 0x3F22F983

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Quaternion Conj(Quaternion q) => new(-q.X, -q.Y, -q.Z, q.W);

    // GetLocalRot @0x141923010: srcParent == -1 ? q[s] : conj(q[sp]) ⊗ q[s]
    internal static Quaternion LocalRot(Quaternion[] q, int s, int sp)
        => sp < 0 ? q[s] : AnimxIk.QMul(Conj(q[sp]), q[s]);

    // The quat rotate inlined in every operator. Grouping differs from
    // FoxVectormath.Rotate in the final sum: ((d·q + t·w) + tCw·qCcw) − tCcw·qCw.
    internal static Vector3 RotateHB(Quaternion q, Vector3 v)
    {
        float tx = (q.Y * v.Z + q.W * v.X) - q.Z * v.Y;
        float ty = (q.Z * v.X + q.W * v.Y) - q.X * v.Z;
        float tz = (q.X * v.Y + q.W * v.Z) - q.Y * v.X;
        float d = (q.Y * v.Y + q.X * v.X) + q.Z * v.Z;
        return new Vector3(
            ((d * q.X + tx * q.W) + tz * q.Y) - ty * q.Z,
            ((d * q.Y + ty * q.W) + tx * q.Z) - tz * q.X,
            ((d * q.Z + tz * q.W) + ty * q.X) - tx * q.Y);
    }

    // Matrix3(Quat)·v — the rotate SetResult uses for the local translation.
    // q2 = q+q; exact entry/products per the SetResult dump.
    internal static Vector3 RotateMat3(Quaternion q, Vector3 v)
    {
        float x2 = q.X + q.X, y2 = q.Y + q.Y, z2 = q.Z + q.Z;
        float m00 = (1f - y2 * q.Y) - z2 * q.Z;
        float m10 = z2 * q.W + q.X * y2;
        float m20 = q.X * z2 - y2 * q.W;
        float m01 = q.Y * x2 - z2 * q.W;
        float m11 = (1f - z2 * q.Z) - x2 * q.X;
        float m21 = x2 * q.W + q.Y * z2;
        float m02 = y2 * q.W + q.Z * x2;
        float m12 = q.Z * y2 - x2 * q.W;
        float m22 = (1f - x2 * q.X) - y2 * q.Y;
        return new Vector3(
            (v.Y * m01 + v.X * m00) + v.Z * m02,
            (v.Y * m11 + v.X * m10) + v.Z * m12,
            (v.Y * m21 + v.X * m20) + v.Z * m22);
    }

    // Vectormath sincosf4, one lane — SetRotX/Y/Z @0x1419202e0 compute this on angle·0.5.
    internal static (float Sin, float Cos) SinCosF4(float x)
    {
        int q = FoxVectormath.CvtF32ToI32(x * TwoOverPi);
        float qf = q;
        float p = (x - qf * Kc1) - qf * Kc2;
        float p2 = p * p;
        float cx = ((Cc0 * p2 + Cc1) * p2 + Cc2) * p2 + 1f;
        float sx = ((Sc0 * p2 + Sc1) * p2 + Sc2) * (p2 * p) + p;
        float sin = (q & 1) == 0 ? sx : cx;
        if ((q & 2) != 0) sin = -sin;
        int q1 = q + 1;
        float cos = (q1 & 1) == 0 ? sx : cx;
        if ((q1 & 2) != 0) cos = -cos;
        return (sin, cos);
    }

    // fn-ptr table @0x142920840: [SetRotX, SetRotY, SetRotZ], indexed by axis.
    internal static Quaternion SetRotAxis(int axis, float angle)
    {
        var (s, c) = SinCosF4(angle * 0.5f);
        return axis switch
        {
            0 => new Quaternion(s, 0f, 0f, c),
            1 => new Quaternion(0f, s, 0f, c),
            _ => new Quaternion(0f, 0f, s, c),
        };
    }

    // Rotation @0x141924d20 — Sony Quat::rotation(v0→v1) with fox's two guards.
    internal static Quaternion RotationFromTo(Vector3 v0, Vector3 v1)
    {
        float px = v0.X * v1.X, py = v0.Y * v1.Y, pz = v0.Z * v1.Z;
        float d = (py + pz) + px;
        if (d + 1f <= 9.99999975e-06f)
        {
            // antiparallel: π about a normalized perp = cross(v0, basis of small |comp|).
            float ax = MathF.Abs(v0.X), ay = MathF.Abs(v0.Y), az = MathF.Abs(v0.Z);
            Vector3 axis = ax >= ay
                ? (ax >= az ? new Vector3(0f, 0f, 1f) : new Vector3(0f, 1f, 0f))    // [0x141dff580]/[0x141dff560]
                : (ay >= az ? new Vector3(0f, 0f, 1f) : new Vector3(1f, 0f, 0f));   // [0x141dff580]/[0x141dff570]
            Vector3 perp = AnimxIk.Cross(v0, axis);
            float lsq = (perp.Z * perp.Z + perp.Y * perp.Y) + perp.X * perp.X;
            float inv0 = AnimxIk.NrRsqrt(lsq);
            var (sh, ch) = SinCosF4(HalfPiF);       // sin/cos(π/2) via the SAME poly the game runs
            return new Quaternion(                  // xyz = (perp·inv)·sin, per dump op order
                (perp.X * inv0) * sh, (perp.Y * inv0) * sh, (perp.Z * inv0) * sh, ch);
        }
        Vector3 sum = v0 + v1;
        float ssq = (sum.Y * sum.Y + sum.Z * sum.Z) + sum.X * sum.X;
        if (!(ssq >= 9.99999997e-07f)) return Quaternion.Identity;
        float d2 = (pz + py) + px;
        float csq = d2 * 2f + 2f;                                   // [0x141e0bdb0] = 2.0f, madd
        float inv = AnimxIk.NrRsqrt(csq);
        Vector3 c = new(
            v1.Z * v0.Y - v1.Y * v0.Z,                              // cross(v0,v1), product order per dump
            v1.X * v0.Z - v1.Z * v0.X,
            v1.Y * v0.X - v1.X * v0.Y);
        float w = (inv * csq) * 0.5f;
        return new Quaternion(c.X * inv, c.Y * inv, c.Z * inv, w);
    }

    // q^t = SlerpWithNormalize(|t|, identity, q), conjugated for negative t
    // (the “Pow with sign” idiom Bend/Roll/BendRoll/RotRoll/SwellRoll all inline).
    internal static Quaternion PowSigned(Quaternion q, float t)
    {
        if (t < 0f)
        {
            var p = FoxVectormath.SlerpGame(t * -1f, Quaternion.Identity, q);
            return Conj(p);
        }
        return FoxVectormath.SlerpGame(t, Quaternion.Identity, q);
    }

    // CalcEulerToQuatWRPY @0x1419213d0.
    internal static Quaternion EulerToQuatWRPY(float a1, float a2, float a3, bool flag)
    {
        float c1 = FoxVectormath.CrtCosF(a1 * 0.5f);
        float c2 = FoxVectormath.CrtCosF(a2 * 0.5f);
        float c3 = FoxVectormath.CrtCosF(a3 * 0.5f);
        float s1 = FoxVectormath.CrtSinF(a1 * 0.5f);
        float s2 = FoxVectormath.CrtSinF(a2 * 0.5f);
        float s3 = FoxVectormath.CrtSinF(a3 * 0.5f);
        float s2s1 = s2 * s1, s2c1 = s2 * c1, s1c2 = s1 * c2, c2c1 = c2 * c1;
        if (!flag)
            return new Quaternion(
                (s1c2) * c3 - (s2c1) * s3,
                (s1c2) * s3 + (s2c1) * c3,
                (c2c1) * s3 - (s2s1) * c3,
                (s2s1) * s3 + (c2c1) * c3);
        return new Quaternion(
            (s2c1) * s3 + (s1c2) * c3,
            (s2c1) * c3 - (s1c2) * s3,
            (c2c1) * s3 + (s2s1) * c3,
            (c2c1) * c3 - (s2s1) * s3);
    }

    // the game's clamp idiom: max(lo) then min(hi) via `v-lo >= 0` / `hi-v >= 0` tests.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ClampG(float v, float lo, float hi)
    {
        if (!(v - lo >= 0f)) v = lo;
        if (!(hi - v >= 0f)) v = hi;
        return v;
    }

    // dot with the operators' shuffle-sum order (z + y) + x on the product lanes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float DotZYX(Vector3 a, Vector3 b) => (a.Z * b.Z + a.Y * b.Y) + a.X * b.X;

    // |q.w| with the ≤1 clamp every *ATrn op performs before acosf.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float AbsWClamped(float w)
    {
        float a = MathF.Abs(w);
        return a - 1f >= 0f ? 1f : a;
    }

    // swing-magnitude idiom of PitchA/YawAPitchL/YawAPitchA:
    // θ = acosf(clamp(sqrt(max((d+1)·2, 0))·0.5, −1, 1)) · 2
    internal static float SwingAngle(float d)
    {
        float t = (d + 1f) * 2f;
        if (!(t >= 0f)) t = 0f;
        float s = MathF.Sqrt(t) * 0.5f;
        if (!(s - -1f >= 0f)) s = -1f;
        if (!(1f - s >= 0f)) s = 1f;
        return FoxVectormath.CrtAcosF(s) * 2f;
    }

    // SetResult @0x1419250f0 (quat+pos) — result = parent ∘ local.
    internal static void SetResult6(Quaternion[] q, Vector3[] pos, int t, int tp, Quaternion local, Vector3 trans)
    {
        q[t] = AnimxIk.QMul(q[tp], local);
        pos[t] = RotateMat3(q[tp], trans) + pos[tp];
    }

    // SetResult @0x141925310 (pos only) + the callers' q[t] = q[tp] copy.
    internal static void SetResult4(Quaternion[] q, Vector3[] pos, int t, int tp, Vector3 trans)
    {
        pos[t] = RotateMat3(q[tp], trans) + pos[tp];
        q[t] = q[tp];
    }
}
