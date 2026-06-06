// Based on LangTool/Lang/LangEntry.cs
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Translation.Lang
{
    [XmlType("Entry")]
    public class LangEntry
    {
        [XmlAttribute]
        public uint Key { get; set; }

        [XmlAttribute]
        public string LangId { get; set; }

        [XmlIgnore]
        public int Offset { get; set; }

        [XmlAttribute]
        public short Color { get; set; }

        [XmlAttribute]
        public string Value { get; set; }

        public bool ShouldSerializeKey()
        {
            return string.IsNullOrEmpty(LangId);
        }

        public void UpdateKey()
        {
            if (!string.IsNullOrEmpty(LangId))
            {
                Key = Fox.GetStrCode32(LangId);
            }
        }
    }
}
