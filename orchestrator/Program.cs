using Microsoft.AspNetCore.Http.HttpResults;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();


var scannerQueueChannel = Channel.CreateUnbounded<(string modelUrl, string callbackUrl)>();
var callbackQueueChannel = Channel.CreateUnbounded<(string callbackUrl, string result)>();

app.MapPost("/enqueue", async (string modelUrl, string callbackUrl) =>
{
    await scannerQueueChannel.Writer.WriteAsync((modelUrl, callbackUrl));
});

app.MapGet("/", () => new
{
    ScannerQueueSize = scannerQueueChannel.Reader.Count,
    CallbackQueueSize = scannerQueueChannel.Reader.Count
});


var scannerTask = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Scanner");
    logger.LogInformation("Observing the queue...");

    await foreach (var (modelUrl, callbackUrl) in scannerQueueChannel.Reader.ReadAllAsync())
    {
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

        await callbackQueueChannel.Writer.WriteAsync((callbackUrl, output));
    }
});

var callbackTask = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Callback");
    logger.LogInformation("Observing the queue...");

    using var httpClient = new HttpClient();

    await foreach (var (callbackUrl, result) in callbackQueueChannel.Reader.ReadAllAsync())
    {
        logger.LogInformation("Invoking {callbackUrl} with result {result}", callbackUrl, result);
        
        try
        {
            await httpClient.PostAsync(callbackUrl, new StringContent(result));
        }
        catch(Exception ex)
        {
            logger.LogError(ex, "Exception raised during callback, no retry is configured. The result will be ignored");
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
