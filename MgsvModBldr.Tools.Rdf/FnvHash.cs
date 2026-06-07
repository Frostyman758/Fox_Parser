// Based on RdfTool/FnvHash.cs
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace MgsvModBldr.Tools.Rdf
{
    public class FnvHash
    {
        public uint HashValue;
        public string StringLiteral = string.Empty;
        public bool IsStringKnown => !string.IsNullOrEmpty(StringLiteral);

        public virtual void Read(BinaryReader reader, Dictionary<uint, string> hashLookupTable, HashIdentifiedDelegate hashIdentifiedCallback)
        {
            HashValue = reader.ReadUInt32();

            if (hashLookupTable.ContainsKey(HashValue))
            {
                StringLiteral = hashLookupTable[HashValue];
                hashIdentifiedCallback.Invoke(HashValue, StringLiteral);
            }
        }

        public virtual void Write(BinaryWriter writer)
        {
            writer.Write(HashValue);
        }

        public void ReadXml(XmlReader reader, string label)
        {
            string value = reader[label].ToLower();

            if (uint.TryParse(value, out uint maybeHash))
            {
                HashValue = maybeHash;
            }
            else
            {
                StringLiteral = value;
                HashValue = HashManager.FNV1Hash32Str(StringLiteral);
            }
        }

        public void WriteXml(XmlWriter writer, string label)
        {
            if (IsStringKnown)
            {
                writer.WriteAttributeString(label, StringLiteral.ToLower());
            }
            else
            {
                writer.WriteAttributeString(label, HashValue.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
