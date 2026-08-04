// Read a v1 (FoxData) gani: track layout + keyframe blobs
using System;
using System.Collections.Generic;
using System.Text;

namespace MgsvModBldr.Tools.Mtar.Transcode
{
    /// <summary>One segment of one track: its type, component bit size and blob bytes.</summary>
    public sealed class V1Segment
    {
        public int UnitIndex;
        public int SegmentIndex;
        public int Type;              // low 4 bits of the packed type byte
        public int ComponentBitSize;
        public byte[] Blob = Array.Empty<byte>();   // empty when the segment has no data
        public bool HasData => Blob.Length > 0;

        /// <summary>Absolute blob start while reading; -1 when the segment carries no data.</summary>
        internal int BlobStart = -1;
    }

    /// <summary>One track (bone): name hash, flags, segments.</summary>
    public sealed class V1Unit
    {
        public uint Name;
        public int Flags;
        public List<V1Segment> Segments = new List<V1Segment>();
    }

    /// <summary>A decoded v1 gani — everything a v2 body needs.</summary>
    public sealed class V1Gani
    {
        public int FrameCount;
        public int FrameScaleByte;
        public int SegmentCount;
        public List<V1Unit> Units = new List<V1Unit>();

        /// <summary>
        /// The gani's event list, verbatim — magic 0x0BFE2CF6, a count, then offsets.
        /// This is byte-compatible with a v2 ".enchnk", and it is NOT optional: it
        /// carries the MTEV_AG_SYNC_L/R foot-plant events that
        /// ImplAnimGraphFootFitEventCacheData::BuildNewTable turns into the foot-phase
        /// table. Drop it and the motion graph cannot resolve which foot is forward, so
        /// locomotion states never transition out — the animation locks movement.
        /// </summary>
        public byte[] Events = Array.Empty<byte>();

        /// <summary>Motion-point (root trajectory) tracks — the v2 .exchnk payload.</summary>
        public byte[] MotionPoints = Array.Empty<byte>();

        /// <summary>Each motion-point unit this clip animates, paired with the bone it hangs off.
        /// A destination archive must declare every one of these or the point has no parent.</summary>
        public List<(uint Mtp, uint Bone)> MotionPointParents = new List<(uint, uint)>();

        /// <summary>Units/segments flattened in file order — v2 stores them this way.</summary>
        public IEnumerable<V1Segment> Flat()
        {
            foreach (var u in Units)
                foreach (var s in u.Segments)
                    yield return s;
        }

