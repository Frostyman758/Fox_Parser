// Based on MtarTool.Core/Mtar/MtarGaniFile2.cs
using MgsvModBldr.Tools.Mtar.Utility;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Mtar.Mtar
{
    [XmlType("Gani", Namespace = "Mtar")]
    public class MtarGaniFile2
    {
        [XmlIgnore]
        public ulong hash;

        /// <summary>Which game's hash scheme this entry used (set on read).</summary>
        [XmlIgnore]
        public bool isGz;

        [XmlAttribute("FilePath")]
        public string name;


        // fox::anim::MtarTableList2, 32 bytes. Sizes are stored in 16-byte lines.

        /// <summary>+0x08 absolute file offset of the gani body (a TrackMiniHeader).</summary>
        [XmlIgnore]
        public uint offset;

        /// <summary>+0x0C size of the unit-track data, in bytes (stored /0x10).</summary>
        [XmlIgnore]
        public int size;

        /// <summary>+0x0E offset to the motion-point tracks within this entry's data.</summary>
        [XmlIgnore]
        public int size2;

        /// <summary>+0x12 shader-track offset. 0 on every entry measured, so it is emitted only
        /// when set — that keeps existing XML byte-identical while letting a shader-animated
        /// archive survive a round trip instead of being silently zeroed.</summary>
        [XmlAttribute("ShaderTracksOffset")]
        public ushort shaderTracksOffset;

        [XmlIgnore]
        public bool shaderTracksOffsetSpecified;

        [XmlIgnore]
        /// <summary>+0x10 motion-point (root trajectory) track bytes — the .mtp payload.</summary>
        public int motionPointsSize;

        /// <summary>+0x18 MotionEventsOffset — absolute offset of this clip's event package
        /// (an EvpHeader), which is what ships as .enchnk and carries the MTEV_AG_SYNC_L/R
        /// foot-plant events the motion graph gates locomotion on.</summary>
        [XmlIgnore]
        public uint endChunkOffset;

        /// <summary>Byte length of the end chunk, derived from the next chunk's offset by
        /// MtarFile2.Read. 0 means "not computed" and falls back to the sentinel scan.</summary>
        [XmlIgnore]
        public int endChunkSize;

        public void Read(Stream input)
        {
            BinaryReader reader = new BinaryReader(input, Encoding.Default, true);

            hash = reader.ReadUInt64();
            isGz = NameResolver.IsGzHash(hash);
            name = NameResolver.TryFindName(NameResolver.GetHashFromULong(hash));
            offset = reader.ReadUInt32();
            size = reader.ReadInt16();
            size2 = reader.ReadInt16();
            size *= 0x10;
            motionPointsSize = reader.ReadInt16() * 0x10;
            shaderTracksOffset = reader.ReadUInt16();
            shaderTracksOffsetSpecified = shaderTracksOffset != 0;
            reader.Skip(4);
            endChunkOffset = reader.ReadUInt32();
            reader.Skip(4);
        }

        public void Write(Stream output)
        {
            BinaryWriter writer = new BinaryWriter(output, Encoding.Default, true);

            writer.Write(hash);
            writer.WriteZeros(24);
        }

        public byte[] ReadData(Stream input)
        {
            input.Position = offset;
            byte[] data = new byte[size];
            input.Read(data, 0, size);

            return data;
        }

        public byte[] ReadMotionPointData(Stream input)
        {
            byte[] data = new byte[motionPointsSize];
            input.Read(data, 0, motionPointsSize);

            return data;
        }

        public byte[] ReadEndChunkData(Stream input)
        {
            // End chunks are laid out contiguously and are the last section of the file, so the
            // next chunk's offset IS this one's end. MtarFile2.Read walks those boundaries and
            // fills endChunkSize; the scan below is only a fallback for a lone chunk.
            int size = endChunkSize > 0 ? endChunkSize : GetEndChunkSize(input);
            if (endChunkOffset >= input.Length) return new byte[0];
            if (size < 0 || endChunkOffset + size > input.Length)
                size = (int)(input.Length - endChunkOffset);

            input.Position = endChunkOffset;
            byte[] data = new byte[size];
            int got = 0;
            while (got < size)
            {
                int r = input.Read(data, got, size - got);
                if (r <= 0) break;
                got += r;
            }
            return data;
        }

        // Fallback only. Walks 16-byte lines looking for the terminator, but never reads past the
        // end: the original stepped 12 bytes at a time and compared Position != Length, so a step
        // that overshot the end made the next ReadUInt32 throw "unable to read beyond the end of
        // the stream". That is what broke unpacking of every archive we wrote.
        private int GetEndChunkSize(Stream input)
        {
            BinaryReader reader = new BinaryReader(input, Encoding.Default, true);

            input.Position = endChunkOffset;
            reader.Skip(16);

            while (input.Position + 4 <= input.Length)
            {
                if (reader.ReadUInt32() == 0xBFE2CF6)
                    return (int)((input.Position - 0x4) - endChunkOffset);

                if (input.Position + 12 > input.Length) break;
                reader.Skip(12);
            }

            return (int)(input.Length - endChunkOffset);
        }
    }
}
