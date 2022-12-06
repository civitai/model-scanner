using System.Text.RegularExpressions;

namespace ModelScanner;
public static class StringExtensions
{
    public static string UrlFileExtension(this string url)
    {
        url = url.Split('?')[0];
        url = url.Split('/').Last();
        return url.Contains('.') ? url.Substring(url.LastIndexOf('.')+1).ToLower() : "";
    }

    public static string SafeFilename(this string filename)
    {
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(filename), @"[^a-z0-9]", "_").Truncate(20);
        var ext = Path.GetExtension(filename);
        return name + ext;
    }

    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}
