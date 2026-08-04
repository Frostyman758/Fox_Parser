// Engine blend-tree emulation: bake N weighted motions into one static pose
// Ported verbatim from FoxBrowser.Models.Anim 04/08/2026 — do not re-derive, copy.
using System.Numerics;

namespace MgsvModBldr.Tools.Anim;

// fox::anim::impl::BlendControlImpl::UpdatePoseBlend, viewer-side. The engine blends
// in POSE space (per rig-unit slot values) BEFORE pose→skeleton, as a normalized
// incremental InterpPose chain over the blend tree's children:
//   out = child0; acc = w0; for each next child: acc += w_i; if (acc > 0) out = interp(out, v_i, w_i/acc)
// (weights = the DYNAMIC per-motion floats at BlendControlImpl+[impl+4], ramped by
// game logic — captured per record by tap v4 / ATP3.)
// Viewer equivalent: sample every channel of every motion at its own frame, run the
// same chain on the VALUES (SlerpGame/LerpGame — the engine's interpolators), and
// emit a synthetic single-frame animation (static channels, flat SegIndex preserved)
// that the existing FK/IK/frdv pipeline consumes unchanged.
// Assumes all motions share one layout (same mtar CommonInfo) — true for anything
// the engine can put in one slot; a motion missing a channel (decode-dropped) is
// skipped for that channel and its weight excluded by construction.
public static class AnimBlend
{
    public readonly record struct Mot(GaniAnimation Anim, float Frame, float Weight);

    public static GaniAnimation Bake(IReadOnlyList<Mot> mots)
    {
        var head = mots[0].Anim;
        var res = new GaniAnimation { FrameCount = 1, SkeletonNames = head.SkeletonNames };
        foreach (var ht in head.Tracks)
        {
            var track = new GaniTrack { NameHash32 = ht.NameHash32, IsStatic = true };
            foreach (var hc in ht.Channels)
            {
                var ch = new GaniChannel
                {
                    IsRot = hc.IsRot, SegIndex = hc.SegIndex, SegType = hc.SegType,
                    IsStatic = true, FrameScale = hc.FrameScale,
                };
                if (hc.IsRot)
                {
                    Quaternion outQ = default; float acc = 0f; bool first = true;
                    foreach (var m in mots)
                    {
                        if (ChannelOf(m.Anim, hc) is not { } c) continue;
                        var v = c.SampleRot(m.Frame);
                        if (first) { outQ = v; acc = m.Weight; first = false; }
                        else { acc += m.Weight; if (acc > 0f) outQ = FoxVectormath.SlerpGame(m.Weight / acc, outQ, v); }
                    }
                    if (first) continue;                       // no motion carries this channel
                    ch.QuatKeys = new[] { outQ };
                    ch.Rot.Add((0, outQ));
                    track.Rot.Add((0, outQ));
                }
                else
                {
                    Vector3 outV = default; float acc = 0f; bool first = true;
                    foreach (var m in mots)
                    {
                        if (ChannelOf(m.Anim, hc) is not { } c) continue;
                        var v = c.SampleVec(m.Frame);
                        if (first) { outV = v; acc = m.Weight; first = false; }
                        else { acc += m.Weight; if (acc > 0f) outV = FoxVectormath.LerpGame(m.Weight / acc, outV, v); }
                    }
                    if (first) continue;
                    ch.VecKeys = new[] { outV };
                    ch.Pos.Add((0, outV));
                    track.Pos.Add((0, outV));
                }
                track.Channels.Add(ch);
                track.SegTypes.Add((ch.SegType, true));
            }
            res.Tracks.Add(track);
        }
        return res;
    }

    // matching channel in another motion: same flat segment (shared layout)
    static GaniChannel? ChannelOf(GaniAnimation a, GaniChannel like)
        => a.ChannelBySeg(like.SegIndex) is { } c && c.IsRot == like.IsRot ? c : null;
}
