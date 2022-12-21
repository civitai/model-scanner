using System.ComponentModel.DataAnnotations;

namespace ModelScanner.CleanupStorage;

public class CleanupStorageOptions
{
    /// <summary>
    /// Get or set the minimum age since last modification to be considered for cleanup
    /// </summary>
    [Required]
    public TimeSpan CutoffInterval { get; set; } = TimeSpan.FromHours(24);
}
