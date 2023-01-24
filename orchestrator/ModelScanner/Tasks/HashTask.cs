using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace ModelScanner.Tasks;

public class HashTask : IJobTask
{
    public JobTaskTypes TaskType => JobTaskTypes.Hash;

    public static Dictionary<string, string> GenerateModelHashes(string filePath)
    {
        string GetHashString(byte[] hashBytes) 
            => BitConverter.ToString(hashBytes).Replace("-", string.Empty);

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
            var hash = GetHashString(hashBytes);
            
            var shortHash = hash.Substring(0, 8);

            return shortHash;
        }

        string ComputeCRC32Hash(Stream fileStream)
        {
            var crc32 = new Crc32();
            crc32.Append(fileStream);
            return GetHashString(crc32.GetCurrentHash());
        }

        SHA256 sha256 = SHA256.Create();

        using var fileStream = File.OpenRead(filePath);
        var sha256HashString = GetHashString(SHA256.HashData(fileStream));
        fileStream.Position = 0;
        var autov1HashString = ComputeAutoV1Hash(fileStream);
        var autov2HashString = sha256HashString.Substring(0, 10);
        fileStream.Position = 0;
        var blake3HashString = GetHashString(new Blake3.Blake3HashAlgorithm().ComputeHash(fileStream));
        fileStream.Position = 0;
        var crc32HashString = ComputeCRC32Hash(fileStream);

        var result = new Dictionary<string, string>()
        {
            ["SHA256"] = sha256HashString,
            ["AutoV1"] = autov1HashString!,
            ["AutoV2"] = autov2HashString,
            ["Blake3"] = blake3HashString,
            ["CRC32"] = crc32HashString
        };

        return result;
    }

    public Task<bool> Process(string filePath, ScanResult result, CancellationToken cancellationToken)
    {
        var hashes = GenerateModelHashes(filePath);

        result.Hashes = hashes;

        return Task.FromResult(true);
    }
}
