// .frdv help-bone driver parser
// Ported from FoxBrowser.Models.Anim 04/08/2026 — copy, not a re-derivation; only
// FmdlModel/FmdlBone are renamed to AnimSkeleton/AnimBone (identical shape).
using System.Numerics;


namespace MgsvModBldr.Tools.Anim;

// Fox Engine HELP BONE driver (.frdv / "FoxRigDriver", fpk type 664 "Helper bone data").
//
// A driven-bone layer that runs AFTER the gani pose + IK, BEFORE skinning. The character
// skeleton carries dedicated *_HLP "twist / roll / muscle" bones that the mesh is partially
// skinned to; without driving them they freeze at bind while the real limb rotates, and the
// skinned vertices pinch into the classic "candy-wrapper" knot. Each operator distributes a
// fraction of a SOURCE bone's local motion onto a TARGET helper bone so the mesh follows.
//
// Format (little-endian, confirmed against the engine + the FRDV 010 template):
//   "FRDV" | u32 magic(0x0BFFB0A8) | u32 entryCount | align16 | u32 offsets[entryCount]
//   each entry is 128 bytes at its offset:
//     +0x00 short Type   +0x02/04/06 Target/Source/Source2   +0x08/0a/0c TargetParent/SourceParent/SourceParent2
//     +0x10 float Weight   +0x14 Param2   +0x18/1c LimitMin/Max   +0x20 int Axis/TransAxis
//     +0x24 float Param@24  +0x2c/30 float  +0x34 int   +0x40 Vector3 VecA   +0x50 Vector3 VecB
// Skel indices index the model's FULL skeleton DIRECTLY (canonical SKL order WAIST=0…
// RFARL_HLP=110…), i.e. model.Bones[idx] — NOT the compact .frig BoneList.
//
// Operators (from Tpp_main_win64 fox::animx::*Operator + HelpBone::ExecCore dispatch). Shared
// primitives: GetLocalRot(s,sp)=Conj(orient[sp])·orient[s]; Swing(lr,a)=FromTo(a, R(lr)·a);
// Twist(lr,a)=lr·Conj(Swing); Pow(q,t)=Slerp(Identity,q,t) (t<0 ⇒ reversed); SetResult = FK
// orient[t]=orient[tP]·local, pos[t]=pos[tP]+R(orient[tP])·bindLocal[t]. Implemented:
//   2 Rot, 5 Bend, 7 Roll, 8 BendRoll, 9 RotRoll, 22 Mirror               (rotation)
//   10 PitchL, 1 RotATrn, 4 BendATrn                                       (rotation→translation)
//   6 BendATrnBend                                                         (bend + bend→translation)
//   11 PitchA, 13 YawAPitchL, 12 RollPitchL                               (angle/roll + pitch-linear)
// ALL operator types ported from the dev exe.c (02/08/2026): 1 RotATrn, 2 Rot,
//   3 RotATurnRot, 4 BendATrn, 5 Bend, 6 BendATrnBend, 7 Roll, 8 BendRoll, 9 RotRoll,
//   10 PitchL, 11 PitchA, 12 RollPitchL, 13 YawAPitchL, 14 YawAPitchA, 15 Dircns,
//   16 Swell / 17+18 SwellRot (bone SCALE via FxOut.Scales), 19 PitchASwitchLinear,
//   20 ParamSwitchAbs (demo SHADER stream), 21 PitchACycleParam, 22 Mirror,
//   23 PitchALinearParam (19-23 emit material params via FxOut.MatParams).
public sealed class FrdvFile
{
    public sealed class Op
    {
        readonly byte[] _d;
        public Op(byte[] d) { _d = d; }
        public short Type => BitConverter.ToInt16(_d, 0);
        public short Target => BitConverter.ToInt16(_d, 2);
        public short Source => BitConverter.ToInt16(_d, 4);
        public short Source2 => BitConverter.ToInt16(_d, 6);
        public short TargetParent => BitConverter.ToInt16(_d, 8);
        public short SourceParent => BitConverter.ToInt16(_d, 0xa);
        public short SourceParent2 => BitConverter.ToInt16(_d, 0xc);
        public float F(int o) => BitConverter.ToSingle(_d, o);
        public int I(int o) => BitConverter.ToInt32(_d, o);
        public Vector3 V(int o) => new(F(o), F(o + 4), F(o + 8));
    }

