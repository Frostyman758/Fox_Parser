// Gani frame -> GPU bone palette
// Ported from FoxBrowser.Models.Anim 04/08/2026 — copy, not a re-derivation; only
// FmdlModel/FmdlBone are renamed to AnimSkeleton/AnimBone (identical shape).
using System.Linq;
using System.Numerics;


namespace MgsvModBldr.Tools.Anim;

// Builds the GPU bone-palette (one skin matrix per FMDL bone) for a gani frame.
//
// Skinning math (System.Numerics is row-vector: result = v · M, compose A-then-B = A*B):
//   inverseBind[b] = Translate(-WorldPosition[b])               // bind pose is translation-only
//   LOCAL unit:  animLocal[b] = Rotate(rot)*Translate(trans); animWorld[b] = animLocal*animWorld[parent]
//   WORLD unit:  worldPos = LocalPosition·animWorld[parent];   animWorld[b] = Rotate(rot_world)*Translate(worldPos)
//   skin[b]      = inverseBind[b] * animWorld[b]               // rest pose ⇒ identity (either path)
//
// World-space rig units (ORIENTATION/TWO_BONE/ARM) supply a bone's absolute world
// orientation — the chain only provides position. Composing them through the parent
// like a local transform double-applies the parent rotation and scatters the parts;
// hence the per-bone rig-unit type from the .frig.
//
// TRANSLATION: by default the gani's per-bone translation is NOT applied — bind-local
// offsets are used and only rotations animate. In Fox motion-archive *_layers ganis the
// translation channels carry mixed model-space-absolute (Transform/Root) and additive
// (LocalTransform/TwoBoneTrans) values that are composited by the motion graph (.mog);
// applying them naively as local offsets scatters the model. Rotation alone reproduces
// the full skeletal pose. applyGaniTranslation re-enables them for experiments.
//
// The renderer uploads these row-major; its column-major structured buffer makes
// mul(M,pos) apply pos·skin (same convention as the MVP).
public static class AnimSkinner
{
    // Harness-only knobs to isolate convention bugs (default off).
    public static bool DiagConjugate = false;
    public static bool DiagNoWorldSpace = false;
    public static bool DiagIkLog = false;                       // print IK goal/bind numbers
    // Poles decode as model-space here (engine-diff: identity = 0.00°, FK compose = 0.83°),
    // so identity is CORRECT; the flag re-enables FK compose for experiments only.
    public static bool DiagIkParentRotFk = false;
    public static int DiagIkRootMode = 1;                       // 0=sub root translation, 1=full inverse root (default — body is root-local), 2=none
    // harness instrument: bones forced to a supplied transform (skip their own drive/solve)
    public static Dictionary<int, Matrix4x4>? DiagBoneOverride;
    public static readonly List<string> IkLog = new();

    // Game-exact two-bone IK (AnimxIk: CalcIkTwoBone + the leg driver's clamps) instead of
    // the legacy analytic solve. Guarded per-job — degenerate rig data (bad plane normal,
    // side ∥ bone, non-unit result) falls back to legacy, so worst case == the old look.
    // Set false to force the legacy solver everywhere (the pre-IK backup behaviour).
    public static bool UseGameIk = true;

    // Frame tension [0,1] for the sub-normal wrinkle map, set by the last BuildPalette call
    // (from the .frdv tension operators). The caller passes it to FmdlRenderer.SetTension.
    // Single render is synchronous, so read it immediately after BuildPalette.
    public static float SubTension;

    // World-space bone positions (post-viewShift) from the last BuildPalette — the camera-lock (C)
    // and the bone/limb-colour overlays (B/L) read this. Populated just before skinning.
    public static Vector3[]? LastBonePos;

    // Model-space bone transforms BEFORE viewShift, from the last BuildPalette — what the
    // engine's own pose arrays hold. --enginediff compares against these.
    public static Matrix4x4[]? LastAnimWorld;

