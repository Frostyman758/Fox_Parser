// Merge several v1 layouts into one shared v2 track layout
// 04/08/2026
using System;
using System.Collections.Generic;
using System.Text;
using MgsvModBldr.Tools.Mtar.Mtar;
using MgsvModBldr.Tools.Mtar.Utility;

namespace MgsvModBldr.Tools.Mtar.Transcode
{
    /// <summary>
    /// A v2 mtar keeps ONE track layout for every clip in it, but a v1 archive lets each clip
    /// carry its own — so archives like TppRaven_layers hold the same 27 units with a differing
    /// number of position segments and cannot share a layout as authored.
    ///
    /// v2 can still express them: a gani writes offset 0 for a segment it does not animate. So
    /// the shared layout is the UNION of every clip's segments, and each clip fills the slots it
    /// has. Unit NAMES must still agree — a different unit list is a different rig.
    /// </summary>
    public sealed class UnionLayout
    {
        public sealed class Seg
        {
            public int Type;
            public byte ComponentBitSize;      // from the first clip that animates this slot
            public int DataOffset;             // carried as authored; never dereferenced
        }

        public sealed class Unit
        {
            public uint Name;
            public byte Flags;
            public ushort Pad;
            public List<Seg> Segments = new List<Seg>();
        }

        public List<Unit> Units = new List<Unit>();

        public int SegmentCount
        {
            get { int n = 0; foreach (var u in Units) n += u.Segments.Count; return n; }
        }

        /// <summary>True when the union added nothing to this layout, so it can ship as authored.</summary>
        public bool Matches(MtarTrackInfo t)
        {
            if (t is null || t.units.Count != Units.Count) return false;
            for (int i = 0; i < Units.Count; i++)
            {
                if (t.units[i].segments.Count != Units[i].Segments.Count) return false;
                for (int s = 0; s < Units[i].Segments.Count; s++)
                    if ((t.units[i].segments[s].packed & 0x0F) != Units[i].Segments[s].Type) return false;
            }
            return true;
        }

        /// <summary>Is every type in <paramref name="a"/> found in order within <paramref name="b"/>?</summary>
        private static bool IsSubsequence(List<Seg> a, List<V1Segment> b)
        {
            int j = 0;
            foreach (var s in a)
            {
                while (j < b.Count && b[j].Type != s.Type) j++;
                if (j >= b.Count) return false;
                j++;
            }
            return true;
        }

        /// <summary>Unit names alone — what every clip in one archive must agree on.</summary>
        public static string UnitSignature(V1Gani g)
        {
            var sb = new StringBuilder();
            foreach (var u in g.Units) sb.Append(u.Name.ToString("x8")).Append(';');
            return sb.ToString();
        }

        /// <summary>
        /// Fold a clip in. Segments align by TYPE, in order: walk the union from the current
        /// slot, take the first matching type, and insert one where none is found. Two segments
        /// of the same type in a unit stay distinct because the cursor advances past each match.
        /// </summary>
        public void Merge(V1Gani g)
        {
            // UNITS merge by name, the same subsequence walk the segments use: a clip need not
            // drive every unit (facial clips drive as few as 8 of 38), and v2 says so with a 0
            // unit offset — MtarTrackUnit.absent.
            int ucur = 0;
            foreach (var su in g.Units)
            {
                int at = -1;
                for (int k = ucur; k < Units.Count; k++)
                    if (Units[k].Name == su.Name) { at = k; break; }
                if (at < 0)
                {
                    at = ucur;
                    Units.Insert(at, new Unit { Name = su.Name, Flags = (byte)su.Flags });
                }
                MergeSegments(Units[at], su);
                ucur = at + 1;
            }
        }

