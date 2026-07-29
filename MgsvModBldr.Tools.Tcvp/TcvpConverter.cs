// Based on TcvpTool/Program.cs
using System.Text;
using System.Xml;

namespace MgsvModBldr.Tools.Tcvp;

public static class TcvpConverter
{
    public static string Unpack(string tcvpPath)
    {
        var outPath = tcvpPath + ".xml";

        var tcvp = new TcvpFile();
        using (var reader = new BinaryReader(File.Open(tcvpPath, FileMode.Open, FileAccess.Read, FileShare.Read)))
            tcvp.Read(reader);

        var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = true };
        using (var writer = XmlWriter.Create(outPath, settings))
            tcvp.WriteXml(writer);
        return outPath;
    }

    public static string Pack(string xmlPath)
    {
        var outPath = xmlPath.Substring(0, xmlPath.Length - ".xml".Length);

        var settings = new XmlReaderSettings { IgnoreWhitespace = true };
        var tcvp = new TcvpFile();
        using (var reader = XmlReader.Create(xmlPath, settings))
            tcvp.ReadXml(reader);

        using (var writer = new BinaryWriter(File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.None)))
            tcvp.Write(writer);
        return outPath;
    }
}
