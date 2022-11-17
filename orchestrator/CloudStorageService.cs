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

        return fileUri.Authority.EndsWith(serviceUri.Authority, StringComparison.Ordinal);
    }

    Task<PutObjectResponse> UploadFileInternal(string filePath, string key)
    {
        var request = new PutObjectRequest
        {
            FilePath = filePath,
            BucketName = _uploadBucket,
            // DisablePayloadSigning = true must be passed as Cloudflare R2 does not currently support the Streaming SigV4 implementation used by AWSSDK.S3.
            DisablePayloadSigning = true,
            Key = "imported/"+key
        };

        return _amazonS3Client.PutObjectAsync(request);
    }

    async Task<bool> ObjectExists(string key)
    {
        try
        {
            var response = await _amazonS3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _uploadBucket,
                Key = key,
            });

            return true;
        }
        catch (AmazonS3Exception ex)
            when (ex.ErrorCode == "NotFound")
        {
            return false;
        }
    }

    public async Task<string> UploadFile(string filePath, string suggestedKey)
    {
        var key = await ObjectExists(suggestedKey) switch
        {
            // Garble up some new unique file name
            true => $"{Path.GetFileNameWithoutExtension(suggestedKey)}-{Path.GetRandomFileName()}{Path.GetExtension(suggestedKey)}",
            false => suggestedKey
        };

        // Try and upload the file with its original name
        var response = await UploadFileInternal(filePath, key);

        if (response.HttpStatusCode is not System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Expected the upload to have succeeded, got: {response.HttpStatusCode}");
        }

        // Generate a url. This URL is not pre-signed. Alternative, use:
        return _amazonS3Client.GetPreSignedURL(new GetPreSignedUrlRequest {
            BucketName = _uploadBucket,
            Key = key,
            Expires = DateTime.UtcNow.AddMinutes(60) // Url is valid for 1 hour. Copied from civitai main platform
        });
    }
}