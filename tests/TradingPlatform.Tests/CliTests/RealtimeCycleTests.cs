using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

/// <summary>
/// The real `paper` command, end to end, with a FAKE live-quote provider: proves the overlay is actually wired into position
/// evaluation and into paper fills (not just unit-correct in isolation).
/// </summary>
public class RealtimeCycleTests
{
    private sealed class CompositeProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner; private readonly Dictionary<Type, object> _o;
        public CompositeProvider(IServiceProvider inner, Dictionary<Type, object> o) { _inner = inner; _o = o; }
        public object? GetService(Type t) => _o.TryGetValue(t, out var v) ? v : _inner.GetService(t);
    }

    private sealed class FakeQuotes : IRealtimeQuoteProvider
    {
        public RealtimeQuote? Quote; public int Calls;
        public string SourceName => "Fake";
        public Task<IReadOnlyDictionary<string, RealtimeQuote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
        {
            Calls++;
            var d = new Dictionary<string, RealtimeQuote>(StringComparer.OrdinalIgnoreCase);
            if (Quote is not null && symbols.Contains(Quote.Symbol, StringComparer.OrdinalIgnoreCase)) d[Quote.Symbol] = Quote;
            return Task.FromResult<IReadOnlyDictionary<string, RealtimeQuote>>(d);
        }
    }

    private static DateOnly LastWeekdayBeforeToday()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(new ScannerOptions().MarketTimeZoneId);
        var d = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).Date).AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    private sealed class Ctx : IDisposable
    {
        public readonly CliTestHost Host = CliTestHost.Create();
        public readonly FakeQuotes Quotes = new();
        public readonly DateOnly LastBar = LastWeekdayBeforeToday();
        public void Dispose() => Host.Dispose();

        public async Task SeedAsync(DateOnly? surveillanceDate = null, DateOnly? classifiedFrom = null)
        {
            var inst = Host.Repo<IInstrumentRepository>(); var bars = Host.Repo<IHistoricalBarRepository>();
            await inst.UpsertAsync(new Instrument { Symbol = "ALPHA", NseSymbol = "ALPHA", YahooSymbol = "ALPHA.NS", CompanyName = "Alpha", Exchange = "NSE", Active = true, AverageDailyTurnover = 50_000_000m });
            var bench = Host.Options<BenchmarkOptions>().Value;
            foreach (var (sym, start) in new[] { (bench.BroadIndexSymbol, 15000m), (bench.MidcapIndexSymbol!, 18000m), ("ALPHA", 100m) })
            {
                var list = new List<HistoricalBar>(); var d = LastBar; var n = 0;
                while (n < 420) { if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) { list.Add(MakeBar(sym, d, sym == "ALPHA" ? 100m : start * (1m + (420 - n) * -0.0002m))); n++; } d = d.AddDays(-1); }
                list.Reverse();
                if (sym == "ALPHA") list[^1] = MakeBar(sym, LastBar, 100m);          // last cached close is exactly 100
                await bars.UpsertRangeAsync(list);
            }
            await Host.Repo<IUniverseClassificationRepository>().UpsertAsync(new UniverseClassificationSnapshot { InstrumentId = (await inst.GetBySymbolAsync("ALPHA"))!.Id, Symbol = "ALPHA", MarketCapBucket = MarketCapBucket.Small, EffectiveFrom = classifiedFrom ?? new DateOnly(2024, 1, 1) });
            await Host.Repo<ISurveillanceRepository>().UpsertAsync(new SurveillanceStatus { Symbol = "ALPHA", AsOfDate = surveillanceDate ?? LastBar, Flags = RiskFlags.None, Availability = DataAvailability.Available, Source = "t" });
            await Host.Repo<IPositionRepository>().UpsertAsync(new Position { Symbol = "ALPHA", Quantity = 100, AverageEntryPrice = 100m, InitialStop = 95m, CurrentStop = 95m, InitialRiskAmount = 500m, RiskPerShare = 5m, HighestStageFilled = PyramidStage.Stage1Initial, IsOpen = true, OpenedAt = DateTimeOffset.UtcNow.AddDays(-10) });
        }

        private static HistoricalBar MakeBar(string sym, DateOnly d, decimal close) =>
            new() { Symbol = sym, Timestamp = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)), Open = close, High = close * 1.005m, Low = close * 0.995m, Close = close, AdjustedClose = close, Volume = 2_000_000, Source = "t", Timeframe = Timeframe.Daily };

        public RealtimeQuote LiveQuote(decimal last, decimal low, TimeSpan age) => new("ALPHA", last, 99.5m, 100.5m, low, 100m, 800_000m, DateTimeOffset.UtcNow - age, "Fake");

        public async Task<(int Code, string Out)> PaperAsync(params string[] flags)
        {
            var sp = new CompositeProvider(Host.Provider, new Dictionary<Type, object> { [typeof(IRealtimeQuoteProvider)] = Quotes });
            T Svc<T>() where T : notnull => sp.GetRequiredService<T>();
            var oldOut = Console.Out; var oldErr = Console.Error; var sw = new StringWriter();
            Console.SetOut(sw); Console.SetError(sw);
            try
            {
                var code = await TradeCycleCommand.RunAsync(new[] { "paper" }.Concat(flags).ToArray(), true,
                    Svc<IInstrumentRepository>(), Svc<IHistoricalBarRepository>(), Svc<IUniverseClassificationRepository>(), Svc<ISurveillanceRepository>(),
                    Svc<IPositionRepository>(), Svc<IOrderRepository>(), Svc<ITradeRepository>(), Svc<ISignalRepository>(), Svc<IRiskEventRepository>(),
                    Svc<IDrawdownEventRepository>(), Svc<IPortfolioSnapshotRepository>(), Svc<IEngineStateRepository>(),
                    Host.Options<ScannerOptions>(), Host.Options<IndicatorOptions>(), Host.Options<VcpOptions>(), Host.Options<RsOptions>(), Host.Options<StockScoringOptions>(),
                    Host.Options<StrategyOptions>(), Host.Options<MarketRegimeOptions>(), Host.Options<ExitOptions>(), Host.Options<RiskOptions>(), Host.Options<PortfolioOptions>(),
                    Host.Options<BacktestOptions>(), Host.Options<BenchmarkOptions>(), Host.Options<LiveTradingOptions>(), Host.Options<ZerodhaOptions>(), sp, CliTestHost.Logger);
                return (code, sw.ToString());
            }
            finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        }
    }

    [Fact]
    public async Task LiveQuoteBelowTheStop_TriggersTheExit_AndPaperFillsAtTheLivePrice_NotYesterdaysOpen()
    {
        using var c = new Ctx(); await c.SeedAsync();
        c.Quotes.Quote = c.LiveQuote(last: 94m, low: 93.5m, age: TimeSpan.FromSeconds(10));   // daily bars alone (last close 100) would show NO stop hit

        var (code, output) = await c.PaperAsync("--realtime-assume-open");
        Assert.Equal(0, code);
        Assert.True(output.Contains("Data: REAL-TIME overlay — Fake: 1 valid of 1"), output);
        Assert.True(output.Contains("EXIT  ALPHA") && output.Contains("StopLoss"), output);
        Assert.Empty(await c.Host.Repo<IPositionRepository>().GetOpenAsync());

        var orders = await c.Host.Repo<IOrderRepository>().GetRecentAsync(5);
        var fill = Assert.Single(await c.Host.Repo<IOrderRepository>().GetFillsAsync(orders[0].Id));
        Assert.True(fill.Price > 90m && fill.Price < 95m, $"fill at {fill.Price} should be near the LIVE 94, not the cached ~100");
        var trade = Assert.Single(await c.Host.Repo<ITradeRepository>().GetBySymbolAsync("ALPHA"));
        Assert.True(trade.RealizedPnl < 0m && trade.RMultiple < 0m && trade.RMultiple > -2m);
    }

    [Fact]
    public async Task SameSituation_WithRealtimeOff_UsesDailyBarsOnly_AndHoldsThePosition()
    {
        using var c = new Ctx(); await c.SeedAsync();
        c.Quotes.Quote = c.LiveQuote(94m, 93.5m, TimeSpan.FromSeconds(10));

        var (code, output) = await c.PaperAsync("--realtime-assume-open", "--no-realtime");
        Assert.Equal(0, code);
        Assert.True(output.Contains("Data: daily bars (real-time disabled by --no-realtime)"), output);
        Assert.Equal(0, c.Quotes.Calls);                                         // no quote was even requested
        Assert.Equal(1, (await c.Host.Repo<IPositionRepository>().GetOpenAsync()).Count);
    }

    [Fact]
    public async Task StaleQuote_IsNotTrusted_PositionStaysOnDailyBars_AndTheReasonIsPrinted()
    {
        using var c = new Ctx(); await c.SeedAsync();
        c.Quotes.Quote = c.LiveQuote(94m, 93.5m, TimeSpan.FromMinutes(20));       // a delayed feed

        var (code, output) = await c.PaperAsync("--realtime-assume-open");
        Assert.Equal(0, code);
        Assert.True(output.Contains("0 valid of 1") && output.Contains("no live data for ALPHA: stale quote"), output);
        Assert.Equal(1, (await c.Host.Repo<IPositionRepository>().GetOpenAsync()).Count);   // never acted on the untrusted price
    }

    [Fact]
    public async Task MarketClosed_NoRealtimeIsAttempted_WithoutTheAssumeOpenFlag()
    {
        using var c = new Ctx(); await c.SeedAsync();
        c.Quotes.Quote = c.LiveQuote(94m, 93.5m, TimeSpan.FromSeconds(10));
        var (code, output) = await c.PaperAsync();   // no --realtime-assume-open: the real clock decides (tests run any time; if the NSE happens to be open this still must not crash)
        Assert.Equal(0, code);
        Assert.True(output.Contains("Data: REAL-TIME overlay") || output.Contains("market closed — using final daily bars"), output);
    }

    [Fact]
    public async Task StatusRecordedToday_IsUsedByTheScan_EvenThoughTheLastBarIsYesterdays()
    {
        // Regression: the scan used to look statuses up "as of the last bar" (yesterday), so a classification/surveillance entry made this
        // morning was "from the future", read as Unknown, and rejected every symbol until tonight's bar arrived.
        using var c = new Ctx();
        var today = RealtimeBarOverlay.MarketDate(DateTimeOffset.UtcNow, new ScannerOptions());
        await c.SeedAsync(surveillanceDate: today, classifiedFrom: today);

        var outcome = await ScanCycle.RunAsync(c.Host.Repo<IInstrumentRepository>(), c.Host.Repo<IHistoricalBarRepository>(), c.Host.Repo<IUniverseClassificationRepository>(),
            c.Host.Repo<ISurveillanceRepository>(), c.Host.Repo<IScannerResultRepository>(),
            new TradingPlatform.Scanner.ScannerEngineOptions
            {
                Scanner = c.Host.Options<ScannerOptions>().Value, Indicator = c.Host.Options<IndicatorOptions>().Value, Vcp = c.Host.Options<VcpOptions>().Value, Rs = c.Host.Options<RsOptions>().Value,
                StockScoring = c.Host.Options<StockScoringOptions>().Value, Strategy = c.Host.Options<StrategyOptions>().Value, MarketRegime = c.Host.Options<MarketRegimeOptions>().Value,
                Risk = c.Host.Options<RiskOptions>().Value, Portfolio = c.Host.Options<PortfolioOptions>().Value,
            },
            c.Host.Options<BenchmarkOptions>().Value, CliTestHost.Logger, default);

        Assert.Null(outcome.Error);
        Assert.False(outcome.Rejections!.Any(r => r.Gate == "SurveillanceClear"), string.Join("; ", outcome.Rejections!.Select(r => $"{r.Symbol}:{r.Gate}:{r.Detail}")));
        Assert.Equal(1, outcome.Ranked.Count);
    }
}
