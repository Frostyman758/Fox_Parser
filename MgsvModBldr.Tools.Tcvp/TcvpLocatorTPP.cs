// Based on TcvpTool/TcvpLocatorTPP.cs
using System.IO;
using System.Xml;
using System.Xml.Schema;

namespace MgsvModBldr.Tools.Tcvp
{
    public class TcvpLocatorTPP : ILocatorTcvp
    {
        public Vector3 Translation { get; set; }
        public TcvpParamsTPP Params { get; set; }

        public void Read(BinaryReader reader)
        {
            Translation = new Vector3();
            Translation.Read(reader);

            Params = new TcvpParamsTPP();
            Params.Read(reader);
        }

        public void Write(BinaryWriter writer)
        {
            Translation.Write(writer);
            Params.Write(writer);
        }

        public void ReadXml(XmlReader reader)
        {
            reader.ReadStartElement("locator");

            Translation = new Vector3();
            Translation.ReadXml(reader);

            reader.Read();

            Params = new TcvpParamsTPP();
            Params.ReadXml(reader);

            reader.Read();
        }

        public void WriteXml(XmlWriter writer)
        {
            writer.WriteStartElement("translation");
            Translation.WriteXml(writer);
            writer.WriteEndElement();

            writer.WriteStartElement("parameters");
            Params.WriteXml(writer);
            writer.WriteEndElement();
        }

        public XmlSchema GetSchema()
        {
            return null;
        }
    }
}
