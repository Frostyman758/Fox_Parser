// Based on TwpfTool/TwpParamKeyStringId.cs
using System;
using System.IO;
using System.Xml.Serialization;
using CityHash = MgsvModBldr.Tools.GameHashing.GameCityHash;

namespace MgsvModBldr.Tools.Twpf
{
    [XmlType]
    public class TwpParamKeyStringId : TwpParamKey
    {
        [XmlAttribute]
        public string stringId;
        public void Read(BinaryReader reader, System.Collections.Generic.Dictionary<ulong, string> dict)
        {
            base.Read(reader);
            var stringIdHash = reader.ReadUInt64();
            if (!dict.TryGetValue(stringIdHash, out stringId))
                stringId = stringIdHash.ToString();

            if (TwpfLog.IsVerbose)
                Console.WriteLine($"Time: {base.time}, Value: {stringId}");
        }
        public new void Write(BinaryWriter writer)
        {
            base.Write(writer);
            if (ulong.TryParse(stringId, out ulong stringIdHash))
                writer.Write(stringIdHash);
            else
                writer.Write(StrCode(stringId));
        }
        public static ulong StrCode(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            const ulong seed0 = 0x9ae16a3b2f90404f;
            ulong seed1 = text.Length > 0 ? (uint)((text[0]) << 16) + (uint)text.Length : 0;
            return CityHash.CityHash64WithSeeds(text + "\0", seed0, seed1) & 0xFFFFFFFFFFFF;
        }
    }
}
