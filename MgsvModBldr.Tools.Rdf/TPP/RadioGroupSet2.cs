// Based on RdfTool/TPP/RadioGroupSet2.cs
using System;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Collections.Generic;

namespace MgsvModBldr.Tools.Rdf
{
    public class RadioGroupSet2
    {
        public FoxHash Name { get; set; }

        public List<FoxHash> GroupNames = new List<FoxHash>();
        public void Read(BinaryReader reader, HashManager hashManager, HashIdentifiedDelegate hashIdentifiedCallback)
        {
            Name = new FoxHash();
            Name.Read(reader, hashManager.StrCode32LookupTable, hashIdentifiedCallback);

            byte count = reader.ReadByte();

            for (int i = 0; i < count; i++)
            {
                FoxHash labelName = new FoxHash();
                labelName.Read(reader, hashManager.StrCode32LookupTable, hashIdentifiedCallback);
                GroupNames.Add(labelName);
            }
        }
        public void Write(BinaryWriter writer)
        {
            Name.Write(writer);
            writer.Write((byte)GroupNames.Count);
            foreach (FoxHash labelName in GroupNames)
            {
                labelName.Write(writer);
            }
        }
        public void ReadXml(XmlReader reader)
        {
            Name = new FoxHash();
            Name.ReadXml(reader, "id");
            bool doNodeLoop = true;
            if (reader.IsEmptyElement)
                doNodeLoop = false;
            reader.ReadStartElement("groupSet2");
            while (doNodeLoop)
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        FoxHash label = new FoxHash();
                        label.ReadXml(reader, "group2Id");
                        GroupNames.Add(label);
                        reader.ReadStartElement("group2Id");
                        continue;
                    case XmlNodeType.EndElement:
                        return;
                }
        }

        public void WriteXml(XmlWriter writer)
        {
            writer.WriteStartElement("groupSet2");

            Name.WriteXml(writer, "id");

            foreach (FoxHash groupName in GroupNames)
            {
                writer.WriteStartElement("group2Id");
                groupName.WriteXml(writer, "group2Id");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        public XmlSchema GetSchema() { return null; }
    }
}
