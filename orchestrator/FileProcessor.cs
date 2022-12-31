using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

        string actualFileUrl, localFilePath = null;

        try
        {
            (actualFileUrl, localFilePath) = await PrepareFileAsync(fileUrl, cancellationToken);
            if (actualFileUrl is null)
            {
                await ReportFileAsync(callbackUrl, new ScanResult
                {
                    FileExists = 0
                }, cancellationToken);
            }
            else
            {
                Debug.Assert(localFilePath is not null);

                // TODO: Report new file location

                var result = await ScanFileAsync(actualFileUrl, localFilePath, cancellationToken);
                await ReportFileAsync(callbackUrl, result, cancellationToken);
            }
        }
        finally
        {
            if (localFilePath is not null)
            {
                File.Delete(localFilePath);
            }
        }
    }

    async Task<(string? actualFileUrl, string? localFilePath)> PrepareFileAsync(string fileUrl, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();

        try
        {
            _logger.LogInformation("Downloading {fileUrl}", fileUrl);

            using var response = await httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? Path.GetFileName(new Uri(fileUrl).AbsolutePath);
            var filePath = Path.Combine(_localStorageOptions.TempFolder, fileName);

            if (!Path.Exists(filePath) || _localStorageOptions.AlwaysInvalidate)
            {
                using (var tempStream = File.Open(filePath, FileMode.Create))
                {
                    _logger.LogInformation("Temporary storage: {filePath}", filePath);

                    var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await responseStream.CopyToAsync(tempStream, cancellationToken);
                }
            }

            if (!_cloudStorageService.IsCloudStored(fileUrl))
            {
                _logger.LogInformation("Uploading {fileUrl} to cloud storage", fileUrl);
                // TODO Koen: Preserve filename/folder structure
                var actualFileUrl = await _cloudStorageService.ImportFile(filePath, Path.GetFileName(fileUrl), cancellationToken);
                _logger.LogInformation("Uploaded {fileUrl} as {actualFileUrl}", fileUrl, actualFileUrl);

                return (actualFileUrl, filePath);
            }
            else
            {
                return (fileUrl, filePath);
            }
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("{fileUrl} was not found, skipping...", fileUrl);

            return (null, null);
        }
    }

    async Task<ScanResult> ScanFileAsync(string fileUrl, string filePath, CancellationToken cancellationToken)
    {
        const string inPath = "/data/model.in";
        const string outPath = "/data/";

        var fileExtension = Path.GetExtension(filePath);
        var result = new ScanResult { Url = fileUrl, FileExists = 1 };

        await RunClamScan(result);
        await RunPickleScan(result);
        // TODO: Run this as a dedicated job with a unique endpoint
        await RunConversion(result);
        await RunModelHashing(result);

        return result;

        // TODO Model Conversion: Test locally and ensure we can run on low RAM
        // TODO Model Conversion: Update endpoint to handle conversions
        async Task RunConversion(ScanResult result)
        {
            async Task ConvertAndUpload(string fromType, string targetType)
            {
                var convertedFilePath = Path.ChangeExtension(filePath, targetType);

                try
                {
                    var (exitCode, output) = await RunCommandInDocker($"python3 /convert/{fromType}_to_{targetType}.py {inPath} {outPath}/{Path.GetFileName(convertedFilePath)}");
                    if (exitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        var convertedFileInfo = new FileInfo(convertedFilePath);
                        if (!convertedFileInfo.Exists || convertedFileInfo.Length < 1024 * 1024)
                        {
                            _logger.LogWarning("Expected an acceptable conversion, got a small file... skipping conversion");
                        }
                        else
                        {
                            _logger.LogInformation("Uploading {outputFile} to cloud storage", convertedFilePath);
                            var outputFileUrl = await _cloudStorageService.ImportFile(convertedFilePath, Path.GetFileName(fileUrl), cancellationToken);
                            _logger.LogInformation("Uploaded {outputFile} as {outputFileUrl}", convertedFilePath, outputFileUrl);

                            var convertedFileHash = await GenerateSHA256Hash(convertedFilePath);

                            result.Conversions.Add(targetType, new ScanResult.Conversion(outputFileUrl, new Dictionary<string, string>
                            {
                                { "SHA256", convertedFileHash }
                            }));
                        }
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
        }

        // TODO Model Hash: Test locally and ensure we can run on low RAM
        // TODO Model Conversion: Update endpoint to handle hashes
        async Task RunModelHashing(ScanResult result)
        {
            var hash = await GenerateSHA256Hash(filePath);
            
            result.Hashes.Add("SHA256", hash);
        }

        async Task<string> GenerateSHA256Hash(string filePath)
        {
            using var fileStream = File.OpenRead(filePath);

            var hasher = SHA256.Create();
            Stream openfilestream = File.OpenRead(filePath);
            var bytehash = await hasher.ComputeHashAsync(openfilestream, cancellationToken);

            return BitConverter.ToString(bytehash).Replace("-", "");
        }

        async Task RunPickleScan(ScanResult result)
        {
            // safetensors are safe...
            if (fileExtension == "safetensors")
            {
                result.PicklescanExitCode = 0;
                result.PicklescanOutput = "safetensors";
                // TODO Improve Pickle Scan: It probably makes sense to verify that this is indeed a safetensor file
                return;
            }

            var (exitCode, output) = await RunCommandInDocker($"picklescan -p {inPath} -l DEBUG");

            result.PicklescanExitCode = exitCode;
            result.PicklescanOutput = output;
            result.PicklescanGlobalImports = ParseGlobalImports(output);
            result.PicklescanDangerousImports = ParseDangerousImports(output);

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

        async Task RunClamScan(ScanResult result)
        {
            var (exitCode, output) = await RunCommandInDocker($"clamscan {inPath}");

            result.ClamscanExitCode = exitCode;
            result.ClamscanOutput = output;
        }

        async Task<(int exitCode, string output)> RunCommandInDocker(string command)
        {
            _logger.LogInformation("Executing {command} for file {fileUrl}", command, fileUrl);

            var stopwatch = Stopwatch.StartNew();

            var process = new Process
            {
                // TODO: Double check perf time when constrained to 1cpu and 1024mb mem
                StartInfo = new ProcessStartInfo("docker", $"run -v {Path.GetFullPath(filePath)}:{inPath} -v {Path.GetFullPath(_localStorageOptions.TempFolder)}:{outPath} --rm civitai-model-scanner {command}")
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
            catch (TaskCanceledException)
            {
                process.Kill(); // Ensure that we abort the docker process when we cancel quickly
                throw;
            }

            var output = outputBuilder.ToString();

            _logger.LogInformation("Executed {command} for file {fileUrl} completed with exit code {exitCode} in {elapsed}", command, fileUrl, process.ExitCode, stopwatch.Elapsed);
            _logger.LogInformation(output);

            return (process.ExitCode, output);
        }
    }

    public async Task ReportFileAsync(string callbackUrl, ScanResult result, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();

        _logger.LogInformation("Invoking {callbackUrl} with result {result}", callbackUrl, result);
        await httpClient.PostAsJsonAsync(callbackUrl, result, cancellationToken);
    }
}
