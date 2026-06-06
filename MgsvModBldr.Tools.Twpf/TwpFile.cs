// Based on TwpfTool/TwpFile.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Twpf
{
    [XmlType]
    public class TwpFile
    {
        private static readonly string sign = "TWPFwin";
        [XmlElement]
        public List<TwpGroup> groups;

        public void Read(BinaryReader reader, Dictionary<ulong, string> dict)
        {
            //header
            string signature = new string(reader.ReadChars(7));
            if (signature != sign)
                throw new ArgumentOutOfRangeException();
            byte version = reader.ReadByte();
            if (version!=1)
                throw new ArgumentOutOfRangeException();
            uint headerSize = reader.ReadUInt32();
            if (headerSize!=reader.BaseStream.Position)
                throw new ArgumentOutOfRangeException();
            if (TwpfLog.IsVerbose)
                Console.WriteLine($"{signature} v{version}, header size: {headerSize}");

            //offsets
            int groupCount = reader.ReadInt32();
            int tagCount = reader.ReadInt32();
            if (TwpfLog.IsVerbose)
                Console.WriteLine($"Group count: {groupCount} Tag count: {tagCount}");
            int[] groupOffsets = new int[groupCount];

            for (int i = 0; i < groupCount-1; i++)
            {
                groupOffsets[i+1] = reader.ReadInt32();
                if (TwpfLog.IsVerbose)
                    Console.WriteLine($"Offset #{i+1}: {groupOffsets[i + 1]}");
                long returnPos = reader.BaseStream.Position;

                reader.BaseStream.Position = returnPos;
            }
            groupOffsets[0] = (int)reader.BaseStream.Position;

            //groups
            groups = new List<TwpGroup>();
            foreach (int groupOffset in groupOffsets)
            {
                TwpGroup group = new TwpGroup();
                reader.BaseStream.Position = groupOffset;
                group.Read(reader,dict);
                groups.Add(group);
            }
        }
        public void Write(BinaryWriter writer)
        {
            //header
            writer.Write(sign.ToCharArray());
            writer.Write((byte)1);
            writer.Write((int)writer.BaseStream.Position + 4);

            //offsets
            writer.Write(groups.Count);
            long offsetToGroupOffsets = writer.BaseStream.Position;
            for (int i = 0; i < groups.Count; i++)
                writer.Write(0);

            //groups
            foreach (TwpGroup group in groups)
            {
                int index = groups.IndexOf(group);
                long returnPos = writer.BaseStream.Position;
                writer.BaseStream.Position = offsetToGroupOffsets + (index) * 4;
                writer.Write((int)returnPos);
                writer.BaseStream.Position = returnPos;
                group.Write(writer);
            }

            //tag offsets and indices
            Dictionary<string,ushort> tagEnum = new Dictionary<string, ushort>();
            ushort tagIndex = 0;
            List<uint> offsetsToTagNames = new List<uint>();
            foreach (TwpGroup group in groups)
                foreach (TwpParamTagGroup tagGroup in group.paramTagGroups)
                    foreach (TwpParamTagDefs paramTagDefs in tagGroup.paramTagDefs)
                        if (!tagEnum.ContainsKey(paramTagDefs.tagName))
                        {
                            tagEnum.Add(paramTagDefs.tagName, tagIndex);

                            offsetsToTagNames.Insert(tagIndex, (uint)writer.BaseStream.Position);

                            char[] stringChars = paramTagDefs.tagName.ToCharArray();
                            foreach (var chara in stringChars)
                                writer.Write(chara);
                            writer.Write((byte)0);

                            tagIndex++;
                        }

            writer.BaseStream.Position = 20 + (groups.Count * 4);
            foreach (TwpGroup group in groups)
            {
                writer.BaseStream.Position += 4 + (group.paramTagGroups.Count * 4);
                foreach (TwpParamTagGroup tagGroup in group.paramTagGroups)
                {
                    writer.BaseStream.Position += (tagGroup.paramTagDefs.Count * 4);
                    foreach (TwpParamTagDefs paramTagDefs in tagGroup.paramTagDefs)
                    {
                        writer.Write(offsetsToTagNames[tagEnum[paramTagDefs.tagName]]);
                        writer.BaseStream.Position += 1;
                        writer.Write(tagEnum[paramTagDefs.tagName]);
                        writer.BaseStream.Position += 1 + (paramTagDefs.weatherDefs.Count * 4);
                        foreach (TwpParamWeatherDefs weatherDefs in paramTagDefs.weatherDefs)
                        {
                            writer.BaseStream.Position += 4 + (weatherDefs.paramKeys.Count * 4);
                            for (int i = 0; i < weatherDefs.paramKeys.Count; i++)
                            {
                                writer.BaseStream.Position += 4;
                                switch (tagGroup.paramType)
                                {
                                    case ParamType.Float:
                                        writer.BaseStream.Position += 4;
                                        break;
                                    case ParamType.Vector3:
                                        writer.BaseStream.Position += 12;
                                        break;
                                    case ParamType.PathId:
                                    case ParamType.StringId:
                                        writer.BaseStream.Position += 8;
                                        break;
                                    default:
                                        throw new ArgumentOutOfRangeException();
                                }
                            }
                        }
                    }
                    writer.BaseStream.Position += 4;
                }
            }
        }
    }
}
