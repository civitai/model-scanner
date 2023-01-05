using System.Security.Cryptography;

namespace ModelScanner.Tasks;

class HashTask : IJobTask
{
    public JobTaskTypes TaskType => JobTaskTypes.Hash;

    public static Dictionary<string, string> GenerateModelHashes(string filePath)
    {
        // A helper method so that we can use stackalloc
        string? ComputeAutoV1Hash(Stream fileStream)
        {
            const int minFileSize = 0x100000 * 2;
            if (fileStream.Length < minFileSize)
            {
                // We're unable to compute auto v1 hashes for files that have fewer than the required number of bytes availablbe.
                return null;
            }

            fileStream.Seek(0x100000, SeekOrigin.Begin);
            Span<byte> buffer = stackalloc byte[0x10000];
            fileStream.ReadExactly(buffer);

            var hashBytes = SHA256.HashData(buffer);
            var hash = BitConverter.ToString(hashBytes).Replace("-", string.Empty);

            var shortHash = hash.Substring(0, 8);

            return shortHash;
        }

        SHA256 sha256 = SHA256.Create();

        using var fileStream = File.OpenRead(filePath);
        var sha256HashBytes = SHA256.HashData(fileStream);
        var sha256HashString = BitConverter.ToString(sha256HashBytes).Replace("-", string.Empty);

        var autov1HashString = ComputeAutoV1Hash(fileStream);

        var result = new Dictionary<string, string>();

        result["SHA256"] = sha256HashString;
        if (autov1HashString is not null)
        {
            result["AutoV1"] = autov1HashString;
        }

        return result;
    }

    public Task<bool> Process(string filePath, ScanResult result, CancellationToken cancellationToken)
    {
        var hashes = GenerateModelHashes(filePath);

        result.Hashes = hashes;

        return Task.FromResult(true);
    }
}
