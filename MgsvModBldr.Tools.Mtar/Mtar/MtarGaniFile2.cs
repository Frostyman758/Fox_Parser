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

        [XmlAttribute("FilePath")]
        public string name;

        [XmlIgnore]
        public uint offset;

        [XmlIgnore]
        public int size;

        [XmlIgnore]
        public int size2;

        [XmlIgnore]
        public int exChunkSize;

        [XmlIgnore]
        public uint endChunkOffset;

        public void Read(Stream input)
        {
            BinaryReader reader = new BinaryReader(input, Encoding.Default, true);

            hash = reader.ReadUInt64();
            name = NameResolver.TryFindName(NameResolver.GetHashFromULong(hash));
            offset = reader.ReadUInt32();
            size = reader.ReadInt16();
            size2 = reader.ReadInt16();
            size *= 0x10;
            exChunkSize = reader.ReadInt16() * 0x10;
            reader.Skip(6);
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

        public byte[] ReadExChunkData(Stream input)
        {
            byte[] data = new byte[exChunkSize];
            input.Read(data, 0, exChunkSize);

            return data;
        }

        public byte[] ReadEndChunkData(Stream input)
        {
            int size = GetEndChunkSize(input);
            input.Position = endChunkOffset;
            byte[] data = new byte[size];
            input.Read(data, 0, size);
            return data;
        }

        private int GetEndChunkSize(Stream input)
        {
            BinaryReader reader = new BinaryReader(input, Encoding.Default, true);

            int size = 0x0;
            uint lineValue = 0x0;
            bool run = true;

            input.Position = endChunkOffset;
            reader.Skip(16);

            while (run)
            {
                if (input.Position != input.Length)
                {
                    lineValue = reader.ReadUInt32();
                }
                else
                {
                    size = (int)(input.Length - endChunkOffset);

                    return size;
                }

                if (lineValue == 0xBFE2CF6)
                {
                    run = false;
                }
                else
                {
                    reader.Skip(12);
                }
            }

            size = (int)((input.Position - 0x4) - endChunkOffset);

            return size;
        }
    }
}
