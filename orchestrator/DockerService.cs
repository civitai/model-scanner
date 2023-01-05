using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ModelScanner;

class DockerService
{
    readonly ILogger<DockerService> _logger;
    readonly LocalStorageOptions _localStorageOptions;

    public DockerService(ILogger<DockerService> logger, IOptions<LocalStorageOptions> localStorageOptions)
    {
        _logger = logger;
        _localStorageOptions = localStorageOptions.Value;
    }

    public const string InPath = "/data/model.in";
    public const string OutFolderPath = "/data/";

    public async Task<(int exitCode, string output)> RunCommandInDocker(string command, string filePath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing {command} for file {filePath}", command, filePath);

        var stopwatch = Stopwatch.StartNew();

        var process = new Process
        {
            // TODO: Double check perf time when constrained to 1cpu and 1024mb mem
            StartInfo = new ProcessStartInfo("docker", $"run -v {Path.GetFullPath(filePath)}:{InPath} -v {Path.GetFullPath(_localStorageOptions.TempFolder)}:{OutFolderPath} --rm civitai-model-scanner {command}")
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

        _logger.LogInformation("Executed {command} for file {filePath} completed with exit code {exitCode} in {elapsed}", command, filePath, process.ExitCode, stopwatch.Elapsed);
        _logger.LogInformation(output);

        return (process.ExitCode, output);
    }
}
