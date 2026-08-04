// .frig rig parser
// Ported verbatim from FoxBrowser.Models.Anim 04/08/2026 — do not re-derive, copy.
using System.Numerics;

namespace MgsvModBldr.Tools.Anim;

// Fox Engine rig (.frig) — the bridge between a gani's rig-unit tracks and an FMDL's
// bones. A gani animates RIG UNITS (positionally: gani track[i] drives rig unit i). The
// frig's BoneList tells, per bone, WHICH rig unit drives it (RigIndex) and that bone's
// StrCode32 name. So the resolve is:
//     FMDL bone (matched by name hash) <- gani track[ BoneList[b].RigIndex ]
// For a standard humanoid the rig units are named after their bones, so a gani track's
// name hashes equal to the bone name and direct matching happens to work; for a custom
// rig (Sahelanthropus) the unit names differ and ONLY this indirection resolves them.
// Port of Rollins fox_frig_types.py / frig.bt (version 102). Parse-only, in-memory.
public sealed class FrigFile
{
    public enum RigUnitType
    {
        Root = 1, Orientation = 2, TwoBone = 3, LocalOrientation = 4, LocalTransform = 5,
        ThreeBoneLikeTwoBone = 6, Transform = 7, Arm = 8, LocalTransformSrt = 9,
        AnimalLeg = 10, MultiLocalOrientation = 11, TwoBoneTrans = 12,
    }

    public readonly record struct FrigBone(uint RigIndex, uint NameHash32);

    /// <summary>
    /// A named per-rig-unit weight set from the frig's MaskDef — ADDED to this copy; the
    /// viewer's original reads MaskDefOffset and discards it.
    ///
    /// The rig ships the masks the engine's own mirror uses: `MirrorL` and `MirrorR` select
    /// the left and right unit sets, in order, so pairing limbs is reading Konami's answer
    /// rather than guessing from layout shape. `human_finger.frig` carries 15 of them —
    /// Lower/Upper/Head/LArm/RArm/LHand/RHand/MirrorL/MirrorR/Carry*/Weak*/HeadAnd*.
    /// </summary>
    public readonly record struct RigMask(uint NameHash, string Name, float[] Weights);

    public readonly List<RigMask> Masks = new();

