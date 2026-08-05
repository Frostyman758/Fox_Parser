// Based on LangTool/ExtensionMethods.cs
using System.IO;
using System.Linq;
using System.Text;

namespace MgsvModBldr.Tools.Translation.Lang
{
    internal static class LangExtensions
    {
        internal static string ReadNullTerminatedString(this BinaryReader reader)
        {
            StringBuilder builder = new StringBuilder();
            char nextCharacter;
            while ((nextCharacter = reader.ReadChar()) != 0x00)
            {
                builder.Append(nextCharacter);
            }
            return builder.ToString();
        }

        // byte-wise so multi-byte utf8 cannot trip the char decoder
        internal static string ReadNullTerminatedUtf8(this Stream stream)
        {
            var bytes = new System.Collections.Generic.List<byte>(64);
            int b;
            while ((b = stream.ReadByte()) > 0) bytes.Add((byte)b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        internal static void WriteNullTerminatedString(this BinaryWriter writer, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text + '\0');
            writer.Write(data, 0, data.Length);
        }

        internal static void AlignWrite(this BinaryWriter writer, int alignment, byte data)
        {
            long alignmentRequired = writer.BaseStream.Position % alignment;
            byte[] alignmentBytes = Enumerable.Repeat(data, (int)(alignment - alignmentRequired)).ToArray();
            writer.Write(alignmentBytes, 0, alignmentBytes.Length);
        }
    }
}
