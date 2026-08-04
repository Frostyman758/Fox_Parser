// Decoded gani animation + channels
// Ported verbatim from FoxBrowser.Models.Anim 04/08/2026 — do not re-derive, copy.
using System.Linq;
using System.Numerics;

namespace MgsvModBldr.Tools.Anim;

// One decoded sub-channel of a track (a single track segment). A track can carry several:
// e.g. an Arm unit's track is (shoulder-rot q, hand-effector v, elbow-pole q) and a finger
// unit's track is 16 rotations (one per finger bone). The merged Rot/Pos lists conflate
// these — Channels keeps them separate so IK can read the pole, and fingers their own key.
//
// Sampling is a 1:1 port of the game's evaluation (Tpp_main_win64, fox::anim):
//  • segment walk = DataSegment Play* (add dt; stay while time <= dur; advance while
//    dur < time, subtracting one duration per step — float subtraction CHAIN, not a sum)
//  • duration_i = float(deltaByte_i) * frameScale;  invDur_i = 1f / duration_i  — both
//    precomputed once, exactly as SetNextQuatDataCore / InitVectorLinear* do
//  • t = timeInSegment * invDur   (never a divide at eval time)
//  • quats  → FoxVectormath.SlerpGame (Vectormath slerp + rsqrt-NR renormalize); the
//    hemisphere fix happens HERE on the start key, not at decode (keys stay raw)
//  • vectors → FoxVectormath.LerpGame ((1-t)*cur + t*next)
//  • hermite → FoxVectormath.HermiteGame; the first segment uses m0 == m1 (the game
//    seeds +0x60 from +0x70 in InitVectorHermiteControl — key0 stores no tangent)
//  • static segments hold one key; quats still run SlerpGame(0, q, q) (the game's
//    GetDataQuat renormalizes them through the same path), vectors return the raw key.
public sealed class GaniChannel
{
    public bool IsRot;
    public int SegIndex = -1;                               // FLAT engine segment index across the gani (frig seg shorts index this)
    public GaniSegmentType SegType;
    public bool IsStatic;
    public bool IsHermite;
    public float FrameScale = 1f;                           // TrackHeader byte @+0x10 (frame step)

    // exact per-key data (raw decode; frames = accumulated deltas, for display/enumeration)
    public Quaternion[] QuatKeys = Array.Empty<Quaternion>();   // public here: the mirror rewrites these in place
    public Vector3[] VecKeys = Array.Empty<Vector3>();
    internal Vector3[] TanKeys = Array.Empty<Vector3>();    // hermite; [0] unused
    public float[] Durations = Array.Empty<float>();      // n-1 segment durations
    public float[] InvDur = Array.Empty<float>();         // 1f / Durations[i]

    // legacy views (kept for enumeration/stats consumers; values are the raw decoded keys)
    public readonly List<(int frame, Quaternion rot)> Rot = new();
    public readonly List<(int frame, Vector3 pos)> Pos = new();

    private int KeyCount => IsRot ? QuatKeys.Length : VecKeys.Length;

    // Game segment-advance machine: find (segment, timeInSegment) for scaled time T,
    // reproducing the float subtraction chain and boundary comparisons of the Play*
    // loops. Well-formed ganis have Σdur == frameCount*scale, so the wrap/end branch is
    // only reachable past the end — we freeze on the last key there (game "anim ended":
    // cur = next).
    private (int seg, float t, bool ended) Walk(float time)
    {
        int n = KeyCount;
        if (IsStatic || n <= 1 || Durations.Length == 0) return (0, 0f, false);
        if (time <= Durations[0]) return (0, time * InvDur[0], false);
        int i = 0;
        while (true)
        {
            time -= Durations[i];
            if (i + 1 >= Durations.Length) return (Durations.Length - 1, 0f, true);
            i++;
            if (!(Durations[i] < time)) break;              // continue while dur < time
        }
        return (i, time * InvDur[i], false);
    }

    public Quaternion SampleRot(float frame)
    {
        int n = QuatKeys.Length;
        if (n == 0) return Quaternion.Identity;
        if (IsStatic || n == 1) return FoxVectormath.SlerpGame(0f, QuatKeys[0], QuatKeys[0]);
        var (seg, t, ended) = Walk(frame * FrameScale);
        if (ended) return FoxVectormath.SlerpGame(0f, QuatKeys[n - 1], QuatKeys[n - 1]);
        return FoxVectormath.SlerpGame(t, QuatKeys[seg], QuatKeys[seg + 1]);
    }