    public List<Op> Operators { get; } = new();

    // harness probe: (targetBone, composedLocal, yaw, pitch) per applied type-14 op
    public static Action<int, Quaternion, float, float>? Debug14;

    // Optional side-outputs of Apply beyond the pose: per-bone scale (Swell ops
    // 16/17/18 — the engine routes these through SetBoneScale into skinning) and
    // material parameter writes (ops 19/20/21/23 — SetMaterialParameter pairs).
    // StreamParam feeds op 20 (ParamSwitchAbs) the demo SHADER track value for
    // (id1@0x2c, id2@0x30); return null when no stream carries it.
    public sealed class FxOut
    {
        public readonly Dictionary<int, Vector3> Scales = new();
        public readonly List<(uint Mat, uint Param, float Value)> MatParams = new();
        public Func<uint, uint, float?>? StreamParam;
    }

    public static FrdvFile? TryParse(byte[] d)
    {
        if (d.Length < 16 || d[0] != 'F' || d[1] != 'R' || d[2] != 'D' || d[3] != 'V') return null;
        uint count = BitConverter.ToUInt32(d, 8);
        if (count == 0 || count > 8192) return null;
        var f = new FrdvFile();
        int tbl = 16;                                   // 12-byte header, FAlign(16)
        for (int e = 0; e < count; e++)
        {
            if (tbl + e * 4 + 4 > d.Length) break;
            int off = (int)BitConverter.ToUInt32(d, tbl + e * 4);
            if (off < 0 || off + 0x60 > d.Length) continue;
            var entry = new byte[0x80];
            Array.Copy(d, off, entry, 0, Math.Min(0x80, d.Length - off));
            f.Operators.Add(new Op(entry));
        }
        return f.Operators.Count > 0 ? f : null;
    }

    const float Deg2Rad = MathF.PI / 180f;

    // Diagnostic: when non-null, only operators of these types run (bisect which operator
    // misbehaves at extreme poses). null = all. Set by the frigshot `hb27`/`hbNN` modes.
    public static HashSet<int>? OnlyTypes;

    // A/B fallback: lr*Conj(Swing) (the pre-31/07/2026 order). Engine-diff proved the
    // default conj(Swing)*lr: helper errors 2.97°→1.04°, T=12 twist bones ≤0.9°.
    public static bool TwistOrderOld;