    /// <summary>Unit indices a mask selects (weight &gt; 0), in rig order. Empty if absent.</summary>
    public List<int> MaskUnits(string name)
    {
        var outp = new List<int>();
        foreach (var m in Masks)
        {
            if (!string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            for (int i = 0; i < m.Weights.Length; i++) if (m.Weights[i] > 0f) outp.Add(i);
            break;
        }
        return outp;
    }

    /// <summary>
    /// Left/right unit pairs straight off `MirrorL` / `MirrorR`, paired in rig order.
    /// Empty when the rig has no mirror masks — which is the honest answer for a rig that
    /// was never authored to mirror, rather than a humanoid guess applied to a bird.
    /// </summary>
    public List<(int, int)> MirrorPairs()
    {
        var l = MaskUnits("MirrorL");
        var r = MaskUnits("MirrorR");
        var outp = new List<(int, int)>();
        for (int i = 0; i < l.Count && i < r.Count; i++) outp.Add((l[i], r[i]));
        return outp;
    }

    // Full rig-unit definition (only the fields we need to evaluate the unit / solve IK).
    // Indices are SKELETON indices — positions in the BoneList (skel_index N == Bones[N]).
    // For IK units (TwoBone / Arm) the chain_* indices are the chain bones and effector is
    // the IK goal bone; the gani track for that unit carries the effector world position.
    // PlaneNormal = the unit's `chain_plane_normal` (the engine's CalcIkTwoBone arg5) — the fixed
    // bind-space "up" that fixes each IK bone's ROLL. Without it the 2-bone solve has no roll
    // reference (arbitrary/collapsing forearm & shin, ankle twist). Zero for non-IK units.
    // ChainD + ParamA/B: AnimalLeg only (5-bone leg; params read 0.6/1.5 in tpp_horse).
    // Seg* (AnimalLeg): the def's 4 segment shorts (runtime +0x32..+0x38) — per-slot channel
    // indices into the unit's gani track (RigAnimalLegDef::UpdatePose → GetDataQuat(idx)):
    // SegQ0→slot0 (scapula rot), SegV→slot3/4 (hoof target vec), SegQ1→slot1 (bend plane),
    // SegQ2→slot2 (cannon rot).
    public readonly record struct RigUnit(
        RigUnitType Type, int SkelIndex, int Effector,
        int ChainA, int ChainB, int ChainC, Vector3 PlaneNormal = default,
        int ChainD = -1, float ParamA = 0f, float ParamB = 0f,
        int SegQ0 = -1, int SegV = -1, int SegQ1 = -1, int SegQ2 = -1,
        int TrackCount = 0);

    public int RigUnitCount;
    public int SegmentCount;
    public readonly List<RigUnitType> UnitTypes = new();   // one per rig unit, by index
    public readonly List<RigUnit> Units = new();           // one per rig unit, full def
    public readonly List<FrigBone> Bones = new();          // skeleton bones (name + driving unit)

    public static FrigFile? TryParse(byte[] data)
    {
        try { return Parse(data); } catch { return null; }
    }

    public static FrigFile Parse(byte[] data)
    {
        var f = new FrigFile();
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        // Header (32 bytes): FoxDataName(8), Version(4), RigUnitCount(4), SegmentCount(4),
        //                    FileSize(4), BoneListOffset(4), MaskDefOffset(4)
        br.BaseStream.Position = 8;                 // skip FoxDataName
        uint version = br.ReadUInt32();
        if (version != 102) throw new InvalidDataException($"frig version {version} != 102");
        f.RigUnitCount = (int)br.ReadUInt32();
        f.SegmentCount = (int)br.ReadUInt32();
        br.ReadUInt32();                            // FileSize
        int boneListOffset = (int)br.ReadUInt32();
        int maskDefOffset = (int)br.ReadUInt32();

        // RigDef: RigUnitCount int offsets, each to a RigUnitDef whose first uint is Type.
        // We only need the per-index Type (to flag world-space units later); the bone
        // linkage comes from the BoneList, not the unit defs.
        var unitOffsets = new int[f.RigUnitCount];
        for (int i = 0; i < f.RigUnitCount; i++) unitOffsets[i] = br.ReadInt32();
        foreach (var off in unitOffsets)
        {
            if (off <= 0 || off + 4 > data.Length) { f.UnitTypes.Add(0); f.Units.Add(default); continue; }
            br.BaseStream.Position = off;
            var u = ReadRigUnit(br);
            f.UnitTypes.Add(u.Type);
            f.Units.Add(u);
        }

        // BoneList at BoneListOffset: int count, then Bone { uint RigIndex; uint Name; }
        br.BaseStream.Position = boneListOffset;
        int boneCount = br.ReadInt32();
        if (boneCount < 0 || boneListOffset + 4 + (long)boneCount * 8 > data.Length)
            throw new InvalidDataException($"frig bone count {boneCount} out of range");
        for (int i = 0; i < boneCount; i++)
        {
            uint rigIndex = br.ReadUInt32();
            uint name = br.ReadUInt32();
            f.Bones.Add(new FrigBone(rigIndex, name));
        }

        // MaskDef: u32 unitCount, u32 maskCount, maskCount x u32 offset (relative to the
        // section), then each mask = u32 nameHash, 12-byte NUL-padded name, unitCount floats.
        if (maskDefOffset > 0 && maskDefOffset + 8 <= data.Length)
        {
            br.BaseStream.Position = maskDefOffset;
            int maskUnits = br.ReadInt32();
            int maskCount = br.ReadInt32();
            if (maskUnits > 0 && maskUnits <= 4096 && maskCount > 0 && maskCount <= 4096)
            {
                var offs = new int[maskCount];
                for (int i = 0; i < maskCount; i++) offs[i] = br.ReadInt32();
                foreach (var off in offs)
                {
                    long at = maskDefOffset + off;
                    if (at < 0 || at + 16 + (long)maskUnits * 4 > data.Length) continue;
                    br.BaseStream.Position = at;
                    uint hash = br.ReadUInt32();
                    var raw = br.ReadBytes(12);
                    int len = Array.IndexOf(raw, (byte)0);
                    var nm = System.Text.Encoding.ASCII.GetString(raw, 0, len < 0 ? raw.Length : len);
                    var w = new float[maskUnits];
                    for (int i = 0; i < maskUnits; i++) w[i] = br.ReadSingle();
                    f.Masks.Add(new RigMask(hash, nm, w));
                }
            }
        }
        return f;
    }

    // Read one RigUnitDef (faithful to fox_frig_types.py RigUnitDef.read). Base is 16B
    // (Type u32 / TrackCount s16 / BoneCount s16 / ParentBoneIndex s16 / ParentUnitIndex
    // s16 / Padding u32), then type-specific index data. We keep only the skeleton/chain/
    // effector indices used to evaluate the unit.
    private static RigUnit ReadRigUnit(BinaryReader br)
    {
        var type = (RigUnitType)br.ReadUInt32();
        int trackCount = br.ReadInt16();
        br.ReadInt16(); br.ReadInt16(); br.ReadInt16(); br.ReadUInt32();                  // bone/parent/parent/pad
        int skel = -1, eff = -1, a = -1, b = -1, c = -1, d = -1;
        int sq0 = -1, sv = -1, sq1 = -1, sq2 = -1;
        float pA = 0f, pB = 0f;
        Vector3 planeN = default;
        Vector3 ReadPlaneNormal() { br.BaseStream.Position += 16; var n = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); br.ReadSingle(); return n; }  // skip unknown(16), read chain_plane_normal(12)+pad(4)
        switch (type)
        {
            case RigUnitType.Root:
                sq0 = br.ReadInt16(); sv = br.ReadInt16();                        // seg_a, seg_b
                break;
            case RigUnitType.Orientation:
            case RigUnitType.LocalOrientation:
                skel = br.ReadInt16(); sq0 = br.ReadInt16();                      // skel_index, seg_a
                break;
            case RigUnitType.Transform:
            case RigUnitType.LocalTransform:
                skel = br.ReadInt16(); sq0 = br.ReadInt16(); sv = br.ReadInt16(); // skel_index, seg_a, seg_b
                break;
            case RigUnitType.ThreeBoneLikeTwoBone:
                planeN = ReadPlaneNormal();
                a = br.ReadInt16(); b = br.ReadInt16(); c = br.ReadInt16();
                sq0 = br.ReadInt16(); sv = br.ReadInt16();                        // seg_a/b
                break;
            case RigUnitType.Arm:
                planeN = ReadPlaneNormal();
                a = br.ReadInt16(); b = br.ReadInt16(); c = br.ReadInt16();
                sq0 = br.ReadInt16(); sv = br.ReadInt16(); sq1 = br.ReadInt16();  // seg_a/b/c
                eff = br.ReadInt16();
                break;
            case RigUnitType.TwoBone:
                planeN = ReadPlaneNormal();
                a = br.ReadInt16(); b = br.ReadInt16();
                sq0 = br.ReadInt16(); sv = br.ReadInt16();                        // seg_a/b
                eff = br.ReadInt16();
                break;
            case RigUnitType.LocalTransformSrt:
                skel = br.ReadInt16();
                break;
            case RigUnitType.AnimalLeg:
                // side axis (16: xyz+pad, ±X mirrored per leg side) + 2 params (0.6/1.5 in
                // tpp_horse) + FIVE chain shorts (scapula..hoof) + 4 seg shorts. The 010
                // template's "unknown(24)" = axis+params; it also misses the 5th index.
                // Seg shorts mirror runtime def +0x32..+0x38 (UpdatePose slot binding):
                // slot0 quat, target vec, slot1 quat, slot2 quat.
                planeN = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); br.ReadSingle();
                pA = br.ReadSingle(); pB = br.ReadSingle();
                a = br.ReadInt16(); b = br.ReadInt16(); c = br.ReadInt16();
                d = br.ReadInt16(); eff = br.ReadInt16();                         // skel_d, skel_e (hoof)
                sq0 = br.ReadInt16(); sv = br.ReadInt16();
                sq1 = br.ReadInt16(); sq2 = br.ReadInt16();
                break;
            case RigUnitType.MultiLocalOrientation:
                skel = br.ReadInt16(); sq0 = br.ReadInt16();                      // skel_start, seg_start
                break;
            case RigUnitType.TwoBoneTrans:
                br.BaseStream.Position += 16;                                     // unknown(16)
                a = br.ReadInt16(); b = br.ReadInt16();                           // skel_a/b
                break;
        }
        return new RigUnit(type, skel, eff, a, b, c, planeN, d, pA, pB, sq0, sv, sq1, sq2, trackCount);
    }

    // How a bone is driven: which gani track, the rig-unit type (local vs world orientation),
    // and the sub-channel index within the track. Channel >= 0 is for MULTI_LOCAL_ORIENTATION
    // units (fingers): one track carries N rotations, bone[start+i] ← channel i. -1 means use
    // the track's (single) merged rotation.
    // SegRot/SegPos = the unit's baked FLAT segment indices (the engine's real binding —
    // GaniAnimation.ChannelBySeg). Track/Channel remain as the legacy fallback for rigs
    // whose units group 1:1 with tracks (humans; where both views agree).
    public readonly record struct BoneDrive(int Track, RigUnitType Type, int Channel = -1,
                                            int SegRot = -1, int SegPos = -1);

    // World-space rig units set a bone's WORLD orientation directly (the chain only
    // supplies position) rather than a parent-relative local transform.
    public static bool IsWorldSpace(RigUnitType t) =>
        t is RigUnitType.Orientation or RigUnitType.TwoBone or RigUnitType.Arm;

    // Resolve FMDL bones to gani tracks through this rig. Returns FMDL-bone-index ->
    // (track, unitType) for every bone whose name matches a frig bone AND whose driving
    // rig unit has a track in the gani. (A single rig unit can drive several bones, so
    // the map is bone->unit, not unit->bone.)
    public Dictionary<int, BoneDrive> ResolveBoneDrives(
        IReadOnlyList<ulong> fmdlNames, IReadOnlyList<int> boneNameIndex, int trackCount, out int matchCount)
    {
        var hashToBone = new Dictionary<uint, int>(boneNameIndex.Count);
        for (int b = 0; b < boneNameIndex.Count; b++)
        {
            int ni = boneNameIndex[b];
            if (ni >= 0 && ni < fmdlNames.Count) hashToBone.TryAdd((uint)(fmdlNames[ni] & 0xFFFFFFFF), b);
        }

        var map = new Dictionary<int, BoneDrive>();
        for (int bi = 0; bi < Bones.Count; bi++)                            // bi == this bone's skel index
        {
            var fb = Bones[bi];
            if (fb.RigIndex >= (uint)trackCount) continue;                 // no track for this unit
            if (!hashToBone.TryGetValue(fb.NameHash32, out int bone)) continue;
            var type = fb.RigIndex < (uint)UnitTypes.Count ? UnitTypes[(int)fb.RigIndex] : RigUnitType.LocalOrientation;
            // A MULTI_LOCAL_ORIENTATION unit drives a run of bones from one multi-rotation
            // track; this bone's channel = its offset from the unit's skel_index_start.
            int channel = -1;
            int segRot = -1, segPos = -1;
            if (fb.RigIndex < (uint)Units.Count)
            {
                var un = Units[(int)fb.RigIndex];
                switch (type)
                {
                    case RigUnitType.Orientation:
                    case RigUnitType.LocalOrientation:
                        segRot = un.SegQ0;
                        break;
                    case RigUnitType.Transform:
                    case RigUnitType.LocalTransform:
                        segRot = un.SegQ0; segPos = un.SegV;
                        break;
                    case RigUnitType.MultiLocalOrientation:
                        if (un.SkelIndex >= 0 && bi >= un.SkelIndex)
                        {
                            channel = bi - un.SkelIndex;
                            if (un.SegQ0 >= 0) segRot = un.SegQ0 + channel;
                        }
                        break;
                }
            }
            map[bone] = new BoneDrive((int)fb.RigIndex, type, channel, segRot, segPos);
        }
        matchCount = map.Count;
        return map;
    }

    // Back-compat thin wrapper: bone -> track only (used by the --frig diagnostic).
    public Dictionary<int, int> ResolveBonesToTracks(
        IReadOnlyList<ulong> fmdlNames, IReadOnlyList<int> boneNameIndex, int trackCount, out int matchCount)
        => ResolveBoneDrives(fmdlNames, boneNameIndex, trackCount, out matchCount)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Track);

    // An IK chain to solve: the chain bones (FMDL indices, root→tip), the effector bone,
    // and the gani track that carries the effector's world position. ChainC is -1 for a
    // 2-bone (TwoBone, leg) chain. Track == rig-unit index (gani track i drives unit i).
    // ChainD + ParamA/B + Seg* (per-slot channel indices): AnimalLeg 5-bone chains only.
    public readonly record struct IkJob(RigUnitType Type, int ChainA, int ChainB, int ChainC, int Effector, int Track, Vector3 PlaneNormal = default,
                                        int ChainD = -1, float ParamA = 0f, float ParamB = 0f,
                                        int SegQ0 = -1, int SegV = -1, int SegQ1 = -1, int SegQ2 = -1);

    // Resolve every IK unit (TwoBone leg / Arm) to FMDL bones. Each unit's skel/chain
    // indices index the BoneList (skel_index N == Bones[N]); we map those to FMDL bones by
    // name hash. Only jobs whose chain + effector all resolve and whose track exists are
    // returned — these are solved analytically in AnimSkinner instead of (wrongly) applying
    // the effector orientation to every chain bone.
    public List<IkJob> ResolveIkJobs(
        IReadOnlyList<ulong> fmdlNames, IReadOnlyList<int> boneNameIndex, int trackCount)
    {
        var hashToBone = new Dictionary<uint, int>(boneNameIndex.Count);
        for (int b = 0; b < boneNameIndex.Count; b++)
        {
            int ni = boneNameIndex[b];
            if (ni >= 0 && ni < fmdlNames.Count) hashToBone.TryAdd((uint)(fmdlNames[ni] & 0xFFFFFFFF), b);
        }
        int Map(int skel) => skel >= 0 && skel < Bones.Count && hashToBone.TryGetValue(Bones[skel].NameHash32, out int fb) ? fb : -1;

        var jobs = new List<IkJob>();
        for (int u = 0; u < Units.Count && u < trackCount; u++)
        {
            var unit = Units[u];
            if (unit.Type is not (RigUnitType.TwoBone or RigUnitType.Arm or RigUnitType.ThreeBoneLikeTwoBone
                                  or RigUnitType.AnimalLeg)) continue;
            int a = Map(unit.ChainA), b = Map(unit.ChainB), c = Map(unit.ChainC), e = Map(unit.Effector);
            if (a < 0 || b < 0 || e < 0) continue;                 // chain root, mid, and goal must all be real bones
            jobs.Add(new IkJob(unit.Type, a, b, c, e, u, unit.PlaneNormal,
                               unit.Type == RigUnitType.AnimalLeg ? Map(unit.ChainD) : -1,
                               unit.ParamA, unit.ParamB,
                               unit.SegQ0, unit.SegV, unit.SegQ1, unit.SegQ2));
        }
        return jobs;
    }
}
