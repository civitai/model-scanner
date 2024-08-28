using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

public record ScanResult {
    public record Conversion(string? Url, Dictionary<string, string>? Hashes, string ConversionOutput)
    {
        [JsonPropertyName("sizeKB")]
        public double? SizeKB { get; set; }
    }
        

    public required string Url { get; set; }
    public int FileExists { get; set; }
    public int PicklescanExitCode { get; set; }
    public string? PicklescanOutput { get; set; }
    public HashSet<string>? PicklescanGlobalImports { get; set; }
    public HashSet<string>? PicklescanDangerousImports { get; set; }
    public Dictionary<string, Conversion> Conversions { get; set; } = new();
    public Dictionary<string, string> Hashes { get; set; } = new();
    public JsonDocument? Metadata { get; set; }
    public int ClamscanExitCode { get; set; }
    public string? ClamscanOutput { get; set; }
    public HashSet<string>? Fixed { get; set; }
}
