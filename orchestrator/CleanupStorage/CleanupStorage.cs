using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelScanner.Database;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ModelScanner.CleanupStorage
{
    public partial class CleanupStorageJob
    {
        readonly CloudStorageService _cloudStorageService;
        readonly ILogger<CleanupStorageJob> _logger;
        readonly CleanupStorageOptions _options;
        readonly CivitaiDbContext _dbContext;

        public CleanupStorageJob(CloudStorageService cloudStorageService, CivitaiDbContext dbContext, ILogger<CleanupStorageJob> logger, IOptions<CleanupStorageOptions> options)
        {
            _cloudStorageService = cloudStorageService;
            _dbContext = dbContext;
            _logger = logger;
            _options = options.Value;
        }

        public async Task PerformCleanup(CancellationToken cancellationToken)
        {
            var indexedDatabase = await IndexDatabase(cancellationToken);
            _logger.LogInformation("Found {count} relevant files in the database", indexedDatabase.Count);

            var cutoffDate = DateTime.UtcNow.Add(-_options.CutoffInterval);
            var objects = _cloudStorageService.ListObjects(cancellationToken);

            await foreach (var (path, lastModified, eTag) in objects)
            {
                if (lastModified >= cutoffDate)
                {
                    _logger.LogInformation("Skipping {path} as it's not yet of age to be considered", path);
                    continue;
                }

                if (!TryParseCloudObjectPath(path, out var userId, out var fileName))
                {
                    _logger.LogInformation("Skipping {path} as it was not in the expected format", path);
                    continue;
                }

                if(indexedDatabase.Contains((userId, fileName)))
                {
                    _logger.LogInformation("Skipping {path} as it is referred to in the database", path);
                    continue;
                }

                _logger.LogInformation("Cleaning up {path}", path);
                var staleUrl =  await _cloudStorageService.SoftDeleteObject(path, eTag, cancellationToken);

                _logger.LogInformation("Moved {path} to {staleUrl}", path, staleUrl);
            }
        }

        [GeneratedRegex(@"^\/?(\d+)\/model\/(.+)$")]
        private static partial Regex CloudPathRegex();

        bool TryParseCloudObjectPath(string path, out int userId, [NotNullWhen(true)]out string? fileName)
        {
            var match = CloudPathRegex().Match(path);

            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].ValueSpan, out userId))
                {
                    fileName = match.Groups[2].Value;
                    return true;
                }
            }

            userId = default;
            fileName = default;

            return false;
        }

        async Task<HashSet<(int userId, string fileName)>> IndexDatabase(CancellationToken cancellationToken)
        {
            var result = new HashSet<(int userId, string fileName)>();

            var query = _dbContext.ModelFiles
                .Select(x => x.Url)
                .AsAsyncEnumerable();

            await foreach (var fileUrl in query)
            {
                var fileUri = new Uri(fileUrl, UriKind.Absolute);
                if (TryParseCloudObjectPath(fileUri.AbsolutePath, out var userId, out var fileName))
                {
                    result.Add((userId, fileName));
                }
            }

            return result;
        }
    }
}
