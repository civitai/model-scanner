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

    public CloudStorageService(IOptions<CloudStorageOptions> options)
	{
        _amazonS3Client = new AmazonS3Client(new BasicAWSCredentials(options.Value.AccessKey, options.Value.SecretKey), new AmazonS3Config
        {
            ServiceURL = options.Value.ServiceUrl
        });

        _uploadBucket = options.Value.UploadBucket;
    }

    public bool IsCloudStored(string fileUrl)
    {
        var serviceUri = new Uri(_amazonS3Client.Config.ServiceURL);
        var fileUri = new Uri(fileUrl);

        return fileUri.Authority == serviceUri.Authority;
    }

    Task<PutObjectResponse> UploadFileInternal(string filePath, string key)
    {
        var request = new PutObjectRequest
        {
            FilePath = filePath,
            BucketName = _uploadBucket,
            // DisablePayloadSigning = true must be passed as Cloudflare R2 does not currently support the Streaming SigV4 implementation used by AWSSDK.S3.
            DisablePayloadSigning = true,
            Key = key
        };

        return _amazonS3Client.PutObjectAsync(request);

    }

    public async Task<string> UploadFile(string filePath, string suggestedKey)
    {
        var key = suggestedKey;

        // Try and upload the file with its original name
        var response = await UploadFileInternal(filePath, key);

        // On conflict, generate a new random name and repeat again
        if (response.HttpStatusCode is System.Net.HttpStatusCode.Conflict)
        {
            key = Path.ChangeExtension(Path.GetRandomFileName(), Path.GetExtension(filePath));
            response = await UploadFileInternal(filePath, key);
        }

        if (response.HttpStatusCode is not System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Expected the upload to have succeeded, got: {response.HttpStatusCode}");
        }

        return $"{_amazonS3Client.Config.ServiceURL}{_uploadBucket}/{key}";
    }
}