// Gani container assembly
// Ported verbatim from FoxBrowser.Models.Anim 04/08/2026 — do not re-derive, copy.
using System.Linq;
using System.Numerics;

namespace MgsvModBldr.Tools.Anim;

// Assembles decoded gani animations from the container pieces (CommonInfo layout +
// skeleton, per-gani track data). v2 (GANI2 / CommonInfo) is the TPP-retail target;
// v1 (old FoxData) is a follow-up. Uses GaniStructs + AnimBitReader for the actual
// keyframe decode.
public static class GaniFile
{
    // CommonInfo node name hashes (CommonInfoNodeType — fox_gani_enums.py).
    private const uint NodeLayoutTrack = 1337830127;
    private const uint NodeSkeletonList = 2447659851;

    // Parse a v2 CommonInfo (.trk) blob: a linked list of MtarMiniDataNode (16B:
    // name u32, dataSize u32, nextNodeOffset u32, padding u32) whose data follows
    // each node. Extracts the shared layout track (bone names + segment types) and
    // the skeleton name list.
    // start = where the CommonInfo chain begins (MtarFile2.commonInfoOffset). Taking an
    // offset instead of a slice keeps a 20 MB archive from being copied to read its header.
    public static (TrackHeader? header, List<TrackUnit> units, List<string> skel) ReadCommonInfoV2(byte[] trk, int start = 0)
    {
        TrackHeader? header = null;
        var units = new List<TrackUnit>();
        var skel = new List<string>();
        int nodePos = start;
        while (nodePos + 16 <= trk.Length)
        {
            var r = new LeReader(trk, nodePos);
            uint name = r.U32();
            int dataSize = (int)r.U32();
            int nextOff = (int)r.U32();
            r.U32();                       // padding
            int dataStart = nodePos + 16;
            if (name == NodeLayoutTrack) (header, units) = ReadLayout(trk, dataStart);
            else if (name == NodeSkeletonList) skel = ReadSkeleton(trk, dataStart, dataSize);
            // MotionPoints node ignored (facial/morph — follow-up).
            if (nextOff == 0) break;
            nodePos += nextOff;
        }
        return (header, units, skel);
    }

    // Layout = TrackHeader + per-unit (seek by unit offset). No data blobs — just the
    // bone-name hash + per-segment type for each track.
    private static (TrackHeader, List<TrackUnit>) ReadLayout(byte[] d, int start)
    {
        var r = new LeReader(d, start);
        var header = TrackHeader.Read(r);
        var units = new List<TrackUnit>();
        for (int i = 0; i < header.UnitCount; i++)
        {
            r.Pos = start + header.UnitOffsets[i];
            units.Add(TrackUnit.Read(r));
        }
        return (header, units);
    }

    private static List<string> ReadSkeleton(byte[] d, int start, int size)
    {
        int end = Math.Min(d.Length, start + Math.Max(0, size));
        if (end <= start) return new();
        var text = System.Text.Encoding.UTF8.GetString(d, start, end - start);
        var list = new List<string>();
        foreach (var s in text.Split('\0')) if (s.Length > 0) list.Add(s);
        return list;
    }

