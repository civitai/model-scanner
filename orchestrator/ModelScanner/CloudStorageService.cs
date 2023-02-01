using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Hangfire;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

public class CloudStorageOptions
{
    [Required] public string AccessKey { get; set; } = default!;
    [Required] public string SecretKey { get; set; } = default!;
    [Required] public string ServiceUrl { get; set; } = default!;
    [Required] public string UploadBucket { get; set; } = "civitai-prod-new";// default!;
    [Required] public string TempBucket { get; set; } = "civitai-prod";///default!; =

    /// <summary>
    /// A timestamp indicating from what age an object is considered stale in temp storage
    /// Stale objects are subject for automatic removal
    /// </summary>
    public TimeSpan StaleAge { get; set; } = TimeSpan.FromDays(30);
}

public class CloudStorageService
{
    readonly AmazonS3Client _amazonS3Client;
    readonly CloudStorageOptions _options;
    readonly string _baseUrl;
    readonly ILogger<CloudStorageService> _logger;

    public CloudStorageService(IOptions<CloudStorageOptions> options, ILogger<CloudStorageService> logger)
	{
        _amazonS3Client = new AmazonS3Client(new BasicAWSCredentials(options.Value.AccessKey, options.Value.SecretKey), new AmazonS3Config
        {
            ServiceURL = options.Value.ServiceUrl
        });
        _options = options.Value;
        _baseUrl = $"{options.Value.ServiceUrl}/{options.Value.UploadBucket}/";
        _logger = logger;
    }

    public string? GetCloudStoredBucketName(string fileUrl)
    {
        var uri = new Uri(fileUrl);
        if (uri.Host.StartsWith(_options.UploadBucket, StringComparison.OrdinalIgnoreCase))
        {
            return _options.UploadBucket;
        }

        if (uri.Host.StartsWith(_options.TempBucket, StringComparison.OrdinalIgnoreCase))
        {
            return _options.TempBucket;
        }

        if (uri.AbsolutePath.StartsWith($"/{_options.UploadBucket}", StringComparison.OrdinalIgnoreCase))
        {
            return _options.UploadBucket;
        }

        if (uri.AbsolutePath.StartsWith($"/{_options.TempBucket}", StringComparison.OrdinalIgnoreCase))
        {
            return _options.TempBucket;
        }

        return null;
    }

    public CloudStorageOptions Options => _options;

    async Task UploadFileInternal(string filePath, string key, CancellationToken cancellationToken)
    {
        var transferUtility = new TransferUtility(_amazonS3Client);

        AWSConfigsS3.UseSignatureVersion4 = true;
        await transferUtility.UploadAsync(new TransferUtilityUploadRequest
        {
            FilePath = filePath,
            BucketName = _options.UploadBucket,
            Key = key,
            // DisablePayloadSigning = true must be passed as Cloudflare R2 does not currently support the Streaming SigV4 implementation used by AWSSDK.S3.
            DisablePayloadSigning = true,
            PartSize = 1024 * 1024 * 100, // 100mb parts (This is a suggested value, not tested for optimal reliability / performance)
            //CalculateContentMD5Header = false,
            //DisableMD5Stream = true
        }, cancellationToken);
    }

    public Task<string> ImportFile(string filePath, string suggestedKey, CancellationToken cancellationToken)
    {
        var key = $"imported/{Path.GetFileNameWithoutExtension(suggestedKey)}.{Path.GetRandomFileName()}{Path.GetExtension(suggestedKey)}";

        return UploadFile(filePath, key, cancellationToken);
    }

    public async Task<string> UploadFile(string filePath, string key, CancellationToken cancellationToken)
    {
        await UploadFileInternal(filePath, key, cancellationToken);

        // Generate a url. This URL is not pre-signed
        return _baseUrl + key.TrimStart('/');
    }

    [Queue("cleanup")]
    public async Task CleanupTempStorage(CancellationToken cancellationToken)
    {
        var staleBeforeDate = DateTime.UtcNow.Subtract(_options.StaleAge);
        ListObjectsV2Response? response = null;
        Queue<S3Object> staleObjects = new Queue<S3Object>();

        do
        {
            response = await _amazonS3Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _options.TempBucket,
                ContinuationToken = response?.NextContinuationToken
            });


            foreach (var s3Object in response.S3Objects)
            {
                if (s3Object.LastModified < staleBeforeDate)
                {
                    _logger.LogInformation("Marking {key} as stale", s3Object.Key);
                    staleObjects.Enqueue(s3Object);
                }
            }
        }
        while (response.IsTruncated);

        while (staleObjects.Count > 0)
        {
            const int bulkSize = 1000;
            var bulkRequest = new List<KeyVersion>();

            while (bulkRequest.Count < bulkSize)
            {
                var nextObject = staleObjects.Dequeue();
                bulkRequest.Add(new KeyVersion
                {
                    Key = nextObject.Key
                });
            }

            //await _amazonS3Client.DeleteObjectsAsync(new DeleteObjectsRequest
            //{
            //    BucketName = _options.TempBucket,
            //    Objects = bulkRequest
            //});
        }
    }

    [Queue("delete-objects")]
    public async Task DeleteOject(string key, CancellationToken cancellationToken)
    {
        await _amazonS3Client.DeleteObjectAsync(_options.TempBucket, key, cancellationToken);
        await _amazonS3Client.DeleteObjectAsync(_options.UploadBucket, key, cancellationToken);
    }
}