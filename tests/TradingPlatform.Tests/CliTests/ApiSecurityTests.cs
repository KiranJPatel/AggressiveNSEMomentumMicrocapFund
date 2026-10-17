using TradingPlatform.Application.Api;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class ApiSecurityTests
{
    [Fact]
    public void NoConfiguredKey_MeansNoGate()
    {
        Assert.True(ApiSecurity.IsAuthorized(null, null));
        Assert.True(ApiSecurity.IsAuthorized("", "anything"));
    }

    [Fact]
    public void ConfiguredKey_RequiresAnExactMatch()
    {
        Assert.True(ApiSecurity.IsAuthorized("s3cret", "s3cret"));
        Assert.False(ApiSecurity.IsAuthorized("s3cret", null));
        Assert.False(ApiSecurity.IsAuthorized("s3cret", ""));
        Assert.False(ApiSecurity.IsAuthorized("s3cret", "S3CRET"));
        Assert.False(ApiSecurity.IsAuthorized("s3cret", "s3cret "));
        Assert.False(ApiSecurity.IsAuthorized("s3cret", "s3cre"));
    }

    [Fact]
    public void LoopbackBindings_AreRecognised()
    {
        Assert.True(ApiSecurity.IsLoopbackOnly(null));
        Assert.True(ApiSecurity.IsLoopbackOnly(""));
        Assert.True(ApiSecurity.IsLoopbackOnly("http://localhost:5000"));
        Assert.True(ApiSecurity.IsLoopbackOnly("http://127.0.0.1:5000"));
        Assert.True(ApiSecurity.IsLoopbackOnly("http://[::1]:5000"));
        Assert.True(ApiSecurity.IsLoopbackOnly("http://localhost:5000;https://127.0.0.1:5001"));
    }

    [Fact]
    public void AnythingElse_CountsAsExposed()
    {
        Assert.False(ApiSecurity.IsLoopbackOnly("http://0.0.0.0:5000"));
        Assert.False(ApiSecurity.IsLoopbackOnly("http://+:5000"));
        Assert.False(ApiSecurity.IsLoopbackOnly("http://*:5000"));
        Assert.False(ApiSecurity.IsLoopbackOnly("http://[::]:5000"));
        Assert.False(ApiSecurity.IsLoopbackOnly("http://192.168.1.20:5000"));
        Assert.False(ApiSecurity.IsLoopbackOnly("http://myhost.example.com:5000"));
        Assert.False(ApiSecurity.IsLoopbackOnly("http://localhost:5000;http://0.0.0.0:5001"), "one exposed URL is enough");
        Assert.False(ApiSecurity.IsLoopbackOnly("not a url"), "unparseable must fail toward exposed");
    }

    [Fact]
    public void Startup_RefusesExposedBindWithoutKey_UnlessExplicitlyOverridden()
    {
        Assert.NotNull(ApiSecurity.StartupViolation("http://0.0.0.0:5000", null, false));
        Assert.True(ApiSecurity.StartupViolation("http://0.0.0.0:5000", null, false)!.Contains("TRADING_API_KEY"));
        Assert.Null(ApiSecurity.StartupViolation("http://0.0.0.0:5000", "k", false));
        Assert.Null(ApiSecurity.StartupViolation("http://0.0.0.0:5000", null, true));
        Assert.Null(ApiSecurity.StartupViolation(null, null, false));
    }
}
