using TradingPlatform.Application.Cli;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class UniverseManagementTests
{
    private static readonly DateOnly Today = new(2026, 10, 7);

    private sealed class Ctx : IDisposable
    {
        public readonly CliTestHost Host = CliTestHost.Create();
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "tp_uni_" + Guid.NewGuid().ToString("N"));
        public IInstrumentRepository Instruments => Host.Repo<IInstrumentRepository>();
        public IUniverseClassificationRepository Classifications => Host.Repo<IUniverseClassificationRepository>();
        public ISurveillanceRepository Surveillance => Host.Repo<ISurveillanceRepository>();
        public IHistoricalBarRepository Bars => Host.Repo<IHistoricalBarRepository>();
        public UniverseServices Services => new(Host.Db, Instruments, Classifications, Surveillance, new SqliteFundamentalRepository(Host.Db), new SqliteInstitutionalActivityRepository(Host.Db), Bars, Host.Repo<IEngineStateRepository>(), new ScannerOptions());
        public Ctx() => Directory.CreateDirectory(Dir);
        public string File(string name, string content) { var p = Path.Combine(Dir, name); System.IO.File.WriteAllText(p, content); return p; }
        public void Dispose() { Host.Dispose(); try { Directory.Delete(Dir, true); } catch { } }

        public async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var code = await UniverseCommand.RunAsync(new[] { "universe" }.Concat(args).ToArray(), Services, CliTestHost.Logger, o, e, Today);
            return (code, o.ToString(), e.ToString());
        }

        public async Task<ImportResult> ImportAsync(string kind, string csv, ImportOptions? options = null, bool dryRun = false) =>
            await ImportCommand.ImportAsync(kind, CsvTable.Parse(csv), "t.csv", dryRun, Host.Db, Instruments, Classifications, Surveillance,
                new SqliteFundamentalRepository(Host.Db), new SqliteInstitutionalActivityRepository(Host.Db), default, options);
    }

    // The shape of the real universe.csv: lowercase 'symbol', zeros for unknown numbers, 'NA' industries, a duplicated symbol, a shared ISIN.
    private const string RealShape =
        "symbol,CompanyName,Isin,Sector,Industry,MarketCap,AverageDailyTurnover,LotSize\n" +
        "AAA,Aaa Ltd,INE000000001,,Chemicals,0,0,1\n" +
        "BBB,Bbb Ltd,INE000000002,,NA,0,0,10\n" +
        "CCC,Ccc Ltd,INE000000002,,Pharma,0,0,5\n" +
        "AAA,Aaa Ltd,INE000000001,,Chemicals,0,0,2\n";

    // ---- importer policies ----
    [Fact]
    public async Task Import_RealFileShape_Succeeds_DuplicateKeepsFirst_WithWarningNamingTheDifferingColumn()
    {
        using var c = new Ctx();
        var r = await c.ImportAsync("universe", RealShape);
        Assert.True(r.Ok); Assert.Equal(3, r.Written);
        Assert.True(r.Warnings!.Any(w => w.Contains("duplicate symbol AAA") && w.Contains("LotSize ('1' vs '2')")));
        Assert.Equal(1, (await c.Instruments.GetBySymbolAsync("AAA"))!.LotSize);   // the FIRST row won
    }

    [Fact]
    public async Task Import_DuplicatePolicyLast_UsesTheLaterRow_AndErrorPolicyRejects()
    {
        using var c = new Ctx();
        Assert.True((await c.ImportAsync("universe", RealShape, new ImportOptions(DuplicatePolicy.Last))).Ok);
        Assert.Equal(2, (await c.Instruments.GetBySymbolAsync("AAA"))!.LotSize);
        var strict = await c.ImportAsync("universe", RealShape.Replace("AAA,Aaa Ltd,INE000000001,,Chemicals,0,0,1", "AAA,Aaa Ltd,INE000000001,,Chemicals,0,0,1"), new ImportOptions(DuplicatePolicy.Error));
        Assert.False(strict.Ok);
        Assert.True(strict.Errors.Any(x => x.Contains("duplicate symbol AAA")));
    }

    [Fact]
    public async Task Import_ZeroMarketCapAndTurnover_AreStoredAsUnknownNull_AndNaIndustryIsNull()
    {
        using var c = new Ctx();
        await c.ImportAsync("universe", RealShape);
        var bbb = (await c.Instruments.GetBySymbolAsync("BBB"))!;
        Assert.Null(bbb.MarketCap); Assert.Null(bbb.AverageDailyTurnover); Assert.Null(bbb.Industry);
        Assert.Equal("Pharma", (await c.Instruments.GetBySymbolAsync("CCC"))!.Industry);
    }

    [Fact]
    public async Task Import_SharedIsin_WarnsButKeepsBothSymbols()
    {
        using var c = new Ctx();
        var r = await c.ImportAsync("universe", RealShape);
        Assert.True(r.Warnings!.Any(w => w.Contains("INE000000002") && w.Contains("BBB") && w.Contains("CCC")));
        Assert.NotNull(await c.Instruments.GetBySymbolAsync("BBB")); Assert.NotNull(await c.Instruments.GetBySymbolAsync("CCC"));
    }

    [Fact]
    public async Task MarketCapKind_UpdatesOnlyThoseColumns_RejectsUnknownSymbolAndNonPositive()
    {
        using var c = new Ctx();
        await c.ImportAsync("universe", RealShape);
        var ok = await c.ImportAsync("marketcap", "Symbol,MarketCap,AverageDailyTurnover\nAAA,12500,0\nBBB,3000,45000000\n");
        Assert.True(ok.Ok);
        var aaa = (await c.Instruments.GetBySymbolAsync("AAA"))!;
        Assert.Equal(12500m, aaa.MarketCap);
        Assert.Equal("Chemicals", aaa.Industry);            // untouched
        Assert.Equal("Aaa Ltd", aaa.CompanyName);           // untouched
        Assert.Null(aaa.AverageDailyTurnover);              // 0 = unknown, not stored
        Assert.Equal(45000000m, (await c.Instruments.GetBySymbolAsync("BBB"))!.AverageDailyTurnover);

        var bad = await c.ImportAsync("marketcap", "Symbol,MarketCap\nNOPE,100\nAAA,0\n");
        Assert.False(bad.Ok);
        Assert.True(bad.Errors.Any(x => x.Contains("not in the universe")) && bad.Errors.Any(x => x.Contains("positive")));
    }

    [Fact]
    public async Task DeactivateMissing_IsOptIn_AndReversible()
    {
        using var c = new Ctx();
        await c.ImportAsync("universe", RealShape);
        await c.ImportAsync("universe", "Symbol,CompanyName\nAAA,Aaa Ltd\n");                       // default: others untouched
        Assert.Equal(3, (await c.Instruments.GetActiveAsync()).Count);
        var dry = await c.ImportAsync("universe", "Symbol,CompanyName\nAAA,Aaa Ltd\n", new ImportOptions(DeactivateMissing: true), dryRun: true);
        Assert.Equal(2, dry.Deactivated); Assert.Equal(3, (await c.Instruments.GetActiveAsync()).Count);   // dry run changes nothing
        var real = await c.ImportAsync("universe", "Symbol,CompanyName\nAAA,Aaa Ltd\n", new ImportOptions(DeactivateMissing: true));
        Assert.Equal(2, real.Deactivated);
        Assert.Equal(1, (await c.Instruments.GetActiveAsync()).Count);
        Assert.Equal(3, (await c.Instruments.GetAllAsync()).Count);                                   // data kept
        Assert.True(await c.Instruments.SetActiveAsync("BBB", true)); Assert.Equal(2, (await c.Instruments.GetActiveAsync()).Count);
    }

    // ---- the universe command ----
    [Fact]
    public async Task Command_ImportListShowStats_WorkTogether()
    {
        using var c = new Ctx();
        var path = c.File("u.csv", RealShape);
        var imp = await c.RunAsync("import", "--file", path);
        Assert.Equal(0, imp.Code);

        var list = await c.RunAsync("list");
        Assert.True(list.Out.Contains("AAA") && list.Out.Contains("CCC") && list.Out.Contains("3 of 3"), "LIST: " + list.Out);
        var search = await c.RunAsync("list", "--search", "pharma");
        Assert.True(search.Out.Contains("1 of 1"), "SEARCH: " + search.Out);

        var show = await c.RunAsync("show", "--symbol", "AAA");
        Assert.True(show.Out.Contains("Aaa Ltd") && show.Out.Contains("never recorded") && show.Out.Contains("none cached"), "SHOW: " + show.Out);
        Assert.Equal(1, (await c.RunAsync("show", "--symbol", "ZZZ")).Code);

        var stats = await c.RunAsync("stats");
        Assert.True(stats.Out.Contains("3 total, 3 active") && stats.Out.Contains("3 UNKNOWN") && stats.Out.Contains("Last download: never"), "STATS: " + stats.Out);
    }

    [Fact]
    public async Task Command_ActivateDeactivate_AndMissingSymbolIsReported()
    {
        using var c = new Ctx();
        await c.RunAsync("import", "--file", c.File("u.csv", RealShape));
        Assert.Equal(0, (await c.RunAsync("deactivate", "--symbols", "AAA,BBB")).Code);
        Assert.Equal(1, (await c.Instruments.GetActiveAsync()).Count);
        Assert.Equal(1, (await c.RunAsync("list", "--inactive")).Out.Contains("2 of 2") ? 1 : 0);
        var missing = await c.RunAsync("activate", "--symbol", "NOPE");
        Assert.Equal(1, missing.Code); Assert.True(missing.Err.Contains("NOPE"));
        Assert.Equal(1, (await c.RunAsync("activate")).Code);
    }

    [Fact]
    public async Task Command_ClassifySingle_WritesPointInTimeSnapshot_AndShowListsHistory()
    {
        using var c = new Ctx();
        await c.RunAsync("import", "--file", c.File("u.csv", RealShape));
        Assert.Equal(0, (await c.RunAsync("classify", "--symbol", "AAA", "--bucket", "small", "--rank", "320", "--from", "2026-01-01")).Code);
        Assert.Equal(0, (await c.RunAsync("classify", "--symbol", "AAA", "--bucket", "Mid", "--from", "2026-09-01")).Code);
        Assert.Equal(MarketCapBucket.Small, (await c.Classifications.GetClassificationAsync("AAA", new DateOnly(2026, 8, 1)))!.MarketCapBucket);
        Assert.Equal(MarketCapBucket.Mid, (await c.Classifications.GetClassificationAsync("AAA", Today))!.MarketCapBucket);
        var show = await c.RunAsync("show", "--symbol", "AAA");
        Assert.True(show.Out.Contains("Small (rank 320)") && show.Out.Contains("Mid"));
        Assert.Equal(1, (await c.RunAsync("classify", "--symbol", "AAA", "--bucket", "Huge")).Code);
        Assert.Equal(1, (await c.RunAsync("classify", "--symbol", "NOPE", "--bucket", "Mid")).Code);
    }

    [Fact]
    public async Task Command_ClassifyAuto_RequiresAbsoluteCutoffs_AndSkipsSymbolsWithoutMarketCap()
    {
        using var c = new Ctx();
        await c.RunAsync("import", "--file", c.File("u.csv", RealShape));
        await c.RunAsync("import", "--kind", "marketcap", "--file", c.File("mc.csv", "Symbol,MarketCap\nAAA,120000\nBBB,40000\nCCC,3000\n"));
        await c.Instruments.UpsertAsync(new Instrument { Symbol = "DDD", NseSymbol = "DDD", YahooSymbol = "DDD.NS", CompanyName = "Ddd", Exchange = "NSE", Active = true }); // no market cap

        var none = await c.RunAsync("classify", "--auto");
        Assert.Equal(1, none.Code); Assert.True(none.Err.Contains("--large-min"));
        var order = await c.RunAsync("classify", "--auto", "--large-min", "100", "--mid-min", "500", "--small-min", "50");
        Assert.Equal(1, order.Code); Assert.True(order.Err.Contains("--large-min > --mid-min"));

        var ok = await c.RunAsync("classify", "--auto", "--large-min", "100000", "--mid-min", "30000", "--small-min", "10000", "--from", "2026-10-01");
        Assert.Equal(0, ok.Code);
        Assert.Equal(MarketCapBucket.Large, (await c.Classifications.GetClassificationAsync("AAA", Today))!.MarketCapBucket);
        Assert.Equal(MarketCapBucket.Mid, (await c.Classifications.GetClassificationAsync("BBB", Today))!.MarketCapBucket);
        Assert.Equal(MarketCapBucket.Micro, (await c.Classifications.GetClassificationAsync("CCC", Today))!.MarketCapBucket);   // below small-min
        Assert.Null(await c.Classifications.GetClassificationAsync("DDD", Today));                                             // unknown cap stays unclassified
        Assert.True(ok.Out.Contains("1 active instrument(s) have NO market cap"));

        await c.RunAsync("classify", "--auto", "--large-min", "100000", "--mid-min", "30000", "--small-min", "10000", "--from", "2026-10-01");
        Assert.Equal(1, (await c.Classifications.GetHistoryAsync("AAA")).Count);                                               // re-run is idempotent
    }

    // ---- surveillance ----
    [Fact]
    public async Task Command_SurveillanceSingle_AndFutureDateRejected()
    {
        using var c = new Ctx();
        await c.RunAsync("import", "--file", c.File("u.csv", RealShape));
        Assert.Equal(0, (await c.RunAsync("surveillance", "--symbol", "AAA", "--flags", "ASM|LiquidityRisk")).Code);
        var s = await c.Surveillance.GetStatusAsync("AAA", Today);
        Assert.Equal(RiskFlags.Asm | RiskFlags.LiquidityRisk, s.Flags); Assert.Equal(DataAvailability.Available, s.Availability);
        Assert.Equal(0, (await c.RunAsync("surveillance", "--symbol", "BBB", "--flags", "none")).Code);
        Assert.Equal(RiskFlags.None, (await c.Surveillance.GetStatusAsync("BBB", Today)).Flags);
        Assert.Equal(1, (await c.RunAsync("surveillance", "--symbol", "AAA", "--flags", "BOGUS")).Code);
        Assert.Equal(1, (await c.RunAsync("surveillance", "--symbol", "AAA", "--flags", "ASM", "--as-of", "2026-12-31")).Code);
    }

    [Fact]
    public async Task Command_SurveillanceLists_UnlistedStayUnknownUnlessAssumedClear()
    {
        using var c = new Ctx();
        await c.RunAsync("import", "--file", c.File("u.csv", RealShape));
        var asm = c.File("asm.csv", "Symbol,Name\nAAA,Aaa\nUNKNOWN1,Nope\n");
        var gsm = c.File("gsm.txt", "Symbol\nAAA\nBBB\n");

        var partial = await c.RunAsync("surveillance", "--asm-file", asm, "--gsm-file", gsm);
        Assert.Equal(0, partial.Code);
        Assert.True(partial.Out.Contains("rest stay Unknown"));
        Assert.Equal(RiskFlags.Asm | RiskFlags.Gsm, (await c.Surveillance.GetStatusAsync("AAA", Today)).Flags);
        Assert.Equal(RiskFlags.Gsm, (await c.Surveillance.GetStatusAsync("BBB", Today)).Flags);
        Assert.Equal(DataAvailability.Unknown, (await c.Surveillance.GetStatusAsync("CCC", Today)).Availability);    // NOT assumed clear

        var full = await c.RunAsync("surveillance", "--asm-file", asm, "--gsm-file", gsm, "--assume-unlisted-clear");
        Assert.Equal(0, full.Code);
        Assert.Equal(DataAvailability.Available, (await c.Surveillance.GetStatusAsync("CCC", Today)).Availability);
        Assert.Equal(RiskFlags.None, (await c.Surveillance.GetStatusAsync("CCC", Today)).Flags);
    }

    [Fact]
    public async Task Command_SurveillanceClearAll_IsExplicit_AndCannotCombineWithLists()
    {
        using var c = new Ctx();
        await c.RunAsync("import", "--file", c.File("u.csv", RealShape));
        Assert.Equal(1, (await c.RunAsync("surveillance")).Code);   // nothing specified = nothing happens, never an implicit "all clear"
        Assert.Equal(DataAvailability.Unknown, (await c.Surveillance.GetStatusAsync("AAA", Today)).Availability);
        Assert.Equal(1, (await c.RunAsync("surveillance", "--clear-all", "--asm-file", c.File("a.csv", "Symbol\nAAA\n"))).Code);
        Assert.Equal(0, (await c.RunAsync("surveillance", "--clear-all")).Code);
        Assert.True((await c.Instruments.GetActiveAsync()).All(i => c.Surveillance.GetStatusAsync(i.Symbol, Today).Result.Availability == DataAvailability.Available));
    }

    [Fact]
    public async Task SurveillanceStaleness_FridayListCoversMonday_ButNotALongStaleList()
    {
        using var c = new Ctx();
        await c.Surveillance.UpsertAsync(new SurveillanceStatus { Symbol = "AAA", AsOfDate = new DateOnly(2026, 10, 2), Flags = RiskFlags.Asm, Availability = DataAvailability.Available, Source = "t" }); // Friday
        var monday = new DateOnly(2026, 10, 5);
        var within = await c.Surveillance.GetLatestStatusAsync("AAA", monday, 3);
        Assert.Equal(DataAvailability.Available, within.Availability); Assert.Equal(RiskFlags.Asm, within.Flags);
        Assert.Equal(DataAvailability.Unknown, (await c.Surveillance.GetLatestStatusAsync("AAA", new DateOnly(2026, 10, 6), 3)).Availability);   // 4 days old
        Assert.Equal(DataAvailability.Unknown, (await c.Surveillance.GetLatestStatusAsync("AAA", monday, 0)).Availability);                     // exact-date mode
        Assert.Equal(DataAvailability.Unknown, (await c.Surveillance.GetLatestStatusAsync("AAA", new DateOnly(2026, 10, 1), 3)).Availability);  // never a FUTURE status
    }

    [Fact]
    public async Task OldImportAlias_StillRoutesThroughUniverseImport()
    {
        using var c = new Ctx();
        // The CLI dispatcher rewrites `import ...` to `universe import ...`; this pins that `universe import --kind X` accepts the legacy flag set.
        var r = await c.RunAsync("import", "--kind", "universe", "--file", c.File("u.csv", RealShape), "--dry-run");
        Assert.Equal(0, r.Code);
        Assert.Equal(0, (await c.Instruments.GetAllAsync()).Count);
    }
}
