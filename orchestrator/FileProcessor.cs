using Amazon.Runtime.Internal;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

class FileProcessor
{
    readonly ILogger<FileProcessor> _logger;
    readonly CloudStorageService _cloudStorageService;
    readonly LocalStorageOptions _localStorageOptions;

    public FileProcessor(ILogger<FileProcessor> logger, CloudStorageService cloudStorageService, IOptions<LocalStorageOptions> localStorageOptions)
    {
        _logger = logger;
        _cloudStorageService = cloudStorageService;
        _localStorageOptions = localStorageOptions.Value;
    }

    public async Task ProcessFile(string fileUrl, string callbackUrl, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_localStorageOptions.TempFolder))
        {
            Directory.CreateDirectory(_localStorageOptions.TempFolder);
        }

        var filePath = Path.Combine(_localStorageOptions.TempFolder, Path.GetFileName(fileUrl));

        if (File.Exists(filePath))
        {
            _logger.LogWarning("{filePath} already exists, generating a new random file name...", filePath);

            // Randomize a filename if it already exists
            filePath = Path.Combine(_localStorageOptions.TempFolder, Guid.NewGuid().ToString());
        }

        try
        {
            var actualFileUrl = await PrepareFileAsync(fileUrl, filePath, cancellationToken);
            if (actualFileUrl is null)
            {
                await ReportFileAsync(callbackUrl, new ScanResult
                {
                    FileExists = 0
                }, cancellationToken);
            }
            else
            {
                Debug.Assert(actualFileUrl is not null);

                var result = await ScanFileAsync(actualFileUrl, filePath, cancellationToken);
                await ReportFileAsync(actualFileUrl, result, cancellationToken);
            }
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    async Task<string?> PrepareFileAsync(string fileUrl, string filePath, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();

        try
        {
            _logger.LogInformation("Downloading {fileUrl} to {filePath}", fileUrl, filePath);

            using var fileStream = await httpClient.GetStreamAsync(fileUrl, cancellationToken);
            using (var tempStream = File.Open(filePath, FileMode.Create))
            {
                await fileStream.CopyToAsync(tempStream, cancellationToken);
            }

            if (!_cloudStorageService.IsCloudStored(fileUrl))
            {
                _logger.LogInformation("Uploading {fileUrl} to cloud storage", fileUrl);
                var actualFileUrl = await _cloudStorageService.UploadFile(filePath, Path.GetFileName(fileUrl), cancellationToken);
                _logger.LogInformation("Uploaded {fileUrl} as {actualFileUrl}", fileUrl, actualFileUrl);

                return actualFileUrl;
            }
            else
            {
                return fileUrl;
            }
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("{fileUrl} was not found, skipping...", fileUrl);

            return null;
        }
    }

    async Task<ScanResult> ScanFileAsync(string fileUrl, string filePath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Scanning {fileUrl}", fileUrl);

        var stopwatch = Stopwatch.StartNew();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker", $"run -v {filePath}:/data/model.bin --rm civitai-model-scanner /data/model.bin")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        var outputBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
            outputBuilder.Append(e.Data);
        process.ErrorDataReceived += (_, e) =>
            outputBuilder.Append(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch(TaskCanceledException)
        {
            process.Kill(); // Ensure that we abort the docker process when we cancel quickly
            throw;
        }

        var output = outputBuilder.ToString();

        _logger.LogInformation("Scan for {fileUrl} completed in {elapsed}, queuing callback...", fileUrl, stopwatch.Elapsed);
        _logger.LogInformation(output);

        var result = JsonSerializer.Deserialize<ScanResult>(output, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

        result.PicklescanGlobalImports = ParseGlobalImports(result.PicklescanOutput);
        result.PicklescanDangerousImports = ParseDangerousImports(result.PicklescanOutput);
        result.Url = fileUrl;

        return result;

        HashSet<string> ParseGlobalImports(string? picklescanOutput)
        {
            var result = new HashSet<string>();

            if (picklescanOutput is not null)
            {
                const string globalImportListsRegex = """Global imports in (?:.+): {(.+)}""";

                foreach (Match globalImportListMatch in Regex.Matches(picklescanOutput, globalImportListsRegex))
                {
                    var globalImportList = globalImportListMatch.Groups[1];
                    const string globalImportsRegex = """\((.+?)\)""";

                    foreach (Match globalImportMatch in Regex.Matches(globalImportList.Value, globalImportsRegex))
                    {
                        result.Add(globalImportMatch.Groups[1].Value);
                    }
                }
            }

            return result;
        }

        HashSet<string> ParseDangerousImports(string? picklescanOutput)
        {
            var result = new HashSet<string>();

            if (picklescanOutput is not null)
            {
                const string dangerousImportsRegex = """dangerous import '(.+)'""";
                var dangerousImportMatches = Regex.Matches(picklescanOutput, dangerousImportsRegex);

                foreach (Match dangerousImporMatch in dangerousImportMatches)
                {
                    var dangerousImport = dangerousImporMatch.Groups[1];
                    result.Add(dangerousImport.Value);
                }
            }

            return result;
        }
    }

    public async Task ReportFileAsync(string callbackUrl, ScanResult result, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();

        _logger.LogInformation("Invoking {callbackUrl} with result {result}", callbackUrl, result);
        await httpClient.PostAsJsonAsync(callbackUrl, result, cancellationToken);
    }
}
