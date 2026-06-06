// Based on TwpfTool/TwpParamKeyFloat.cs
using System;
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Twpf
{
    [XmlType]
    public class TwpParamKeyFloat : TwpParamKey
    {
        [XmlAttribute]
        public float value;

        public new void Read(BinaryReader reader)
        {
            base.Read(reader);
            value = reader.ReadSingle();
            if (TwpfLog.IsVerbose)
                Console.WriteLine($"Time: {base.time}, Value: {value}");
        }
        public new void Write(BinaryWriter writer)
        {
            base.Write(writer);
            writer.Write(value);
        }
    }
}
