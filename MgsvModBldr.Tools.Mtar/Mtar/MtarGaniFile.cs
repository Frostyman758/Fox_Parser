// Based on MtarTool.Core/Mtar/MtarGaniFile.cs
using MgsvModBldr.Tools.Mtar.Utility;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Mtar.Mtar
{
    [XmlType("Entry", Namespace = "Mtar")]
    public class MtarGaniFile
    {
        [XmlIgnore]
        public ulong hash;

        /// <summary>Which game's hash scheme this entry used (set on read).</summary>
        [XmlIgnore]
        public bool isGz;

        [XmlAttribute("FilePath")]
        public string name;

        [XmlIgnore]
        public uint offset;

        /// <summary>+0x0C data length in bytes, as recorded in the entry.</summary>
        [XmlIgnore]
        public int size;

        /// <summary>Span from this body to the next one — size plus the alignment padding.
        /// Konami's padding is not always zeros, so the padding has to travel with the body or a
        /// repack silently blanks it.</summary>
        [XmlIgnore]
        public int paddedSize;

        /// <summary>Emitted only when padding is carried, so the recorded length can be written
        /// back instead of the (larger) blob length.</summary>
        [XmlAttribute("DataSize")]
        public int dataSize;

        [XmlIgnore]
        public bool dataSizeSpecified;

        public void Read(Stream input)
        {
            BinaryReader reader = new BinaryReader(input, Encoding.Default, true);

            hash = reader.ReadUInt64();
            isGz = NameResolver.IsGzHash(hash);
            name = NameResolver.TryFindName(NameResolver.GetHashFromULong(hash)) + ".gani";
            offset = reader.ReadUInt32();
            size = reader.ReadInt32();
        }

        public void Write(Stream output)
        {
            BinaryWriter writer = new BinaryWriter(output, Encoding.Default, true);

            writer.Write(hash);
            writer.WriteZeros(8);
        }

        public byte[] ReadData(Stream input)
        {
            int n = paddedSize > size ? paddedSize : size;
            if (offset + n > input.Length) n = (int)(input.Length - offset);
            input.Position = offset;
            byte[] data = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = input.Read(data, got, n - got);
                if (r <= 0) break;
                got += r;
            }
            return data;
        }
    }
}
