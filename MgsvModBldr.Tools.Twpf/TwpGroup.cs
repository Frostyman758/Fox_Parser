// Based on TwpfTool/TwpGroup.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Twpf
{
    public enum WeatherParamGroupName : ushort
    {
        TppGlobalVolumetricFog=1,
        TppAtmosphere_TppSky=2,
        GrPluginSettings=3,
        ColorCorrection=4,
        LineSSAOParameters=5,
        WeatherParameters=7,
        NoiseEnvelopeGenerator=8,
        LocalReflectionSettings=9,
        GenerativeClouds=10,
    };

    [XmlType]
    public class TwpGroup
    {
        [XmlAttribute]
        public WeatherParamGroupName groupType;
        [XmlElement]
        public List<TwpParamTagGroup> paramTagGroups;

        public void Read(BinaryReader reader, Dictionary<ulong, string> dict)
        {
            ushort paramTagGroupCount = reader.ReadUInt16();
            paramTagGroups = new List<TwpParamTagGroup>();
            groupType = (WeatherParamGroupName)reader.ReadUInt16();
            if (TwpfLog.IsVerbose)
                Console.WriteLine($"Param tag group count: {paramTagGroupCount}, Group type: {groupType}");
            uint[] paramTagGroupOffsets = new uint[paramTagGroupCount];
            for (int i = 0; i < paramTagGroupCount; i++)
            {
                paramTagGroupOffsets[i]=reader.ReadUInt32();
                if (TwpfLog.IsVerbose)
                    Console.WriteLine($"Offset #{i}: {paramTagGroupOffsets[i]}");
            }
            foreach (uint paramTagGroupOffset in paramTagGroupOffsets)
            {
                reader.BaseStream.Position = paramTagGroupOffset;
                TwpParamTagGroup twpParamTagGroup = new TwpParamTagGroup();
                twpParamTagGroup.Read(reader,dict);
                paramTagGroups.Add(twpParamTagGroup);
            }
        }
        public void Write(BinaryWriter writer)
        {
            writer.Write((ushort)paramTagGroups.Count);
            writer.Write((ushort)groupType);
            long offsetToTagGroupOffsets = writer.BaseStream.Position;
            for (int i = 0; i < paramTagGroups.Count; i++)
                writer.Write(0);
            foreach (TwpParamTagGroup paramTagGroup in paramTagGroups)
            {
                int index = paramTagGroups.IndexOf(paramTagGroup);

                long returnPos = writer.BaseStream.Position;
                writer.BaseStream.Position = offsetToTagGroupOffsets + (index * 4);
                writer.Write((int)returnPos);
                writer.BaseStream.Position = returnPos;

                paramTagGroup.Write(writer);
            }
        }
    }
}
