// Based on TcvpTool/TcvpFile.cs (diagnostic Console output removed)
// BUGFIX vs the reference: TcvpTool 0.6 added each locator to the list
// TWICE in Read (Locators.Add before AND after locator.Read), which
// doubled the locator count and corrupted every round-trip. Fixed here
// to a single add, so unpack->repack is byte-exact against the original
// game file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Tcvp
{
    public enum Version
    {
        GZ = 0,
        TPP = 1
    }

    public class TcvpFile : IXmlSerializable
    {
        public Version version;
        public List<ILocatorTcvp> Locators = new List<ILocatorTcvp>();

        public void Read(BinaryReader reader)
        {
            // Read header
            char[] signature = reader.ReadChars(4); //TCVP string
            if (new string(signature) != "TCVP")
            {
                throw new ArgumentOutOfRangeException();
            }
            version = (Version) reader.ReadUInt16();
            ushort locatorCount = reader.ReadUInt16();
            reader.ReadUInt32(); // something/12

            // Read locators
            for (int i = 0; i < locatorCount; i++)
            {
                switch (version)
                {
                    case Version.GZ:
                        ILocatorTcvp locatorGZ = new TcvpLocatorGZ();
                        locatorGZ.Read(reader);
                        Locators.Add(locatorGZ);
                        break;
                    case Version.TPP:
                        ILocatorTcvp locatorTPP = new TcvpLocatorTPP();
                        locatorTPP.Read(reader);
                        Locators.Add(locatorTPP);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public void Write(BinaryWriter writer)
        {
            // Write header
            writer.Write('T'); writer.Write('C'); writer.Write('V'); writer.Write('P');
            writer.Write((short)version);
            writer.Write((ushort)Locators.Count);
            writer.Write(12);

            // Write locators
            foreach (var locator in Locators)
            {
                locator.Write(writer);
            }
        }

        public void ReadXml(XmlReader reader)
        {
            reader.Read();
            reader.Read();

            version = (Version)short.Parse(reader["version"]);

            reader.ReadStartElement("tcvp");
            while (2 > 1)
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        ILocatorTcvp newLocator = CreateLocator();
                        newLocator.ReadXml(reader);
                        Locators.Add(newLocator);
                        reader.ReadEndElement();
                        continue;
                    case XmlNodeType.EndElement:
                        return;
                }
            }
        }

        public void WriteXml(XmlWriter writer)
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("tcvp");
            writer.WriteAttributeString("version", ((short)version).ToString());

            foreach (ILocatorTcvp locator in Locators)
            {
                writer.WriteStartElement("locator");
                locator.WriteXml(writer);
                writer.WriteEndElement();
            }
            writer.WriteEndDocument();
        }

        ILocatorTcvp CreateLocator()
        {
            switch (version)
            {
                case Version.GZ:
                    return new TcvpLocatorGZ();
                case Version.TPP:
                    return new TcvpLocatorTPP();
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public XmlSchema GetSchema()
        {
            return null;
        }
    }
}
