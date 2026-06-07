// Based on MtarTool.Core/Common/ArchiveFile.cs
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Mtar.Common
{
    [XmlType]
    public abstract class ArchiveFile
    {
        [XmlAttribute("Name")]
        public string name;

        [XmlIgnore]
        public bool numberNames = false;

        public abstract void Read(Stream input);

        public abstract void Export(Stream output, string path);

        public abstract void Import(Stream output, string path);
    }
}
