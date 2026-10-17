using Microsoft.Extensions.Configuration;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CoreTests;

/// <summary>
/// Loads the real config/appsettings.json (not a copy) and binds it against
/// every Options class, so config/code drift is caught here rather than at
/// runtime startup.
/// </summary>
public class ConfigurationBindingTests
{
    private static IConfiguration LoadRealConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradingPlatform.sln")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not locate repo root (TradingPlatform.sln) from test execution directory.");

        var configPath = Path.Combine(dir.FullName, "config");
        return new ConfigurationBuilder()
            .SetBasePath(configPath)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
    }

    [Fact]
    public void AppSettingsJson_BindsToPortfolioOptions_WithExpectedValues()
    {
        var config = LoadRealConfig();
        var o = new PortfolioOptions();
        config.GetSection(PortfolioOptions.SectionName).Bind(o);

        Assert.Equal(1_000_000m, o.InitialCapital);
        Assert.Equal(10, o.MaxPositions);
        Assert.Equal(35m, o.MaxMicrocapExposurePercent);
    }

    [Fact]
    public void AppSettingsJson_BindsToRiskOptions_WithEightDrawdownTiers()
    {
        var config = LoadRealConfig();
        var o = new RiskOptions();
        config.GetSection(RiskOptions.SectionName).Bind(o);

        Assert.Equal(8, o.DrawdownTiers.Count);
        Assert.Equal(1.0m, o.RiskPercentPerTrade);
        Assert.Equal(50m, o.WeeklyLossLimitRiskReductionPercent);
        Assert.Equal(75m, o.MonthlyLossLimitRiskReductionPercent);
    }

    [Fact]
    public void AppSettingsJson_BindsToStrategyOptions_WithNineWeightsSummingToOneHundred()
    {
        var config = LoadRealConfig();
        var o = new StrategyOptions();
        config.GetSection(StrategyOptions.SectionName).Bind(o);

        var w = o.Weights;
        var total = w.RelativeStrength + w.VolumeAccumulation + w.PriceMomentum
            + w.EarningsAcceleration + w.VcpQuality + w.RevenueAcceleration
            + w.TrendStructure + w.InstitutionalActivity + w.SectorStrength;

        Assert.Equal(100m, total);
        Assert.Equal("AggressiveMomentumV1", o.StrategyVersion);
    }

    [Fact]
    public void AppSettingsJson_BindsToZerodhaOptions_AdapterNotYetVerified()
    {
        var config = LoadRealConfig();
        var o = new ZerodhaOptions();
        config.GetSection(ZerodhaOptions.SectionName).Bind(o);

        Assert.False(o.AdapterVerifiedAgainstLiveDocs,
            "Zerodha adapter must stay flagged UNVERIFIED until manually confirmed against live docs (spec §6.2).");
    }

    [Fact]
    public void AppSettingsJson_BindsToBacktestOptions_CommissionRatesNotYetVerified()
    {
        var config = LoadRealConfig();
        var o = new BacktestOptions();
        config.GetSection(BacktestOptions.SectionName).Bind(o);

        Assert.False(o.Commission.RatesVerifiedCurrent,
            "Transaction cost rates must be verified before live deployment (spec §5.4).");
        Assert.Equal(20, o.AdtvLookbackDays);
        Assert.Equal(2m, o.LiquidityBasedMicrocapMultiplier);
    }

    [Fact]
    public void AppSettingsJson_BindsToLiveTradingOptions_DefaultsToSafestConfiguration()
    {
        var config = LoadRealConfig();
        var o = new LiveTradingOptions();
        config.GetSection(LiveTradingOptions.SectionName).Bind(o);

        Assert.Equal(TradingMode.Paper, o.Mode);
        Assert.False(o.AllowLiveTrading,
            "Shipped config must never enable live trading by default (spec §6.1).");
    }

    private static IConfiguration LoadWithOverlay(string environment)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradingPlatform.sln"))) dir = dir.Parent;
        return new ConfigurationBuilder()
            .SetBasePath(Path.Combine(dir!.FullName, "config"))
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .Build();
    }

    [Fact]
    public void DevelopmentOverlay_NeverEnablesLiveTrading_AndUsesItsOwnDatabaseAndFolders()
    {
        var prod = LoadWithOverlay("Production");
        var dev = LoadWithOverlay("Development");

        var live = new LiveTradingOptions();
        dev.GetSection(LiveTradingOptions.SectionName).Bind(live);

        Assert.False(live.AllowLiveTrading, "the Development overlay must never be able to arm live trading");
        Assert.NotEqual(prod["Database:DatabasePath"], dev["Database:DatabasePath"]);
        Assert.NotEqual(prod["Paths:Logs"], dev["Paths:Logs"]);
        Assert.Equal("false", (dev["Realtime:Enabled"] ?? "").ToLowerInvariant());
    }

    [Fact]
    public void AppSettingsJson_BindsTheUniverseDerivedRegimeInputs_WithFailClosedDefault()
    {
        var o = new MarketRegimeOptions();
        LoadRealConfig().GetSection(MarketRegimeOptions.SectionName).Bind(o);

        Assert.Equal(20, o.BreadthMinimumSampleSize);
        Assert.Equal(180, o.BreakoutSuccessLookbackDays);
        Assert.Equal(10, o.BreakoutSuccessMinimumTrades);
        Assert.True(o.FailClosedWithoutTrend, "shipping config must fail closed when the index trend cannot be computed (spec §0.4)");
    }

    [Fact]
    public void AppSettingsJson_BindsDeliveryAndRefinementOptions_WithSafeDefaults()
    {
        var cfg = LoadRealConfig();
        var scoring = new StockScoringOptions(); cfg.GetSection(StockScoringOptions.SectionName).Bind(scoring);
        var strategy = new StrategyOptions(); cfg.GetSection(StrategyOptions.SectionName).Bind(strategy);
        var backtest = new BacktestOptions(); cfg.GetSection(BacktestOptions.SectionName).Bind(backtest);
        var regime = new MarketRegimeOptions(); cfg.GetSection(MarketRegimeOptions.SectionName).Bind(regime);

        Assert.False(scoring.UseDeliveryPercent, "delivery scoring must ship OFF: it needs imported data");
        Assert.Equal(30m, scoring.DeliveryBlendWeightPercent);
        Assert.Equal(20, scoring.DeliveryLookbackDays);
        Assert.Equal(10, scoring.DeliveryMinSampleDays);
        Assert.True(strategy.BreakoutMinDeliveryRatio is null, "the delivery gate must ship OFF");
        Assert.Equal(SignalToOrderLag.NextOpen, backtest.SignalToOrderLag);
        Assert.False(backtest.RefinementSkipWhenNoIntradayData);
        Assert.Equal(5, backtest.RefinementVolumeBaselineSessions);
        Assert.Equal(1.0m, backtest.RefinementMinTriggerRelativeVolume);
        Assert.Equal(10, regime.WeeklyFastPeriod);
        Assert.Equal(30, regime.WeeklySlowPeriod);
        Assert.True(regime.WeeklyBearishDeployableCapPercent is null, "the weekly-bias exposure cap must ship OFF");
    }

    [Fact]
    public void AppSettingsJson_ShipsTheGapRiskGuardOn()
    {
        var risk = new RiskOptions(); LoadRealConfig().GetSection(RiskOptions.SectionName).Bind(risk);

        Assert.True(risk.ResizeEntryForGapRisk, "entries filled above the pivot must be scaled to the risk budget by default");
    }
}
