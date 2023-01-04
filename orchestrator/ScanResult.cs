using System.Globalization;

record ScanResult {
    public record Conversion(string Url, Dictionary<string, string> Hashes);

    public string? Url { get; set; }
    public int FileExists { get; set; }
    public int PicklescanExitCode { get; set; }
    public string? PicklescanOutput { get; set; }
    public HashSet<string>? PicklescanGlobalImports { get; set; }
    public HashSet<string>? PicklescanDangerousImports { get; set; }
    public Dictionary<string, string> Conversions { get; set; } = new();
    public Dictionary<string,  string> Hashes { get; set; } = new();
    public int ClamscanExitCode { get; set; }
    public string? ClamscanOutput { get; set; }
}
