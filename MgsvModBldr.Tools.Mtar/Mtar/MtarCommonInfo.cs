// typed models for the three CommonInfo node payloads
using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using MgsvModBldr.Tools.Mtar.Utility;

namespace MgsvModBldr.Tools.Mtar.Mtar
{
    /// <summary>
    /// CommonInfo node names. The chain runs from MtarHeader.CommonInfoOffset (file start) as
    ///   0x00 u32 Name | 0x04 u32 DataSize | 0x08 u32 NextNodeOffset (self-rel, 0 = last) | 0x0C 0
    /// and AnimFile::GetSkeletonList2 walks it looking for one of these.
    /// </summary>
    public static class MtarNode
    {
        public const uint TrackInfo = 0x4fbdaaef;
        public const uint MotionPointUnits = 0x3b9a7784;
        public const uint SkeletonList = 0x91e4534b;
    }

    /// <summary>
    /// One segment of a track unit — fox::anim::TrackData.
    /// The runtime finds a segment's keyframes by <see cref="SegmentId"/>, indexing the
    /// TrackControl's own table (`*(uint*)(this + (SegmentId - SegmentsStartIndex)*8 + 0x14)`).
    /// <see cref="DataOffset"/> is never dereferenced: all three walkers — ChangeTimeFast,
    /// GetTrackControlSize and the TrackControl ctor — only use `&amp;record->DataOffset` as the
    /// record's own address when stepping. It is carried as authored.
    /// </summary>
    [XmlType("Segment", Namespace = "Mtar")]
    public class MtarTrackSegment
    {
        [XmlAttribute("DataOffset")] public int dataOffset;
        [XmlAttribute("SegmentId")] public short segmentId;
        /// <summary>Low nibble = segment type; bit 7 = another record follows, 8 bytes on.</summary>
        [XmlAttribute("Packed")] public byte packed;
        [XmlAttribute("ComponentBitSize")] public byte componentBitSize;
    }

    /// <summary>fox::anim::TrackUnit — one animated track (a bone or a motion point).</summary>
    [XmlType("Unit", Namespace = "Mtar")]
    public class MtarTrackUnit
    {
        /// <summary>StrCode32. Empty means the unit offset was 0 — an absent slot.</summary>
        [XmlAttribute("Name")] public string name;
        /// <summary>The bones this rig unit drives, from the built-in rig tables. A unit name is
        /// a hash that reverses against no dictionary, so this is its only readable label.</summary>
        [XmlAttribute("Bones")] public string bones;
        [XmlIgnore] public bool bonesSpecified;
        /// <summary>fox::anim::TrackUnitFlags — LOOP=1, HERMITE_VECTOR_INTERPOLATION=2, IS_STATIC=4.</summary>
        [XmlAttribute("Flags")] public byte flags;
        [XmlAttribute("Pad")] public ushort pad;
        /// <summary>Authored byte offset of this unit from the payload start. Kept rather than
        /// recomputed: Konami leaves a run of zeros between the offset array and the first unit,
        /// and the engine only ever reaches a unit through this offset.</summary>
        [XmlAttribute("Offset")] public int offset;
        [XmlAttribute("Absent")] public bool absent;
        [XmlIgnore] public bool absentSpecified;
        [XmlArray("Segments")] public List<MtarTrackSegment> segments = new List<MtarTrackSegment>();
    }

    /// <summary>
    /// Node 0x4fbdaaef — the track layout every clip in the archive shares.
    /// Payload is a TrackHeader, then one u32 offset per unit (self-relative to the payload start),
    /// then the units with their segment records.
    /// </summary>
    [XmlType("TrackInfo", Namespace = "Mtar")]
    public class MtarTrackInfo
    {
        [XmlAttribute("SegmentCount")] public uint segmentCount;
        [XmlAttribute("TrackId")] public ushort trackId;
        [XmlAttribute("UnknownA")] public byte unknownA;
        [XmlAttribute("UnknownB")] public byte unknownB;
        [XmlAttribute("FrameCount")] public int frameCount;
        [XmlAttribute("FrameRate")] public sbyte frameRate;
        /// <summary>TrackHeader bytes 0x11..0x13, kept as authored.</summary>
        [XmlAttribute("HeaderTail")] public string headerTail;
        [XmlArray("Units")] public List<MtarTrackUnit> units = new List<MtarTrackUnit>();

