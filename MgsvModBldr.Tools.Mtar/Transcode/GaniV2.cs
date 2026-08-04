// Write a v2 gani body from a decoded v1 gani
using System;
using System.Collections.Generic;
using System.IO;

namespace MgsvModBldr.Tools.Mtar.Transcode
{
    /// <summary>
    /// Emits the v2 per-gani body. A v2 mtar keeps the track LAYOUT once, in the shared
    /// .trk, so the body carries only what varies per animation:
    ///
    ///   u32 frameCount | u8 pad | u8 paramCount | u16 pad
    ///   paramCount x { u32 nameHash, f32 value }
    ///   unitCount x u8 unitFlags        (align to 4)
    ///   segmentCount x { u8 componentBitSize, u24 offset }   offset self-relative, 0 = none
    ///   16 bytes of padding
    ///   ...blobs...
    ///
    /// Blobs are copied verbatim from v1: both formats use the same keyframe encoding
    /// (confirmed on matched GZ/TPP pairs — identical segment types, component bit sizes
    /// and blob lengths), so nothing is re-encoded and nothing is lost.
    /// </summary>
    public static class GaniV2
    {
        /// <summary>Blobs are 16-byte aligned in the shipped files; match that.</summary>
        private const int BlobAlign = 16;

        public static byte[] Write(V1Gani gani)
        {
            var segs = new List<V1Segment>(gani.Flat());

            // Header size is fixed once the counts are known, so blob offsets can be
            // computed before anything is written.
            int pos = 8;                                   // frameCount + pad/paramCount/pad
            pos += gani.Units.Count;                       // one flag byte per unit
            pos = Align(pos, 4);
            int tableAt = pos;
            pos += segs.Count * 4;                         // the Gani2TrackData table
            pos += 16;                                     // trailing FSkip(16)
            int blobsAt = Align(pos, BlobAlign);

            var offsets = new int[segs.Count];
            int cursor = blobsAt;
            for (int i = 0; i < segs.Count; i++)
            {
                if (!segs[i].HasData) { offsets[i] = 0; continue; }
                offsets[i] = cursor;
                cursor += segs[i].Blob.Length;
            }
            int total = Align(cursor, BlobAlign);

            var outp = new byte[total];
            using var ms = new MemoryStream(outp);
            using var w = new BinaryWriter(ms);

            w.Write((uint)gani.FrameCount);
            w.Write((byte)0);                              // pad0
            w.Write((byte)0);                              // paramCount — v1 carries none
            w.Write((ushort)0);                            // pad1
            foreach (var u in gani.Units) w.Write((byte)u.Flags);
            ms.Position = tableAt;

            for (int i = 0; i < segs.Count; i++)
            {
                // The offset is relative to its own 4-byte table entry.
                int rel = offsets[i] == 0 ? 0 : offsets[i] - (tableAt + i * 4);
                if (rel < 0 || rel > 0xFFFFFF)
                    throw new InvalidDataException($"segment {i} blob offset {rel} does not fit in 24 bits");
                w.Write((byte)segs[i].ComponentBitSize);
                w.Write((byte)(rel & 0xFF));
                w.Write((byte)((rel >> 8) & 0xFF));
                w.Write((byte)((rel >> 16) & 0xFF));
            }

            for (int i = 0; i < segs.Count; i++)
            {
                if (offsets[i] == 0) continue;
                Buffer.BlockCopy(segs[i].Blob, 0, outp, offsets[i], segs[i].Blob.Length);
            }
            return outp;
        }

        private static int Align(int n, int a) => (n + a - 1) / a * a;
    }
}