    // drives: optional FMDL-bone-index -> (track, rig-unit-type) resolved through the
    // model's rig (.frig). When supplied it takes precedence over the direct name match —
    // this is what makes custom rigs (Sahelanthropus, animals) animate, where the gani's
    // rig-unit track names don't equal the FMDL bone names. Pass null for standard humanoid
    // skeletons (direct StrCode32 name match, all treated as local transforms).
    public static Matrix4x4[] BuildPalette(AnimSkeleton model, GaniAnimation anim, float frame,
        IReadOnlyDictionary<int, FrigFile.BoneDrive>? drives = null, bool applyGaniTranslation = false,
        IReadOnlyList<FrigFile.IkJob>? ikJobs = null,
        FrigFile? frig = null, IReadOnlyList<FrdvFile.Op>? helpBones = null)
    {
        int count = model.Bones.Count;

        // bone -> (track, isWorldSpace, channel, type)  channel >= 0 = finger sub-rotation.
        // The unit TYPE decides whether the bone also carries a TRANSLATION channel: Root /
        // Transform / LocalTransform units move the bone (the body's crouch, the pelvis rising
        // as he climbs out) — dropping these leaves the limb roots at bind height while the
        // world-space effector targets move, so the arms can't reach.
        var boneDrive = new Dictionary<int, (GaniTrack track, bool ws, int ch, FrigFile.RigUnitType type, GaniChannel? chRot, GaniChannel? chPos)>(count);
        if (drives is not null)
        {
            foreach (var (bi, d) in drives)
                if (d.Track >= 0 && d.Track < anim.Tracks.Count)
                    // chRot/chPos = the unit's baked FLAT segments (the engine's binding);
                    // the track fallback covers rigs whose units group 1:1 with tracks.
                    boneDrive[bi] = (anim.Tracks[d.Track], FrigFile.IsWorldSpace(d.Type), d.Channel, d.Type,
                                     anim.ChannelBySeg(d.SegRot) is { IsRot: true } cr ? cr : null,
                                     anim.ChannelBySeg(d.SegPos) is { IsRot: false } cp ? cp : null);
        }
        else
        {
            var boneNameIdx = new int[count];
            for (int i = 0; i < count; i++) boneNameIdx[i] = model.Bones[i].NameIndex;
            // Direct StrCode32 name match (humanoids): every track is a local transform.
            var trackToBone = anim.ResolveToBones(model.Names, boneNameIdx, out _);
            foreach (var (ti, bi) in trackToBone) boneDrive[bi] = (anim.Tracks[ti], false, -1, FrigFile.RigUnitType.LocalOrientation, null, null);
        }
        // Root / Transform / LocalTransform carry the body's WORLD position — the character
        // actually translates through the world (crouches, climbs out, ends up elsewhere).
        // These positions are model-space ABSOLUTE (not parent-relative), so the bone sits at
        // the given world point. The whole posed character is then re-centred for the preview
        // by a single camera-style shift (−rootWorld), which keeps body AND targets in one
        // frame so the limbs don't stretch to bridge a moving target.
        // Transform/LocalTransform carry the body's LOCAL movement (the waist's crouch/climb
        // height). NOT Root — that's the horizontal world walk, which the cube-relative effector
        // channels already exclude, so we keep the character centred and don't apply it.
        static bool CarriesTranslation(FrigFile.RigUnitType t) =>
            t is FrigFile.RigUnitType.Transform
              or FrigFile.RigUnitType.LocalTransform or FrigFile.RigUnitType.LocalTransformSrt;
        // Effector targets are WORLD positions (they carry the cube's forward walk + yaw). The
        // body is rendered centred at the origin, so map each target back into the body frame
        // with the inverse cube transform: local = R⁻¹·(world − cubePos). Without the −cubePos
        // the feet stay where the cube has walked to and the legs splay back (the MJ lean).
        Quaternion rootRotF = anim.Tracks.Count > 0 ? anim.Tracks[0].SampleRot(frame) : Quaternion.Identity;
        Quaternion rootRotInv = Quaternion.Conjugate(rootRotF);
        Vector3 rootPos = Vector3.Zero;
        if (anim.Tracks.Count > 0) anim.Tracks[0].TrySamplePos(frame, out rootPos);
        // Bringing everything into the cube's frame removes the world yaw, so the character
        // never turns. Re-apply the cube's rotation SINCE frame 0 as a final uniform turn — he
        // ends facing where the animation rotates him to (the trashbox turn-around), while
        // frame 0 stays as the natural start view. This was the "reset" that flattened every anim.
        Quaternion rootRot0 = anim.Tracks.Count > 0 ? anim.Tracks[0].SampleRot(0) : Quaternion.Identity;
        Matrix4x4 viewShift = Matrix4x4.CreateFromQuaternion(rootRotF * Quaternion.Conjugate(rootRot0));

        // IK setup. A gani track for a TwoBone/Arm unit carries the EFFECTOR world position
        // (not a chain rotation); applying its rotation to every chain bone (the old path)
        // splayed the limbs straight. Instead we solve 2-bone IK analytically at the chain
        // root, integrated into the FK pass so the foot/hand and their children follow the
        // corrected knee/elbow. ikSolveAt[chainRoot] = (mid, end, track); ikSet = the mid bone
        // (its own FK is skipped); ikBindOnly = bones held at bind (the arm's shoulder, whose
        // track rotation is conflated with the pole channel).
        // LEG (TwoBone): 2-bone IK thigh(A)→shin(B) reaching foot(Effector).
        // ARM (Arm/ThreeBone): shoulder(A) is the FK base (held at bind); 2-bone IK on
        // upper-arm(B)→forearm(C) reaching the hand(Effector) — solving A→B→Effector skips the
        // forearm and the elbow can't bend.
        var ikSolveAt = new Dictionary<int, (int mid, int end, int track, bool arm, Vector3 side)>();
        var animalLeg = new Dictionary<int, FrigFile.IkJob>();               // AnimalLeg chain root → full job
        var ikSet = new HashSet<int>();
        var ikBindOnly = new HashSet<int>();
        var ikShoulder = new Dictionary<int, (GaniTrack track, int ch)>();   // arm's ChainA ← track's first rot channel (world)
        if (ikJobs is not null && !DiagNoWorldSpace)
            foreach (var j in ikJobs)
            {
                if (j.Type == FrigFile.RigUnitType.AnimalLeg)
                {
                    // 5-bone quadruped leg — its own solver (RigAnimalLegDef::PoseToSkeleton),
                    // NOT the 2-bone treatment. Writes A..D; the hoof stays an FK child of D.
                    // Its target lives at the def's baked FLAT segment, not on "its" track.
                    if (anim.ChannelBySeg(j.SegV) is { IsRot: false }
                        && j.ChainB >= 0 && j.ChainC >= 0 && j.ChainD >= 0 && j.Effector >= 0)
                    {
                        animalLeg[j.ChainA] = j;
                        ikSet.Add(j.ChainB); ikSet.Add(j.ChainC); ikSet.Add(j.ChainD);
                    }
                    continue;
                }
                if (j.Track >= anim.Tracks.Count || !anim.Tracks[j.Track].HasPos) continue;   // no effector channel → leave FK
                bool arm = j.Type is FrigFile.RigUnitType.Arm or FrigFile.RigUnitType.ThreeBoneLikeTwoBone;
                if (arm && j.ChainB >= 0 && j.ChainC >= 0 && j.Effector >= 0)
                {
                    ikSolveAt[j.ChainB] = (j.ChainC, j.Effector, j.Track, true, j.PlaneNormal);
                    ikSet.Add(j.ChainC);
                    // The shoulder (ChainA) is driven by the arm track's FIRST rotation channel
                    // (the clavicle/shoulder rotation; the merged Rot would conflate it with the
                    // pole). World-space, like the spine Orientation units. Holding it at bind
                    // is fine near frame 0 but drifts badly as the shoulder rotates.
                    if (j.ChainA >= 0 && anim.Tracks[j.Track].Channels.Count > 0) ikShoulder[j.ChainA] = (anim.Tracks[j.Track], 0);
                }
                else if (!arm && j.ChainA >= 0 && j.ChainB >= 0 && j.Effector >= 0)
                {
                    ikSolveAt[j.ChainA] = (j.ChainB, j.Effector, j.Track, false, j.PlaneNormal);
                    ikSet.Add(j.ChainB);
                }
            }

        // The effector position channel is an OFFSET from the effector bone's bind position,
        // expressed in the IK chain's PARENT frame (clavicle for arms, pelvis for legs):
        //   effector = parentCurrentTransform · ( (effectorBind − parentBind) + offset ).
        // So the target follows the parent as the body leans — which is why the hand stays
        // reachable when the torso drapes forward. (A root-frame target leaves the hand high
        // while the leaned shoulder drops away, flinging the arm straight up.) Legs happen to
        // work either way because the pelvis barely leaves the root frame.

        var animWorld = new Matrix4x4[count];
        var skin = new Matrix4x4[count];
        for (int b = 0; b < count; b++)   // FMDL bones are parent-before-child
        {
            var bone = model.Bones[b];
            Vector3 localOffset = new(bone.LocalPosition.X, bone.LocalPosition.Y, bone.LocalPosition.Z);
            int parent = bone.ParentIndex;
            Matrix4x4 parentW = parent >= 0 && parent < count ? animWorld[parent] : Matrix4x4.Identity;

            if (DiagBoneOverride is { } ovr && ovr.TryGetValue(b, out var mo))
            {
                // harness instrument: adopt an externally supplied transform (e.g. the
                // engine's own pose for bones a game plugin rewrites — horse terrain pitch)
                animWorld[b] = mo;
                continue;
            }
            if (animalLeg.TryGetValue(b, out var alJob))
            {
                // AnimalLeg chain root: solve the whole 5-bone leg here (parent already
                // FK-correct). Writes animWorld for A..D; the hoof follows as FK child.
                SolveAnimalLegAt(model, anim, animWorld, alJob, frame, rootPos, rootRotInv, parentW);
                continue;
            }
            if (ikSet.Contains(b))           // mid (shin/forearm): already placed by the IK solve at the chain root
                continue;                    // animWorld[b] already set; skin computed in the 2nd pass

            Quaternion rot = Quaternion.Identity;
            Vector3 trans = localOffset;
            bool ws = false, absPos = false;
            if (ikShoulder.TryGetValue(b, out var sh))
            {
                rot = sh.track.Channels[sh.ch].SampleRot(frame);   // arm shoulder: first rot channel, world-space
                ws = !DiagNoWorldSpace;
            }
            else if (boneDrive.TryGetValue(b, out var d) && !ikBindOnly.Contains(b))
            {
                // the unit's baked flat segment first (the engine's binding); then the finger
                // sub-channel; then the track's merged rotation (legacy 1:1 rigs)
                rot = d.chRot is not null ? d.chRot.SampleRot(frame)
                    : d.ch >= 0 && d.ch < d.track.Channels.Count && d.track.Channels[d.ch].IsRot
                    ? d.track.Channels[d.ch].SampleRot(frame)
                    : d.track.SampleRot(frame);
                if (DiagConjugate) rot = Quaternion.Conjugate(rot);
                // ROOT is the WORLD frame — absolute, horizontal forward + yaw (never changes
                // height). TRANSFORM/LocalTransform are LOCAL within the body (relative to the
                // moving root) — they carry the crouch/climb height change. So only Root is
                // absolute; the rest layer locally on top of the world frame.
                if (CarriesTranslation(d.type) && d.chPos is not null) { trans = d.chPos.SampleVec(frame); absPos = d.type == FrigFile.RigUnitType.Root; }
                else if (CarriesTranslation(d.type) && d.track.TrySamplePos(frame, out var ap)) { trans = ap; absPos = d.type == FrigFile.RigUnitType.Root; }
                else if (applyGaniTranslation && d.track.TrySamplePos(frame, out var p)) trans = p;
                ws = d.ws && !DiagNoWorldSpace;
            }

            if (absPos)
            {
                // model-space absolute: bone sits at the world position from the track
                animWorld[b] = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(trans);
            }
            else if (ws)
            {
                // World-space orientation: rotation is absolute, position follows the chain.
                Vector3 worldPos = Vector3.Transform(localOffset, parentW);
                animWorld[b] = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(worldPos);
            }
            else
            {
                var local = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(trans);
                animWorld[b] = local * parentW;
            }

            // Chain root reached (parent already FK-correct): solve IK now, overwriting this
            // bone's world transform and placing the mid bone.
            if (ikSolveAt.TryGetValue(b, out var job) && anim.Tracks[job.track].TrySamplePos(frame, out var off))
            {
                Vector3 goalFmdl = Vector3.Transform(off - rootPos, rootRotInv);   // inverse cube transform: world target → centred body frame
                // bend (pole) QUAT from the gani's pole channel — the LAST rotation channel
                // of the IK track (leg track = effector,pole; arm track = shoulder,effector,pole).
                // The engine's leg driver composes parentRot·poleQuat, swings unit-X by it, and
                // double-crosses against the aim (AnimxIk.SolveTwoBoneAtRoot).
                var rotChs = anim.Tracks[job.track].Channels.Where(c => c.IsRot).ToList();
                Quaternion? poleQ = rotChs.Count > 0 ? rotChs[^1].SampleRot(frame) : null;
                if (DiagIkLog) IkLog.Add($"  job root={b} reqFrame={frame:F1}/{anim.FrameCount} off={off:F3} goalFmdl={goalFmdl:F3} side={job.side:F2}");
                // the effector bone's own rotation (hand/foot, world-space) — used to match the
                // distal bone's roll so the wrist/ankle doesn't twist
                Quaternion endRot = boneDrive.TryGetValue(job.end, out var ed) ? ed.track.SampleRot(frame) : Quaternion.Identity;
                int rp = model.Bones[b].ParentIndex;
                Quaternion parentRot = rp >= 0 && rp < count
                    ? Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(animWorld[rp]))
                    : Quaternion.Identity;
                SolveTwoBone(model, animWorld, b, job.mid, job.end, goalFmdl, poleQ, job.arm, endRot, job.side, parentRot);
            }
        }