    // Run the help-bone operators over the model-space pose (mutates animWorld for the helper
    // target bones). `frig` is unused for mapping (kept for signature compat). Returns the frame
    // TENSION scalar [0,1] for the sub-normal (wrinkle) map — derived from the joint-driven
    // material operators (type 19, "TensionRate"; sourced from shoulders/hips/torso). The engine
    // routes tension per material; we drive the sub_nrm globally by the strongest flexing joint,
    // which is enough to fade wrinkles in as the body bends and keep them off at rest.
    public static float Apply(AnimSkeleton model, FrigFile? frig, Matrix4x4[] animWorld, IReadOnlyList<Op> ops, List<string>? log = null, FxOut? fx = null)
    {
        if (ops.Count == 0) return 0f;
        int count = model.Bones.Count;
        int Map(int skel) => skel >= 0 && skel < count ? skel : -1;

        var orient = new Quaternion[count];
        var pos = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            orient[i] = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(animWorld[i]));
            pos[i] = animWorld[i].Translation;
        }

        Quaternion LocalRot(int s, int sp) => sp < 0 ? orient[s] : Quaternion.Conjugate(orient[sp]) * orient[s];

        int applied = 0;
        foreach (var op in ops)
        {
            if (OnlyTypes is not null && !OnlyTypes.Contains(op.Type)) continue;
            int tgt = Map(op.Target), src = Map(op.Source), src2 = Map(op.Source2);
            int tgtP = Map(op.TargetParent), srcP = Map(op.SourceParent), srcP2 = Map(op.SourceParent2);
            if (tgt < 0 || tgtP < 0) continue;

            var lp = model.Bones[tgt].LocalPosition;
            Vector3 trans = new(lp.X, lp.Y, lp.Z);
            Quaternion local = Quaternion.Identity;
            bool ok = true;

            switch (op.Type)
            {
                case 2:                                                  // Rot
                    if (src < 0) { ok = false; break; }
                    local = Pow(LocalRot(src, srcP), op.F(0x10));
                    break;
                case 5:                                                  // Bend (swing)
                    if (src < 0) { ok = false; break; }
                    local = Pow(Swing(LocalRot(src, srcP), op.V(0x40)), op.F(0x10));
                    break;
                case 7:                                                  // Roll (twist)
                    if (src < 0) { ok = false; break; }
                    local = Pow(Twist(LocalRot(src, srcP), op.V(0x40)), op.F(0x10));
                    break;
                case 8:                                                  // BendRoll (bend ⊗ twist)
                {
                    if (src < 0) { ok = false; break; }
                    var lr = LocalRot(src, srcP); var ax = op.V(0x40);
                    local = Pow(Swing(lr, ax), op.F(0x10)) * Pow(Twist(lr, ax), op.F(0x24));
                    break;
                }
                case 9:                                                  // RotRoll (rot(src1) ⊗ roll(src2))
                {
                    if (src < 0) { ok = false; break; }
                    var rot = Pow(LocalRot(src, srcP), op.F(0x10));
                    var roll = src2 >= 0 ? Pow(Twist(LocalRot(src2, srcP2), op.V(0x40)), op.F(0x24)) : Quaternion.Identity;
                    local = rot * roll;
                    break;
                }
                case 22:                                                 // Mirror
                {
                    if (src < 0) { ok = false; break; }
                    var l = LocalRot(src, srcP);
                    local = new Quaternion(-l.X, l.Y, l.Z, -l.W);
                    break;
                }
                case 10:                                                 // PitchL (dot → translation)
                {
                    if (src < 0) { ok = false; break; }
                    var ra = Vector3.Transform(op.V(0x40), LocalRot(src, srcP));
                    float p = Math.Clamp(Vector3.Dot(ra, op.V(0x50)) * op.F(0x10), op.F(0x18), op.F(0x1c));
                    SetComp(ref trans, op.I(0x20), p * 0.1f);
                    break;
                }
                case 1:                                                  // RotATrn (rotation magnitude → translation)
                {
                    if (src < 0) { ok = false; break; }
                    float ang = MathF.Acos(Math.Clamp(MathF.Abs(LocalRot(src, srcP).W), 0f, 1f));
                    float a = Math.Clamp(ang * (op.F(0x10) * 360f / MathF.PI), op.F(0x18), op.F(0x1c));
                    SetComp(ref trans, op.I(0x20), a * 0.1f);
                    break;
                }
                case 3:                                                  // RotATurnRot (powered rotation + angle → translation)
                {
                    if (src < 0) { ok = false; break; }
                    var lr = LocalRot(src, srcP);
                    local = Pow(lr, op.F(0x24));
                    float ang = MathF.Acos(Math.Min(MathF.Abs(lr.W), 1f));
                    float a = Math.Clamp(ang * (op.F(0x10) * 360f / MathF.PI), op.F(0x18), op.F(0x1c));
                    SetComp(ref trans, op.I(0x20), (a + op.F(0x14)) * 0.1f);
                    break;
                }
                case 4:                                                  // BendATrn (bend angle → translation)
                {
                    if (src < 0) { ok = false; break; }
                    var sw = Swing(LocalRot(src, srcP), op.V(0x40));
                    float ang = MathF.Acos(Math.Clamp(MathF.Abs(sw.W), 0f, 1f));
                    float a = Math.Clamp(ang * (op.F(0x10) * 360f / MathF.PI), op.F(0x18), op.F(0x1c));
                    SetComp(ref trans, op.I(0x20), a * 0.1f);
                    break;
                }
                case 6:                                                  // BendATrnBend (bend rotation + bend angle → translation)
                {
                    if (src < 0) { ok = false; break; }
                    var sw = Swing(LocalRot(src, srcP), op.V(0x40));
                    float ang = MathF.Acos(Math.Clamp(MathF.Abs(sw.W), 0f, 1f));
                    float a = Math.Clamp(ang * (op.F(0x10) * 360f / MathF.PI), op.F(0x18), op.F(0x1c));
                    SetComp(ref trans, op.I(0x20), (a + op.F(0x14)) * 0.1f);
                    local = Pow(sw, op.F(0x24));
                    break;
                }
                case 11:                                                 // PitchA (pitch angle → axis rotation)
                {
                    if (src < 0) { ok = false; break; }
                    float pitch = PitchAngle(LocalRot(src, srcP), op.V(0x40), op.V(0x50)) * op.F(0x10);
                    pitch = Math.Clamp(pitch, op.F(0x18) * Deg2Rad, op.F(0x1c) * Deg2Rad);
                    local = AxisAngle(op.I(0x20), pitch);
                    break;
                }
                case 12:                                                 // RollPitchL (roll rotation + pitch → translation)
                {
                    if (src < 0) { ok = false; break; }
                    var lr = LocalRot(src, srcP); var a1 = op.V(0x40);
                    var ra = Vector3.Transform(a1, lr);
                    float p = Math.Clamp(Vector3.Dot(ra, op.V(0x50)) * op.F(0x24), op.F(0x2c), op.F(0x30));
                    SetComp(ref trans, op.I(0x34), p * 0.1f);
                    local = Pow(Twist(lr, a1), op.F(0x10));
                    break;
                }
                case 13:                                                 // YawAPitchL (yaw angle rotation + pitch → translation)
                {
                    if (src < 0) { ok = false; break; }
                    var lr = LocalRot(src, srcP); var a1 = op.V(0x40); var a2 = op.V(0x50);
                    var ra = Vector3.Transform(a1, lr);
                    float p = Math.Clamp(Vector3.Dot(ra, a2) * op.F(0x24), op.F(0x2c), op.F(0x30));
                    SetComp(ref trans, op.I(0x34), p * 0.1f);
                    float yaw = YawAngle(lr, a1, a2) * op.F(0x10);
                    yaw = Math.Clamp(yaw, op.F(0x18) * Deg2Rad, op.F(0x1c) * Deg2Rad);
                    local = AxisAngle(op.I(0x20), yaw);
                    break;
                }
                case 14:                                                 // YawAPitchA (yaw angle + pitch angle rotation)
                {
                    // fox::animx::YawAPitchAOperator (dev exe.c @5763589): yaw/pitch fractions as
                    // in ops 11/13; mode I(0x38)==0 blends a TARGET DIRECTION in the
                    // (a1, a2, a1×a2) frame — v = w·a1 + sp·a2 + sy·cross, sp=sin((p/n)·π/2)·sin n,
                    // sy=sin(−(y/n)·π/2)·sin n, w=±√(1−sp²−sy²) (negative past n=π/2) — and the
                    // result is the shortest arc a1→v. The axis ints are euler-mode-only.
                    if (src < 0) { ok = false; break; }
                    var lr = LocalRot(src, srcP); var a1 = op.V(0x40); var a2 = op.V(0x50);
                    float yaw = YawAngle(lr, a1, a2) * op.F(0x10);
                    yaw = Math.Clamp(yaw, op.F(0x18) * Deg2Rad, op.F(0x1c) * Deg2Rad);
                    float pitch = PitchAngle(lr, a1, a2) * op.F(0x24);
                    pitch = Math.Clamp(pitch, op.F(0x2c) * Deg2Rad, op.F(0x30) * Deg2Rad);
                    if (op.I(0x38) == 1)
                    {
                        local = AxisAngle(op.I(0x20), yaw) * AxisAngle(op.I(0x34), pitch);   // euler orders unseen in data
                        break;
                    }
                    float n = MathF.Abs(yaw) + MathF.Abs(pitch);
                    float sp = 0f, sy = 0f;
                    if (n >= 1e-10f)
                    {
                        float s = MathF.Sin(n);
                        sp = MathF.Sin(pitch / n * (MathF.PI * 0.5f)) * s;
                        sy = MathF.Sin(yaw / n * (-MathF.PI * 0.5f)) * s;
                    }
                    float w = MathF.Sqrt(MathF.Max(0f, 1f - sp * sp - sy * sy));
                    if (n >= MathF.PI * 0.5f) w = -w;
                    var v = w * a1 + sp * a2 + sy * Vector3.Cross(a1, a2);
                    local = FromTo(a1, v);
                    Debug14?.Invoke(tgt, local, yaw, pitch);
                    break;
                }
                case 15:                                                 // Dircns (two-point aim constraint, writes rig-space)
                {
                    if (src < 0 || src2 < 0) { ok = false; break; }
                    // position always: bind under parent (engine SetResult overload runs first)
                    pos[tgt] = pos[tgtP] + Vector3.Transform(trans, orient[tgtP]);
                    var a = op.V(0x40); var bv = op.V(0x50);
                    var p1 = pos[src] + Vector3.Transform(op.V(0x60) * 0.1f, orient[src]) - pos[tgt];
                    var p2 = pos[src2] + Vector3.Transform(op.V(0x70) * 0.1f, orient[src2]) - pos[tgt];
                    if (p1.LengthSquared() >= 1e-6f && p2.LengthSquared() >= 1e-6f)
                    {
                        var u = Vector3.Cross(a, bv);
                        var x = Norm(p1);
                        var wAx = Vector3.Cross(Norm(p2), x);
                        if (u.LengthSquared() >= 1e-8f && wAx.LengthSquared() >= 1e-6f &&
                            Vector3.Cross(wAx, x).LengthSquared() >= 1e-6f)
                        {
                            // rotation aligning (a → x, û → ŵ), built from the engine's own
                            // shortest-arc primitive (== the frame-matrix alignment it solves)
                            var q1 = FromTo(Norm(a), x);
                            var u1 = Vector3.Transform(Norm(u), q1);
                            var q2 = FromTo(u1, Norm(wAx));
                            orient[tgt] = Quaternion.Normalize(q1 * q2);
                        }
                    }
                    animWorld[tgt] = Matrix4x4.CreateFromQuaternion(orient[tgt]) * Matrix4x4.CreateTranslation(pos[tgt]);
                    applied++;
                    continue;                                            // custom write — skip the tail
                }
                case 16:                                                 // Swell (twist fraction → bone scale only)
                {
                    if (src < 0) { ok = false; break; }
                    var lr = LocalRot(src, srcP);
                    float dot = Math.Clamp(Quaternion.Dot(lr, Swing(lr, op.V(0x40))), -1f, 1f);
                    float t = MathF.Abs(MathF.Acos(MathF.Abs(dot)) * (2f / MathF.PI));
                    if (fx is not null) fx.Scales[tgt] = Vector3.One + t * op.V(0x50);
                    applied++;
                    continue;                                            // pose untouched
                }
                case 17: case 18:                                        // SwellRot (powered twist + scale)
                {
                    if (src < 0) { ok = false; break; }
                    var lr = LocalRot(src, srcP); var a1 = op.V(0x40);
                    var tw = Twist(lr, a1);
                    float t = MathF.Abs(MathF.Acos(Math.Clamp(MathF.Abs(tw.W), 0f, 1f)) * (2f / MathF.PI));
                    local = Pow(tw, op.F(0x10));
                    if (fx is not null) fx.Scales[tgt] = Vector3.One + t * op.V(0x50);
                    break;
                }
                case 19:                                                 // PitchASwitchLinear → 2 material params
                {
                    if (src < 0 || fx is null) { ok = false; break; }
                    float angDeg = PitchAngle(LocalRot(src, srcP), op.V(0x40), op.V(0x50)) * (180f / MathF.PI);
                    float thr = op.F(0x10);
                    float va, vb, x0;
                    if (angDeg < thr) { va = op.F(0x70); vb = op.F(0x74); x0 = op.F(0x78); }
                    else { va = op.F(0x60); vb = op.F(0x64); x0 = op.F(0x68); }
                    float slope = (va - vb) / (thr - x0);
                    float v = va - slope * thr + slope * angDeg;
                    v = Math.Clamp(v, Math.Min(va, vb), Math.Max(va, vb));
                    fx.MatParams.Add(((uint)op.I(0x20), (uint)op.I(0x38), v));
                    fx.MatParams.Add(((uint)op.I(0x34), (uint)op.I(0x3c), angDeg < thr ? op.F(0x18) : op.F(0x1c)));
                    ok = false;                                          // no pose change
                    break;
                }
                case 20:                                                 // ParamSwitchAbs (demo SHADER track → 2 material params)
                {
                    if (fx?.StreamParam is null) { ok = false; break; }
                    float? sv = fx.StreamParam((uint)op.I(0x2c), (uint)op.I(0x30));
                    if (sv is { } val)
                    {
                        fx.MatParams.Add(((uint)op.I(0x20), (uint)op.I(0x38), MathF.Abs(val) * op.F(0x10)));
                        fx.MatParams.Add(((uint)op.I(0x34), (uint)op.I(0x3c), val < 0f ? op.F(0x18) : op.F(0x1c)));
                    }
                    ok = false;
                    break;
                }
                case 21:                                                 // PitchACycleParam (integer-degree cycle ramp)
                {
                    if (src < 0 || fx is null) { ok = false; break; }
                    float angDeg = PitchAngle(LocalRot(src, srcP), op.V(0x40), op.V(0x50)) * (180f / MathF.PI);
                    float cyc = op.F(0x14);
                    long m = (long)(angDeg + 360f), c = (long)cyc;
                    float v = c != 0 ? (m % c) / cyc * (op.F(0x1c) - op.F(0x18)) + op.F(0x18) : op.F(0x18);
                    fx.MatParams.Add(((uint)op.I(0x20), (uint)op.I(0x38), v));
                    ok = false;
                    break;
                }
                case 23:                                                 // PitchALinearParam (linear clamp)
                {
                    if (src < 0 || fx is null) { ok = false; break; }
                    float angDeg = PitchAngle(LocalRot(src, srcP), op.V(0x40), op.V(0x50)) * (180f / MathF.PI);
                    float v = Math.Clamp(angDeg * op.F(0x10) + op.F(0x14), op.F(0x18), op.F(0x1c));
                    fx.MatParams.Add(((uint)op.I(0x20), (uint)op.I(0x38), v));
                    ok = false;
                    break;
                }
                default:
                    ok = false;
                    break;
            }
            if (!ok) continue;

            Quaternion pOri = orient[tgtP];
            orient[tgt] = Quaternion.Normalize(pOri * local);
            pos[tgt] = pos[tgtP] + Vector3.Transform(trans, pOri);
            animWorld[tgt] = Matrix4x4.CreateFromQuaternion(orient[tgt]) * Matrix4x4.CreateTranslation(pos[tgt]);
            applied++;
        }

        // TENSION for the sub-normal wrinkle map: the type-19 ("TensionRate") operators read a
        // joint's flex (e.g. shoulder = UARM rel SHLD) and drive a material's tension. We take
        // the strongest flex across them as a global wrinkle strength — 0 at rest, rising as the
        // body bends. (The engine routes this per material; global is the pragmatic first cut.)
        float tension = 0f;
        foreach (var op in ops)
        {
            if (op.Type != 19) continue;
            int src = Map(op.Source), srcP = Map(op.SourceParent);
            if (src < 0) continue;
            float flex = 2f * MathF.Acos(Math.Clamp(MathF.Abs(LocalRot(src, srcP).W), 0f, 1f));   // total swing angle
            tension = MathF.Max(tension, Math.Clamp(flex / (MathF.PI * 0.5f), 0f, 1f));            // normalise by 90°
        }
        log?.Add($"helpbones: {applied}/{ops.Count} applied (bones={count}) tension={tension:0.00}");
        return tension;
    }

    // ── operator primitives ───────────────────────────────────────────────────────

    // swing = rotation taking `axis` to where the local rotation moves it (the non-twist tilt).
    static Quaternion Swing(Quaternion lr, Vector3 axis)
    {
        axis = Norm(axis);
        return FromTo(axis, Vector3.Transform(axis, lr));
    }

    // twist = the component of `lr` purely about `axis`: lr = Swing ⊗ Twist, so
    // Twist = Conj(Swing) ⊗ lr (fixed-axis decomposition, engine-verified).
    static Quaternion Twist(Quaternion lr, Vector3 axis)
        => TwistOrderOld ? lr * Quaternion.Conjugate(Swing(lr, axis))
                         : Quaternion.Conjugate(Swing(lr, axis)) * lr;

    // signed pitch angle (radians): how far the local rotation pitches axis1 toward axis2,
    // scaled by the total swing magnitude. Mirrors fox::animx::PitchAOperator.
    static float PitchAngle(Quaternion lr, Vector3 a1, Vector3 a2)
    {
        a1 = Norm(a1); a2 = Norm(a2);
        Vector3 ra = Vector3.Transform(a1, lr);
        float proj = Vector3.Dot(ra, a2);
        float perp = -Vector3.Dot(ra, Vector3.Cross(a1, a2));
        float frac = MathF.Atan2(MathF.Abs(proj), MathF.Abs(perp) + 1e-10f) / (MathF.PI * 0.5f);
        if (proj < 0f) frac = -frac;
        float swing = MathF.Acos(Math.Clamp(Vector3.Dot(ra, a1), -1f, 1f));
        return frac * swing;
    }

    // signed yaw angle (radians) — like PitchAngle but measured 90° around. Mirrors YawAPitchL.
    static float YawAngle(Quaternion lr, Vector3 a1, Vector3 a2)
    {
        a1 = Norm(a1); a2 = Norm(a2);
        Vector3 ra = Vector3.Transform(a1, lr);
        float proj = Vector3.Dot(ra, a2);
        float perp = -Vector3.Dot(ra, Vector3.Cross(a1, a2));
        float atan = MathF.Atan2(MathF.Abs(proj), MathF.Abs(perp) + 1e-10f) / (MathF.PI * 0.5f);
        float frac = perp < 0f ? atan - 1f : 1f - atan;
        float swing = MathF.Acos(Math.Clamp(Vector3.Dot(ra, a1), -1f, 1f));
        return frac * swing;
    }

    static Quaternion AxisAngle(int axis, float rad) => axis switch
    {
        0 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, rad),
        1 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, rad),
        _ => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rad),
    };

    static void SetComp(ref Vector3 v, int i, float val) { if (i == 0) v.X = val; else if (i == 1) v.Y = val; else v.Z = val; }

    // q^t == Slerp(Identity, q, t); t<0 reverses (== conjugate of the positive power), shortest path.
    static Quaternion Pow(Quaternion q, float t)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0f) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        float w = Math.Clamp(q.W, -1f, 1f);
        float s = MathF.Sqrt(MathF.Max(0f, 1f - w * w));
        if (s < 1e-6f) return Quaternion.Identity;
        float angle = MathF.Acos(w) * t;
        float ns = MathF.Sin(angle) / s;
        return new Quaternion(q.X * ns, q.Y * ns, q.Z * ns, MathF.Cos(angle));
    }

    // shortest-arc rotation taking unit vector a to unit vector b (== engine Rotation()).
    static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        a = Norm(a); b = Norm(b);
        float d = Vector3.Dot(a, b);
        if (d >= 1f - 1e-6f) return Quaternion.Identity;
        if (d <= -1f + 1e-6f) { Vector3 p = Perp(a); return Quaternion.CreateFromAxisAngle(p, MathF.PI); }
        Vector3 c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }

    static Vector3 Perp(Vector3 v)
    {
        Vector3 c = Vector3.Cross(v, Vector3.UnitX);
        if (c.LengthSquared() < 1e-6f) c = Vector3.Cross(v, Vector3.UnitY);
        return Norm(c);
    }

    static Vector3 Norm(Vector3 v) { float l = v.Length(); return l > 1e-8f ? v / l : Vector3.Zero; }
}