        public static MtarTrackInfo Read(byte[] b, int at, int size)
        {
            var t = new MtarTrackInfo();
            int unitCount = BitConverter.ToInt32(b, at);
            t.segmentCount = BitConverter.ToUInt32(b, at + 4);
            t.trackId = BitConverter.ToUInt16(b, at + 8);
            t.unknownA = b[at + 10];
            t.unknownB = b[at + 11];
            t.frameCount = BitConverter.ToInt32(b, at + 12);
            t.frameRate = (sbyte)b[at + 16];
            t.headerTail = $"{b[at + 17]:x2} {b[at + 18]:x2} {b[at + 19]:x2}";

            var rig = MgsvModBldr.Tools.Anim.FrigBones.ForUnitCount(unitCount);
            for (int i = 0; i < unitCount; i++)
            {
                uint off = BitConverter.ToUInt32(b, at + 0x14 + i * 4);
                var u = new MtarTrackUnit();
                if (rig is not null && i < rig.UnitBones.Length && rig.UnitBones[i].Length > 0)
                {
                    var nm = new List<string>();
                    foreach (var h in rig.UnitBones[i]) nm.Add(StrCode32Names.Text(h));
                    u.bones = string.Join(" ", nm);
                    u.bonesSpecified = true;
                }
                if (off == 0) { u.absent = true; u.absentSpecified = true; t.units.Add(u); continue; }
                u.offset = (int)off;
                int ua = at + (int)off;
                u.name = StrCode32Names.Text(BitConverter.ToUInt32(b, ua));
                int segs = b[ua + 4];
                u.flags = b[ua + 5];
                u.pad = BitConverter.ToUInt16(b, ua + 6);
                if (rig is not null && i < rig.UnitBones.Length && rig.UnitBones[i].Length > 0)
                {
                    var nm = new List<string>();
                    foreach (var h in rig.UnitBones[i]) nm.Add(StrCode32Names.Text(h));
                    u.bones = string.Join(" ", nm);
                    u.bonesSpecified = true;
                }
                for (int s = 0; s < segs; s++)
                {
                    int sa = ua + 8 + s * 8;
                    u.segments.Add(new MtarTrackSegment
                    {
                        dataOffset = BitConverter.ToInt32(b, sa),
                        segmentId = BitConverter.ToInt16(b, sa + 4),
                        packed = b[sa + 6],
                        componentBitSize = b[sa + 7],
                    });
                }
                t.units.Add(u);
            }
            return t;
        }

        public byte[] Write()
        {
            int n = units.Count;
            int end = 0x14 + n * 4;
            foreach (var u in units)
                if (!u.absent) end = Math.Max(end, u.offset + 8 + u.segments.Count * 8);
            var offs = new int[n];
            for (int i = 0; i < n; i++) offs[i] = units[i].absent ? 0 : units[i].offset;
            var outp = new byte[end];
            BitConverter.GetBytes(n).CopyTo(outp, 0);
            BitConverter.GetBytes(segmentCount).CopyTo(outp, 4);
            BitConverter.GetBytes(trackId).CopyTo(outp, 8);
            outp[10] = unknownA; outp[11] = unknownB;
            BitConverter.GetBytes(frameCount).CopyTo(outp, 12);
            outp[16] = (byte)frameRate;
            var tail = (headerTail ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < 3 && i < tail.Length; i++)
                outp[17 + i] = Convert.ToByte(tail[i], 16);

            for (int i = 0; i < n; i++)
            {
                BitConverter.GetBytes((uint)offs[i]).CopyTo(outp, 0x14 + i * 4);
                if (offs[i] == 0) continue;
                var u = units[i];
                int ua = offs[i];
                BitConverter.GetBytes(StrCode32Names.Value(u.name)).CopyTo(outp, ua);
                outp[ua + 4] = (byte)u.segments.Count;
                outp[ua + 5] = u.flags;
                BitConverter.GetBytes(u.pad).CopyTo(outp, ua + 6);
                for (int s = 0; s < u.segments.Count; s++)
                {
                    int sa = ua + 8 + s * 8;
                    var g = u.segments[s];
                    BitConverter.GetBytes(g.dataOffset).CopyTo(outp, sa);
                    BitConverter.GetBytes(g.segmentId).CopyTo(outp, sa + 4);
                    outp[sa + 6] = g.packed;
                    outp[sa + 7] = g.componentBitSize;
                }
            }
            return outp;
        }
    }