        /// <summary>
        /// Structural signature: unit names + per-unit segment types. A v2 mtar keeps ONE
        /// shared track layout for all its ganis, so every gani transcoded into the same
        /// mtar must agree on this. Component bit sizes are deliberately excluded — those
        /// live per-gani in the v2 mini-header and are free to vary.
        /// </summary>
        public string Signature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var u in Units)
            {
                sb.Append(u.Name.ToString("x8")).Append(':');
                foreach (var s in u.Segments) sb.Append(s.Type).Append(',');
                sb.Append(';');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Parses the FoxData node tree a v1 gani is wrapped in (ROOT → MOTION → UNIT) and
    /// pulls out the track layout plus each segment's keyframe blob.
    ///
    /// The blobs are NOT re-encoded anywhere downstream: v1 and v2 use the same keyframe
    /// encoding (verified on matched GZ/TPP pairs — same segment types, same component bit
    /// sizes, same blob lengths), so a transcode copies them byte for byte.
    /// </summary>
    public static class GaniV1
    {
        private const uint FoxRoot = 3933341002, FoxMotion = 143688520, FoxUnit = 3337172921;

        /// <summary>The MOTION child holding the event list (a v2 .enchnk payload).</summary>
        private const uint FoxEvents = 0x1622762d;

        /// <summary>The MOTION child holding the motion-point tracks (a v2 .exchnk payload).</summary>
        private const uint FoxMotionPoints = 0x1d75f6f3;

        /// <summary>Parent records: 8-byte pairs indexed by motion-point unit index; the second
        /// word is the bone. AnimFile::GetMotionPointParent finds the unit index in 0x1d75f6f3,
        /// then returns pairs[index*2 + 1] from here.</summary>
        private const uint FoxMotionPointParents = 0xf0f377d9;


        /// <summary>Event-list magic, shared by the v1 node payload and the v2 .enchnk.</summary>
        private const uint EventMagic = 0x0BFE2CF6;

        // FoxDataNode (48B): name(0) nameStr(4) flags(8) dataOff(12,s) dataSize(16)
        //   parent(20,s) child(24,s) prev(28,s) next(32,s) params(36,s). Signed, self-relative.
        private static (uint name, int dataOff, int child, int next) Node(byte[] d, int p) =>
            (BitConverter.ToUInt32(d, p), BitConverter.ToInt32(d, p + 12),
             BitConverter.ToInt32(d, p + 24), BitConverter.ToInt32(d, p + 32));

        private static int FindChild(byte[] d, int parent, int childOff, uint target)
        {
            if (childOff == 0) return -1;
            int p = parent + childOff;
            for (int guard = 0; guard < 8192; guard++)
            {
                if (p < 0 || p + 48 > d.Length) return -1;
                var n = Node(d, p);
                if (n.name == target) return p;
                if (n.next == 0) return -1;
                p += n.next;
            }
            return -1;
        }

        /// <summary>
        /// Read the gani whose FoxData starts at <paramref name="start"/> within
        /// <paramref name="file"/>. <paramref name="length"/> bounds the last blob.
        /// Returns null when the node tree isn't a bone animation (camera/demo-only).
        /// </summary>
        public static V1Gani Read(byte[] file, int start, int length)
        {
            if (start < 0 || start + 32 > file.Length) return null;
            int nodes = start + (int)BitConverter.ToUInt32(file, start + 4);
            if (nodes < 0 || nodes + 48 > file.Length) return null;

            var root = Node(file, nodes);
            if (root.name != FoxRoot) return null;
            int motion = FindChild(file, nodes, root.child, FoxMotion);
            if (motion < 0) return null;
            int motionChild = Node(file, motion).child;
            int unit = FindChild(file, motion, motionChild, FoxUnit);
            if (unit < 0) return null;

            int pay = unit + Node(file, unit).dataOff;
            if (pay < 0 || pay + 20 > file.Length) return null;

            int unitCount = (int)BitConverter.ToUInt32(file, pay);
            int segCount = (int)BitConverter.ToUInt32(file, pay + 4);
            if (unitCount <= 0 || unitCount > 4096) return null;

            // The event list runs from its node's data to the end of the gani; the node's
            // own size field reads 0, so the entry's length is what bounds it.
            int evNode = FindChild(file, motion, motionChild, FoxEvents);
            byte[] events = Array.Empty<byte>();
            if (evNode >= 0)
            {
                int evAt = evNode + Node(file, evNode).dataOff;
                int evEnd = length > 0 ? Math.Min(start + length, file.Length) : file.Length;
                if (evAt > 0 && evAt + 4 <= evEnd && BitConverter.ToUInt32(file, evAt) == EventMagic)
                {
                    events = new byte[evEnd - evAt];
                    Buffer.BlockCopy(file, evAt, events, 0, events.Length);
                }
            }

            // Motion points carry the root trajectory. The v1 node payload and the v2
            // .exchnk are the same bytes: TrackHeader, per-unit offsets, then units keyed by
            // StrCode32 of an MTP_* name. Its dataSize is valid (unlike the event node's).
            byte[] motionPoints = Array.Empty<byte>();
            int mpNode = FindChild(file, motion, motionChild, FoxMotionPoints);
            if (mpNode >= 0)
            {
                var mn = Node(file, mpNode);
                int mpAt = mpNode + mn.dataOff;
                int mpSize = (int)BitConverter.ToUInt32(file, mpNode + 16);
                if (mpAt > 0 && mpSize > 0 && mpAt + mpSize <= file.Length)
                {
                    motionPoints = new byte[mpSize];
                    Buffer.BlockCopy(file, mpAt, motionPoints, 0, mpSize);
                }
            }

            // Bind each motion-point unit to its parent bone, exactly as the engine does.
            var mpParents = new List<(uint, uint)>();
            if (motionPoints.Length >= 0x14)
            {
                int pn = FindChild(file, motion, motionChild, FoxMotionPointParents);
                int pAt = pn >= 0 ? pn + Node(file, pn).dataOff : -1;
                int units = BitConverter.ToInt32(motionPoints, 0);
                for (int i = 0; i < units && pAt > 0; i++)
                {
                    int slot = 0x14 + i * 4;
                    if (slot + 4 > motionPoints.Length) break;
                    uint off = BitConverter.ToUInt32(motionPoints, slot);
                    if (off == 0 || off + 4 > motionPoints.Length) continue;
                    uint mtp = BitConverter.ToUInt32(motionPoints, (int)off);
                    int pr = pAt + i * 8 + 4;
                    if (pr + 4 > file.Length) break;
                    mpParents.Add((mtp, BitConverter.ToUInt32(file, pr)));
                }
            }
            var g = new V1Gani
            {
                Events = events,
                MotionPoints = motionPoints,
                MotionPointParents = mpParents,
                SegmentCount = segCount,
                FrameCount = (int)BitConverter.ToUInt32(file, pay + 12),
                FrameScaleByte = (sbyte)(BitConverter.ToUInt32(file, pay + 16) & 0xFF),
            };

            var offsets = new int[unitCount];
            for (int i = 0; i < unitCount; i++)
            {
                int at = pay + 20 + i * 4;
                if (at + 4 > file.Length) return null;
                offsets[i] = (int)BitConverter.ToUInt32(file, at);
            }

            // Pass 1: layout + each blob's start. Sizes come after, from the next start.
            var starts = new List<int>();
            for (int i = 0; i < unitCount; i++)
            {
                int up = pay + offsets[i];
                if (up < 0 || up + 8 > file.Length) return null;
                var u = new V1Unit { Name = BitConverter.ToUInt32(file, up), Flags = file[up + 5] };
                int n = file[up + 4];
                for (int s = 0; s < n; s++)
                {
                    int e = up + 8 + s * 8;
                    if (e + 8 > file.Length) return null;
                    int dataOff = BitConverter.ToInt32(file, e);
                    var seg = new V1Segment
                    {
                        UnitIndex = i,
                        SegmentIndex = s,
                        Type = file[e + 6] & 0x0F,
                        ComponentBitSize = file[e + 7],
                    };
                    if (dataOff != 0)
                    {
                        int blob = e + dataOff;
                        if (blob > 0 && blob < file.Length) { seg.BlobStart = blob; starts.Add(blob); }
                    }
                    u.Segments.Add(seg);
                }
                g.Units.Add(u);
            }

            // Pass 2: a blob runs to the next blob start (or the gani's end).
            starts.Sort();
            int end = Math.Min(file.Length, length > 0 ? start + length : file.Length);
            foreach (var seg in g.Flat())
            {
                if (seg.BlobStart < 0) { seg.Blob = Array.Empty<byte>(); continue; }
                int b = seg.BlobStart;
                int idx = starts.BinarySearch(b);
                int next = (idx >= 0 && idx + 1 < starts.Count) ? starts[idx + 1] : end;
                if (next <= b || next > file.Length) { seg.Blob = Array.Empty<byte>(); continue; }
                seg.Blob = new byte[next - b];
                Buffer.BlockCopy(file, b, seg.Blob, 0, next - b);
            }
            return g;
        }
    }
}