    // Decode one v2 per-gani blob into a GaniAnimation, using the shared CommonInfo
    // layout (bone names + segment types) and skeleton list. The blob is self-
    // contained: all offsets are relative to its start (== file_header.tracks_offset).
    public static GaniAnimation DecodeV2Gani(byte[] gani, TrackHeader layoutHeader, List<TrackUnit> layoutUnits, List<string> skel)
    {
        int trackCount = layoutHeader.UnitCount;
        int segTotal = layoutHeader.SegmentCount;
        var r = new LeReader(gani, 0);
        var mini = TrackMiniHeader.Read(r, trackCount, segTotal);
        int gani2Base = LeReader.Align(TrackMiniHeader.BaseSize + mini.ParamCount * 8 + trackCount, 4);

        var anim = new GaniAnimation { FrameCount = mini.FrameCount, SkeletonNames = skel };
        float scale = layoutHeader.FrameScaleByte;                 // cvtepi32 of the header byte
        int abs = 0;
        for (int ti = 0; ti < trackCount && ti < layoutUnits.Count; ti++)
        {
            var unit = layoutUnits[ti];
            int flags = ti < mini.UnitFlags.Count ? mini.UnitFlags[ti] : 0;
            bool isStatic = (flags & 0x4) != 0;
            bool hermite = ((flags | unit.UnitFlags) & 0x2) != 0;  // HERMITE: layout unit flag (runtime reads TrackUnit+5)
            var track = new GaniTrack { NameHash32 = unit.Name, IsStatic = isStatic };
            for (int si = 0; si < unit.SegmentCount; si++, abs++)
            {
                if (abs >= mini.SegmentHeaders.Count) break;
                var seg = unit.Segments[si];
                var hdr = mini.SegmentHeaders[abs];
                // Offset 0 = this clip does not animate the segment. The shared layout declares
                // every segment any clip in the archive uses, so most clips leave some empty
                // (92 across 76 clips in stock player2_resident). It is self-relative to its own
                // table entry, so 0 resolves back INTO the table — decoding that yields
                // plausible-looking garbage keys rather than an error.
                if (hdr.DataOffset == 0) { track.SegTypes.Add((seg.TdType, false)); continue; }
                int blobOff = gani2Base + abs * Gani2TrackData.EntrySize + hdr.DataOffset;
                if (blobOff < 0 || blobOff >= gani.Length) { track.SegTypes.Add((seg.TdType, false)); continue; }
                bool ok = false;
                try
                {
                    var keys = AnimBitReader.DecodeSegment(gani, blobOff, seg.TdType, hdr.ComponentBitSize, isStatic, hermite, mini.FrameCount);
                    ok = Accumulate(track, keys, isStatic, scale, abs);   // FLAT index — frig seg shorts address this
                }
                catch (IndexOutOfRangeException) { /* truncated/garbage segment — skip, harness flags it */ }
                track.SegTypes.Add((seg.TdType, ok));
            }
            anim.Tracks.Add(track);
        }
        return anim;
    }

    // Build a GaniChannel from a decoded segment: absolute frames for enumeration, plus
    // the exact per-segment durations (float(delta)*scale) and reciprocals the game's
    // evaluator precomputes (SetNextQuatDataCore / InitVectorLinear*). QUAT→Rot,
    // VEC3→Pos; each segment is ALSO kept as its own Channel (un-merged) so multi-channel
    // tracks (arm shoulder/effector/pole; 16-key finger tracks) read per sub-channel.
    private static bool Accumulate(GaniTrack track, GaniSegKeys keys, bool isStatic, float scale, int segIndex = -1)
    {
        var type = keys.Type;
        bool isRot = type is GaniSegmentType.Quat or GaniSegmentType.QuatDiff;
        bool isVec = type is GaniSegmentType.Vector3 or GaniSegmentType.VectorDiff;
        if (!isRot && !isVec) return false;   // FLOAT / VECTOR2 / VECTOR4 are aux/shader channels — not bone transforms.

        int n = keys.Count;
        var ch = new GaniChannel
        {
            IsRot = isRot,
            SegIndex = segIndex,
            SegType = type,
            IsStatic = isStatic,
            IsHermite = keys.IsHermite,
            FrameScale = scale,
        };
        if (n > 1)
        {
            ch.Durations = new float[n - 1];
            ch.InvDur = new float[n - 1];
            for (int i = 1; i < n; i++)
            {
                float d = keys.Deltas[i] * scale;      // cvtepi32(delta) * scale, exact game order
                ch.Durations[i - 1] = d;
                ch.InvDur[i - 1] = 1f / d;
            }
        }

        int acc = 0;
        if (isRot)
        {
            ch.QuatKeys = keys.Quats;
            for (int i = 0; i < n; i++) { acc += keys.Deltas[i]; track.Rot.Add((acc, keys.Quats[i])); ch.Rot.Add((acc, keys.Quats[i])); }
        }
        else
        {
            ch.VecKeys = keys.Vecs;
            ch.TanKeys = keys.Tans;
            for (int i = 0; i < n; i++) { acc += keys.Deltas[i]; track.Pos.Add((acc, keys.Vecs[i])); ch.Pos.Add((acc, keys.Vecs[i])); }
        }
        track.Channels.Add(ch);
        return true;
    }