    public Vector3 SampleVec(float frame)
    {
        int n = VecKeys.Length;
        if (n == 0) return default;
        if (IsStatic || n == 1) return VecKeys[0];
        var (seg, t, ended) = Walk(frame * FrameScale);
        if (ended) return VecKeys[n - 1];
        if (IsHermite)
        {
            // first segment: m0 = m1 (init copies +0x70 → +0x60); afterwards m0 is the
            // previous key's tangent.
            Vector3 m1 = TanKeys[seg + 1];
            Vector3 m0 = seg == 0 ? m1 : TanKeys[seg];
            return FoxVectormath.HermiteGame(t, VecKeys[seg], VecKeys[seg + 1], m0, m1);
        }
        return FoxVectormath.LerpGame(t, VecKeys[seg], VecKeys[seg + 1]);
    }
}

public sealed class GaniTrack
{
    public uint NameHash32;                                 // StrCode32 bone-name hash
    public bool IsStatic;
    public readonly List<(int frame, Quaternion rot)> Rot = new();
    public readonly List<(int frame, Vector3 pos)> Pos = new();
    public readonly List<GaniChannel> Channels = new();     // per-segment, un-merged
    public readonly List<(GaniSegmentType type, bool decoded)> SegTypes = new();   // EVERY layout segment in order (incl. dropped)
    public bool HasRot => Rot.Count > 0;
    public bool HasPos => Pos.Count > 0;

    // The rotation channels in order: [0] = first (arm shoulder FK / finger 0), [^1] = pole.
    public IEnumerable<GaniChannel> RotChannels => Channels.Where(c => c.IsRot);

    // TrackControl::GetTransformData walks a unit's segments in order and writes the rot
    // output for EVERY quat segment and the pos output for every vector segment — so the
    // LAST channel of each kind wins. Mirror that here.
    public Quaternion SampleRot(float frame)
    {
        for (int i = Channels.Count - 1; i >= 0; i--)
            if (Channels[i].IsRot) return Channels[i].SampleRot(frame);
        return Quaternion.Identity;
    }

    public bool TrySamplePos(float frame, out Vector3 pos)
    {
        for (int i = Channels.Count - 1; i >= 0; i--)
            if (!Channels[i].IsRot && Channels[i].SegType is GaniSegmentType.Vector3 or GaniSegmentType.VectorDiff)
            {
                pos = Channels[i].SampleVec(frame);
                return true;
            }
        pos = default;
        return false;
    }
}

// A decoded gani animation: per-bone tracks + the skeleton name list that bridges
// the gani's StrCode32 track names to FMDL bone hashes.
public sealed class GaniAnimation
{
    public int FrameCount;
    public readonly List<GaniTrack> Tracks = new();
    public IReadOnlyList<string> SkeletonNames = Array.Empty<string>();

    // Channel by FLAT engine segment index — the address space of the frig defs' baked seg
    // shorts (TrackControl::SegmentOffsets[idx]; RigDef::UpdatePose GetDataQuat(idx)).
    // Track grouping is authoring-side only; the engine binds by this flat order. Null when
    // that segment is aux-typed or failed to decode.
    private Dictionary<int, GaniChannel>? _bySeg;
    public GaniChannel? ChannelBySeg(int segIdx)
    {
        if (segIdx < 0) return null;
        if (_bySeg is null)
        {
            _bySeg = new Dictionary<int, GaniChannel>();
            foreach (var t in Tracks) foreach (var c in t.Channels) if (c.SegIndex >= 0) _bySeg.TryAdd(c.SegIndex, c);
        }
        return _bySeg.TryGetValue(segIdx, out var ch) ? ch : null;
    }

    // Map each gani track to an FMDL bone index. The FMDL stores bone names as 48-bit
    // StringId hashes; a gani track name is StrCode32 == StringId & 0xFFFFFFFF. So we
    // match DIRECTLY by the low 32 bits of each FMDL bone hash — no skeleton strings
    // needed (TPP retail strips them, which a string bridge can't survive).
    // Returns track-index → bone-index (matched tracks only) + the match count.
    public Dictionary<int, int> ResolveToBones(IReadOnlyList<ulong> fmdlNames, IReadOnlyList<int> boneNameIndex, out int matchCount)
    {
        var str32ToBone = new Dictionary<uint, int>(boneNameIndex.Count);
        for (int b = 0; b < boneNameIndex.Count; b++)
        {
            int ni = boneNameIndex[b];
            if (ni >= 0 && ni < fmdlNames.Count) str32ToBone.TryAdd((uint)(fmdlNames[ni] & 0xFFFFFFFF), b);
        }

        var map = new Dictionary<int, int>();
        for (int t = 0; t < Tracks.Count; t++)
            if (str32ToBone.TryGetValue(Tracks[t].NameHash32, out int bone))
                map[t] = bone;
        matchCount = map.Count;
        return map;
    }
}
