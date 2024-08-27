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
        return _baseUrl + key.TrimStart('/');
    }

    public async Task<string> UploadFile(FileStream fileStream, string key, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            InputStream = fileStream,
            AutoCloseStream = false,
            BucketName = _options.UploadBucket,
            // DisablePayloadSigning = true must be passed as Cloudflare R2 does not currently support the Streaming SigV4 implementation used by AWSSDK.S3.
            DisablePayloadSigning = true,
            Key = key
        };

        var response = await _amazonS3Client.PutObjectAsync(request, cancellationToken);

        if (response.HttpStatusCode is not System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Expected the upload to have succeeded, got: {response.HttpStatusCode}");
        }

        // Generate a url. This URL is not pre-signed
        return _baseUrl + key.TrimStart('/');
    }

    public async Task<string> CopyFile(string currentBucket, string key, CancellationToken cancellationToken)
    {
        // Create a list to store the copy part responses.
        var copyResponses = new List<CopyPartResponse>();

        // Setup information required to initiate the multipart upload.
        var initiateRequest = new InitiateMultipartUploadRequest
        {
            BucketName = _options.UploadBucket,
            Key = key
        };

        // Initiate the upload.
        var initResponse = await _amazonS3Client.InitiateMultipartUploadAsync(initiateRequest);

        // Save the upload ID.
        var uploadId = initResponse.UploadId;

        // Get the size of the object.
        var metadataRequest = new GetObjectMetadataRequest
        {
            BucketName = currentBucket,
            Key = key
        };

        var metadataResponse = await _amazonS3Client.GetObjectMetadataAsync(metadataRequest);
        var objectSize = metadataResponse.ContentLength; // Length in bytes.

        // Copy the parts.
        long partSize = 100 * (long)Math.Pow(2, 20); // Part size is 100 MB.
        long bytePosition = 0;
           
        for (var i = 1; bytePosition < objectSize; i++)
        {
            CopyPartRequest copyRequest = new CopyPartRequest
            {
                DestinationBucket = _options.UploadBucket,
                DestinationKey = key,
                SourceBucket = currentBucket,
                SourceKey = key,
                UploadId = uploadId,
                FirstByte = bytePosition,
                LastByte = bytePosition + partSize - 1 >= objectSize ? objectSize - 1 : bytePosition + partSize - 1,
                PartNumber = i
            };

            copyResponses.Add(await _amazonS3Client.CopyPartAsync(copyRequest));

            bytePosition += partSize;
        }

        // Set up to complete the copy.
        var completeRequest = new CompleteMultipartUploadRequest
        {
            BucketName = _options.UploadBucket,
            Key = key,
            UploadId = initResponse.UploadId
        };

        completeRequest.AddPartETags(copyResponses);

        // Complete the copy.
        await _amazonS3Client.CompleteMultipartUploadAsync(completeRequest);

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

            await _amazonS3Client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _options.TempBucket,
                Objects = bulkRequest
            });
        }
    }

    [Queue("delete-objects")]
    public async Task DeleteOject(string key, CancellationToken cancellationToken)
    {
        await _amazonS3Client.DeleteObjectAsync(_options.TempBucket, key, cancellationToken);
        await _amazonS3Client.DeleteObjectAsync(_options.UploadBucket, key, cancellationToken);
    }
}