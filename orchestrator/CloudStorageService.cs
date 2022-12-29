using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

public class CloudStorageOptions
{
    [Required] public string AccessKey { get; set; } = default!;
    [Required] public string SecretKey { get; set; } = default!;
    [Required] public string ServiceUrl { get; set; } = default!;
    [Required] public string UploadBucket { get; set; } = default!;
    [Required] public string TempBucket { get; set; } = default!;

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

    public bool IsCloudStored(string fileUrl) 
        => fileUrl.StartsWith(_baseUrl, StringComparison.OrdinalIgnoreCase);

    Task<PutObjectResponse> UploadFileInternal(string filePath, string key, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            FilePath = Path.GetFullPath(filePath),
            BucketName = _options.UploadBucket,
            // DisablePayloadSigning = true must be passed as Cloudflare R2 does not currently support the Streaming SigV4 implementation used by AWSSDK.S3.
            DisablePayloadSigning = true,
            Key = key
        };

        return _amazonS3Client.PutObjectAsync(request, cancellationToken);
    }

    public Task<string> ImportFile(string filePath, string suggestedKey, CancellationToken cancellationToken)
    {
        var key = $"imported/{Path.GetFileNameWithoutExtension(suggestedKey)}.{Path.GetRandomFileName()}{Path.GetExtension(suggestedKey)}";

        return UploadFile(filePath, key, cancellationToken);
    }

    public async Task<string> UploadFile(string filePath, string key, CancellationToken cancellationToken)
    {
        var response = await UploadFileInternal(filePath, key, cancellationToken);

        if (response.HttpStatusCode is not System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Expected the upload to have succeeded, got: {response.HttpStatusCode}");
        }

        // Generate a url. This URL is not pre-signed
        return _baseUrl + key;
    }

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
}