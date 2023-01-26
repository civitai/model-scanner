using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;

namespace ModelScanner.Tasks;

class ScanTask : IJobTask
{
    readonly DockerService _dockerService;

    public ScanTask(DockerService dockerService)
    {
        _dockerService = dockerService;
    }

    public JobTaskTypes TaskType => JobTaskTypes.Scan;

    public async Task<bool> Process(string filePath, ScanResult result, CancellationToken cancellationToken)
    {
        var fileExtension = Path.GetExtension(filePath);

        await RunClamScan(result);
        await RunPickleScan(result);
        
        return true;

        async Task RunPickleScan(ScanResult result)
        {
            // safetensors are safe...
            if (fileExtension.EndsWith("safetensors", StringComparison.OrdinalIgnoreCase))
            {
                result.PicklescanExitCode = 0;
                result.PicklescanOutput = "safetensors";
                // TODO Improve Pickle Scan: It probably makes sense to verify that this is indeed a safetensor file
                return;
            }

            var (exitCode, output) = await _dockerService.RunCommandInDocker($"picklescan -p {DockerService.InPath} -l DEBUG", filePath, cancellationToken);

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
            var (exitCode, output) = await _dockerService.RunCommandInDocker($"clamscan {DockerService.InPath}", filePath, cancellationToken);

            result.ClamscanExitCode = exitCode;
            result.ClamscanOutput = output;
        }

    }
}
