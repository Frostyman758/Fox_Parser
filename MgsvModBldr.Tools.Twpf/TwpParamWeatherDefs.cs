// Based on TwpfTool/TwpParamWeatherDefs.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Twpf
{
    public enum Weather : ushort
    {
        SUNNY=0,
        CLOUDY=1,
        RAINY=2,
        SANDSTORM=3,
        FOGGY=4,
        POURING=5,
    }
    [XmlType]
    public class TwpParamWeatherDefs
    {
        [XmlAttribute]
        public Weather weatherType;
        [XmlElement]
        public List<TwpParamKey> paramKeys;

        public void Read(BinaryReader reader, ParamType paramType, Dictionary<ulong, string> dict)
        {
            weatherType = (Weather)reader.ReadUInt16();
            ushort keyCount = reader.ReadUInt16();
            if (TwpfLog.IsVerbose)
                Console.WriteLine($"Weather type: {weatherType}, Key count: {keyCount}");
            paramKeys = new List<TwpParamKey>();
            int[] keyOffsets = new int[keyCount];
            for (int i = 0; i < keyCount; i++)
            {
                keyOffsets[i] = reader.ReadInt32();
                if (TwpfLog.IsVerbose)
                    Console.WriteLine($"Offset #{i}: {keyOffsets[i]}");
            }

            foreach (int keyOffset in keyOffsets)
            {
                reader.BaseStream.Position = keyOffset;
                switch(paramType)
                {
                    case ParamType.Float:
                        TwpParamKeyFloat paramKey1 = new TwpParamKeyFloat();
                        paramKey1.Read(reader);
                        paramKeys.Add(paramKey1);
                        break;
                    case ParamType.Vector3:
                        TwpParamKeyVector3 paramKey2 = new TwpParamKeyVector3();
                        paramKey2.Read(reader);
                        paramKeys.Add(paramKey2);
                        break;
                    case ParamType.PathId:
                        TwpParamKeyPathId paramKey3 = new TwpParamKeyPathId();
                        paramKey3.Read(reader);
                        paramKeys.Add(paramKey3);
                        break;
                    case ParamType.StringId:
                        TwpParamKeyStringId paramKey4 = new TwpParamKeyStringId();
                        paramKey4.Read(reader,dict);
                        paramKeys.Add(paramKey4);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
        public void Write(BinaryWriter writer, ParamType paramType)
        {
            writer.Write((ushort)weatherType);
            writer.Write((ushort)paramKeys.Count);

            long offsetToKeyOffsets = writer.BaseStream.Position;
            for (int i = 0; i < paramKeys.Count; i++)
                writer.Write(0);

            foreach (TwpParamKey paramKey in paramKeys)
            {
                int index = paramKeys.IndexOf(paramKey);

                long returnPos = writer.BaseStream.Position;
                writer.BaseStream.Position = offsetToKeyOffsets + (index * 4);
                writer.Write((int)returnPos);
                writer.BaseStream.Position = returnPos;

                switch (paramType)
                {
                    case ParamType.Float:
                        ((TwpParamKeyFloat)paramKey).Write(writer);
                        break;
                    case ParamType.Vector3:
                        ((TwpParamKeyVector3)paramKey).Write(writer);
                        break;
                    case ParamType.PathId:
                        ((TwpParamKeyPathId)paramKey).Write(writer);
                        break;
                    case ParamType.StringId:
                        ((TwpParamKeyStringId)paramKey).Write(writer);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
    }
}
