// Based on RdfTool/TPP/RadioGroupPart.cs
using System.IO;
using System;
using System.Xml;
using System.Globalization;

namespace MgsvModBldr.Tools.Rdf
{
    public abstract class RadioGroupPart
    {
        public sbyte DialogueEventIndex { get; set; } // index into table
        public sbyte CharaIndex { get; set; } // index into table
        public byte IntervalNextLabelId { get; set; } // 4 bits
        public virtual void Read(BinaryReader reader, HashManager hashManager, HashIdentifiedDelegate hashIdentifiedCallback)
        {
            DialogueEventIndex = reader.ReadSByte();
            CharaIndex = reader.ReadSByte();
            IntervalNextLabelId = (byte)(reader.ReadByte() >> 4);
        }
        public virtual void Write(BinaryWriter writer)
        {
            writer.Write(DialogueEventIndex);
            writer.Write(CharaIndex);
        }
        public virtual void WriteXml(XmlWriter writer)
        {
        }
    }
}
