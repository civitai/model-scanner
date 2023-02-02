using Microsoft.Extensions.Options;

namespace ModelScanner.Tasks;

class ImportTask : IJobTask
{
    readonly ILogger<ImportTask> _logger;
    readonly CloudStorageService _cloudStorageService;

    public ImportTask(ILogger<ImportTask> logger, CloudStorageService cloudStorageService)
    {
        _logger = logger;
        _cloudStorageService = cloudStorageService;
    }

    public JobTaskTypes TaskType => JobTaskTypes.Import;

    public async Task<bool> Process(string filePath, ScanResult result, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();

        try
        {
            var currentBucket = _cloudStorageService.GetCloudStoredBucketName(result.Url);
            if (currentBucket != _cloudStorageService.Options.UploadBucket)
            {
                if (currentBucket is null)
                {
                    _logger.LogInformation("Importing {fileUrl} to cloud storage", result.Url);
                    var importedUrl = await _cloudStorageService.ImportFile(filePath, Path.GetFileName(result.Url), cancellationToken);
                    _logger.LogInformation("Importing {fileUrl} as {actualFileUrl}", result.Url, importedUrl);

                    result.Url = importedUrl;
                }
                else
                {
                    var fileSize = new FileInfo(filePath).Length;
                    var objectKey = new Uri(result.Url).AbsolutePath.TrimStart('/');

                    // We're going to straight upload small files while copying over larger files
                    // This is as a workaround to R2 storage not allowing direct copying and failing on small multipart copies
                    if (fileSize <= 100 * 1024 * 1024)
                    {
                        _logger.LogInformation("Uploading {fileUrl} to cloud storage", result.Url);
                        var uploadedUrl = await _cloudStorageService.UploadFile(filePath, objectKey, cancellationToken);
                        _logger.LogInformation("Uploading {fileUrl} as {actualFileUrl}", result.Url, uploadedUrl);

                        result.Url = uploadedUrl;
                    }
                    else
                    {
                        _logger.LogInformation("Copying {fileUrl} to cloud storage", result.Url);
                        var copiedUrl = await _cloudStorageService.CopyFile(currentBucket, objectKey, cancellationToken);
                        _logger.LogInformation("Copying {fileUrl} as {actualFileUrl}", result.Url, copiedUrl);

                        result.Url = copiedUrl;
                    }
                }
            }
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("{fileUrl} was not found, skipping...", result.Url);
            return false;
        }

        return true;
    }
}
