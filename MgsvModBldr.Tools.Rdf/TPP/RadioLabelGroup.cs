// Based on RdfTool/TPP/RadioLabelGroup.cs
using System;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Collections.Generic;
using System.Globalization;

namespace MgsvModBldr.Tools.Rdf
{
    public class RadioLabelGroup : RadioGroupPart
    {
        public FoxHash Name { get; set; }
        public List<RadioLabelPart2> LabelParts = new List<RadioLabelPart2>();
        public override void Read(BinaryReader reader, HashManager hashManager, HashIdentifiedDelegate hashIdentifiedCallback)
        {
            Name = new FoxHash();
            Name.Read(reader, hashManager.StrCode32LookupTable, hashIdentifiedCallback);

            base.Read(reader, hashManager, hashIdentifiedCallback);
        }
        public void ReadGroup(BinaryReader reader, HashManager hashManager, HashIdentifiedDelegate hashIdentifiedCallback)
        {
            Name = new FoxHash();
            Name.Read(reader, hashManager.StrCode32LookupTable, hashIdentifiedCallback);

            byte count = reader.ReadByte();

            for (int i = 0; i < count; i++)
            {
                RadioLabelPart2 voiceClip = new RadioLabelPart2();
                voiceClip.Read(reader, hashManager, hashIdentifiedCallback);
                LabelParts.Add(voiceClip);
            }
        }
        public override void Write(BinaryWriter writer)
        {
            Name.Write(writer);

            base.Write(writer);

            writer.Write((byte)(1|IntervalNextLabelId << 4));
        }
        public void WriteGroup(BinaryWriter writer)
        {
            Name.Write(writer);
            writer.Write((byte)LabelParts.Count);
            foreach (RadioLabelPart2 voiceClip in LabelParts)
            {
                voiceClip.Write(writer);
            }
        }
        public void ReadXml(XmlReader reader)
        {
            Name = new FoxHash();
            Name.ReadXml(reader, "id");
            IntervalNextLabelId = byte.Parse(reader["intervalNextLabelId"]);
        }

        public override void WriteXml(XmlWriter writer)
        {
            writer.WriteStartElement("labelGroup");
            Name.WriteXml(writer, "id");
        }

        public XmlSchema GetSchema() { return null; }
    }
}