    // ── v1 (old FoxData) gani decode ─────────────────────────────────────────────
    // Old-format mtars (GZ/legacy; ~a third of the master) embed each gani as a FoxData
    // node tree (ROOT→MOTION→UNIT…) instead of v2's shared CommonInfo + flat data. The
    // UNIT node's payload is the SAME TrackHeader + TrackUnit layout we already parse,
    // but with the keyframe blobs INLINE (each TrackData.DataOffset is self-relative). So
    // we reuse TrackHeader/TrackUnit/AnimBitReader + Accumulate, and only add the FoxData
    // node walk. Bone names are the same StrCode32 track hashes, so binding is identical
    // to v2 (direct match or via the rig). Port of Rollins fwrap_gani1_reader.py.
    private const uint FoxRoot = 3933341002, FoxMotion = 143688520, FoxUnit = 3337172921, FoxSklList = 2447659851;

    // FoxDataNode (48B): name(0) nameStr(4) flags(8) dataOff(12,s) dataSize(16)
    //   parent(20,s) child(24,s) prev(28,s) next(32,s) params(36,s) +8 pad. Offsets are
    //   signed and relative to the node's own position.
    private static (uint name, int dataOff, int child, int next) FoxNode(byte[] d, int pos) =>
        (BitConverter.ToUInt32(d, pos), BitConverter.ToInt32(d, pos + 12),
         BitConverter.ToInt32(d, pos + 24), BitConverter.ToInt32(d, pos + 32));

    // Walk a node's sibling chain (first child at parentPos+childOff) for a name hash.
    private static int FoxFindChild(byte[] d, int parentPos, int childOff, uint target)
    {
        if (childOff == 0) return -1;
        int pos = parentPos + childOff;
        for (int guard = 0; guard < 8192; guard++)
        {
            if (pos < 0 || pos + 48 > d.Length) return -1;
            var n = FoxNode(d, pos);
            if (n.name == target) return pos;
            if (n.next == 0) return -1;
            pos += n.next;
        }
        return -1;
    }

    // Locate the UNIT track layout inside an old-format gani: TrackHeader + TrackUnits +
    // the absolute payload start (DataOffsets are relative to it) + optional SKL_LIST bone
    // names. header==null ⇒ this gani has no bone animation (camera/demo-only).
    private static (TrackHeader? header, List<TrackUnit> units, int payload, List<string> skel) ReadV1Layout(byte[] file, int ganiStart)
    {
        var none = ((TrackHeader?)null, new List<TrackUnit>(), 0, new List<string>());
        if (ganiStart < 0 || ganiStart + 32 > file.Length) return none;
        int nodesStart = ganiStart + (int)BitConverter.ToUInt32(file, ganiStart + 4);   // FoxDataHeader.nodes_offset
        if (nodesStart < 0 || nodesStart + 48 > file.Length) return none;
        var root = FoxNode(file, nodesStart);
        if (root.name != FoxRoot) return none;
        int motionPos = FoxFindChild(file, nodesStart, root.child, FoxMotion);
        if (motionPos < 0) return none;
        var motion = FoxNode(file, motionPos);
        var skel = new List<string>();
        int sklPos = FoxFindChild(file, motionPos, motion.child, FoxSklList);
        if (sklPos >= 0) skel = ReadStringData(file, sklPos);
        int unitPos = FoxFindChild(file, motionPos, motion.child, FoxUnit);
        if (unitPos < 0) return ((TrackHeader?)null, new List<TrackUnit>(), 0, skel);
        var unit = FoxNode(file, unitPos);
        int payload = unitPos + unit.dataOff;
        if (payload < 0 || payload + TrackHeader.BaseSize > file.Length) return ((TrackHeader?)null, new List<TrackUnit>(), 0, skel);
        var header = TrackHeader.Read(new LeReader(file, payload));
        var units = new List<TrackUnit>();
        for (int i = 0; i < header.UnitCount; i++)
        {
            int uoff = payload + header.UnitOffsets[i];
            if (uoff < 0 || uoff + TrackUnit.BaseSize > file.Length) break;
            units.Add(TrackUnit.Read(new LeReader(file, uoff)));
        }
        return (header, units, payload, skel);
    }

