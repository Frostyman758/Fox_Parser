// .fv2 <-> xml facade
// Serialises the FULL Fv2 struct (keeps the unknown indices FvTwool's
// named export drops), so the round-trip is byte-exact vs the game file.
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Fv2;

public static class Fv2Converter
{
    public static string Unpack(string fv2Path)
    {
        var outPath = fv2Path + ".xml";

        var fv2 = new Fv2();
        fv2.Read(fv2Path);

        var serializer = new XmlSerializer(typeof(Fv2));
        var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = true };
        using (var writer = XmlWriter.Create(outPath, settings))
            serializer.Serialize(writer, fv2);
        return outPath;
    }

    public static string Pack(string xmlPath)
    {
        var outPath = xmlPath.Substring(0, xmlPath.Length - ".xml".Length);

        var serializer = new XmlSerializer(typeof(Fv2));
        Fv2 fv2;
        using (var stream = File.Open(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            fv2 = (Fv2)serializer.Deserialize(stream);

        fv2.Write(outPath);
        return outPath;
    }
}
