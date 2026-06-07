// Based on MtarTool.Core/ExtensionMethods.cs
using System.IO;
using System.Linq;

namespace MgsvModBldr.Tools.Mtar
{
    internal static class MtarExtensions
    {
        internal static void Skip(this Stream stream, int count)
        {
            stream.Seek(count, SeekOrigin.Current);
        }

        internal static void Skip(this BinaryReader reader, int count)
        {
            reader.BaseStream.Skip(count);
        }

        internal static void WriteZeros(this BinaryWriter writer, int count)
        {
            byte[] zeros = new byte[count];
            writer.Write(zeros);
        }

        internal static void AlignWrite(this Stream output, int alignment, byte data)
        {
            long alignmentRequired = output.Position % alignment;
            if (alignmentRequired > 0)
            {
                byte[] alignmentBytes = Enumerable.Repeat(data, (int)(alignment - alignmentRequired)).ToArray();
                output.Write(alignmentBytes, 0, alignmentBytes.Length);
            }
        }
    }
}
