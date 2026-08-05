// Refuse to ship a motion-point payload the engine would fault on
// 04/08/2026
using System;

namespace MgsvModBldr.Tools.Mtar.Transcode
{
    /// <summary>
    /// A .mtp is a TrackHeader — the same shape as a .trk:
    ///   u32 unitCount | u32 trackCount | u32 flags | u32 frameCount | u32 frameRate
    ///   unitCount x u32 unitOffset (payload-relative, from 0x14)
    /// and each unit record is `u32 mtpName | u8 segCount@+4 | 8-byte segment records@+8`.
    ///
    /// `fox::anim::TrackControl::GetTrackControlSize` walks precisely that and dereferences the
    /// segment record WITHOUT A NULL CHECK, so a payload that is merely "a bit off" is not a
    /// visual glitch — it is an access violation the moment the motion graph binds the clip.
    /// A corrupt one shipped on 04/08/2026 and crashed the game; this is the gate that stops it
    /// happening again, and it is cheap enough to run on every clip.
    /// </summary>
    public static class MotionPointCheck
    {
        /// <summary>Null when the payload is safe; otherwise why it is not.</summary>
        public static string Why(byte[] mp)
        {
            if (mp is null || mp.Length == 0) return null;         // no motion points is fine
            if (mp.Length < 0x14) return $"only {mp.Length} bytes — shorter than the header";

            int units = BitConverter.ToInt32(mp, 0);
            if (units <= 0) return $"unit count {units}";
            if (0x14 + units * 4 > mp.Length)
                return $"unit count {units} needs {0x14 + units * 4} bytes, payload is {mp.Length}";

            for (int i = 0; i < units; i++)
            {
                uint off = BitConverter.ToUInt32(mp, 0x14 + i * 4);
                if (off == 0) continue;                            // unit not animated here
                if (off + 8 > mp.Length) return $"unit {i} offset {off} is past the {mp.Length}-byte payload";
                int segs = mp[off + 4];
                if (off + 8 + segs * 8 > mp.Length)
                    return $"unit {i} declares {segs} segments that run past the payload";
            }
            return null;
        }

        public static bool Valid(byte[] mp) => Why(mp) is null;
    }
}
