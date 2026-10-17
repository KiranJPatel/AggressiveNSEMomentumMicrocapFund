using TradingPlatform.Application.Cli;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class DataImportTests
{
    private sealed class Ctx : IDisposable
    {
        public readonly CliTestHost Host = CliTestHost.Create();
        public IInstrumentRepository Instruments => Host.Repo<IInstrumentRepository>();
        public IUniverseClassificationRepository Classifications => Host.Repo<IUniverseClassificationRepository>();
        public ISurveillanceRepository Surveillance => Host.Repo<ISurveillanceRepository>();
        public SqliteFundamentalRepository Fundamentals => new(Host.Db);
        public SqliteInstitutionalActivityRepository Institutional => new(Host.Db);
        public void Dispose() => Host.Dispose();

        public Task<ImportResult> ImportAsync(string kind, string csv, bool dryRun = false) =>
            ImportCommand.ImportAsync(kind, CsvTable.Parse(csv), "t.csv", dryRun, Host.Db, Instruments, Classifications, Surveillance, Fundamentals, Institutional);
    }

    private const string Universe = "Symbol,CompanyName,Sector,MarketCap,LotSize\nABC,Abc Ltd,Capital Goods,25000,1\nXYZ,Xyz Ltd,,,\n";

    // ---- CSV reader ----
    [Fact]
    public void Csv_HandlesQuotesCommasNewlinesBomAndCrlf()
    {
        var t = CsvTable.Parse("\uFEFFSymbol,CompanyName\r\nABC,\"Abc, \"\"Best\"\" Ltd\"\r\n\r\nXYZ,\"Two\nLines\"\r\n");
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("Abc, \"Best\" Ltd", t.Rows[0].Get("CompanyName"));
        Assert.Equal("Two\nLines", t.Rows[1].Get("companyname"));
        Assert.Null(t.Rows[1].Get("Nope"));
    }

    [Fact]
    public void Csv_Unterminated_Quote_Throws()
    {
        Assert.Throws<FormatException>(() => CsvTable.Parse("a,b\n1,\"oops\n"));
    }

    // ---- universe ----
    [Fact]
    public async Task Universe_WritesInstruments_WithSafeDefaults_AndBlanksStayNull()
    {
        using var c = new Ctx();
        var r = await c.ImportAsync("universe", Universe);
        Assert.True(r.Ok); Assert.Equal(2, r.Written);
        var xyz = (await c.Instruments.GetBySymbolAsync("XYZ"))!;
        Assert.Equal("XYZ.NS", xyz.YahooSymbol);
        Assert.Equal("NSE", xyz.Exchange);
        Assert.True(xyz.Active);
        Assert.Null(xyz.MarketCap);            // blank stays null — never fabricated
        Assert.Null(xyz.Sector);
        Assert.Equal(25000m, (await c.Instruments.GetBySymbolAsync("ABC"))!.MarketCap);
    }

    [Fact]
    public async Task Import_IsAllOrNothing_OneBadRowWritesNothing_AndReportsEveryErrorWithLineNumbers()
    {
        using var c = new Ctx();
        var r = await ImportCommand.ImportAsync("universe", CsvTable.Parse("Symbol,CompanyName,MarketCap\nGOOD,Good Ltd,100\nBAD,Bad Ltd,notanumber\nGOOD,Dup Ltd,5\n"), "t.csv", false, c.Host.Db,
            c.Instruments, c.Classifications, c.Surveillance, c.Fundamentals, c.Institutional, default, new ImportOptions(DuplicatePolicy.Error));
        Assert.False(r.Ok);
        Assert.Equal(0, r.Written);
        Assert.Equal(2, r.Errors.Count);
        Assert.True(r.Errors.Any(e => e.StartsWith("line 3:") && e.Contains("MarketCap")));
        Assert.True(r.Errors.Any(e => e.StartsWith("line 4:") && e.Contains("duplicate")));
        Assert.Null(await c.Instruments.GetBySymbolAsync("GOOD"));
    }

    [Fact]
    public async Task DryRun_ValidatesButWritesNothing()
    {
        using var c = new Ctx();
        var r = await c.ImportAsync("universe", Universe, dryRun: true);
        Assert.True(r.Ok); Assert.Equal(0, r.Written);
        Assert.Null(await c.Instruments.GetBySymbolAsync("ABC"));
    }

    [Fact]
    public async Task MissingRequiredColumn_IsReported()
    {
        using var c = new Ctx();
        var r = await c.ImportAsync("universe", "Symbol,Sector\nABC,X\n");
        Assert.False(r.Ok);
        Assert.True(r.Errors[0].Contains("CompanyName"));
    }

    [Fact]
    public async Task Reimport_IsIdempotent()
    {
        using var c = new Ctx();
        await c.ImportAsync("universe", Universe);
        await c.ImportAsync("universe", Universe);
        Assert.Equal(2, (await c.Instruments.GetActiveAsync()).Count);
    }

    // ---- classification + sector provider ----
    [Fact]
    public async Task Classification_RequiresKnownInstrument_AndResolvesPointInTime()
    {
        using var c = new Ctx();
        var orphan = await c.ImportAsync("classification", "Symbol,MarketCapBucket,EffectiveFrom\nNOPE,Small,2026-01-01\n");
        Assert.False(orphan.Ok);
        Assert.True(orphan.Errors[0].Contains("not in the universe"));

        await c.ImportAsync("universe", Universe);
        var r = await c.ImportAsync("classification", "Symbol,MarketCapBucket,Sector,EffectiveFrom,EffectiveTo\nABC,Small,Industrials,2026-01-01,2026-06-30\nABC,Mid,Industrials,2026-07-01,\n");
        Assert.True(r.Ok);
        Assert.Equal(MarketCapBucket.Small, (await c.Classifications.GetClassificationAsync("ABC", new DateOnly(2026, 3, 1)))!.MarketCapBucket);
        Assert.Equal(MarketCapBucket.Mid, (await c.Classifications.GetClassificationAsync("ABC", new DateOnly(2026, 9, 1)))!.MarketCapBucket);
        Assert.Null(await c.Classifications.GetClassificationAsync("ABC", new DateOnly(2025, 1, 1)));

        // re-import same EffectiveFrom must not create duplicate rows
        await c.ImportAsync("classification", "Symbol,MarketCapBucket,EffectiveFrom\nABC,Micro,2026-07-01\n");
        Assert.Equal(MarketCapBucket.Micro, (await c.Classifications.GetClassificationAsync("ABC", new DateOnly(2026, 9, 1)))!.MarketCapBucket);
    }

    [Fact]
    public async Task SectorProvider_PrefersClassification_FallsBackToInstrument_ElseNull()
    {
        using var c = new Ctx();
        await c.ImportAsync("universe", Universe);                 // ABC sector "Capital Goods", XYZ none
        var sectors = new SqliteSectorClassificationProvider(c.Classifications, c.Instruments);
        var d = new DateOnly(2026, 8, 1);
        Assert.Equal("Capital Goods", await sectors.GetSectorAsync("ABC", d));
        Assert.Null(await sectors.GetSectorAsync("XYZ", d));        // unknown stays unknown
        await c.ImportAsync("classification", "Symbol,MarketCapBucket,Sector,EffectiveFrom\nABC,Small,Defence,2026-01-01\n");
        Assert.Equal("Defence", await sectors.GetSectorAsync("ABC", d));
    }

    // ---- surveillance ----
    [Fact]
    public async Task Surveillance_ParsesFlags_AndRejectsUnknownFlagNames()
    {
        using var c = new Ctx();
        var ok = await c.ImportAsync("surveillance", "Symbol,AsOfDate,Flags\nABC,2026-10-05,ASM|LiquidityRisk\nXYZ,2026-10-05,\n");
        Assert.True(ok.Ok);
        var abc = await c.Surveillance.GetStatusAsync("ABC", new DateOnly(2026, 10, 5));
        Assert.Equal(RiskFlags.Asm | RiskFlags.LiquidityRisk, abc.Flags);
        Assert.Equal(DataAvailability.Available, abc.Availability);
        Assert.Equal(DataAvailability.Available, (await c.Surveillance.GetStatusAsync("XYZ", new DateOnly(2026, 10, 5))).Availability);
        Assert.Equal(DataAvailability.Unknown, (await c.Surveillance.GetStatusAsync("ABC", new DateOnly(2026, 10, 6))).Availability); // other date: Unknown, never "Safe"

        var bad = await c.ImportAsync("surveillance", "Symbol,AsOfDate,Flags\nABC,2026-10-05,SUPERBAD\n");
        Assert.False(bad.Ok);
    }

    // ---- fundamentals / institutional ----
    [Fact]
    public async Task Fundamentals_BlankGrowthIsUnavailable_NotZero_AndProviderIsPointInTime()
    {
        using var c = new Ctx();
        var r = await c.ImportAsync("fundamentals", "Symbol,AsOfDate,EarningsGrowthPercent,RevenueGrowthPercent\nABC,2026-06-30,42.5,\nABC,2026-09-30,55,18\n");
        Assert.True(r.Ok);

        var q2 = await c.Fundamentals.GetFundamentalsAsync("ABC", new DateOnly(2026, 8, 1));       // must not see the future Sep-30 snapshot
        Assert.Equal(new DateOnly(2026, 6, 30), q2.AsOfDate);
        Assert.Equal(42.5m, q2.EarningsGrowthPercent);
        Assert.Equal(DataAvailability.Available, q2.EarningsGrowthAvailability);
        Assert.Null(q2.RevenueGrowthPercent);
        Assert.Equal(DataAvailability.Unavailable, q2.RevenueGrowthAvailability);

        var none = await c.Fundamentals.GetFundamentalsAsync("ABC", new DateOnly(2026, 1, 1));
        Assert.Equal(DataAvailability.Unknown, none.EarningsGrowthAvailability);
        Assert.Null(none.EarningsGrowthPercent);
    }

    [Fact]
    public async Task Institutional_ImportsScore_AndUnknownWhenAbsent()
    {
        using var c = new Ctx();
        Assert.True((await c.ImportAsync("institutional", "Symbol,AsOfDate,ActivityScore\nABC,2026-09-30,72.5\n")).Ok);
        var s = await c.Institutional.GetActivityAsync("ABC", new DateOnly(2026, 10, 5));
        Assert.Equal(72.5m, s.ActivityScore);
        Assert.Equal(DataAvailability.Unknown, (await c.Institutional.GetActivityAsync("ZZZ", new DateOnly(2026, 10, 5))).Availability);
    }

    // ---- enrichment contract ----
    [Fact]
    public async Task Enrichment_PassesNullWhenNothingImported_SoUnimportedDataCannotChangeScores()
    {
        using var c = new Ctx();
        var (f, i, _) = await ScannerEnrichment.LoadAsync(c.Fundamentals, c.Institutional, "ABC", new DateOnly(2026, 10, 5), default);
        Assert.Null(f); Assert.Null(i);

        await c.ImportAsync("fundamentals", "Symbol,AsOfDate,EarningsGrowthPercent\nABC,2026-09-30,30\n");
        (f, i, _) = await ScannerEnrichment.LoadAsync(c.Fundamentals, c.Institutional, "ABC", new DateOnly(2026, 10, 5), default);
        Assert.NotNull(f); Assert.Equal(30m, f!.EarningsGrowthPercent); Assert.Null(i);
    }

    [Fact]
    public async Task Enrichment_ProvidesThePriorSnapshot_ForAcceleration_OnlyWhenOneExists()
    {
        using var c = new Ctx();
        await c.ImportAsync("fundamentals", "Symbol,AsOfDate,EarningsGrowthPercent\nABC,2026-09-30,50\n");
        var (f, _, prior) = await ScannerEnrichment.LoadAsync(c.Fundamentals, c.Institutional, "ABC", new DateOnly(2026, 10, 5), default);
        Assert.NotNull(f); Assert.Null(prior);                         // a single snapshot has no prior — never invented

        await c.ImportAsync("fundamentals", "Symbol,AsOfDate,EarningsGrowthPercent\nABC,2026-06-30,20\n");
        (f, _, prior) = await ScannerEnrichment.LoadAsync(c.Fundamentals, c.Institutional, "ABC", new DateOnly(2026, 10, 5), default);
        Assert.Equal(50m, f!.EarningsGrowthPercent);
        Assert.Equal(20m, prior!.EarningsGrowthPercent);
        Assert.Equal(new DateOnly(2026, 6, 30), prior.AsOfDate);
    }
}
