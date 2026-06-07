// Based on TcvpTool/ILocatorTcvp.cs
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Tcvp
{
    public interface ILocatorTcvp : IXmlSerializable
    {
        Vector3 Translation { get; set; }

        void Read(BinaryReader reader);
        void Write(BinaryWriter writer);
    }
}