    /// <summary>One motion point and the bone it is attached to.</summary>
    [XmlType("MotionPoint", Namespace = "Mtar")]
    public class MtarMotionPointUnit
    {
        /// <summary>StrCode32 of an MTP_* name.</summary>
        [XmlAttribute("Name")] public string name;
        /// <summary>StrCode32 of the SKL_* bone. AnimFile::GetMotionPointParent returns this.</summary>
        [XmlAttribute("Bone")] public string bone;
    }

    /// <summary>
    /// Node 0x3b9a7784 — every motion-point unit this archive's clips may use, and its parent bone.
    /// `u32 Count` then Count x {u32 name, u32 bone}. A clip whose .mtp names a unit that is not
    /// listed here has no parent to attach to.
    /// </summary>
    [XmlType("MotionPointUnits", Namespace = "Mtar")]
    public class MtarMotionPointUnits
    {
        [XmlElement("MotionPoint")] public List<MtarMotionPointUnit> units = new List<MtarMotionPointUnit>();

        public static MtarMotionPointUnits Read(byte[] b, int at, int size)
        {
            var m = new MtarMotionPointUnits();
            int c = BitConverter.ToInt32(b, at);
            for (int i = 0; i < c; i++)
                m.units.Add(new MtarMotionPointUnit
                {
                    name = StrCode32Names.Text(BitConverter.ToUInt32(b, at + 4 + i * 8)),
                    bone = StrCode32Names.Text(BitConverter.ToUInt32(b, at + 8 + i * 8)),
                });
            return m;
        }

        public byte[] Write()
        {
            var outp = new byte[4 + units.Count * 8];
            BitConverter.GetBytes(units.Count).CopyTo(outp, 0);
            for (int i = 0; i < units.Count; i++)
            {
                BitConverter.GetBytes(StrCode32Names.Value(units[i].name)).CopyTo(outp, 4 + i * 8);
                BitConverter.GetBytes(StrCode32Names.Value(units[i].bone)).CopyTo(outp, 8 + i * 8);
            }
            return outp;
        }
    }

    /// <summary>Node 0x91e4534b — `u32 Count` then Count x StrCode32 SKL_* bone names.</summary>
    [XmlType("SkeletonList", Namespace = "Mtar")]
    public class MtarSkeletonList
    {
        [XmlElement("Bone")] public List<string> bones = new List<string>();

        /// <summary>A fixed 12-byte trailer after the bone array, present on every archive.
        /// DataControl::MakeMatchList2 reads exactly Count hashes and stops, so nothing in the
        /// engine consumes these three words; their purpose is unidentified. Carried as authored.</summary>
        [XmlAttribute("Trailer")] public string trailer;

        public static MtarSkeletonList Read(byte[] b, int at, int size)
        {
            var s = new MtarSkeletonList();
            int c = BitConverter.ToInt32(b, at);
            for (int i = 0; i < c && 4 + i * 4 + 4 <= size; i++)
                s.bones.Add(StrCode32Names.Text(BitConverter.ToUInt32(b, at + 4 + i * 4)));
            int t = at + 4 + c * 4;
            s.trailer = $"{BitConverter.ToUInt32(b, t):x8} {BitConverter.ToUInt32(b, t + 4):x8}"
                      + $" {BitConverter.ToUInt32(b, t + 8):x8}";
            return s;
        }

        public byte[] Write()
        {
            var outp = new byte[4 + bones.Count * 4 + 12];
            BitConverter.GetBytes(bones.Count).CopyTo(outp, 0);
            for (int i = 0; i < bones.Count; i++)
                BitConverter.GetBytes(StrCode32Names.Value(bones[i])).CopyTo(outp, 4 + i * 4);
            var tw = (trailer ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < 3 && i < tw.Length; i++)
                BitConverter.GetBytes(Convert.ToUInt32(tw[i], 16)).CopyTo(outp, 4 + bones.Count * 4 + i * 4);
            return outp;
        }
    }
}
