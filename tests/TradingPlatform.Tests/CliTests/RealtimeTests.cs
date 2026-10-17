using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Yahoo;
using TradingPlatform.Execution.Paper;
using TradingPlatform.Execution.Zerodha;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class RealtimeTests
{
    private static readonly ScannerOptions Scanner = new();
    private static readonly RealtimeOptions Opts = new();
    private static DateTimeOffset Ist(int y, int m, int d, int h, int min, int sec = 0) => new DateTimeOffset(y, m, d, h, min, sec, TimeSpan.FromHours(5.5)).ToUniversalTime();
    private static readonly DateTimeOffset Now = Ist(2026, 10, 8, 11, 0);   // Thursday 11:00 IST, session ~28% elapsed

    private static HistoricalBar DayBar(string sym, DateOnly d, decimal close, long volume = 1_000_000) =>
        new() { Symbol = sym, Timestamp = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)), Open = close, High = close + 1, Low = close - 1, Close = close, AdjustedClose = close, Volume = volume, Source = "t", Timeframe = Timeframe.Daily };

    private static List<HistoricalBar> History(string sym = "AAA") => new()
    {
        DayBar(sym, new DateOnly(2026, 10, 5), 99), DayBar(sym, new DateOnly(2026, 10, 6), 99.5m), DayBar(sym, new DateOnly(2026, 10, 7), 100),
    };

    private static RealtimeQuote Quote(string sym = "AAA", decimal last = 103, DateTimeOffset? at = null, decimal prev = 100, decimal volume = 400_000) =>
        new(sym, last, 101, 104, 100.5m, prev, volume, at ?? Now.AddSeconds(-30), "Test");

    // ---------------- parsers ----------------
    private static string YahooJson(string extraMeta = "", bool withTime = true, bool withOpens = true) =>
        "{\"chart\":{\"result\":[{\"meta\":{\"regularMarketPrice\":103.5,\"regularMarketDayHigh\":104,\"regularMarketDayLow\":100.5,\"regularMarketVolume\":400000,\"chartPreviousClose\":100"
        + (withTime ? ",\"regularMarketTime\":" + Now.AddSeconds(-20).ToUnixTimeSeconds() : "") + extraMeta + "},\"timestamp\":[1,2],\"indicators\":{\"quote\":[{\"open\":"
        + (withOpens ? "[null,101.25,102]" : "[null,null]") + "}]}}],\"error\":null}}";

    [Fact]
    public void YahooQuoteParser_ReadsPriceRangeVolumePrevCloseTimeAndFirstNonNullOpen()
    {
        var (q, problem) = YahooQuoteParser.Parse(YahooJson(), "AAA");
        Assert.Null(problem);
        Assert.Equal(103.5m, q!.LastPrice); Assert.Equal(104m, q.DayHigh); Assert.Equal(100.5m, q.DayLow); Assert.Equal(100m, q.PreviousClose);
        Assert.Equal(400_000m, q.Volume); Assert.Equal(101.25m, q.Open); Assert.Equal(Now.AddSeconds(-20).ToUnixTimeSeconds(), q.QuoteTimeUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public void YahooQuoteParser_FallsBackToLastPriceForOpen_AndRefusesIncompleteQuotes()
    {
        Assert.Equal(103.5m, YahooQuoteParser.Parse(YahooJson(withOpens: false), "AAA").Quote!.Open);
        Assert.True(YahooQuoteParser.Parse(YahooJson(withTime: false), "AAA").Problem!.Contains("regularMarketTime"));     // no timestamp => freshness unknowable => no quote
        Assert.True(YahooQuoteParser.Parse("{\"chart\":{\"result\":null,\"error\":{\"description\":\"No data found\"}}}", "AAA").Problem!.Contains("No data found"));
        Assert.True(YahooQuoteParser.Parse("not json", "AAA").Problem!.Contains("unparseable"));
        Assert.True(YahooQuoteParser.Parse(YahooJson().Replace("\"regularMarketPrice\":103.5,", ""), "AAA").Problem!.Contains("regularMarketPrice"));
    }

    private static string KiteJson(string ts = "2026-10-08 10:59:40") =>
        "{\"status\":\"success\",\"data\":{\"NSE:AAA\":{\"last_price\":103.5,\"volume\":400000,\"timestamp\":\"" + ts + "\",\"ohlc\":{\"open\":101,\"high\":104,\"low\":100.5,\"close\":100}},"
        + "\"NSE:BBB\":{\"last_price\":55,\"volume\":10,\"ohlc\":{\"open\":50,\"high\":56,\"low\":49,\"close\":50}},"           // no timestamp
        + "\"NSE:CCC\":{\"last_price\":0,\"volume\":10,\"timestamp\":\"2026-10-08 10:59:40\",\"ohlc\":{\"open\":50,\"high\":56,\"low\":49,\"close\":50}}}}";   // zero price

    [Fact]
    public void KiteQuoteParser_ParsesIstTimestampToUtc_AndOmitsIncompleteEntries()
    {
        var quotes = KiteQuoteParser.Parse(200, KiteJson());
        var q = Assert.Single(quotes);
        Assert.Equal("AAA", q.Symbol); Assert.Equal(103.5m, q.LastPrice); Assert.Equal(100m, q.PreviousClose); Assert.Equal(400_000m, q.Volume);
        Assert.Equal(Ist(2026, 10, 8, 10, 59, 40), q.QuoteTimeUtc);
    }

    [Fact]
    public void KiteQuoteParser_NonSuccessStatus_Throws_AndEmptyDataIsEmpty()
    {
        Assert.Throws<KiteDataException>(() => KiteQuoteParser.Parse(403, "{}"));
        Assert.Throws<KiteDataException>(() => KiteQuoteParser.Parse(200, "garbage"));
        Assert.Empty(KiteQuoteParser.Parse(200, "{\"status\":\"success\",\"data\":{}}"));
    }

    // ---------------- overlay ----------------
    [Fact]
    public void SessionFraction_TracksTheNseSession_ClampedToZeroAndOne()
    {
        Assert.Equal(0m, RealtimeBarOverlay.SessionFraction(Ist(2026, 10, 8, 8, 0), Scanner));
        Assert.Equal(1m, RealtimeBarOverlay.SessionFraction(Ist(2026, 10, 8, 16, 0), Scanner));
        var mid = RealtimeBarOverlay.SessionFraction(Ist(2026, 10, 8, 12, 22, 30), Scanner);   // exactly halfway (09:15-15:30)
        Assert.True(Math.Abs(mid - 0.5m) < 0.001m);
    }

    [Fact]
    public void Overlay_ValidQuote_AppendsTodaysBar_KeepsHistory_AndProjectsVolume()
    {
        var history = History();
        var o = RealtimeBarOverlay.Build(history, Quote(), Now, Scanner, Opts);
        Assert.True(o.Valid, o.Rejection);
        Assert.Equal(4, o.Bars!.Count); Assert.Equal(3, history.Count);                         // input untouched
        var bar = o.Bars[^1];
        Assert.Equal(new DateOnly(2026, 10, 8), DateOnly.FromDateTime(bar.Timestamp.DateTime));
        Assert.True(bar.Open == 101m && bar.High == 104m && bar.Low == 100.5m && bar.Close == 103m);
        Assert.Equal("realtime:Test", bar.Source);
        Assert.True(bar.Volume > 1_380_000 && bar.Volume < 1_460_000, $"projected volume was {bar.Volume}");   // 400k / ~0.28 of the session
        for (var i = 1; i < o.Bars.Count; i++) Assert.True(o.Bars[i].Timestamp > o.Bars[i - 1].Timestamp);
    }

    [Fact]
    public void Overlay_EarlyInTheSession_UsesRawVolume_SoBreakoutConfirmationIsHarderNotEasier()
    {
        var early = Ist(2026, 10, 8, 9, 30);   // ~4% elapsed, below the 15% projection threshold
        var o = RealtimeBarOverlay.Build(History(), Quote(at: early.AddSeconds(-10)), early, Scanner, Opts);
        Assert.True(o.Valid, o.Rejection);
        Assert.Equal(400_000, o.Bars![^1].Volume);
    }

    [Fact]
    public void Overlay_ReplacesAProvisionalBarAlreadyStoredForToday()
    {
        var history = History(); history.Add(DayBar("AAA", new DateOnly(2026, 10, 8), 150, 5));   // a stale partial bar for today
        var o = RealtimeBarOverlay.Build(history, Quote(), Now, Scanner, Opts);
        Assert.True(o.Valid, o.Rejection);
        Assert.Equal(4, o.Bars!.Count);
        Assert.Equal(103m, o.Bars[^1].Close);
    }

    [Fact]
    public void Overlay_RejectsEveryKindOfBadQuote_WithAReason()
    {
        string? Why(RealtimeQuote q, List<HistoricalBar>? h = null, RealtimeOptions? o = null) => RealtimeBarOverlay.Build(h ?? History(), q, Now, Scanner, o ?? Opts).Rejection;

        Assert.True(Why(Quote(at: Now.AddSeconds(-900)))!.Contains("stale quote"));                        // a delayed feed looks like this
        Assert.True(Why(Quote(at: Now.AddMinutes(10)))!.Contains("future"));
        Assert.True(Why(Quote(at: Now.AddDays(-1)), o: new RealtimeOptions { MaxQuoteAgeSeconds = 10_000_000 })!.Contains("not today's session"));
        Assert.True(Why(Quote(last: 140))!.Contains("from the previous close"));                           // +40% = data error / corporate action
        Assert.True(Why(Quote(prev: 90, last: 91))!.Contains("cache is behind"));                           // quote's prev close disagrees with cached 100
        Assert.True(Why(Quote(last: 0))!.Contains("non-positive"));
        Assert.True(Why(Quote() with { DayHigh = 99, DayLow = 100 })!.Contains("below day low"));
    }

    [Fact]
    public void Overlay_WithNoCachedHistory_StillWorks_ButCannotCrossCheckPreviousClose()
    {
        var o = RealtimeBarOverlay.Build(new List<HistoricalBar>(), Quote(), Now, Scanner, Opts);
        Assert.True(o.Valid); Assert.Equal(1, o.Bars!.Count);
    }

    [Fact]
    public void Overlay_ExecutionBar_FillsAtTheLivePrice_WithTheLargerOfTodayAndLastSessionVolume()
    {
        var o = RealtimeBarOverlay.Build(History(), Quote(volume: 50_000), Now, Scanner, Opts);
        Assert.Equal(103m, o.ExecutionBar!.Open);                 // market orders reference the LIVE price, not yesterday's open
        Assert.Equal(1_000_000, o.ExecutionBar.Volume);           // early-day volume so far (50k) would manufacture phantom partial fills
        var busy = RealtimeBarOverlay.Build(History(), Quote(volume: 2_500_000), Now, Scanner, Opts);
        Assert.Equal(2_500_000, busy.ExecutionBar!.Volume);
    }

    // ---------------- session ----------------
    private sealed class FakeProvider : IRealtimeQuoteProvider
    {
        public Dictionary<string, RealtimeQuote> Quotes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Requested { get; } = new(); public bool Throw;
        public string SourceName => "Fake";
        public Task<IReadOnlyDictionary<string, RealtimeQuote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
        {
            if (Throw) throw new HttpRequestException("down");
            Requested.AddRange(symbols);
            return Task.FromResult<IReadOnlyDictionary<string, RealtimeQuote>>(symbols.Where(Quotes.ContainsKey).ToDictionary(s => s, s => Quotes[s], StringComparer.OrdinalIgnoreCase));
        }
    }

    private static RealtimeSession Session(FakeProvider p, RealtimeOptions? o = null) => new(p, o ?? Opts, Scanner, NullLogger.Instance, () => Now);
    private static IReadOnlyList<HistoricalBar> Daily(string s) => History(s);

    [Fact]
    public async Task Session_SortsSymbolsIntoValid_Rejected_AndNoQuote()
    {
        var p = new FakeProvider();
        p.Quotes["GOOD"] = Quote("GOOD"); p.Quotes["STALE"] = Quote("STALE", at: Now.AddMinutes(-30));
        var s = Session(p);
        await s.RefreshAsync(new[] { "GOOD", "STALE", "MISSING" }, Daily, default);

        Assert.NotNull(s.OverlayBars("GOOD")); Assert.NotNull(s.ExecutionBar("GOOD"));
        Assert.Null(s.OverlayBars("STALE")); Assert.True(s.WhyNoOverlay("STALE")!.Contains("stale"));
        Assert.True(s.WhyNoOverlay("MISSING")!.Contains("no quote returned by Fake"));
        Assert.True(s.WhyNoOverlay("NEVERASKED")!.Contains("not requested"));
        Assert.Equal(3, s.Requested); Assert.Equal(1, s.ValidCount); Assert.Equal(2, s.Problems.Count);
        Assert.True(s.Summary().Contains("1 valid of 3 requested"));
    }

    [Fact]
    public async Task Session_NeverRequestsASymbolTwice_AndHonoursTheCycleCap()
    {
        var p = new FakeProvider(); foreach (var x in new[] { "A", "B", "C" }) p.Quotes[x] = Quote(x);
        var s = Session(p, new RealtimeOptions { MaxSymbolsPerCycle = 2 });
        await s.RefreshAsync(new[] { "A", "B" }, Daily, default);
        await s.RefreshAsync(new[] { "A", "B", "C" }, Daily, default);
        Assert.Equal(2, p.Requested.Count);                                    // A,B once; C over the cap
        Assert.True(s.WhyNoOverlay("C")!.Contains("not requested"));
    }

    [Fact]
    public async Task Session_ProviderOutage_DegradesToDailyBars_WithoutThrowing()
    {
        var p = new FakeProvider { Throw = true };
        var s = Session(p);
        await s.RefreshAsync(new[] { "A", "B" }, Daily, default);
        Assert.Equal(0, s.ValidCount); Assert.Equal(2, s.Problems.Count);
        Assert.Null(s.OverlayBars("A"));
    }

    [Fact]
    public async Task RealtimeQuoteSource_UsesTheLiveBar_ElseFallsBack_AndRecordsTheStaleFills()
    {
        var p = new FakeProvider(); p.Quotes["LIVE"] = Quote("LIVE");
        var s = Session(p); await s.RefreshAsync(new[] { "LIVE", "OLD" }, Daily, default);
        var cachedBar = DayBar("X", new DateOnly(2026, 10, 7), 100);
        var src = new RealtimeQuoteSource(new FixedQuotes(cachedBar), s);

        var live = await src.GetQuoteAsync("LIVE");
        Assert.Equal(103m, live.Bar.Open); Assert.Equal(123m, live.AverageDailyTradedValue);        // liquidity/bucket still come from the cached source
        var old = await src.GetQuoteAsync("OLD");
        Assert.Equal(100m, old.Bar.Open);
        Assert.Equal("OLD", Assert.Single(src.SymbolsFilledOnStaleData));
    }

    private sealed class FixedQuotes : IQuoteSource
    {
        private readonly HistoricalBar _bar; public FixedQuotes(HistoricalBar bar) => _bar = bar;
        public Task<PaperQuote> GetQuoteAsync(string symbol, CancellationToken ct = default) => Task.FromResult(new PaperQuote(_bar, 123m, MarketCapBucket.Small));
    }

    // ---------------- providers + factory ----------------
    private sealed class FakeIntraday : IYahooIntradayFetcher
    {
        public List<string> Asked { get; } = new();
        public Task<string> FetchIntradayRawJsonAsync(string symbol, CancellationToken ct = default)
        {
            Asked.Add(symbol);
            if (symbol.StartsWith("BAD")) throw new YahooHttpException(symbol, 404, "nf");
            return Task.FromResult(YahooJson());
        }
    }

    [Fact]
    public async Task YahooRealtimeProvider_AppliesSuffix_SkipsFailures_AndNeverThrowsForOneSymbol()
    {
        var f = new FakeIntraday();
        var p = new YahooRealtimeQuoteProvider(f, new YahooFinanceOptions(), NullLogger.Instance);
        var q = await p.GetQuotesAsync(new[] { "AAA", "BADCO", "^CRSLDX", "AAA" }, default);
        Assert.Equal(2, q.Count); Assert.True(q.ContainsKey("AAA") && q.ContainsKey("^CRSLDX")); Assert.False(q.ContainsKey("BADCO"));
        Assert.True(f.Asked.Contains("AAA.NS") && f.Asked.Contains("^CRSLDX") && f.Asked.Count(x => x == "AAA.NS") == 1);   // de-duplicated; index untouched
    }

    private static ServiceProvider Services(bool intraday)
    {
        var sc = new ServiceCollection(); sc.AddHttpClient(); sc.AddSingleton<IOptions<YahooFinanceOptions>>(Options.Create(new YahooFinanceOptions()));
        if (intraday) sc.AddSingleton<IYahooIntradayFetcher>(new FakeIntraday());
        return sc.BuildServiceProvider();
    }

    [Fact]
    public void Factory_AutoWithoutCredentials_UsesYahoo_ExplicitKiteWithoutCredentialsRefusesToDowngrade()
    {
        var z = new ZerodhaOptions { ApiKeyEnvVar = "TP_TEST_NO_SUCH_KEY", AccessTokenEnvVar = "TP_TEST_NO_SUCH_TOKEN" };
        using var sp = Services(true);
        var auto = RealtimeProviderFactory.Create(sp, new RealtimeOptions { Provider = "Auto" }, z, NullLogger.Instance, out var p1);
        Assert.Equal("Yahoo", auto!.SourceName); Assert.Null(p1);

        var kite = RealtimeProviderFactory.Create(sp, new RealtimeOptions { Provider = "Kite" }, z, NullLogger.Instance, out var p2);
        Assert.Null(kite); Assert.True(p2!.Contains("TP_TEST_NO_SUCH_KEY"));                       // does NOT silently fall back to Yahoo

        Assert.Null(RealtimeProviderFactory.Create(sp, new RealtimeOptions { Provider = "bogus" }, z, NullLogger.Instance, out var p3));
        Assert.True(p3!.Contains("bogus"));
        using var none = Services(false);
        Assert.Null(RealtimeProviderFactory.Create(none, new RealtimeOptions { Provider = "Yahoo" }, z, NullLogger.Instance, out var p4));
        Assert.NotNull(p4);
    }

    [Fact]
    public void Factory_AutoWithCredentials_PrefersKite()
    {
        Environment.SetEnvironmentVariable("TP_TEST_KEY", "k"); Environment.SetEnvironmentVariable("TP_TEST_TOKEN", "t");
        try
        {
            var z = new ZerodhaOptions { ApiKeyEnvVar = "TP_TEST_KEY", AccessTokenEnvVar = "TP_TEST_TOKEN" };
            using var sp = Services(true);
            Assert.Equal("Kite", RealtimeProviderFactory.Create(sp, new RealtimeOptions { Provider = "Auto" }, z, NullLogger.Instance, out _)!.SourceName);
            Assert.Equal("Yahoo", RealtimeProviderFactory.Create(sp, new RealtimeOptions { Provider = "Yahoo" }, z, NullLogger.Instance, out _)!.SourceName);
        }
        finally { Environment.SetEnvironmentVariable("TP_TEST_KEY", null); Environment.SetEnvironmentVariable("TP_TEST_TOKEN", null); }
    }
}
