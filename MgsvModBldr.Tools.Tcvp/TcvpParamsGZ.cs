// Based on TcvpTool/TcvpParamsGZ.cs (diagnostic Console output removed)
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Tcvp
{
    public class TcvpParamsGZ : IXmlSerializable
    {
        public byte A { get; set; }
        public byte B { get; set; }
        public byte C { get; set; }
        public byte D { get; set; }
        public byte E { get; set; }
        public byte F { get; set; }
        public byte G { get; set; }
        public byte H { get; set; }

        public virtual void Read(BinaryReader reader)
        {
            A = reader.ReadByte();
            B = reader.ReadByte();
            C = reader.ReadByte();
            D = reader.ReadByte();
            E = reader.ReadByte();
            F = reader.ReadByte();
            G = reader.ReadByte();
            H = reader.ReadByte();
        }

        public virtual void Write(BinaryWriter writer)
        {
            writer.Write(A);
            writer.Write(B);
            writer.Write(C);
            writer.Write(D);
            writer.Write(E);
            writer.Write(F);
            writer.Write(G);
            writer.Write(H);
        }

        public virtual void ReadXml(XmlReader reader)
        {
            A = byte.Parse(reader["a"]);
            B = byte.Parse(reader["b"]);
            C = byte.Parse(reader["c"]);
            D = byte.Parse(reader["d"]);
            E = byte.Parse(reader["e"]);
            F = byte.Parse(reader["f"]);
            G = byte.Parse(reader["g"]);
            H = byte.Parse(reader["h"]);
        }

        public virtual void WriteXml(XmlWriter writer)
        {
            writer.WriteAttributeString("a", A.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("b", B.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("c", C.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("d", D.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("e", E.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("f", F.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("g", G.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("h", H.ToString(CultureInfo.InvariantCulture));
        }

        public XmlSchema GetSchema()
        {
            return null;
        }
    }
}
