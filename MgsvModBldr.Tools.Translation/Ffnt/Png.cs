// Minimal dependency-free PNG codec for the .ffnt font-layer images.
// Writes 8-bit grayscale (the mask we emit); reads 8-bit color types
// 0/2/3/6 with filters 0-4 (no interlace) so externally edited PNGs
// (Photoshop/GIMP RGBA, palette, etc.) repack — matching the reference
// tool's "is this pixel pure white?" mask test. Uses the framework's
// ZLibStream for IDAT (zero external deps, same as the Ftex port).
using System.IO.Compression;
using System.Text;

namespace MgsvModBldr.Tools.Translation.Ffnt
{
    internal static class Png
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // ─── CRC32 (PNG polynomial) ─────────────────────────────────────
        private static readonly uint[] CrcTable = BuildCrcTable();
        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
        private static uint Crc(byte[] buf)
        {
            uint c = 0xFFFFFFFFu;
            foreach (var b in buf) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static void WriteBE(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));  s.WriteByte((byte)v);
        }
        private static uint ReadBE(byte[] b, int o) =>
            (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            WriteBE(s, (uint)data.Length);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            var crcBuf = new byte[typeBytes.Length + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcBuf, 0, typeBytes.Length);
            Buffer.BlockCopy(data, 0, crcBuf, typeBytes.Length, data.Length);
            s.Write(typeBytes, 0, typeBytes.Length);
            s.Write(data, 0, data.Length);
            WriteBE(s, Crc(crcBuf));
        }

        public static void WriteGrayscale8(string path, int width, int height, byte[] pixels)
        {
            using var fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(Signature, 0, Signature.Length);

            var ihdr = new byte[13];
            ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
            ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 0;   // color type: grayscale
            ihdr[10] = 0;  // compression
            ihdr[11] = 0;  // filter
            ihdr[12] = 0;  // interlace
            WriteChunk(fs, "IHDR", ihdr);

            // Raw scanlines: each prefixed with filter byte 0 (none).
            var raw = new byte[height * (width + 1)];
            for (int y = 0; y < height; y++)
            {
                raw[y * (width + 1)] = 0;
                Buffer.BlockCopy(pixels, y * width, raw, y * (width + 1) + 1, width);
            }
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            WriteChunk(fs, "IDAT", ms.ToArray());

            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        public static (int width, int height, bool[] white) DecodeWhiteMask(string path)
        {
            var bytes = File.ReadAllBytes(path);
            for (int i = 0; i < Signature.Length; i++)
                if (bytes[i] != Signature[i]) throw new InvalidDataException("Not a PNG: " + path);

            int pos = 8;
            int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
            byte[] palette = null;
            using var idat = new MemoryStream();
            while (pos + 8 <= bytes.Length)
            {
                uint len = ReadBE(bytes, pos); pos += 4;
                string type = Encoding.ASCII.GetString(bytes, pos, 4); pos += 4;
                int dataStart = pos;
                pos += (int)len;
                int crcPos = pos; pos += 4; // skip crc
                switch (type)
                {
                    case "IHDR":
                        width = (int)ReadBE(bytes, dataStart);
                        height = (int)ReadBE(bytes, dataStart + 4);
                        bitDepth = bytes[dataStart + 8];
                        colorType = bytes[dataStart + 9];
                        interlace = bytes[dataStart + 12];
                        break;
                    case "PLTE":
                        palette = new byte[len];
                        Buffer.BlockCopy(bytes, dataStart, palette, 0, (int)len);
                        break;
                    case "IDAT":
                        idat.Write(bytes, dataStart, (int)len);
                        break;
                    case "IEND":
                        pos = bytes.Length;
                        break;
                }
            }

            if (bitDepth != 8) throw new NotSupportedException($"PNG bit depth {bitDepth} unsupported ({path})");
            if (interlace != 0) throw new NotSupportedException($"Interlaced PNG unsupported ({path})");

            int channels = colorType switch
            {
                0 => 1, // grayscale
                2 => 3, // rgb
                3 => 1, // palette index
                4 => 2, // gray+alpha
                6 => 4, // rgba
                _ => throw new NotSupportedException($"PNG color type {colorType} unsupported ({path})")
            };

            // Inflate IDAT.
            idat.Position = 0;
            byte[] raw;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                z.CopyTo(outMs);
                raw = outMs.ToArray();
            }

            int stride = width * channels;
            var image = new byte[height * stride];
            var prev = new byte[stride];
            int rp = 0;
            for (int y = 0; y < height; y++)
            {
                byte filter = raw[rp++];
                var cur = new byte[stride];
                Buffer.BlockCopy(raw, rp, cur, 0, stride);
                rp += stride;
                Unfilter(filter, cur, prev, channels);
                Buffer.BlockCopy(cur, 0, image, y * stride, stride);
                prev = cur;
            }

            var white = new bool[width * height];
            for (int i = 0; i < width * height; i++)
            {
                int o = i * channels;
                byte r, g, b;
                switch (colorType)
                {
                    case 0: r = g = b = image[o]; break;
                    case 4: r = g = b = image[o]; break;            // gray+alpha
                    case 2: r = image[o]; g = image[o + 1]; b = image[o + 2]; break;
                    case 6: r = image[o]; g = image[o + 1]; b = image[o + 2]; break;
                    case 3:
                        int idx = image[o] * 3;
                        r = palette[idx]; g = palette[idx + 1]; b = palette[idx + 2];
                        break;
                    default: r = g = b = 0; break;
                }
                white[i] = r == 255 && g == 255 && b == 255;
            }
            return (width, height, white);
        }

        private static void Unfilter(byte filter, byte[] cur, byte[] prev, int bpp)
        {
            switch (filter)
            {
                case 0: break;
                case 1: // Sub
                    for (int i = bpp; i < cur.Length; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    break;
                case 2: // Up
                    for (int i = 0; i < cur.Length; i++) cur[i] = (byte)(cur[i] + prev[i]);
                    break;
                case 3: // Average
                    for (int i = 0; i < cur.Length; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + ((a + prev[i]) >> 1));
                    }
                    break;
                case 4: // Paeth
                    for (int i = 0; i < cur.Length; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + Paeth(a, b, c));
                    }
                    break;
                default: throw new NotSupportedException("PNG filter " + filter);
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }
    }
}
