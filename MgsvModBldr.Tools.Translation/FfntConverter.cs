// Based on FfntTool/Program.cs
using System.Linq;
using System.Xml.Serialization;
using MgsvModBldr.Tools.Translation.Ffnt;

namespace MgsvModBldr.Tools.Translation;

public static class FfntConverter
{
    private const int MaxLayers = 8;

    private static XmlSerializer Serializer() =>
        new XmlSerializer(typeof(FfntFile), new[] { typeof(GlyphMap), typeof(FontData) });

    public static string Unpack(string ffntPath)
    {
        var dir = Path.GetDirectoryName(ffntPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(ffntPath);
        var xmlPath = ffntPath + ".xml"; // <name>.ffnt.xml

        FfntFile ffntFile;
        using (var inputStream = File.Open(ffntPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            ffntFile = FfntFile.ReadFfntFile(inputStream);

        var fontData = ffntFile.Entries.OfType<FontData>().Single();

        using (var outputStream = File.Open(xmlPath, FileMode.Create, FileAccess.Write, FileShare.None))
            Serializer().Serialize(outputStream, ffntFile);

        var (width, height) = CalculateSize(fontData.Data.Length);
        SaveFontLayers(fontData.Data, width, height, stem, dir);
        return xmlPath;
    }

    public static string Pack(string xmlPath)
    {
        var outPath = xmlPath.Substring(0, xmlPath.Length - ".xml".Length); // <name>.ffnt
        var dir = Path.GetDirectoryName(xmlPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(outPath);

        FfntFile ffntFile;
        using (var inputStream = File.Open(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            ffntFile = Serializer().Deserialize(inputStream) as FfntFile;

        ffntFile.Entries.OfType<FontData>().Single().Data = ReadFontLayers(dir, stem);

        using (var outputStream = File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
            ffntFile.Write(outputStream);
        return outPath;
    }

    // ─── Font bitmap <-> layer PNGs (reimplements Program.cs sans GDI+) ──

    private static (int width, int height) CalculateSize(int area)
    {
        if (System.Math.Sqrt(area) % 1 == 0) // square (e.g. the latin font)
        {
            int side = (int)System.Math.Sqrt(area);
            return (side, side);
        }
        if (area / 2 % 2 == 0 && System.Math.Sqrt(area / 2) % 1 == 0) // 2:1 (e.g. the kanji font)
        {
            int h = (int)System.Math.Sqrt(area / 2);
            return (2 * h, h);
        }
        throw new System.Exception("Unknown bitmap font dimensions.");
    }

    private static void SaveFontLayers(byte[] ffntData, int width, int height, string stem, string dir)
    {
        for (int i = 0; i < MaxLayers; i++)
        {
            byte[] layer = GetLayer(ffntData, i);
            if (layer != null)
                Png.WriteGrayscale8(Path.Combine(dir, $"{stem}_{i}.png"), width, height, layer);
        }
    }

    private static byte[] GetLayer(byte[] ffntData, int layerIndex)
    {
        int layerMask = 1 << layerIndex;
        byte[] layer = new byte[ffntData.Length];
        bool emptyLayer = true;
        for (int i = 0; i < ffntData.Length; i++)
        {
            if ((ffntData[i] & layerMask) > 0)
            {
                layer[i] = 0xFF;
                emptyLayer = false;
            }
        }
        return emptyLayer ? null : layer;
    }

    private static byte[] ReadFontLayers(string dir, string stem)
    {
        var layers = new List<byte[]>();
        for (int i = 0; i < MaxLayers; i++)
        {
            byte[] layer = ReadFontLayer(dir, stem, i);
            if (layer != null) layers.Add(layer);
        }
        if (layers.Count == 0) return null;

        byte[] result = new byte[layers[0].Length];
        foreach (var layer in layers)
            for (int i = 0; i < result.Length; i++)
                result[i] = (byte)(result[i] | layer[i]);
        return result;
    }

    private static byte[] ReadFontLayer(string dir, string stem, int layerIndex)
    {
        var path = Path.Combine(dir, $"{stem}_{layerIndex}.png");
        if (!File.Exists(path)) return null;
        byte layerMask = (byte)(1 << layerIndex);
        var (width, height, white) = Png.DecodeWhiteMask(path);
        var result = new byte[width * height];
        for (int i = 0; i < result.Length; i++)
            if (white[i]) result[i] = layerMask;
        return result;
    }
}
