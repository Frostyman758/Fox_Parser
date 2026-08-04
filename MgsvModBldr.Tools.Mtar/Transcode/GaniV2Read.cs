// Read a v2 gani body back into the decoded shape
// 04/08/2026
using System;
using System.Collections.Generic;
using MgsvModBldr.Tools.Mtar.Mtar;

namespace MgsvModBldr.Tools.Mtar.Transcode
{
    /// <summary>
    /// The inverse of <see cref="GaniV2.Write"/>. A v2 body carries only what varies per clip —
    /// frame count, unit flags, and per-segment {bit size, offset} — while the unit NAMES and
    /// segment TYPES live once in the archive's shared .trk. Put the two together and you get
    /// exactly the structure a v1 gani decodes to, so a v2 archive can be a transcode SOURCE:
    /// character-to-character ports, mirroring and reversal all work on it unchanged.
    ///
    /// Blob LENGTHS are not stored. Blobs are laid end to end, so a blob runs to the next one
    /// that starts after it — sorted by offset, not by table order, because Konami's writer does
    /// not always emit them in table order.
    /// </summary>
    public static class GaniV2Read
    {
        public static V1Gani Read(byte[] body, MtarTrackInfo trk, int frameScaleByte)
        {
            if (body is null || trk is null || trk.units.Count == 0) return null;
            if (body.Length < 8) return null;

            int unitCount = trk.units.Count;
            int segCount = 0;
            foreach (var u in trk.units) segCount += u.segments.Count;

            int pos = 8;
            pos += body[5] * 8;                       // paramCount x {u32 nameHash, f32 value}
            int flagsAt = pos;
            pos += unitCount;
            pos = (pos + 3) / 4 * 4;
            int tableAt = pos;
            if (tableAt + segCount * 4 > body.Length) return null;

            // Absolute blob starts, then sorted so each length is "up to the next blob".
            var start = new int[segCount];
            var bits = new int[segCount];
            for (int i = 0; i < segCount; i++)
            {
                int e = tableAt + i * 4;
                bits[i] = body[e];
                int rel = body[e + 1] | body[e + 2] << 8 | body[e + 3] << 16;
                start[i] = rel == 0 ? -1 : e + rel;
            }
            var order = new List<int>();
            for (int i = 0; i < segCount; i++) if (start[i] >= 0 && start[i] < body.Length) order.Add(i);
            order.Sort((a, b) => start[a].CompareTo(start[b]));
            var len = new int[segCount];
            for (int k = 0; k < order.Count; k++)
            {
                int i = order[k];
                int end = k + 1 < order.Count ? start[order[k + 1]] : body.Length;
                len[i] = Math.Max(0, end - start[i]);
            }

            int lastSeg = order.Count > 0 ? order[order.Count - 1] : -1;
            int g0FrameCount = (int)BitConverter.ToUInt32(body, 0);

            var g = new V1Gani
            {
                FrameCount = g0FrameCount,
                FrameScaleByte = frameScaleByte,
                SegmentCount = segCount,
                LayoutOffset = -1,                    // v2 keeps the layout in the archive, not the clip
            };

            int seg = 0;
            for (int ui = 0; ui < unitCount; ui++)
            {
                var tu = trk.units[ui];
                var unit = new V1Unit
                {
                    Name = Convert.ToUInt32(tu.name, 16),
                    Flags = flagsAt + ui < body.Length ? body[flagsAt + ui] : tu.flags,
                };
                // The LAST blob runs to end-of-body, which includes the file's 16-byte tail
                // padding — measure that one from its own key stream so a port does not grow
                // 16 bytes per clip per round trip.
                foreach (var ts in tu.segments)
                {
                    if (seg == lastSeg && start[seg] >= 0)
                    {
                        int exact = Measure(body, start[seg], ts.packed & 0x0F, bits[seg],
                                            g0FrameCount, (unit.Flags & 0x2) != 0, (unit.Flags & 0x4) != 0);
                        if (exact > 0 && exact <= len[seg]) len[seg] = exact;
                    }
                    var s = new V1Segment
                    {
                        UnitIndex = ui,
                        SegmentIndex = seg,
                        Type = ts.packed & 0x0F,
                        ComponentBitSize = bits[seg],
                        BlobStart = start[seg],
                    };
                    if (start[seg] >= 0 && len[seg] > 0)
                    {
                        s.Blob = new byte[len[seg]];
                        Buffer.BlockCopy(body, start[seg], s.Blob, 0, len[seg]);
                    }
                    unit.Segments.Add(s);
                    seg++;
                }
                g.Units.Add(unit);
            }
            return g;
        }

        /// <summary>Exact byte length of one keyframe blob, by walking it the way the decoder does.</summary>
        private static int Measure(byte[] b, int at, int type, int bits, int frameCount, bool hermite, bool isStatic)
        {
            if (bits <= 0) return 0;
            if (type == 0 || type == 5)                       // quat: bit-packed, 8-bit deltas
            {
                int key = 3 * bits + 3, bitPos = key;
                if (!isStatic)
                {
                    int acc = 0;
                    while (acc < frameCount)
                    {
                        int by = at + (bitPos >> 3);
                        if (by >= b.Length) return 0;
                        acc += (int)ReadBits(b, at * 8 + bitPos, 8);
                        bitPos += 8 + key;
                    }
                }
                return (bitPos + 7) / 8;
            }
            int comps = type == 1 ? 1 : type == 2 ? 2 : type == 4 ? 4 : 3;
            int sz = bits == 16 ? 2 : bits == 32 ? 4 : 0;
            if (sz == 0) return 0;
            int vlen = comps * sz, off = vlen;
            if (!isStatic)
            {
                int acc = 0;
                while (acc < frameCount)
                {
                    if (at + off >= b.Length) return 0;
                    acc += b[at + off];
                    off += 1 + vlen * (hermite ? 2 : 1);
                }
            }
            return off;
        }

        private static uint ReadBits(byte[] buf, int bitPos, int bitSize)
        {
            int bytePos = bitPos >> 3, bitOffset = bitPos & 7;
            int total = (bitOffset + bitSize + 7) >> 3;
            ulong raw = 0;
            for (int i = 0; i < total && bytePos + i < buf.Length; i++) raw |= (ulong)buf[bytePos + i] << (8 * i);
            return (uint)((raw >> bitOffset) & ((1UL << bitSize) - 1));
        }
    }
}
