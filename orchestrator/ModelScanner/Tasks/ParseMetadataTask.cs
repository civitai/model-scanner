using System.Text.Json;

namespace ModelScanner.Tasks;

public class ParseMetadataTask: IJobTask
{
  readonly ILogger<ParseMetadataTask> _logger;

  public ParseMetadataTask(ILogger<ParseMetadataTask> logger)
  {
      _logger = logger;
  }

  public JobTaskTypes TaskType => JobTaskTypes.ParseMetadata;

  public Task<bool> Process(string filePath, ScanResult result, CancellationToken cancellationToken)
  {
    var fileExtension = Path.GetExtension(filePath);

    if (fileExtension != ".safetensors") {
      _logger.LogInformation("Skipping parsing of metadata for {type} files. Safetensor file required", fileExtension);
      return Task.FromResult(false);
    }

    using var fileStream = File.OpenRead(filePath);
    var buffer = new byte[8];
    fileStream.Position = 0;
    fileStream.ReadExactly(buffer);
    var metadataLength = BitConverter.ToUInt64(buffer);
    var metadataBuffer = new byte[metadataLength];
    fileStream.ReadExactly(metadataBuffer);

    var metadataJson = JsonSerializer.Deserialize<JsonDocument>(metadataBuffer);
    result.metadata = metadataJson;

    return Task.FromResult(true);
  }

}
