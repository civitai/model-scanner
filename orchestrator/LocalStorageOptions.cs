using System.ComponentModel.DataAnnotations;

class LocalStorageOptions
{
    [Required]
    public string TempFolder { get; set; } = default!;
}