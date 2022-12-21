using System.ComponentModel.DataAnnotations;

namespace ModelScanner.FileProcessor;

class LocalStorageOptions
{
    [Required]
    public string TempFolder { get; set; } = default!;
}