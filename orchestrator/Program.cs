using Microsoft.AspNetCore.Http.HttpResults;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();


var scannerQueueChannel = Channel.CreateUnbounded<(string modelUrl, string callbackUrl)>();
var callbackQueueChannel = Channel.CreateUnbounded<(string callbackUrl, ScanResult result)>();

bool isScanning = false;
bool isInvokingCallback = false;

app.MapPost("/enqueue", async (string modelUrl, string callbackUrl) =>
{
    await scannerQueueChannel.Writer.WriteAsync((modelUrl, callbackUrl));
});

app.MapGet("/", () => new
{
    IsScanning = isScanning,
    ScannerQueueSize = scannerQueueChannel.Reader.Count,
    IsInvokingCallback = isInvokingCallback,
    CallbackQueueSize = callbackQueueChannel.Reader.Count
});

var scannerTask = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Scanner");
    logger.LogInformation("Observing the queue...");

    await foreach (var (modelUrl, callbackUrl) in scannerQueueChannel.Reader.ReadAllAsync())
    {
        try
        {
            isScanning = true;

            logger.LogInformation("Scanning {modelurl}", modelUrl);

            var stopwatch = Stopwatch.StartNew();

            var process = new Process
            {
                StartInfo = new ProcessStartInfo("docker", $"run --rm civitai-model-scanner {modelUrl}")
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

            logger.LogInformation("Scan for {modelUrl} completed in {elapsed}, queuing callback...", modelUrl, stopwatch.Elapsed);
            logger.LogDebug(output);

            var result = JsonSerializer.Deserialize<ScanResult>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;

            result.PicklescanGlobalImports = ParseGlobalImports(result.PicklescanOutput);
            result.PicklescanDangerousImports = ParseDangerousImports(result.PicklescanOutput);

            await callbackQueueChannel.Writer.WriteAsync((callbackUrl, result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected exception during processing");
        }
        finally
        {
            isScanning = false;
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
    scannerQueueChannel.Writer.Complete();
    callbackQueueChannel.Writer.Complete();
}

await Task.WhenAll(scannerTask, callbackTask);

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
    public int PicklescanExitCode { get; set; }
    public string? PicklescanOutput { get; set; }
    public HashSet<string>? PicklescanGlobalImports { get; set; }
    public HashSet<string>? PicklescanDangerousImports { get; set; }
    public int ClamscanExitCode { get; set; }
    public string? ClamscanOutput { get; set; }
}