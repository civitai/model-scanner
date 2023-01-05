using System.IO;

namespace ModelScanner.Tasks;

class ConvertTask : IJobTask
{
    readonly ILogger<ConvertTask> _logger;
    readonly DockerService _dockerService;
    readonly CloudStorageService _cloudStorageService;

    public ConvertTask(ILogger<ConvertTask> logger, DockerService dockerService, CloudStorageService cloudStorageService)
    {
        _logger = logger;
        _dockerService = dockerService;
        _cloudStorageService = cloudStorageService;
    }

    public JobTaskTypes TaskType => JobTaskTypes.Convert;

    public async Task<bool> Process(string filePath, ScanResult result, CancellationToken cancellationToken)
    {
        async Task ConvertAndUpload(string fromType, string targetType)
        {
            var convertedFilePath = Path.ChangeExtension(filePath, targetType);

            try
            {
                var (exitCode, output) = await _dockerService.RunCommandInDocker($"python3 /convert/{fromType}_to_{targetType}.py {DockerService.InPath} {DockerService.OutFolderPath}/{Path.GetFileName(convertedFilePath)}", filePath, cancellationToken: cancellationToken);
                if (exitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    var convertedFileInfo = new FileInfo(convertedFilePath);
                    if (!convertedFileInfo.Exists || convertedFileInfo.Length < 1024 * 1024)
                    {
                        _logger.LogWarning("Expected an acceptable conversion, got a small file... skipping conversion");

                        result.Conversions.Add(targetType, new ScanResult.Conversion(null, null, $"Expected an acceptable conversion, got a small file... skipping conversion; Container output: {output}"));
                    }
                    else
                    {
                        _logger.LogInformation("Uploading {outputFile} to cloud storage", convertedFilePath);
                        var outputFileUrl = await _cloudStorageService.ImportFile(convertedFilePath, Path.GetFileName(filePath), cancellationToken);
                        _logger.LogInformation("Uploaded {outputFile} as {outputFileUrl}", convertedFilePath, outputFileUrl);

                        var hashes = HashTask.GenerateModelHashes(convertedFilePath);

                        result.Conversions.Add(targetType, new ScanResult.Conversion(outputFileUrl, hashes, output));
                    }
                }
                else
                {
                    result.Conversions.Add(targetType, new ScanResult.Conversion(null, null, output));
                }
            }
            finally
            {
                try
                {
                    // Ensure to cleanup after ourselves
                    File.Delete(convertedFilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during cleanup of temporary file");
                }
            }
        }

        var fileExtension = Path.GetExtension(filePath);

        switch (fileExtension)
        {
            case ".ckpt":
                await ConvertAndUpload("ckpt", "safetensors");
                break;

            case ".safetensors":
                await ConvertAndUpload("safetensors", "ckpt");
                break;

            default:
                _logger.LogInformation("Skipping conversion as there is no explicit conversion defined from {type}", fileExtension);
                break;
        }

        return true;
    }
}
