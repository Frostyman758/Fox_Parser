// Solve the mirror against the game's own twin clip, in 3D
// 04/08/2026
using System.Numerics;
using MgsvModBldr.Tools.Anim;
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Mtar.Transcode;

namespace MgsvModBldr.Tools.Cli;

// mirror <in.mtar> --solve <leftClip> <rightClip>
//
// Mirrors the left clip and measures it against the game's OWN right clip as POSITIONS IN 3D,
// per unit per frame — not as quaternion keys, which cannot see a limb in the wrong place.
//
// Everything here runs on the PORTED decoder (GaniFile/GaniChannel), and the mirror is applied
// to the DECODED values rather than to bytes. Reflecting a component is the same operation
// either way, and this keeps the measurement off any hand-written sampler: a re-derived
// accumulator was itself the bug in the first version of this harness.
internal static class MirrorSolveCmd
{
    public static int Run(string mtarPath, string leftName, string rightName, List<(int, int)> pairs)
    {
        var file = File.ReadAllBytes(mtarPath);
        int count = (int)BitConverter.ToUInt32(file, 4);
        var dict = MtarGaniNames.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "dict", "mtar_dictionary.txt"));

        int leftAt = -1, rightAt = -1;
        for (int i = 0; i < count; i++)
        {
            int at = 0x20 + i * 16;
            if (at + 16 > file.Length) break;
            if (!dict.TryGetValue(MtarGaniNames.NameHash(BitConverter.ToUInt64(file, at)), out var path)) continue;
            var leaf = path[(path.LastIndexOf('/') + 1)..];
            if (leaf.Equals(leftName, StringComparison.OrdinalIgnoreCase)) leftAt = (int)BitConverter.ToUInt32(file, at + 8);
            else if (leaf.Equals(rightName, StringComparison.OrdinalIgnoreCase)) rightAt = (int)BitConverter.ToUInt32(file, at + 8);
        }
        if (leftAt < 0) { Console.Error.WriteLine($"FOXDIE: no clip named {leftName}"); return 2; }
        if (rightAt < 0) { Console.Error.WriteLine($"FOXDIE: no clip named {rightName}"); return 2; }

        var target = GaniFile.DecodeV1Gani(file, rightAt);
        Console.WriteLine($"{leftName} -> {rightName}   target {target.FrameCount}f, {target.Tracks.Count} tracks");
        Console.WriteLine("  variant            mean 3D err     worst   units");
        Console.WriteLine("  ---------------------------------------------------");

        Console.WriteLine($"  {"none (baseline)",-18}{Fmt(Compare(GaniFile.DecodeV1Gani(file, leftAt), target))}");
        foreach (var axis in new[] { 0, 1, 2 })
            foreach (var (label, swap, reflect) in new[] { ("both", true, true), ("reflect", false, true), ("swap", true, false) })
            {
                if (!reflect && axis != 0) continue;                 // swap-only is axis-free
                var a = GaniFile.DecodeV1Gani(file, leftAt);
                if (reflect) Reflect(a, axis);
                if (swap) Swap(a, pairs);
                Console.WriteLine($"  {$"{"xyz"[axis]} {label}",-18}{Fmt(Compare(a, target))}");
            }
        // Search the whole sphere for the plane that actually mirrors this rig.
        var src0 = GaniFile.DecodeV1Gani(file, leftAt);
        var (bn, be) = SolveNormal(src0, target, pairs);
        Console.WriteLine($"\n  best plane normal ({bn.X,7:F4},{bn.Y,7:F4},{bn.Z,7:F4})  err {be:F4}");

        // Per-unit breakdown for the best variant: WHICH units the mirror breaks.
        var best = GaniFile.DecodeV1Gani(file, leftAt); Reflect(best, 0); Swap(best, pairs);
        var pu = new Dictionary<uint, double>(); Compare(best, target, pu);
        var bl = GaniFile.DecodeV1Gani(file, leftAt);
        var pb = new Dictionary<uint, double>(); Compare(bl, target, pb);
        Console.WriteLine("\n  unit        mirrored   baseline   verdict");
        foreach (var kv in pu)
        {
            double b0 = pb.TryGetValue(kv.Key, out var v) ? v : 0;
            Console.WriteLine($"  {kv.Key:x8}  {kv.Value,9:F4}  {b0,9:F4}   {(kv.Value < b0 ? "better" : "WORSE")}");
        }
        return 0;
    }

    private static string Fmt((double mean, double worst, int units) r)
        => $"{r.mean,11:F4}{r.worst,10:F4}{r.units,8}";

    /// <summary>
    /// Reflect every value across the plane whose normal is <paramref name="keep"/>: a rotation
    /// negates the two vector components that are NOT the normal, a position negates the one
    /// that IS. Identical to the bit-level mirror, done on decoded values.
    /// </summary>
    private static void Reflect(GaniAnimation a, int keep)
    {
        foreach (var t in a.Tracks)
        {
            for (int i = 0; i < t.Rot.Count; i++) t.Rot[i] = (t.Rot[i].Item1, Flip(t.Rot[i].Item2, keep));
            for (int i = 0; i < t.Pos.Count; i++) t.Pos[i] = (t.Pos[i].Item1, Neg(t.Pos[i].Item2, keep));
            foreach (var c in t.Channels)
            {
                for (int i = 0; i < c.Rot.Count; i++) c.Rot[i] = (c.Rot[i].Item1, Flip(c.Rot[i].Item2, keep));
                for (int i = 0; i < c.Pos.Count; i++) c.Pos[i] = (c.Pos[i].Item1, Neg(c.Pos[i].Item2, keep));
                // SampleRot/SampleVec read these, NOT the lists above.
                for (int i = 0; i < c.QuatKeys.Length; i++) c.QuatKeys[i] = Flip(c.QuatKeys[i], keep);
                for (int i = 0; i < c.VecKeys.Length; i++) c.VecKeys[i] = Neg(c.VecKeys[i], keep);
            }
        }
    }

    private static Quaternion Flip(Quaternion q, int keep) => FlipN(q, Axis(keep));

    private static Vector3 Neg(Vector3 v, int keep) => NegN(v, Axis(keep));

    private static Vector3 Axis(int k) => k == 0 ? Vector3.UnitX : k == 1 ? Vector3.UnitY : Vector3.UnitZ;

    /// <summary>
    /// Reflect a rotation across the plane with unit normal n. Conjugating by the reflection
    /// M = I - 2nn^T gives a rotation of -theta about Mv, i.e. +theta about -Mv, so the vector
    /// part becomes 2(v.n)n - v and w is untouched. For n = X that reduces to (x,-y,-z,w) —
    /// the axis-aligned case that was tried first and is only right if the model's symmetry
    /// plane happens to BE a coordinate plane.
    /// </summary>
    private static Quaternion FlipN(Quaternion q, Vector3 n)
    {
        var v = new Vector3(q.X, q.Y, q.Z);
        var r = 2f * Vector3.Dot(v, n) * n - v;
        return new Quaternion(r.X, r.Y, r.Z, q.W);
    }

    /// <summary>Mirror a point across the same plane: p - 2(p.n)n.</summary>
    private static Vector3 NegN(Vector3 v, Vector3 n) => v - 2f * Vector3.Dot(v, n) * n;

    private static void ReflectN(GaniAnimation a, Vector3 n)
    {
        foreach (var t in a.Tracks)
            foreach (var c in t.Channels)
            {
                for (int i = 0; i < c.QuatKeys.Length; i++) c.QuatKeys[i] = FlipN(c.QuatKeys[i], n);
                for (int i = 0; i < c.VecKeys.Length; i++) c.VecKeys[i] = NegN(c.VecKeys[i], n);
            }
    }

    /// <summary>
    /// Search the sphere for the plane that actually mirrors this rig. The axis-aligned guesses
    /// only covered three of infinitely many normals; if the model's symmetry plane is tilted in
    /// the frame the orientations live in, none of them can work.
    /// </summary>
    public static (Vector3 normal, double err) SolveNormal(GaniAnimation source, GaniAnimation target,
                                                          List<(int, int)> pairs, int steps = 64)
    {
        var best = Vector3.UnitX; double bestErr = double.MaxValue;
        for (int i = 0; i <= steps; i++)
        {
            double theta = Math.PI * i / steps;                 // polar
            for (int j = 0; j < 2 * steps; j++)
            {
                double phi = 2 * Math.PI * j / (2 * steps);      // azimuth
                var n = new Vector3((float)(Math.Sin(theta) * Math.Cos(phi)),
                                    (float)(Math.Cos(theta)),
                                    (float)(Math.Sin(theta) * Math.Sin(phi)));
                if (n.LengthSquared() < 1e-6f) continue;
                n = Vector3.Normalize(n);
                var a = Clone(source);
                ReflectN(a, n);
                Swap(a, pairs);
                var (mean, _, _) = Compare(a, target);
                if (mean < bestErr) { bestErr = mean; best = n; }
            }
        }
        return (best, bestErr);
    }

    /// <summary>
    /// A per-unit constant rotation A with q_target ~= A * reflect(q_source).
    ///
    /// No single plane mirrors these values (an 8,192-normal sphere search lands back on X and
    /// is still worse than not mirroring), so the units are not all in one frame — each carries
    /// its own rest orientation. A absorbs that: fit it once from a clip pair, then it either
    /// generalises to the other pairs or the model is wrong. Averaging quaternions by summing
    /// with hemisphere alignment is exact enough for a near-constant offset.
    /// </summary>
    public static Dictionary<uint, Quaternion> SolveCorrection(GaniAnimation source, GaniAnimation target,
                                                              List<(int, int)> pairs, Vector3 n)
    {
        var a = Clone(source);
        ReflectN(a, n);
        Swap(a, pairs);

        var tgt = new Dictionary<uint, GaniTrack>();
        foreach (var t in target.Tracks) tgt.TryAdd(t.NameHash32, t);

        var outp = new Dictionary<uint, Quaternion>();
        foreach (var ta in a.Tracks)
        {
            if (!tgt.TryGetValue(ta.NameHash32, out var tb)) continue;
            Quaternion acc = default; int n2 = 0;
            for (int s = 0; s < 48; s++)
            {
                float u = s / 47f;
                for (int c = 0; c < ta.Channels.Count && c < tb.Channels.Count; c++)
                {
                    if (!ta.Channels[c].IsRot || !tb.Channels[c].IsRot) continue;
                    var qs = ta.Channels[c].SampleRot(u * a.FrameCount);
                    var qt = tb.Channels[c].SampleRot(u * target.FrameCount);
                    var d = qt * Quaternion.Conjugate(qs);
                    if (n2 > 0 && Quaternion.Dot(acc, d) < 0) d = -d;    // same hemisphere
                    acc = new Quaternion(acc.X + d.X, acc.Y + d.Y, acc.Z + d.Z, acc.W + d.W);
                    n2++;
                }
            }
            if (n2 > 0 && acc.LengthSquared() > 1e-8f) outp[ta.NameHash32] = Quaternion.Normalize(acc);
        }
        return outp;
    }

    /// <summary>Apply a fitted per-unit correction after reflect+swap.</summary>
    public static void ApplyCorrection(GaniAnimation a, Dictionary<uint, Quaternion> corr)
    {
        foreach (var t in a.Tracks)
        {
            if (!corr.TryGetValue(t.NameHash32, out var A)) continue;
            foreach (var c in t.Channels)
                for (int i = 0; i < c.QuatKeys.Length; i++) c.QuatKeys[i] = Quaternion.Normalize(A * c.QuatKeys[i]);
        }
    }

    /// <summary>Mean 3D error between two clips — the public form of the metric.</summary>
    public static double MeanError(GaniAnimation a, GaniAnimation b) => Compare(a, b).mean;

    /// <summary>Reflect + swap + apply a fitted correction, in place.</summary>
    public static void MirrorWith(GaniAnimation a, Vector3 n, List<(int, int)> pairs, Dictionary<uint, Quaternion> corr)
    {
        ReflectN(a, n);
        Swap(a, pairs);
        if (corr is not null) ApplyCorrection(a, corr);
    }

    /// <summary>Deep-copy just the sampled key arrays — enough for a reflect+compare pass.</summary>
    private static GaniAnimation Clone(GaniAnimation src)
    {
        var a = new GaniAnimation { FrameCount = src.FrameCount };
        foreach (var t in src.Tracks)
        {
            var nt = new GaniTrack { NameHash32 = t.NameHash32, IsStatic = t.IsStatic };
            foreach (var c in t.Channels)
                nt.Channels.Add(new GaniChannel
                {
                    IsRot = c.IsRot, IsStatic = c.IsStatic, IsHermite = c.IsHermite,
                    FrameScale = c.FrameScale, SegType = c.SegType, SegIndex = c.SegIndex,
                    QuatKeys = (Quaternion[])c.QuatKeys.Clone(),
                    VecKeys = (Vector3[])c.VecKeys.Clone(),
                    Durations = c.Durations, InvDur = c.InvDur,
                });
            a.Tracks.Add(nt);
        }
        return a;
    }

    /// <summary>Move each paired unit's data onto its twin. Track NAMES stay put — the slot is
    /// what identifies a unit, so the comparison still lines up unit for unit.</summary>
    private static void Swap(GaniAnimation a, List<(int, int)> pairs)
    {
        if (pairs is null) return;
        foreach (var (x, y) in pairs)
        {
            if (x < 0 || y < 0 || x >= a.Tracks.Count || y >= a.Tracks.Count || x == y) continue;
            var nx = a.Tracks[x].NameHash32;
            var ny = a.Tracks[y].NameHash32;
            (a.Tracks[x], a.Tracks[y]) = (a.Tracks[y], a.Tracks[x]);
            a.Tracks[x].NameHash32 = nx;
            a.Tracks[y].NameHash32 = ny;
        }
    }

    /// <summary>
    /// Mean and worst 3D distance, resampled on NORMALISED time so clips of different length
    /// line up. An orientation becomes three unit-length points, so a wrong roll shows up as a
    /// distance instead of hiding inside a quaternion. Sampling is GaniChannel's own.
    /// </summary>
    private static (double mean, double worst, int units) Compare(GaniAnimation a, GaniAnimation b)
        => Compare(a, b, null);

    private static (double mean, double worst, int units) Compare(GaniAnimation a, GaniAnimation b, Dictionary<uint, double> perUnit)
    {
        var byName = new Dictionary<uint, GaniTrack>();
        foreach (var t in b.Tracks) byName.TryAdd(t.NameHash32, t);

        double sum = 0, worst = 0; int n = 0, units = 0;
        const int Samples = 32;
        foreach (var ta in a.Tracks)
        {
            if (!byName.TryGetValue(ta.NameHash32, out var tb)) continue;
            units++;
            double unitSum = 0; int unitN = 0;
            for (int s = 0; s < Samples; s++)
            {
                float u = s / (float)(Samples - 1);
                for (int c = 0; c < ta.Channels.Count && c < tb.Channels.Count; c++)
                {
                    var ca = ta.Channels[c]; var cb = tb.Channels[c];
                    if (ca.IsRot != cb.IsRot) continue;
                    if (ca.IsRot)
                    {
                        var qa = ca.SampleRot(u * a.FrameCount);
                        var qb = cb.SampleRot(u * b.FrameCount);
                        foreach (var basis in Basis)
                        {
                            double d = (Vector3.Transform(basis, qa) - Vector3.Transform(basis, qb)).Length();
                            sum += d; n++; unitSum += d; unitN++; if (d > worst) worst = d;
                        }
                    }
                    else
                    {
                        double d = (ca.SampleVec(u * a.FrameCount) - cb.SampleVec(u * b.FrameCount)).Length();
                        sum += d; n++; unitSum += d; unitN++; if (d > worst) worst = d;
                    }
                }
            }
            if (perUnit is not null) perUnit[ta.NameHash32] = unitSum / Math.Max(1, unitN);
        }
        return (n == 0 ? 0 : sum / n, worst, units);
    }

    private static readonly Vector3[] Basis = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
}
