// Based on TcvpTool/TcvpParamsTPP.cs (diagnostic Console output removed)
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Tcvp
{
    public class TcvpParamsTPP : IXmlSerializable
    {
        public ushort A { get; set; }
        public ushort B { get; set; }
        public ushort C { get; set; }
        public ushort D { get; set; }

        public virtual void Read(BinaryReader reader)
        {
            A = reader.ReadUInt16();
            B = reader.ReadUInt16();
            C = reader.ReadUInt16();
            D = reader.ReadUInt16();
        }

        public virtual void Write(BinaryWriter writer)
        {
            writer.Write(A);
            writer.Write(B);
            writer.Write(C);
            writer.Write(D);
        }

        public virtual void ReadXml(XmlReader reader)
        {
            A = ushort.Parse(reader["a"]);
            B = ushort.Parse(reader["b"]);
            C = ushort.Parse(reader["c"]);
            D = ushort.Parse(reader["d"]);
        }

        public virtual void WriteXml(XmlWriter writer)
        {
            writer.WriteAttributeString("a", A.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("b", B.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("c", C.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("d", D.ToString(CultureInfo.InvariantCulture));
        }

        public XmlSchema GetSchema()
        {
            return null;
        }
    }
}
