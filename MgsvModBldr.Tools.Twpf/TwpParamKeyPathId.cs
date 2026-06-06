// Based on TwpfTool/TwpParamKeyPathId.cs
using System;
using System.IO;
using System.Xml.Serialization;
using CityHash = MgsvModBldr.Core.CityHash64;

namespace MgsvModBldr.Tools.Twpf
{
    [XmlType]
    public class TwpParamKeyPathId : TwpParamKey
    {
        [XmlAttribute]
        public string pathId;
        public new void Read(BinaryReader reader)
        {
            base.Read(reader);
            pathId = reader.ReadUInt64().ToString();
            if (TwpfLog.IsVerbose)
                Console.WriteLine($"Time: {base.time}, Value: {pathId}");
        }
        public new void Write(BinaryWriter writer)
        {
            base.Write(writer);
            if (ulong.TryParse(pathId, out ulong pathIdHash))
                writer.Write(pathIdHash);
            else
                writer.Write(HashFileNameWithExtension(pathId));
        }

        private const string ASSETS_CONST = "/Assets/";
        private const ulong MetaFlag = 0x4000000000000;
        private static ulong HashFileName(string text, bool removeExtension = true)
        {
            if (removeExtension)
            {
                int index = text.IndexOf('.');
                text = index == -1 ? text : text.Substring(0, index);
            }

            bool metaFlag = false;
            if (text.StartsWith(ASSETS_CONST))
            {
                text = text.Substring(ASSETS_CONST.Length);

                if (text.StartsWith("tpptest"))
                    metaFlag = true;
            }
            else
                metaFlag = true;

            text = text.TrimStart('/');

            const ulong seed0 = 0x9ae16a3b2f90404f;
            byte[] seed1Bytes = new byte[sizeof(ulong)];
            for (int i = text.Length - 1, j = 0; i >= 0 && j < sizeof(ulong); i--, j++)
                seed1Bytes[j] = Convert.ToByte(text[i]);

            ulong seed1 = BitConverter.ToUInt64(seed1Bytes, 0);
            ulong maskedHash = CityHash.CityHash64WithSeeds(text, seed0, seed1) & 0x3FFFFFFFFFFFF;

            return metaFlag
                ? maskedHash | MetaFlag
                : maskedHash;
        }
        public static string DenormalizeFilePath(string filePath)
        {
            return filePath.Replace("\\", "/");
        }
        public static ulong HashFileNameWithExtension(string filePath)
        {
            filePath = DenormalizeFilePath(filePath);
            string hashablePart;
            string extensionPart;
            int extensionIndex = filePath.IndexOf(".", StringComparison.Ordinal);
            if (extensionIndex == -1)
            {
                hashablePart = filePath;
                extensionPart = "";
            }
            else
            {
                hashablePart = filePath.Substring(0, extensionIndex);
                extensionPart = filePath.Substring(extensionIndex + 1, filePath.Length - extensionIndex - 1);
            }

            ulong typeId = HashFileName(extensionPart, false) & 0x1FFF;
            ulong hash = HashFileName(hashablePart);
            hash = (typeId << 51) | hash;
            return hash;
        }
    }
}
