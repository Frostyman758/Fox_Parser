// Based on fsop_tool.py metadata.json schema
using System.Text.Json.Serialization;

namespace MgsvModBldr.Tools.Fsop;

public sealed class FsopMetadata
{
    [JsonPropertyName("shaders")]
    public List<FsopShaderEntry> Shaders { get; set; } = new();

    [JsonPropertyName("_info")]
    public string? Info { get; set; }
}

public sealed class FsopShaderEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = "shift-jis";

    [JsonPropertyName("vertex_shader_file")]
    public string VertexShaderFile { get; set; } = "";

    [JsonPropertyName("pixel_shader_file")]
    public string PixelShaderFile { get; set; } = "";
}
