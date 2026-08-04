// fox::anim float32 math, exact port
// Ported verbatim from FoxBrowser.Models.Anim 04/08/2026 — do not re-derive, copy.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using X86 = System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;

namespace MgsvModBldr.Tools.Anim;

// Exact float32 port of the anim math in Tpp_main_win64 (BN decomp, fox::anim):
//  • MT_GetQuatDataFromBuffer @0x14191ca80 — quat dequantization (DequantQuat)
//  • TrackControl::GetDataQuat @0x1418f2de0 — Sony Vectormath Aos slerp with the
//    acosf4/sinf4 minimax polynomials, _VM_SLERP_TOL select, hemisphere flip on the
//    START quat, and rsqrtps+Newton-Raphson renormalize (SlerpGame)
//  • DataVectorSegment::PlayVectorLinearControl @0x14191e490 — (1-t)*cur + t*next (LerpGame)
//  • fox::anim::HermiteLerpVector @0x1418fb5d0 — cubic Hermite basis (HermiteGame)
//
// Every operation is kept in float32 in the game's op ORDER (IEEE754 mul/add/sub/div/
// sqrt are exactly reproducible per lane; SIMD lanes collapse to scalar). The two
// non-IEEE pieces are handled explicitly:
//  • rsqrtps — hardware approximation; float.ReciprocalSqrtEstimate JITs to rsqrtss on
//    x64, which is the SAME instruction the game executes, so results are bit-identical
//    on the same CPU (rsqrt output is CPU-family-defined, exactly like the game).
//  • sinf/cosf (CRT, used only by the quat dequantizer) — P/Invoked from ucrtbase so we
//    run the same UCRT code the game links; MathF fallback if unavailable.
public static class FoxVectormath
{
    // ── constants (Sony Vectormath literals; same decimal → same float32 bits) ─────
    private const float SlerpTol = 0.999f;                    // _VM_SLERP_TOL  [0x141e92f20]
    private const float Pi = 3.14159274f;                     // 0x40490FDB     [0x141e3d920]
    // acosf4 minimax (hi × xabs⁴ + lo) — verified: 0x141e92f30 == 0xBAA57A2C == -0.0012624911f
    private const float AcosHi0 = -0.0012624911f, AcosHi1 = 0.0066700901f, AcosHi2 = -0.0170881256f, AcosHi3 = 0.0308918810f;
    private const float AcosLo0 = -0.0501743046f, AcosLo1 = 0.0889789874f, AcosLo2 = -0.2145988016f, AcosLo3 = 1.5707963050f;
    // sinf4 — verified: 0x141e0bde0 == 0xB94C8C6E == -0.0001950727f (_SINCOS_SC0)
    private const float TwoOverPi = 0.63661977236f;           // [0x141e0bd80]
    private const float Kc1 = 1.57079625129f;                 // 0x3FC90FDA
    private const float Kc2 = 7.54978995489e-8f;              // [0x141e0bd40]
    private const float Cc0 = -0.0013602249f, Cc1 = 0.0416566950f, Cc2 = -0.4999990225f;
    private const float Sc0 = -0.0001950727f, Sc1 = 0.0083320758f, Sc2 = -0.1666665247f;

    // ── CRT sinf/cosf (quat dequant only; game calls statically-linked UCRT libm) ──
    [DllImport("ucrtbase", EntryPoint = "sinf", CallingConvention = CallingConvention.Cdecl)]
    private static extern float UcrtSinF(float x);
    [DllImport("ucrtbase", EntryPoint = "cosf", CallingConvention = CallingConvention.Cdecl)]
    private static extern float UcrtCosF(float x);