        // HELP BONES: drive the skeleton's *_HLP twist/roll bones from the posed limbs so the
        // mesh skinned to them follows (un-knots the forearm/wrist). Runs on model-space
        // animWorld AFTER the full pose+IK, BEFORE skinning — exactly as the engine does.
        // Also yields the frame TENSION scalar that fades in the sub-normal wrinkle map.
        SubTension = helpBones is { Count: > 0 }
            ? FrdvFile.Apply(model, frig, animWorld, helpBones, DiagIkLog ? IkLog : null)
            : 0f;

        var bonePos = new Vector3[count];
        for (int b = 0; b < count; b++) bonePos[b] = Vector3.Transform(animWorld[b].Translation, viewShift);
        LastBonePos = bonePos;
        LastAnimWorld = (Matrix4x4[])animWorld.Clone();

        for (int b = 0; b < count; b++)
            skin[b] = InvBind(model.Bones[b]) * animWorld[b] * viewShift;
        return skin;
    }

    static Matrix4x4 InvBind(AnimBone bone)
        => Matrix4x4.CreateTranslation(-new Vector3(bone.WorldPosition.X, bone.WorldPosition.Y, bone.WorldPosition.Z));

    // AnimalLeg (type 10): the quadruped 5-bone leg, engine-exact per
    // RigAnimalLegDef::PoseToSkeleton + UpdatePose. Slots come from the unit's gani track
    // by the def's seg shorts; a slot with no channel in this gani keeps its BIND seed
    // (bind rots are identity; bend plane = SkeletonToPose of the bind chain). The target
    // channel is model-space — mapped into the centred body frame like the human goals.
    static void SolveAnimalLegAt(AnimSkeleton model, GaniAnimation anim, Matrix4x4[] animWorld,
        FrigFile.IkJob j, float frame, Vector3 rootPos, Quaternion rootRotInv, Matrix4x4 parentW)
    {
        Vector3 Bind(int i) => new(model.Bones[i].LocalPosition.X, model.Bones[i].LocalPosition.Y, model.Bones[i].LocalPosition.Z);
        Vector3 BindW(int i) => new(model.Bones[i].WorldPosition.X, model.Bones[i].WorldPosition.Y, model.Bones[i].WorldPosition.Z);

        Quaternion Slot(int segIdx, Quaternion seed)
            => anim.ChannelBySeg(segIdx) is { IsRot: true } ch ? ch.SampleRot(frame) : seed;

        // bind-seed bend plane: c = normalize(cross(p3 − p1, side)) over the BIND chain,
        // slot1 = FromTo(unit-Y → c) — what SkeletonToPose authors for the rest pose.
        Quaternion Slot1Seed()
        {
            Vector3 c = AnimxIk.Cross(BindW(j.ChainD) - BindW(j.ChainB), j.PlaneNormal);
            float len2 = AnimxIk.Dot3(c, c);
            if (len2 < 1e-12f) return Quaternion.Identity;
            c *= AnimxIk.NrRsqrt(len2);
            float gx = c.X, gy = c.Y + 1f, gz = c.Z;
            if (gy * gy + gz * gz + gx * gx < 1e-5f) return new Quaternion(0f, 1f, 0f, 0f);   // opposite → 180° about Y
            float d = c.Y * 2f + 2f;                       // 2 + 2·dot(unitY, c)
            float nr = AnimxIk.NrRsqrt(d);
            return new Quaternion(c.Z * nr, 0f, -c.X * nr, nr * d * 0.5f);   // (cross(Y,c)/√d, ½√d)
        }

        // target: the def's baked flat segment (gated present at setup)
        if (anim.ChannelBySeg(j.SegV) is not { IsRot: false } chV) return;
        Vector3 raw = chV.SampleVec(frame);
        Vector3 goal = Vector3.Transform(raw - rootPos, rootRotInv);         // inverse cube transform, as the human goals

        Quaternion slot0 = Slot(j.SegQ0, Quaternion.Identity);
        Quaternion slot1 = Slot(j.SegQ1, Slot1Seed());
        Quaternion slot2 = Slot(j.SegQ2, Quaternion.Identity);

        Vector3 parentPos = parentW.Translation;
        Quaternion parentRot = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(parentW));

        AnimxIk.SolveAnimalLeg(out var rot0, out var qU, out var qL,
            out var pos0, out var pos1, out var pos2, out var pos3,
            parentPos, parentRot,
            Bind(j.ChainA), Bind(j.ChainB), Bind(j.ChainC), Bind(j.ChainD), Bind(j.Effector),
            j.PlaneNormal, goal, slot0, slot1, slot2);

        float lu = qU.LengthSquared(), ll = qL.LengthSquared();
        bool sane = float.IsFinite(lu) && float.IsFinite(ll)
                 && lu > 0.8f && lu < 1.25f && ll > 0.8f && ll < 1.25f
                 && float.IsFinite(pos3.X) && float.IsFinite(pos3.Y) && float.IsFinite(pos3.Z);
        if (DiagIkLog) IkLog.Add($"  animalLeg A={j.ChainA} goal={goal:F3} pos1={pos1:F3} |qU|²={lu:F3} |qL|²={ll:F3}{(sane ? "" : " REJECTED")}");
        if (!sane)
        {
            // degenerate solve → bind-pose FK so the leg stays attached
            Vector3 p = parentPos + AnimxIk.Rotate(parentRot, Bind(j.ChainA));
            animWorld[j.ChainA] = Matrix4x4.CreateTranslation(p);
            p += Bind(j.ChainB); animWorld[j.ChainB] = Matrix4x4.CreateTranslation(p);
            p += Bind(j.ChainC); animWorld[j.ChainC] = Matrix4x4.CreateTranslation(p);
            p += Bind(j.ChainD); animWorld[j.ChainD] = Matrix4x4.CreateTranslation(p);
            return;
        }
        animWorld[j.ChainA] = Matrix4x4.CreateFromQuaternion(rot0) * Matrix4x4.CreateTranslation(pos0);
        animWorld[j.ChainB] = Matrix4x4.CreateFromQuaternion(qU) * Matrix4x4.CreateTranslation(pos1);
        animWorld[j.ChainC] = Matrix4x4.CreateFromQuaternion(qL) * Matrix4x4.CreateTranslation(pos2);
        animWorld[j.ChainD] = Matrix4x4.CreateFromQuaternion(slot2) * Matrix4x4.CreateTranslation(pos3);
    }

    // Two-bone IK. Preferred path = the ENGINE's solver (AnimxIk: CalcIkTwoBone @0x1418fde30
    // driven exactly like RigTwoBoneDef::PoseToSkeletonTwoBone @0x1418fef90): side axis = the
    // rig unit's chain_plane_normal (fixes each bone's ROLL), bend from the gani pole quat via
    // parentRot·poleQuat → rotate(unitX) → double-cross ⟂ aim, target clamped to reach+5e-4
    // with the near-extension boneA stretch. Falls back to the legacy analytic solve when the
    // rig gives no side axis / the track has no pole channel.
    static void SolveTwoBone(AnimSkeleton model, Matrix4x4[] animWorld, int root, int mid, int end, Vector3 goal, Quaternion? poleQuat = null, bool isArm = false, Quaternion endRot = default, Vector3 side = default, Quaternion parentRot = default)
    {
        Vector3 Bind(int i) => new(model.Bones[i].WorldPosition.X, model.Bones[i].WorldPosition.Y, model.Bones[i].WorldPosition.Z);
        Vector3 Pa = Bind(root), Pb = Bind(mid), Pe = Bind(end);
        float L1 = (Pb - Pa).Length(), L2 = (Pe - Pb).Length();
        if (L1 < 1e-5f || L2 < 1e-5f) return;

        Vector3 rootPos = animWorld[root].Translation;       // chain root world pos (parent already FK-correct)
        Vector3 toGoal = goal - rootPos;
        float dist = toGoal.Length();
        if (DiagIkLog) IkLog.Add($"root={root} mid={mid} end={end} bindA={Pa:F3} bindB={Pb:F3} bindE={Pe:F3} L1={L1:F3} L2={L2:F3} | rootPosFK={rootPos:F3} goal={goal:F3} rawDist={dist:F3}");
        if (dist < 1e-5f) return;

        if (UseGameIk && poleQuat is { } pq)
        {
            // ── game path: bind bone vectors (bind is translation-only, so these ARE the
            // rig def's bone vectors), engine solver, world rotations straight out.
            // The sampled pole quat is already model-space in this viewer (channels are
            // merged/world like the spine Orientation units), so parentRot = identity —
            // composing the FK parent on top double-rotates the pole and flips the bend.
            // Sanity gates: a plane normal that isn't unit-ish, or (near-)parallel to the
            // bind bone, gives QuatFromBasis a non-orthonormal basis → scaled quats →
            // exploded limbs. Any doubt → legacy solver (the backup's proven behaviour).
            float slen = AnimxIk.Dot3(side, side);
            if (slen > 0.25f && slen < 4f)
            {
                Vector3 sideN = side * (1f / MathF.Sqrt(slen));
                Vector3 bindA = Norm(Pb - Pa);
                if (MathF.Abs(Vector3.Dot(bindA, sideN)) < 0.9f)
                {
                    AnimxIk.SolveTwoBoneAtRoot(out var qU, out var qL, out var midW,
                        rootPos, Pb - Pa, Pe - Pb, sideN, goal,
                        DiagIkParentRotFk ? parentRot : Quaternion.Identity, pq);
                    float lu = qU.LengthSquared(), ll = qL.LengthSquared();
                    bool sane = float.IsFinite(lu) && float.IsFinite(ll)
                             && lu > 0.8f && lu < 1.25f && ll > 0.8f && ll < 1.25f
                             && float.IsFinite(midW.X) && float.IsFinite(midW.Y) && float.IsFinite(midW.Z);
                    if (sane)
                    {
                        animWorld[root] = Matrix4x4.CreateFromQuaternion(qU) * Matrix4x4.CreateTranslation(rootPos);
                        animWorld[mid] = Matrix4x4.CreateFromQuaternion(qL) * Matrix4x4.CreateTranslation(midW);
                        return;
                    }
                    if (DiagIkLog)
                    {
                        IkLog.Add($"  gameIK rejected root={root} |qU|²={lu:F3} |qL|²={ll:F3} side={side:F2}");
                        // recompute the solver's intermediates to find the degenerate leg
                        var q12 = AnimxIk.QMul(parentRot, pq);
                        var p = AnimxIk.RotateUnitX(q12);
                        var tv = goal - rootPos;
                        var braw = AnimxIk.Cross(tv, AnimxIk.Cross(p, tv));
                        var bl = MathF.Sqrt(AnimxIk.Dot3(braw, braw));
                        IkLog.Add($"    pole={pq} |pole|²={pq.LengthSquared():F3} p={p:F3} |bendRaw|={bl:F5} " +
                                  $"dot(p,tvN)={AnimxIk.Dot3(p, tv * (1f / MathF.Sqrt(AnimxIk.Dot3(tv, tv)))):F3}");
                    }
                }
                else if (DiagIkLog) IkLog.Add($"  gameIK skipped root={root}: side ∥ bind bone (dot={Vector3.Dot(bindA, sideN):F3})");
            }
            else if (DiagIkLog) IkLog.Add($"  gameIK skipped root={root}: bad side len²={slen:F3} side={side:F2}");
        }

        // ── legacy analytic fallback (no rig side axis / no pole channel / rejected) ───
        Vector3? poleDir = poleQuat is { } pq2 ? Vector3.Transform(Vector3.UnitX, pq2) : null;
        dist = Math.Clamp(dist, MathF.Abs(L1 - L2) + 1e-4f, L1 + L2 - 1e-4f);
        Vector3 dir = toGoal / toGoal.Length();

        // Bend plane. Prefer the gani-supplied pole direction (perpendicularised against the
        // aim); fall back to a bind-derived pole (component of the bind upper-bone direction
        // perpendicular to the straight root→end line, swung onto the aim). The bind fallback
        // is only good when the bind chain is already bent (legs); straight chains (arms) need
        // the gani pole or the elbow bends an arbitrary way.
        Vector3 pole;
        Vector3 polePerp = poleDir is { } pd ? Norm(pd - dir * Vector3.Dot(pd, dir)) : Vector3.Zero;
        if (polePerp.LengthSquared() > 1e-6f) pole = polePerp;
        else
        {
            Vector3 bindUpper = Norm(Pb - Pa), bindStraight = Norm(Pe - Pa);
            Vector3 poleBind = bindUpper - bindStraight * Vector3.Dot(bindUpper, bindStraight);
            if (poleBind.LengthSquared() < 1e-8f) poleBind = PerpendicularTo(bindStraight);
            Quaternion swing = FromTo(bindStraight, dir);
            pole = Norm(Vector3.Transform(Norm(poleBind), swing));
            pole = Norm(pole - dir * Vector3.Dot(pole, dir));
            if (pole.LengthSquared() < 1e-8f) pole = PerpendicularTo(dir);
        }

        float cosA = Math.Clamp((L1 * L1 + dist * dist - L2 * L2) / (2 * L1 * dist), -1f, 1f);
        float along = L1 * cosA;
        float h = L1 * MathF.Sqrt(MathF.Max(0f, 1f - cosA * cosA));
        Vector3 midPos = rootPos + dir * along + pole * h;

        Quaternion rot1 = FromTo(Norm(Pb - Pa), Norm(midPos - rootPos));
        Quaternion rot2 = FromTo(Norm(Pe - Pb), Norm(goal - midPos));
        animWorld[root] = Matrix4x4.CreateFromQuaternion(rot1) * Matrix4x4.CreateTranslation(rootPos);
        animWorld[mid] = Matrix4x4.CreateFromQuaternion(rot2) * Matrix4x4.CreateTranslation(midPos);
    }

    static Vector3 Norm(Vector3 v) { float l = v.Length(); return l > 1e-8f ? v / l : Vector3.UnitY; }

    static Vector3 PerpendicularTo(Vector3 v)
    {
        Vector3 c = Vector3.Cross(v, Vector3.UnitX);
        if (c.LengthSquared() < 1e-6f) c = Vector3.Cross(v, Vector3.UnitY);
        return Norm(c);
    }

    // Shortest-arc rotation taking unit vector a to unit vector b.
    static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        a = Norm(a); b = Norm(b);
        float d = Vector3.Dot(a, b);
        if (d >= 1f - 1e-6f) return Quaternion.Identity;
        if (d <= -1f + 1e-6f) return Quaternion.CreateFromAxisAngle(PerpendicularTo(a), MathF.PI);
        Vector3 c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }
}
