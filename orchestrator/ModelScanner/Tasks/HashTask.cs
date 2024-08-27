using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelScanner.Tasks;

public class HashTask : IJobTask
{
    readonly ILogger<HashTask> _logger;
    readonly CloudStorageService _cloudStorageService;

    public HashTask(ILogger<HashTask> logger, CloudStorageService cloudStorageService)
    {
        _logger = logger;
        _cloudStorageService = cloudStorageService;
    }

    public JobTaskTypes TaskType => JobTaskTypes.Hash;


    /// <summary>
    /// A helper method that 
    ///    1. Reads the metadata length from the first 8 bytes of the file
    ///    2. Tries to deserialize the metadata 
    ///    3. Calculates the SHA256 hash of the file without the metadata
    ///    4. If the hash of the file without the metadata is different from the hash in the metadata, it will update the metadata with the new hash
    /// </summary>
    async Task<string> CalculateV3HashAndEnsureEmbeddedMetadataFixedAsync(FileStream fileStream, ScanResult result)
    {
        // Get the length of the metadata
        var metadataLengthBuffer = new byte[8];
        fileStream.ReadExactly(metadataLengthBuffer);
        var metadataLength = BitConverter.ToUInt64(metadataLengthBuffer, 0);

        // Extract the metadata
        var metadataBytes = new byte[metadataLength];
        fileStream.ReadExactly(metadataBytes);

        // Hash the rest of the file
        var hashBytes = SHA256.HashData(fileStream);

        // Convert the hash to a readable string
        var hashString = BitConverter.ToString(hashBytes).Replace("-", string.Empty);

        // Deserialize the metadata
        try
        {
            var jsonObject = JsonSerializer.Deserialize<JsonObject>(metadataBytes.AsSpan().TrimEnd((byte)0));
            if (jsonObject is not null && jsonObject.TryGetPropertyValue("__metadata__", out var metadataNode))
            {
                if (metadataNode!.AsObject().TryGetPropertyValue("sshs_model_hash", out var sshsModelHashNode))
                {
                    var sshsModelHash = sshsModelHashNode?.ToString();
                    if (sshsModelHash is not null && !hashString.Equals(sshsModelHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // If the embedded hash does not match the actual hash then fix the file on the spot
                        metadataNode.AsObject()["sshs_model_hash"] = JsonValue.Create(hashString);

                        // Replace existing metadata with the updated metadata
                        using var bufferStream = new MemoryStream();
                        using (var jsonWriter = new Utf8JsonWriter(bufferStream, new JsonWriterOptions { Indented = false, SkipValidation = true }))
                        {
                            jsonObject.WriteTo(jsonWriter);
                        }

                        // Construct a new model file with the new metadata (as the new json may be of a different size, we can't reuse the existing file)
                        using var newFileStream = File.Open(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite);
                        BinaryPrimitives.WriteUInt64LittleEndian(metadataLengthBuffer, (ulong)bufferStream.Length);
                        newFileStream.Write(metadataLengthBuffer);
                        bufferStream.Position = 0;
                        await bufferStream.CopyToAsync(newFileStream);
                        fileStream.Position = (long)metadataLength + 8;
                        fileStream.CopyTo(newFileStream); // Copy the rest of the file
                        newFileStream.Flush();

                        // Replace the current file with the new file
                        fileStream.Position = 0;
                        newFileStream.Position = 0;
                        fileStream.SetLength(newFileStream.Length);
                        newFileStream.CopyTo(fileStream);
                        fileStream.Flush();

                        // Upload the file to cloud storage
                        fileStream.Position = 0;
                        var objectKey = new Uri(result.Url).AbsolutePath.TrimStart('/');
                        await _cloudStorageService.UploadFile(fileStream, objectKey, default);

                        result.Fixed ??= new HashSet<string>();
                        result.Fixed.Add("sshs_hash");
                    }
                }
            }
        }
        catch (JsonException)
        {
            // The metadata is invalid, don't bother with it
        }

        return hashString;

    }

    public async Task<Dictionary<string, string>> GenerateModelHashes(string filePath, ScanResult result)
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

        using var fileStream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var autov3HashString = await CalculateV3HashAndEnsureEmbeddedMetadataFixedAsync(fileStream, result);
        fileStream.Position = 0;

        var sha256HashString = GetHashString(SHA256.HashData(fileStream));
        fileStream.Position = 0;
        var autov1HashString = ComputeAutoV1Hash(fileStream);
        var autov2HashString = sha256HashString.Substring(0, 10);
        fileStream.Position = 0;
        var blake3HashString = GetHashString(new Blake3.Blake3HashAlgorithm().ComputeHash(fileStream));
        fileStream.Position = 0;
        var crc32HashString = ComputeCRC32Hash(fileStream);

        var hashes = new Dictionary<string, string>()
        {
            ["SHA256"] = sha256HashString,
            ["AutoV1"] = autov1HashString!,
            ["AutoV2"] = autov2HashString,
            ["AutoV3"] = autov3HashString!,
            ["Blake3"] = blake3HashString,
            ["CRC32"] = crc32HashString
        };

        return hashes;
    }

    public async Task<bool> Process(string filePath, ScanResult result, CancellationToken cancellationToken)
    {
        var hashes = await GenerateModelHashes(filePath, result);

        _logger.LogInformation("Generated {hashes} for {filePath}",
            string.Join(',', hashes),
            filePath);            

        result.Hashes = hashes;

        return true;
    }
}
