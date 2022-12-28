using System.ComponentModel.DataAnnotations;

class LocalStorageOptions
{
    [Required]
    public string TempFolder { get; set; } = default!;

    /// <summary>
    /// Get or set whether existing files can be trusted 
    /// In production we want to ensure that we always invalidate (never trust local files).
    /// While developing, it can be useful to trust local files as it can prevent unnesserary downloads
    /// </summary>
    public bool AlwaysInvalidate { get; set; } = true;
}