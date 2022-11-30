class ScanResult {
    public string? Url { get; set; }
    public int FileExists { get; set; }
    public int PicklescanExitCode { get; set; }
    public string? PicklescanOutput { get; set; }
    public HashSet<string>? PicklescanGlobalImports { get; set; }
    public HashSet<string>? PicklescanDangerousImports { get; set; }
    public int ClamscanExitCode { get; set; }
    public string? ClamscanOutput { get; set; }
}
