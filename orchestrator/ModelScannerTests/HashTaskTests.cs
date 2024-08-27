using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelScanner.Tasks;
using ScenarioTests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ModelScannerTests;

public partial class HashTaskTests
{
    [Scenario(NamingPolicy = ScenarioTestMethodNamingPolicy.Test)]
    public async Task DefaultScenario(ScenarioContext scenario)
    {
        var filePath = Path.Combine(typeof(HashTaskTests).Assembly.Location, "dummy.txt");

        var subject = new HashTask(NullLogger<HashTask>.Instance, default!);
        var result = new ScanResult()
        {
            Url = filePath
        };

        await scenario.SharedFact("We can await the outcome of the scan task", async () =>
        {
            var @continue = await subject.Process("dummy.txt", result, default);
            Assert.True(@continue);
        });

        scenario.Fact("We've calculated the CRC32 hash", CreateHashAsserter("CRC32", "85114A0D"));
        scenario.Fact("We've calculated the SHA256 hash", CreateHashAsserter("SHA256", "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9"));
        scenario.Fact("We've calculated the AutoV1 hash", CreateHashAsserter("AutoV1", null!));
        scenario.Fact("We've calculated the AutoV2 hash", CreateHashAsserter("AutoV2", "B94D27B993"));
        scenario.Fact("We've calculated the Blake3 hash", CreateHashAsserter("Blake3", "D74981EFA70A0C880B8D8C1985D075DBCBF679B99A5F9914E5AAF96B831A9E24"));

        Action CreateHashAsserter(string hashName, string hashValue)
        {
            return () =>
            {
                if (!result.Hashes.TryGetValue(hashName, out var actualHashValue))
                {
                    Assert.Fail($"{hashName} was not generated. Available hashes: {string.Join(",", result.Hashes.Keys)}");
                }

                Assert.Equal(hashValue, actualHashValue);
            };
        }
    }
}
