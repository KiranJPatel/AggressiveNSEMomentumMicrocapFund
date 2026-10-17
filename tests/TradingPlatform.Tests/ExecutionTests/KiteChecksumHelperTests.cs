using System.Security.Cryptography;
using System.Text;
using TradingPlatform.Execution.Zerodha;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class KiteChecksumHelperTests
{
    [Fact]
    public void ComputeChecksum_MatchesIndependentlyComputedSha256Hex()
    {
        const string apiKey = "myapikey123";
        const string requestToken = "requesttokenabc";
        const string apiSecret = "supersecret456";

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey + requestToken + apiSecret))).ToLowerInvariant();

        var actual = KiteChecksumHelper.ComputeChecksum(apiKey, requestToken, apiSecret);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeChecksum_IsDeterministic()
    {
        var a = KiteChecksumHelper.ComputeChecksum("k", "t", "s");
        var b = KiteChecksumHelper.ComputeChecksum("k", "t", "s");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeChecksum_DifferentInputs_ProduceDifferentChecksums()
    {
        var a = KiteChecksumHelper.ComputeChecksum("k1", "t", "s");
        var b = KiteChecksumHelper.ComputeChecksum("k2", "t", "s");
        Assert.True(a != b);
    }

    [Fact]
    public void ComputeChecksum_IsLowercaseHex_64Chars()
    {
        var checksum = KiteChecksumHelper.ComputeChecksum("k", "t", "s");
        Assert.Equal(64, checksum.Length);
        Assert.True(checksum.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public void BuildLoginUrl_IncludesApiKeyAndV3()
    {
        var url = KiteChecksumHelper.BuildLoginUrl("myapikey123");
        Assert.True(url.Contains("api_key=myapikey123"));
        Assert.True(url.Contains("v=3"));
        Assert.True(url.StartsWith("https://kite.zerodha.com"));
    }
}