    // FoxData StringData payload: uint count, then count × { uint hash, uint stringOffset }
    // (inline null-terminated name at entry+stringOffset when non-zero, else hex hash).
    private static List<string> ReadStringData(byte[] d, int nodePos)
    {
        var list = new List<string>();
        var node = FoxNode(d, nodePos);
        if (node.dataOff == 0) return list;
        int payload = nodePos + node.dataOff;
        if (payload < 0 || payload + 4 > d.Length) return list;
        int count = (int)BitConverter.ToUInt32(d, payload);
        for (int i = 0; i < count; i++)
        {
            int es = payload + 4 + i * 8;
            if (es + 8 > d.Length) break;
            uint hash = BitConverter.ToUInt32(d, es);
            int strOff = (int)BitConverter.ToUInt32(d, es + 4);
            if (strOff != 0 && es + strOff < d.Length)
            {
                int sp = es + strOff, end = sp;
                while (end < d.Length && d[end] != 0) end++;
                list.Add(System.Text.Encoding.UTF8.GetString(d, sp, end - sp));
            }
            else list.Add(hash.ToString("x8"));
        }
        return list;
    }

    // Decode one old-format gani (FoxData at ganiStart within the full mtar bytes).
    public static GaniAnimation DecodeV1Gani(byte[] file, int ganiStart)
    {
        var (header, units, payload, skel) = ReadV1Layout(file, ganiStart);
        var anim = new GaniAnimation { FrameCount = header?.FrameCount ?? 0, SkeletonNames = skel };
        if (header is null) return anim;
        float scale = header.FrameScaleByte;
        int abs = 0;                                            // FLAT segment counter, as v2
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            int dataStart = payload + header.UnitOffsets[i] + TrackUnit.BaseSize;
            bool hermite = (u.UnitFlags & 0x2) != 0;
            var track = new GaniTrack { NameHash32 = u.Name, IsStatic = u.IsStatic };
            for (int si = 0; si < u.Segments.Count; si++, abs++)
            {
                var seg = u.Segments[si];
                if (seg.DataOffset == 0) { track.SegTypes.Add((seg.TdType, false)); continue; }
                int blob = dataStart + si * TrackData.EntrySize + seg.DataOffset;
                if (blob < 0 || blob >= file.Length) { track.SegTypes.Add((seg.TdType, false)); continue; }
                bool ok = false;
                try
                {
                    var keys = AnimBitReader.DecodeSegment(file, blob, seg.TdType, seg.ComponentBitSize, u.IsStatic, hermite, header.FrameCount);
                    ok = Accumulate(track, keys, u.IsStatic, scale, abs);
                }
                catch (IndexOutOfRangeException) { /* truncated/garbage segment — skip */ }
                catch (ArgumentOutOfRangeException) { }
                track.SegTypes.Add((seg.TdType, ok));
            }
            anim.Tracks.Add(track);
        }
        return anim;
    }

    // Cheap compat probe: the bone-track name hashes of the FIRST old-format gani (all
    // ganis in an mtar share the skeleton), without decoding any keyframes.
    public static uint[] ProbeV1BoneHashes(byte[] mtar)
    {
        if (mtar.Length < 48) return Array.Empty<uint>();
        if (BitConverter.ToUInt32(mtar, 4) == 0) return Array.Empty<uint>();   // file_count
        int tracksOffset = (int)BitConverter.ToUInt32(mtar, 32 + 8);           // first old table entry's tracks_offset
        var (header, units, _, _) = ReadV1Layout(mtar, tracksOffset);
        return header is null ? Array.Empty<uint>() : units.Select(u => u.Name).ToArray();
    }
}