        private static void MergeSegments(Unit dst, V1Unit src)
        {
            // Splicing a clip into a shorter union invents slots when clips ORDER their segments
            // differently: union [Quat] + clip [Vec,Quat] -> [Vec,Quat], then clip [Quat,Vec]
            // appends a second Vec. When the union so far is a subsequence of a RICHER clip,
            // adopt that clip's order instead — it is the better authority for this unit.
            if (src.Segments.Count > dst.Segments.Count && IsSubsequence(dst.Segments, src.Segments))
            {
                var carried = dst.Segments;
                dst.Segments = new List<Seg>();
                foreach (var s in src.Segments)
                    dst.Segments.Add(new Seg { Type = s.Type, ComponentBitSize = s.HasData ? (byte)s.ComponentBitSize : (byte)0 });
                // Keep bit sizes already learned from earlier clips.
                int c = 0;
                foreach (var old in carried)
                    for (int k = c; k < dst.Segments.Count; k++)
                        if (dst.Segments[k].Type == old.Type)
                        {
                            if (dst.Segments[k].ComponentBitSize == 0) dst.Segments[k].ComponentBitSize = old.ComponentBitSize;
                            c = k + 1; break;
                        }
                return;
            }

            int cursor = 0;
            foreach (var s in src.Segments)
            {
                int at = -1;
                for (int k = cursor; k < dst.Segments.Count; k++)
                    if (dst.Segments[k].Type == s.Type) { at = k; break; }
                if (at < 0)
                {
                    at = cursor;
                    dst.Segments.Insert(at, new Seg { Type = s.Type });
                }
                var slot = dst.Segments[at];
                if (slot.ComponentBitSize == 0 && s.HasData)
                {
                    slot.ComponentBitSize = (byte)s.ComponentBitSize;
                    slot.DataOffset = 0;
                }
                cursor = at + 1;
            }
        }

        /// <summary>
        /// For each shared slot, the clip's segment that fills it — or null. Same walk as
        /// <see cref="Merge"/>, so a clip always lands where its data was merged.
        /// </summary>
        public V1Segment[] SlotMap(V1Gani g)
        {
            var map = new V1Segment[SegmentCount];
            var byUnit = UnitMap(g);
            int slot = 0;
            for (int i = 0; i < Units.Count; i++)
            {
                var dst = Units[i];
                var src = byUnit[i]?.Segments ?? new List<V1Segment>();
                int cursor = 0;
                foreach (var s in src)
                {
                    int at = -1;
                    for (int k = cursor; k < dst.Segments.Count; k++)
                        if (dst.Segments[k].Type == s.Type) { at = k; break; }
                    if (at < 0) continue;                  // cannot happen after Merge
                    map[slot + at] = s;
                    cursor = at + 1;
                }
                slot += dst.Segments.Count;
            }
            return map;
        }

        /// <summary>Per shared unit, the clip's unit filling it — or null when it drives none.
        /// Walked the same way as Merge so a clip lands where its data was merged.</summary>
        public V1Unit[] UnitMap(V1Gani g)
        {
            var map = new V1Unit[Units.Count];
            int cursor = 0;
            foreach (var su in g.Units)
            {
                for (int k = cursor; k < Units.Count; k++)
                    if (Units[k].Name == su.Name) { map[k] = su; cursor = k + 1; break; }
            }
            return map;
        }

        /// <summary>
        /// The shared layout as the .trk's typed model, built on <paramref name="authored"/> —
        /// one source clip's own layout, which supplies the header scalars and, when the union
        /// added nothing, the authored unit offsets and segment ids verbatim.
        ///
        /// When slots WERE added, everything is re-laid: SegmentId is the runtime's index into
        /// the TrackControl table — global and sequential across units, measured on Konami's own
        /// files — so an inserted slot renumbers all of them. `packed` bit 7 means "another
        /// record follows in this unit", so only the last one in each unit clears it.
        /// </summary>
        public MtarTrackInfo ToTrackInfo(MtarTrackInfo authored)
        {
            if (Matches(authored)) return authored;

            var t = new MtarTrackInfo
            {
                segmentCount = (uint)SegmentCount,
                trackId = authored.trackId,
                unknownA = authored.unknownA,
                unknownB = authored.unknownB,
                frameCount = authored.frameCount,
                frameRate = authored.frameRate,
                headerTail = authored.headerTail,
            };

            // Units follow the offset array; each is an 8-byte head plus its segment records.
            int at = 0x14 + Units.Count * 4;
            short id = 0;
            foreach (var u in Units)
            {
                var mu = new MtarTrackUnit
                {
                    name = StrCode32Names.Text(u.Name),
                    flags = u.Flags,   // IS_STATIC etc, as the source authored it
                    pad = u.Pad,
                    offset = at,
                };
                for (int s = 0; s < u.Segments.Count; s++)
                {
                    var seg = u.Segments[s];
                    bool more = s + 1 < u.Segments.Count;
                    mu.segments.Add(new MtarTrackSegment
                    {
                        dataOffset = seg.DataOffset,
                        segmentId = id++,
                        packed = (byte)((seg.Type & 0x0F) | (more ? 0x80 : 0)),
                        componentBitSize = seg.ComponentBitSize,
                    });
                }
                t.units.Add(mu);
                at += 8 + u.Segments.Count * 8;
            }
            return t;
        }
    }
}
