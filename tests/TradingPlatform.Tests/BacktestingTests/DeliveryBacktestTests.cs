using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>The optional delivery-% breakout gate inside the backtest engine (spec §3.4).</summary>
public class DeliveryBacktestTests
{
    private static BacktestEngineOptions Options(decimal? minRatio) => new()
    {
        Indicator = new IndicatorOptions(), Vcp = new VcpOptions(), Rs = new RsOptions(), StockScoring = new StockScoringOptions(),
        Strategy = new StrategyOptions { BreakoutMinDeliveryRatio = minRatio },
        MarketRegime = new MarketRegimeOptions(), Exit = new ExitOptions(), Risk = new RiskOptions { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() },
        Portfolio = new PortfolioOptions { InitialCapital = 1_000_000m }, Backtest = new BacktestOptions(),
    };

    private static BacktestRunRequest Request(IReadOnlyList<DeliveryRecord>? delivery)
    {
        var stock = BacktestEngineFixture.BuildStockBars();
        return new BacktestRunRequest
        {
            StrategyVersion = "t", ConfigurationHash = "c", DataVersion = "d",
            StartDate = DateOnly.FromDateTime(stock[0].Timestamp.Date), EndDate = DateOnly.FromDateTime(stock[^1].Timestamp.Date),
            SurvivorshipBiased = true, BroadIndexBars = BacktestEngineFixture.BuildFlatBenchmark(stock.Count),
            Symbols = new[] { new BacktestSymbolContext { Symbol = BacktestEngineFixture.Symbol, Bars = stock, MarketCapBucket = MarketCapBucket.Mid, DeliveryRecords = delivery } },
        };
    }

    /// <summary>One record per stock bar date: <paramref name="before"/> percent until the session before <paramref name="from"/>, <paramref name="after"/> from then on.</summary>
    private static List<DeliveryRecord> Records(DateOnly from, decimal before, decimal after) =>
        BacktestEngineFixture.BuildStockBars().Select(b => DateOnly.FromDateTime(b.Timestamp.Date))
            .Select(d => new DeliveryRecord { Symbol = BacktestEngineFixture.Symbol, TradeDate = d, DeliveryPercent = d < from ? before : after }).ToList();

    [Fact]
    public void Gate_NotConfigured_IgnoresDeliveryData_AndTheGoldenTradeIsUnchanged()
    {
        var baseline = BacktestEngine.Run(Request(null), Options(null));
        var withData = BacktestEngine.Run(Request(Records(new DateOnly(2100, 1, 1), 20m, 20m)), Options(null));

        Assert.Equal(1, baseline.Trades.Count);
        Assert.Equal(baseline.Trades[0].EntryPrice, withData.Trades[0].EntryPrice);
        Assert.Equal(baseline.Trades[0].EntryAt, withData.Trades[0].EntryAt);
    }

    [Fact]
    public void Gate_WithNoDeliveryData_BlocksEveryEntry_FailsClosed()
    {
        Assert.Equal(0, BacktestEngine.Run(Request(null), Options(1.2m)).Trades.Count);
    }

    [Fact]
    public void Gate_WithFlatDelivery_BlocksEntries_BecauseTheRatioNeverExceedsOne()
    {
        var flat = Records(new DateOnly(2100, 1, 1), 20m, 20m);

        Assert.Equal(0, BacktestEngine.Run(Request(flat), Options(1.2m)).Trades.Count);
    }

    [Fact]
    public void Gate_WithADeliverySurgeOnTheSignalDay_LetsTheSameBreakoutThrough()
    {
        var baseline = BacktestEngine.Run(Request(null), Options(null));
        var entryDate = DateOnly.FromDateTime(baseline.Trades[0].EntryAt.Date);
        var bars = BacktestEngineFixture.BuildStockBars();
        var signalIndex = bars.ToList().FindIndex(b => DateOnly.FromDateTime(b.Timestamp.Date) == entryDate) - 1;
        var signalDate = DateOnly.FromDateTime(bars[signalIndex].Timestamp.Date);

        var gated = BacktestEngine.Run(Request(Records(signalDate, 20m, 60m)), Options(1.2m));   // 20% norm, then 60% (3x) from the signal day on

        Assert.Equal(1, gated.Trades.Count);
        Assert.Equal(baseline.Trades[0].EntryAt, gated.Trades[0].EntryAt);
    }
}
