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
            if (!_cloudStorageService.IsCloudStored(result.Url))
            {
                _logger.LogInformation("Uploading {fileUrl} to cloud storage", result.Url);
                // TODO Koen: Preserve filename/folder structure
                var importedUrl = await _cloudStorageService.ImportFile(filePath, Path.GetFileName(result.Url), cancellationToken);
                _logger.LogInformation("Uploaded {fileUrl} as {actualFileUrl}", result.Url, importedUrl);

                result.Url = importedUrl;
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
