// Based on LangTool/Lang/LangFile.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Translation.Lang
{
    [XmlRoot("LangFile")]
    public class LangFile
    {
        private const int LittleEndianConstant = 0x0000454C; // LE
        private const int BigEndianConstant = 0x00004542; // BE
        private const int HeaderSizeV2 = 32;

        public LangFile()
        {
            Entries = new List<LangEntry>();
        }

        [XmlArray("Entries")]
        public List<LangEntry> Entries { get; set; }

        [XmlAttribute("Endianess")]
        public Endianess Endianess { get; set; }

        // GZ 2, TPP 3; omitted for 3 so existing xml stays byte-identical
        [XmlAttribute("Version")]
        public int Version { get; set; } = 3;

        public bool ShouldSerializeVersion()
        {
            return Version != 3;
        }

        public static LangFile ReadLangFile(Stream inputStream, Dictionary<uint, string> dictionary)
        {
            LangFile langFile = new LangFile();
            langFile.Read(inputStream, dictionary);
            return langFile;
        }

        public void Read(Stream inputStream, Dictionary<uint, string> langIdDictionary)
        {
            BinaryReader headerReader = new BinaryReader(inputStream, Encoding.UTF8, true);
            BinaryReader reader;

            int magicNumber = headerReader.ReadInt32();
            int version = headerReader.ReadInt32(); // GZ 2, TPP 3
            int endianess = headerReader.ReadInt32(); // LE, BE
            switch (endianess)
            {
                case LittleEndianConstant: // LE
                    Endianess = Endianess.LittleEndian;
                    reader = headerReader;
                    break;
                case BigEndianConstant: // BE
                    Endianess = Endianess.BigEndian;
                    version = EndianessBitConverter.FlipEndianess(version);
                    reader = new BigEndianBinaryReader(inputStream, Encoding.UTF8, true);
                    break;
                default:
                    throw new Exception(string.Format("Unknown endianess: {0:X}", endianess));
            }

            if (version == 2)
            {
                ReadV2(inputStream, reader);
                return;
            }

            int entryCount = reader.ReadInt32();
            int valuesOffset = reader.ReadInt32();
            int keysOffset = reader.ReadInt32();

            inputStream.Position = valuesOffset;
            Dictionary<int, LangEntry> offsetEntryDictionary = new Dictionary<int, LangEntry>();
            for (int i = 0; i < entryCount; i++)
            {
                int valuePosition = (int)inputStream.Position - valuesOffset;
                short colorId = headerReader.ReadInt16();
                string value = reader.ReadNullTerminatedString();
                offsetEntryDictionary.Add(valuePosition, new LangEntry
                {
                    Color = colorId,
                    Value = value
                });
            }

            inputStream.Position = keysOffset;
            for (int i = 0; i < entryCount; i++)
            {
                uint langIdCode = reader.ReadUInt32();
                int offset = reader.ReadInt32();

                string langId;
                if (langIdDictionary.TryGetValue(langIdCode, out langId))
                {
                    offsetEntryDictionary[offset].LangId = langId;
                }

                offsetEntryDictionary[offset].Key = langIdCode;
            }

            Entries = offsetEntryDictionary.Values.ToList();
        }

        // GZ layout: key table of (name offset, value offset), then plain-text
        // langId names, then [color][string] values. No StrCode32, no dictionary.
        private void ReadV2(Stream inputStream, BinaryReader reader)
        {
            Version = 2;
            int entryCount = reader.ReadInt32();
            int keyTableOffset = reader.ReadInt32();
            int keyNamesOffset = reader.ReadInt32();
            int valuesOffset = reader.ReadInt32();

            var keyOffsets = new uint[entryCount];
            var valueOffsets = new uint[entryCount];
            inputStream.Position = keyTableOffset;
            for (int i = 0; i < entryCount; i++)
            {
                keyOffsets[i] = reader.ReadUInt32();
                valueOffsets[i] = reader.ReadUInt32();
            }

            Entries = new List<LangEntry>(entryCount);
            for (int i = 0; i < entryCount; i++)
            {
                inputStream.Position = keyNamesOffset + keyOffsets[i];
                string langId = inputStream.ReadNullTerminatedUtf8();

                inputStream.Position = valuesOffset + valueOffsets[i];
                short color = reader.ReadInt16();
                string value = inputStream.ReadNullTerminatedUtf8();

                Entries.Add(new LangEntry
                {
                    LangId = langId,
                    Color = color,
                    Value = value,
                    Offset = (int)valueOffsets[i]
                });
            }
        }

        private void WriteV2(Stream outputStream, BinaryWriter headerWriter, BinaryWriter writer)
        {
            long start = outputStream.Position;
            int count = Entries.Count;
            outputStream.Position = start + HeaderSizeV2 + count * 8;

            int keyNamesOffset = (int)(outputStream.Position - start);
            var keyOffsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                keyOffsets[i] = (int)(outputStream.Position - start) - keyNamesOffset;
                writer.WriteNullTerminatedString(Entries[i].LangId ?? string.Empty);
            }

            int valuesOffset = (int)(outputStream.Position - start);
            var valueOffsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                valueOffsets[i] = (int)(outputStream.Position - start) - valuesOffset;
                writer.Write(Entries[i].Color);
                writer.WriteNullTerminatedString(Entries[i].Value ?? string.Empty);
            }

            long endPosition = outputStream.Position;

            outputStream.Position = start;
            headerWriter.Write(0x474e414c); // LANG
            writer.Write(2);
            headerWriter.Write(Endianess == Endianess.LittleEndian ? LittleEndianConstant : BigEndianConstant);
            writer.Write(count);
            writer.Write(HeaderSizeV2);
            writer.Write(keyNamesOffset);
            writer.Write(valuesOffset);
            writer.Write(0);

            for (int i = 0; i < count; i++)
            {
                writer.Write(keyOffsets[i]);
                writer.Write(valueOffsets[i]);
            }

            outputStream.Position = endPosition;
        }

        public void Write(Stream outputStream)
        {
            BinaryWriter headerWriter = new BinaryWriter(outputStream, Encoding.UTF8, true);
            BinaryWriter writer;
            switch (Endianess)
            {
                case Endianess.LittleEndian:
                    writer = headerWriter;
                    break;
                case Endianess.BigEndian:
                    writer = new BigEndianBinaryWriter(outputStream, Encoding.UTF8, true);
                    break;
                default:
                    throw new Exception("Unknown endianess " + Endianess);
            }

            if (Version == 2)
            {
                WriteV2(outputStream, headerWriter, writer);
                return;
            }

            long headerPosition = outputStream.Position;
            outputStream.Position += 24;

            int valuesPosition = (int)outputStream.Position;
            foreach (var entry in Entries)
            {
                entry.UpdateKey();
                entry.Offset = (int)outputStream.Position - valuesPosition;
                headerWriter.Write(entry.Color);
                writer.WriteNullTerminatedString(entry.Value);
            }

            writer.AlignWrite(4, 0x00);

            int keysPosition = (int)outputStream.Position;
            foreach (var entry in Entries.OrderBy(e => e.Key).ThenByDescending(e => e.Offset))
            {
                writer.Write(entry.Key);
                writer.Write(entry.Offset);
            }

            long endPosition = outputStream.Position;

            outputStream.Position = headerPosition;

            headerWriter.Write(0x474e414c); // LANG
            writer.Write(0x0000003);
            headerWriter.Write(Endianess == Endianess.LittleEndian ? LittleEndianConstant : BigEndianConstant);

            writer.Write(Entries.Count);
            writer.Write(valuesPosition);
            writer.Write(keysPosition);

            outputStream.Position = endPosition;
        }
    }
}
