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
}

public class CloudStorageService
{
    readonly AmazonS3Client _amazonS3Client;
    readonly string _uploadBucket;
    readonly string _baseUrl;

    public CloudStorageService(IOptions<CloudStorageOptions> options)
	{
        _amazonS3Client = new AmazonS3Client(new BasicAWSCredentials(options.Value.AccessKey, options.Value.SecretKey), new AmazonS3Config
        {
            ServiceURL = options.Value.ServiceUrl
        });
        _uploadBucket = options.Value.UploadBucket;
        _baseUrl = $"{options.Value.ServiceUrl}/{_uploadBucket}/";
    }

    public bool IsCloudStored(string fileUrl)
    {
        var serviceUri = new Uri(_amazonS3Client.Config.ServiceURL);
        var fileUri = new Uri(fileUrl);

        return fileUri.Authority.EndsWith(serviceUri.Authority, StringComparison.Ordinal);
    }

    Task<PutObjectResponse> UploadFileInternal(string filePath, string key, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            FilePath = filePath,
            BucketName = _uploadBucket,
            // DisablePayloadSigning = true must be passed as Cloudflare R2 does not currently support the Streaming SigV4 implementation used by AWSSDK.S3.
            DisablePayloadSigning = true,
            Key = key
        };

        return _amazonS3Client.PutObjectAsync(request, cancellationToken);
    }

    public async Task<string> UploadFile(string filePath, string suggestedKey, CancellationToken cancellationToken)
    {
        var key = $"imported/{Path.GetFileNameWithoutExtension(suggestedKey)}.{Path.GetRandomFileName()}{Path.GetExtension(suggestedKey)}";
        // Try and upload the file with its original name
        var response = await UploadFileInternal(filePath, key, cancellationToken);

        if (response.HttpStatusCode is not System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Expected the upload to have succeeded, got: {response.HttpStatusCode}");
        }

        // Generate a url. This URL is not pre-signed. Alternative, use:
        return _baseUrl + key;
    }
}