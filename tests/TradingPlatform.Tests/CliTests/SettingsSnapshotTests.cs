using Microsoft.Extensions.Configuration;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class SettingsSnapshotTests
{
    private static IConfiguration Config(params (string, string?)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.Select(p => new KeyValuePair<string, string?>(p.Item1, p.Item2))).Build();

    [Fact]
    public void CredentialLookingKeys_AreDropped_ButEnvVarNamesAreKept()
    {
        var cfg = Config(("Portfolio:InitialCapital", "1000000"), ("Portfolio:ApiSecret", "hunter2"), ("Portfolio:SomeToken", "abc"),
                         ("Zerodha:ApiKeyEnvVar", "ZERODHA_API_KEY"));
        var snap = SettingsSnapshot.Build(cfg, _ => null);
        var portfolio = Assert.Single(snap.Groups);
        Assert.Equal(1, portfolio.Items.Count);
        Assert.Equal("InitialCapital", portfolio.Items[0].Key);
        Assert.False(System.Text.Json.JsonSerializer.Serialize(snap).Contains("hunter2"));
    }

    [Fact]
    public void Credentials_ReportPresenceOnly_NeverTheValue()
    {
        var cfg = Config(("Zerodha:ApiKeyEnvVar", "K"), ("Zerodha:ApiSecretEnvVar", "S"), ("Zerodha:AccessTokenEnvVar", "T"));
        var snap = SettingsSnapshot.Build(cfg, name => name == "K" ? "super-secret-value" : null);
        Assert.Equal("set", snap.Credentials[0].Value);
        Assert.Equal("not set", snap.Credentials[1].Value);
        Assert.Equal("not set", snap.Credentials[2].Value);
        Assert.False(System.Text.Json.JsonSerializer.Serialize(snap).Contains("super-secret-value"));
    }

    [Fact]
    public void LiveTradingAllowed_RequiresBothModeLiveAndTheMasterSwitch()
    {
        Assert.False(SettingsSnapshot.Build(Config(("LiveTrading:Mode", "Live"), ("LiveTrading:AllowLiveTrading", "false")), _ => null).LiveTradingAllowed);
        Assert.False(SettingsSnapshot.Build(Config(("LiveTrading:Mode", "Paper"), ("LiveTrading:AllowLiveTrading", "true")), _ => null).LiveTradingAllowed);
        Assert.True(SettingsSnapshot.Build(Config(("LiveTrading:Mode", "Live"), ("LiveTrading:AllowLiveTrading", "true")), _ => null).LiveTradingAllowed);
    }

    [Fact]
    public void NestedArrays_AreFlattenedWithIndexedKeys()
    {
        var cfg = Config(("Risk:DrawdownTiers:0:Label", "Normal"), ("Risk:DrawdownTiers:1:Label", "Cautious"));
        var risk = Assert.Single(SettingsSnapshot.Build(cfg, _ => null).Groups);
        Assert.Equal(2, risk.Items.Count);
        Assert.Equal("DrawdownTiers.0.Label", risk.Items[0].Key);
    }
}
