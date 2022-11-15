using Microsoft.AspNetCore.Http.HttpResults;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CloudStorageService>();
builder.Services.AddOptions<CloudStorageOptions>()
    .BindConfiguration(nameof(CloudStorageOptions))
    .ValidateDataAnnotations();

var app = builder.Build();

var downloadQueueChannel = Channel.CreateUnbounded<(string fileUrl, string callbackUrl)>();
var scannerQueueChannel = Channel.CreateUnbounded<(string fileUrl, string filePath, string callbackUrl)>();
var callbackQueueChannel = Channel.CreateUnbounded<(string callbackUrl, ScanResult result)>();

bool isDownloading = false;
bool isScanning = false;
bool isInvokingCallback = false;

app.MapPost("/enqueue", async (string fileUrl, string callbackUrl) =>
{
    await downloadQueueChannel.Writer.WriteAsync((fileUrl, callbackUrl));
});

app.MapGet("/", () => new
{
    IsDownloading = isDownloading,
    DownloadQueueSize = downloadQueueChannel.Reader.Count,
    IsScanning = isScanning,
    ScannerQueueSize = scannerQueueChannel.Reader.Count,
    IsInvokingCallback = isInvokingCallback,
    CallbackQueueSize = callbackQueueChannel.Reader.Count,
    Version = 1
});

var downloadTask = Task.Run(async () =>
{
    var cloudStorageService = app.Services.GetRequiredService<CloudStorageService>();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Downloader");
    logger.LogInformation("Observing the queue...");

    await foreach (var (fileUrl, callbackUrl) in downloadQueueChannel.Reader.ReadAllAsync())
    {
        isDownloading = true;
        
        try
        {
            logger.LogInformation("Downloading {fileUrl}", fileUrl);

            using var httpClient = new HttpClient();
            using var fileStream = await httpClient.GetStreamAsync(fileUrl);
            var filePath = Path.GetTempFileName();
            using (var tempStream = File.Open(filePath, FileMode.Create))
            {
                await fileStream.CopyToAsync(tempStream);
            }

            if (!cloudStorageService.IsCloudStored(fileUrl))
            {
                logger.LogInformation("Uploading {fileUrl} to cloud storage", fileUrl);
                var actualFileUrl = await cloudStorageService.UploadFile(filePath, Path.GetFileName(fileUrl));
                logger.LogInformation("Uploaded {fileUrl} as {actualFileUrl}", fileUrl, actualFileUrl);
                await scannerQueueChannel.Writer.WriteAsync((actualFileUrl, filePath, callbackUrl));
            }
            else
            {
                await scannerQueueChannel.Writer.WriteAsync((fileUrl, filePath, callbackUrl));
            }
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                await callbackQueueChannel.Writer.WriteAsync((callbackUrl, new ScanResult
                {
                    Url = fileUrl,
                    FileExists = 0
                }));
            }
        }
        finally
        {
            isDownloading = false;
        }
    }
});

var scannerTask = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Scanner");
    logger.LogInformation("Observing the queue...");

    await foreach (var (fileUrl, filePath, callbackUrl) in scannerQueueChannel.Reader.ReadAllAsync())
    {
        try
        {
            isScanning = true;

            logger.LogInformation("Scanning {fileUrl}", fileUrl);

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

            await process.WaitForExitAsync();
            var output = outputBuilder.ToString();

            logger.LogInformation("Scan for {fileUrl} completed in {elapsed}, queuing callback...", fileUrl, stopwatch.Elapsed);
            logger.LogInformation(output);

            var result = JsonSerializer.Deserialize<ScanResult>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;

            result.PicklescanGlobalImports = ParseGlobalImports(result.PicklescanOutput);
            result.PicklescanDangerousImports = ParseDangerousImports(result.PicklescanOutput);
            result.Url = fileUrl;

            await callbackQueueChannel.Writer.WriteAsync((callbackUrl, result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected exception during processing");
        }
        finally
        {
            isScanning = false;
            File.Delete(filePath); // Ensure that our tmep file is deleted as we do no longer need it as this point
        }
    }
});

var callbackTask = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Callback");
    logger.LogInformation("Observing the queue...");

    using var httpClient = new HttpClient();

    await foreach (var (callbackUrl, result) in callbackQueueChannel.Reader.ReadAllAsync())
    {
        isInvokingCallback = false;

        try
        {
            logger.LogInformation("Invoking {callbackUrl} with result {result}", callbackUrl, result);

            await httpClient.PostAsJsonAsync(callbackUrl, result);
        }
        catch(Exception ex)
        {
            logger.LogError(ex, "Exception raised during callback, no retry is configured. The result will be ignored");
        }
        finally
        {
            isInvokingCallback = false;
        }
    }
});

try
{
    await app.RunAsync();
}
finally
{
    downloadQueueChannel.Writer.Complete();
    scannerQueueChannel.Writer.Complete();
    callbackQueueChannel.Writer.Complete();
}

await Task.WhenAll(downloadTask, scannerTask, callbackTask);

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


class ScanResult {
    public string? Url { get; set; }
    public int FileExists { get; set; }
    public int PicklescanExitCode { get; set; }
    public string? PicklescanOutput { get; set; }
    public HashSet<string>? PicklescanGlobalImports { get; set; }
    public HashSet<string>? PicklescanDangerousImports { get; set; }
    public int ClamscanExitCode { get; set; }
    public string? ClamscanOutput { get; set; }
}
