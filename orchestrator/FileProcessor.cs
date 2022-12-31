using Amazon.Runtime.Internal;
using Amazon.S3.Model.Internal.MarshallTransformations;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using ModelScanner;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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

        var fileUri = new Uri(fileUrl);
        var filePath = Path.Combine(_localStorageOptions.TempFolder, Path.GetFileName(fileUri.LocalPath).SafeFilename());

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
                await ReportFileAsync(callbackUrl, result, cancellationToken);
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
        const string inPath = "/data/model.bin";
        const string outPath = "/data/out/";

        var fileExtension = fileUrl.UrlFileExtension();
        var result = new ScanResult { Url = fileUrl, FileExists = 1 };

        await RunClamScan(result);
        await RunPickleScan(result);
        //await RunConversionAsync(result);
        await RunModelHasing(result);

        return result;

        // TODO Model Conversion: Test locally and ensure we can run on low RAM
        // TODO Model Conversion: Create conversion scripts
        // TODO Model Conversion: Update endpoint to handle conversions
        async Task RunConversionAsync(ScanResult result)
        {
            async Task ConvertAndUpload(string targetType)
            {
                var (exitCode, output) = await RunCommandInDocker($"python convert/${fileExtension}-to-${targetType}.py -i {inPath} -o {outPath}");
                if(exitCode == 0 && !string.IsNullOrEmpty(output)) {
                    var outputFile = Path.Combine(_localStorageOptions.TempFolder, "out", Path.GetFileName(output));
                    _logger.LogInformation("Uploading {outputFile} to cloud storage", outputFile);
                    var outputFileUrl = await _cloudStorageService.UploadFile(outputFile, Path.GetFileName(fileUrl), cancellationToken);
                    _logger.LogInformation("Uploaded {outputFile} as {outputFileUrl}", outputFile, outputFileUrl);
                    result.Conversions.Add(targetType, outputFileUrl);
                }

            }

            if (fileExtension == "safetensors") await ConvertAndUpload("pickletensor");
            else if (fileExtension == "ckpt") await ConvertAndUpload("safetensor");
        }

        // TODO Model Hash: Test locally and ensure we can run on low RAM
        // TODO Model Hash: Create hashing scripts (comma deliminate)
        // TODO Model Conversion: Update endpoint to handle hashes
        async Task RunModelHasing(ScanResult result)
        {
            // A helper method so that we can use stackalloc
            string ComputeAutoV1Hash(Stream fileStream)
            {
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

            result.Hashes["SHA256"] = sha256HashString;
            result.Hashes["AutoV1"] = autov1HashString;
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

            // Setup directory for writing files when needed
            var outDir = Path.Combine(_localStorageOptions.TempFolder, "out");
            Directory.CreateDirectory(outDir);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo("docker", $"run -v {Path.GetFullPath(filePath)}:{inPath} -v {Path.GetFullPath(outDir)}:{outPath} --rm civitai-model-scanner {command}")
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
