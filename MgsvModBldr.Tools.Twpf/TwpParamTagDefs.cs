// Based on TwpfTool/TwpParamTagDefs.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Twpf
{
    [XmlType]
    public class TwpParamTagDefs
    {
        [XmlAttribute]
        public string tagName;
        [XmlElement]
        public List<TwpParamWeatherDefs> weatherDefs;

        public void Read(BinaryReader reader, ParamType paramType, Dictionary<ulong, string> dict)
        {
            uint tagOffset = reader.ReadUInt32();

            long returnPos = reader.BaseStream.Position;
            reader.BaseStream.Position = tagOffset;
            var chars = new List<char>();
            var @char = reader.ReadChar();
            while (@char != '\0')
            {
                chars.Add(@char);
                @char = reader.ReadChar();
            }
            tagName = new string(chars.ToArray());
            reader.BaseStream.Position = returnPos;

            byte weatherCount = reader.ReadByte();
            weatherDefs = new List<TwpParamWeatherDefs>();
            ushort tagIndex = reader.ReadUInt16();
            byte padding = reader.ReadByte();
            if (padding!=0)
                throw new ArgumentOutOfRangeException();
            if (TwpfLog.IsVerbose)
                Console.WriteLine($"Tag name: {tagName}, Weather count: {weatherCount}, Tag index: {tagIndex}");

            int[] weatherOffsets = new int[weatherCount];
            for (int i = 0; i < weatherCount; i++)
            {
                weatherOffsets[i] = reader.ReadInt32();
                if (TwpfLog.IsVerbose)
                    Console.WriteLine($"Offset #{i}: {weatherOffsets[i]}");
            }

            foreach (int weatherOffset in weatherOffsets)
            {
                reader.BaseStream.Position = weatherOffset;
                TwpParamWeatherDefs twpParamWeatherDef = new TwpParamWeatherDefs();
                twpParamWeatherDef.Read(reader, paramType, dict);
                weatherDefs.Add(twpParamWeatherDef);
            }
        }
        public void Write(BinaryWriter writer, ParamType paramType)
        {
            long offsetToStart = writer.BaseStream.Position;
            writer.Write(0);            //tagOffset
            writer.Write((byte)weatherDefs.Count);
            writer.Write((ushort)0);    //tagIndex
            writer.Write((byte)0);
            for (int i = 0; i < weatherDefs.Count; i++)
                writer.Write(0);
            foreach (TwpParamWeatherDefs weatherDef in weatherDefs)
            {
                int index = weatherDefs.IndexOf(weatherDef);

                long returnPos = writer.BaseStream.Position;
                writer.BaseStream.Position = offsetToStart + 8 + (index * 4);
                writer.Write((int)returnPos);
                writer.BaseStream.Position = returnPos;

                weatherDef.Write(writer, paramType);
            }
        }
    }
}
