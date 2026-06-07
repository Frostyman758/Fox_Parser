// Based on TcvpTool/Program.cs
using System.Text;
using System.Xml;

namespace MgsvModBldr.Tools.Tcvp;

/// <summary>
/// Façade for the Fox Engine cover-point (.tcvp) format (GZ + TPP
/// variants). Mirrors Atvaark's TcvpTool, but fixes the reference's
/// double-add bug so the round-trip is byte-exact against the original
/// game file. CLI: <c>.tcvp</c> -> <c>&lt;name&gt;.tcvp.xml</c>;
/// <c>.tcvp.xml</c> -> <c>&lt;name&gt;.tcvp</c>.
/// </summary>
public static class TcvpConverter
{
    /// <summary>Decompile a .tcvp to <c>&lt;name&gt;.tcvp.xml</c>. Returns the xml path.</summary>
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

    /// <summary>Recompile a <c>&lt;name&gt;.tcvp.xml</c> back to <c>&lt;name&gt;.tcvp</c>. Returns the tcvp path.</summary>
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
