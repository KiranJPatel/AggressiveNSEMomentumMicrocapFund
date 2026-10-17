using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class ZerodhaLoginTests
{
    [Fact]
    public void RequestTokenArg_ParsesTheValue_AndRejectsAMissingOne()
    {
        Assert.Equal("abc123", ZerodhaLoginCommand.RequestTokenArg(new[] { "zerodha-login", "--request-token", " abc123 " }, out var m0));
        Assert.False(m0);
        Assert.Null(ZerodhaLoginCommand.RequestTokenArg(new[] { "zerodha-login" }, out var m1));
        Assert.False(m1);
        ZerodhaLoginCommand.RequestTokenArg(new[] { "zerodha-login", "--request-token" }, out var m2);
        Assert.True(m2);
        ZerodhaLoginCommand.RequestTokenArg(new[] { "zerodha-login", "--request-token", "--other" }, out var m3);
        Assert.True(m3, "a following flag is not a token");
    }

    [Fact]
    public async Task WithoutCredentialsInTheEnvironment_ExitsOneAndNamesTheVariables()
    {
        using var host = CliTestHost.Create();
        var options = new ZerodhaOptions { ApiKeyEnvVar = "TP_TEST_NO_SUCH_KEY", ApiSecretEnvVar = "TP_TEST_NO_SUCH_SECRET" };

        var exit = await ZerodhaLoginCommand.RunAsync(new[] { "zerodha-login" }, options, host.Provider, CliTestHost.Logger);

        Assert.Equal(1, exit);
    }
}