    private static readonly bool _haveUcrt = ProbeUcrt();
    private static bool ProbeUcrt()
    {
        try { _ = UcrtSinF(0.5f); return true; } catch { return false; }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float CrtSinF(float x) => _haveUcrt ? UcrtSinF(x) : MathF.Sin(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float CrtCosF(float x) => _haveUcrt ? UcrtCosF(x) : MathF.Cos(x);

    // acosf/atan2f — used by the animx IK/help-bone layer (AnimxHelpBone.cs).
    [DllImport("ucrtbase", EntryPoint = "acosf", CallingConvention = CallingConvention.Cdecl)]
    private static extern float UcrtAcosF(float x);
    [DllImport("ucrtbase", EntryPoint = "atan2f", CallingConvention = CallingConvention.Cdecl)]
    private static extern float UcrtAtan2F(float y, float x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float CrtAcosF(float x) => _haveUcrt ? UcrtAcosF(x) : MathF.Acos(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float CrtAtan2F(float y, float x) => _haveUcrt ? UcrtAtan2F(y, x) : MathF.Atan2(y, x);

    // rsqrtss — same hardware estimate the game's rsqrtps produces per lane.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float RSqrtEst(float x) => float.ReciprocalSqrtEstimate(x);

    // cvtps2dq lane (round-to-nearest-even, the game's MXCSR default).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CvtF32ToI32(float x) =>
        X86.Sse.IsSupported ? X86.Sse.ConvertToInt32(Vector128.CreateScalar(x))
                            : (int)MathF.Round(x, MidpointRounding.ToEven);

    // ── sinf4 (one lane) — Vectormath SSE sin, exact op order ──────────────────────
    internal static float SinF4(float x)
    {
        int q = CvtF32ToI32(x * TwoOverPi);
        float qf = q;                                  // cvtdq2ps: exact
        float p = (x - qf * Kc1) - qf * Kc2;           // range reduction, two subs
        float p2 = p * p;
        float p3 = p2 * p;
        float cx = ((Cc0 * p2 + Cc1) * p2 + Cc2) * p2 + 1f;
        float sx = ((Sc0 * p2 + Sc1) * p2 + Sc2) * p3 + p;
        float r = (q & 1) == 0 ? sx : cx;              // select by quadrant low bit
        return (q & 2) != 0 ? -r : r;                  // sign flip = xor 0x80000000
    }

    // ── acosf4 (one lane) — sqrt(1-|x|) * minimax(|x|), π-mirror for x<0 ────────────
    internal static float AcosF4(float x)
    {
        float xabs = MathF.Abs(x);                     // andps 0x7fffffff
        float t1 = MathF.Sqrt(1f - xabs);              // subps then sqrtps (exact IEEE)
        float x2 = xabs * xabs;
        float x4 = x2 * x2;
        float hi = ((AcosHi0 * xabs + AcosHi1) * xabs + AcosHi2) * xabs + AcosHi3;
        float lo = ((AcosLo0 * xabs + AcosLo1) * xabs + AcosLo2) * xabs + AcosLo3;
        float r = (hi * x4 + lo) * t1;
        return x < 0f ? Pi - r : r;
    }

    // ── GetDataQuat: slerp(t, start, end) + rsqrt-NR normalize, byte-exact ─────────
    // The game evaluates EVERY quat segment through this (static ones with t = 0, both
    // keys equal — the renormalize still runs, so even "constant" quats pass through).
    internal static Quaternion SlerpGame(float t, Quaternion a, Quaternion b)
    {
        // dot(b, a), summed in the game's shuffle order: ((w + z) + y) + x
        float d = ((b.W * a.W + b.Z * a.Z) + b.Y * a.Y) + b.X * a.X;
        bool neg = d < 0f;
        float dsel = neg ? 0f - d : d;                 // hemisphere select
        float xabs = MathF.Abs(dsel);                  // acosf4's internal fabs
        bool interp = xabs < SlerpTol;                 // cmplt(|cos|, 0.999)

        // acosf4 inline (the x<0 mirror is dead here — dsel ≥ 0 — but kept for parity)
        float t1 = MathF.Sqrt(1f - xabs);
        float x2 = xabs * xabs, x4 = x2 * x2;
        float hi = ((AcosHi0 * xabs + AcosHi1) * xabs + AcosHi2) * xabs + AcosHi3;
        float lo = ((AcosLo0 * xabs + AcosLo1) * xabs + AcosLo2) * xabs + AcosLo3;
        float r = (hi * x4 + lo) * t1;
        float angle = dsel < 0f ? Pi - r : r;

        float oneMinusT = 1f - t;
        // angles = angle * [1, 1-t, t] (+0f add carried over from the codegen)
        float s0 = SinF4(angle * 1f + 0f);
        float s1 = SinF4(angle * oneMinusT + 0f);
        float s2 = SinF4(angle * t + 0f);
        float scaleA = interp ? s1 / s0 : oneMinusT;   // divps lanes
        float scaleB = interp ? s2 / s0 : t;

        // hemisphere correction is applied to the START quat (0 - a, masked select)
        float ax = neg ? 0f - a.X : a.X;
        float ay = neg ? 0f - a.Y : a.Y;
        float az = neg ? 0f - a.Z : a.Z;
        float aw = neg ? 0f - a.W : a.W;

        // result = scaleA*a' + scaleB*b  (addps(mul(scaleA,a'), mul(scaleB,b)))
        float rx = scaleA * ax + scaleB * b.X;
        float ry = scaleA * ay + scaleB * b.Y;
        float rz = scaleA * az + scaleB * b.Z;
        float rw = scaleA * aw + scaleB * b.W;

        // renormalize: rsqrtps + one Newton-Raphson step (0.5*nr*(3 - lensq*nr*nr))
        float lensq = ((rw * rw + rz * rz) + ry * ry) + rx * rx;
        float nr = RSqrtEst(lensq);
        float e = (3f - (lensq * nr) * nr) * (0.5f * nr);
        return new Quaternion(rx * e, ry * e, rz * e, rw * e);
    }

    // ── PlayVectorLinearControl value: (1-t)*cur + t*next, per lane ────────────────
    internal static Vector3 LerpGame(float t, Vector3 cur, Vector3 next)
    {
        float omt = 1f - t;
        return new Vector3(
            omt * cur.X + t * next.X,
            omt * cur.Y + t * next.Y,
            omt * cur.Z + t * next.Z);
    }

    // ── HermiteLerpVector: rows (2,-2,1,1 / -3,3,-2,-1 / 0,0,1,0 / 1,0,0,0) ────────
    // Zero-coefficient multiplies are kept: the game really computes 0*p1 etc. and the
    // add order below is the codegen's exactly.
    internal static Vector3 HermiteGame(float t, Vector3 p0, Vector3 p1, Vector3 m0, Vector3 m1)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return new Vector3(
            HermiteLane(t, t2, t3, p0.X, p1.X, m0.X, m1.X),
            HermiteLane(t, t2, t3, p0.Y, p1.Y, m0.Y, m1.Y),
            HermiteLane(t, t2, t3, p0.Z, p1.Z, m0.Z, m1.Z));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HermiteLane(float t, float t2, float t3, float p0, float p1, float m0, float m1)
    {
        float a = (1f * m1 + 1f * m0) + (-2f * p1 + 2f * p0);       // t³ row
        float b = (-1f * m1 + -2f * m0) + (3f * p1 + -3f * p0);     // t² row
        float c = (0f * m1 + 1f * m0) + (0f * p1 + 0f * p0);        // t  row
        float d = (0f * m1 + 0f * m0) + (0f * p1 + 1f * p0);        // 1  row
        return (1f * d + t * c) + (t2 * b + t3 * a);
    }

    // ── MT_GetQuatDataFromBuffer dequant (bit reading is done by the caller) ───────
    // raw a/b/c are the unsigned bitSize-wide components (theta, x, y); signs = the
    // 3 trailing sign bits (bit0→x, bit1→y, bit2→z).
    public static Quaternion DequantQuat(uint a, uint b, uint c, uint signs, int bitSize)
    {
        int maskI = (1 << bitSize) - 1;
        float fmask = maskI;                           // cvtdq2ps: exact
        float inv = 1f / fmask;
        float ft = a * inv;                            // cvt of raw (≤ 2^15) is exact
        float x = b * inv;
        float y = c * inv;
        float halfTheta = ft * Pi * 0.5f;              // (raw/mask)·π then ·0.5
        float z = (1f - x) - y;
        float lensq = (z * z + y * y) + x * x;         // shuffle-sum order (z²+y²)+x²
        float invLen = 1f / MathF.Sqrt(lensq);         // sqrtps then scalar div
        float sx = (signs & 1) != 0 ? -1f : 1f;        // sign vec lanes replaced by -1.0
        float sy = (signs & 2) != 0 ? -1f : 1f;
        float sz = (signs & 4) != 0 ? -1f : 1f;
        float f = CrtSinF(halfTheta) * invLen;
        return new Quaternion(
            x * sx * f,
            y * sy * f,
            z * sz * f,
            CrtCosF(halfTheta));
    }
}
