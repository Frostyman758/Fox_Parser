// Based on TwpfTool/TwpParamKey.cs
using System;
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Twpf
{
    [XmlInclude(typeof(TwpParamKeyFloat))]
    [XmlInclude(typeof(TwpParamKeyVector3))]
    [XmlInclude(typeof(TwpParamKeyPathId))]
    [XmlInclude(typeof(TwpParamKeyStringId))]
    [XmlType]
    public class TwpParamKey
    {
        [XmlAttribute]
        public string time;

        public void Read(BinaryReader reader)
        {
            uint timeInt = reader.ReadUInt32();
            var hour = (int)Math.Floor(timeInt / 60.0f);
            var minute = (int)(timeInt - (hour * 60));
            time=string.Format($"{hour:00}:{minute:00}");
        }
        public void Write(BinaryWriter writer)
        {
            var strings = time.Split(":".ToCharArray());
            if (!byte.TryParse(strings[0], out byte hour)) throw new ArgumentOutOfRangeException();
            if (!byte.TryParse(strings[1], out byte minute)) throw new ArgumentOutOfRangeException();
            uint timeInt = (uint)(hour * 60) + minute;
            writer.Write(timeInt);
        }
    }
}
