using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScenarioTests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ModelScannerTests;

public partial class CloudStorageServiceTests
{
    [Scenario(NamingPolicy = ScenarioTestMethodNamingPolicy.MethodAndTest)]
    public void GetCloudStoredBucketName(ScenarioContext scenario)
    {
        var options = new CloudStorageOptions
        {
            ServiceUrl = "https://empty",
            AccessKey = "access-key",
            SecretKey = "secret-key",
            UploadBucket = "upload",
            TempBucket = "temp"
        };

        var subject = new CloudStorageService(Options.Create(options), NullLogger<CloudStorageService>.Instance);

        scenario.Fact("Returns null when no match", () =>
        {
            var result = subject.GetCloudStoredBucketName("https://nomatch.com/nomatch/nomatch.bin");
            Assert.Null(result);
        });

        scenario.Fact("Returns temp bucket on domain match", () =>
        {
            var result = subject.GetCloudStoredBucketName("https://temp.nomatch.com/nomatch/nomatch.bin");
            Assert.Equal(options.TempBucket, result);
        });

        scenario.Fact("Returns temp bucket on path match", () =>
        {
            var result = subject.GetCloudStoredBucketName("https://nomatch.nomatch.com/temp/nomatch.bin");
            Assert.Equal(options.TempBucket, result);
        });

        scenario.Fact("Returns null on sub-path match", () =>
        {
            var result = subject.GetCloudStoredBucketName("https://nomatch.nomatch.com/nomatch/temp/nomatch.bin");
            Assert.Null(result);
        });

        scenario.Fact("Prefers subdomain over path", () =>
        {
            var result = subject.GetCloudStoredBucketName("https://upload.nomatch.com/temp/nomatch.bin");
            Assert.Equal(options.UploadBucket, result);
        });
    }
}
