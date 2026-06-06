// Based on datfpk qar/qar.go json definition
using System.Text.Json.Serialization;

namespace MgsvModBldr.Tools.Qar;

public sealed class QarManifest
{
    [JsonPropertyName("type")]    public string Type    { get; set; } = "qar";
    [JsonPropertyName("flags")]   public uint   Flags   { get; set; }
    [JsonPropertyName("version")] public uint   Version { get; set; } = 2;
    [JsonPropertyName("entries")] public List<QarManifestEntry> Entries { get; set; } = new();
}

public sealed class QarManifestEntry
{
    [JsonPropertyName("filePath")]   public string FilePath   { get; set; } = string.Empty;
    [JsonPropertyName("compressed")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool   Compressed { get; set; }
    [JsonPropertyName("metaFlag")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool   MetaFlag   { get; set; }
    [JsonPropertyName("encryption")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public uint   Encryption { get; set; }
    [JsonPropertyName("key")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public uint   Key        { get; set; }
    [JsonPropertyName("hash")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public ulong  Hash       { get; set; }
}
