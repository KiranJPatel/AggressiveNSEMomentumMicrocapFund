using Microsoft.Extensions.Logging.Abstractions;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class DeliveryImportTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), $"tp_deliv_{Guid.NewGuid():N}"); Directory.CreateDirectory(d); return d; }

    private const string Bhav =
        "SYMBOL, SERIES, DATE1, TTL_TRD_QNTY, DELIV_QTY, DELIV_PER\n" +
        "AAA, EQ, 07-Oct-2026, 1000, 400, 40.00\n" +
        "BBB, EQ, 07-Oct-2026, 2000, 1500, 75.00\n" +
        "OUTSIDER, EQ, 07-Oct-2026, 100, 50, 50.00\n";

    private static async Task SeedInstruments(CliTestHost host, params string[] symbols)
    {
        foreach (var sym in symbols)
            await host.Repo<IInstrumentRepository>().UpsertAsync(new Instrument { Symbol = sym, NseSymbol = sym, YahooSymbol = sym + ".NS", CompanyName = sym, Exchange = "NSE", Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
    }

    private static Task<int> Run(CliTestHost host, params string[] args) =>
        DeliveryImportCommand.RunAsync(args, host.Repo<IDeliveryDataRepository>(), host.Repo<IInstrumentRepository>(), NullLogger.Instance);

    // ---------------- repository ----------------

    [Fact]
    public async Task Repository_UpsertsInPlace_ReadsOrderedAndBounded_AndReportsTheLatestDate()
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db"));
        SchemaInitializer.Initialize(db);
        var repo = new SqliteDeliveryDataRepository(db);
        Assert.Null(await repo.GetLatestDateAsync());

        DeliveryRecord R(string sym, int day, decimal pct, long? traded = null) => new() { Symbol = sym, TradeDate = new DateOnly(2026, 10, day), DeliveryPercent = pct, TotalTradedQuantity = traded, DeliverableQuantity = traded / 2, Source = "t" };
        await repo.UpsertRangeAsync(new[] { R("AAA", 9, 30), R("AAA", 7, 40, 1000), R("AAA", 8, 50), R("BBB", 8, 99) });
        await repo.UpsertRangeAsync(new[] { R("aaa", 8, 55) });          // same symbol (case-insensitive) and date: updated, not duplicated

        var rows = await repo.GetRangeAsync("AAA", new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 8));
        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateOnly(2026, 10, 7), rows[0].TradeDate);
        Assert.Equal(1000L, rows[0].TotalTradedQuantity);
        Assert.Equal(500L, rows[0].DeliverableQuantity);
        Assert.Equal(55m, rows[1].DeliveryPercent);
        Assert.Null(rows[1].TotalTradedQuantity);
        Assert.Equal(new DateOnly(2026, 10, 9), await repo.GetLatestDateAsync());
        Assert.Equal(1, (await repo.GetRangeAsync("BBB", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))).Count);   // other symbols untouched
    }

    // ---------------- command ----------------

    [Fact]
    public async Task Import_KeepsOnlyTheActiveUniverse_ByDefault_AndAllSymbolsOnRequest()
    {
        using var host = CliTestHost.Create();
        await SeedInstruments(host, "AAA", "BBB");
        var dir = TempDir(); var file = Path.Combine(dir, "sec_bhavdata_full_07102026.csv"); await File.WriteAllTextAsync(file, Bhav);
        try
        {
            Assert.Equal(0, await Run(host, "import-delivery", "--file", file));
            var repo = host.Repo<IDeliveryDataRepository>();
            Assert.Equal(1, (await repo.GetRangeAsync("AAA", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))).Count);
            Assert.Equal(0, (await repo.GetRangeAsync("OUTSIDER", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))).Count);

            Assert.Equal(0, await Run(host, "import-delivery", "--file", file, "--all-symbols"));
            Assert.Equal(1, (await repo.GetRangeAsync("OUTSIDER", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))).Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Import_IsIdempotent_ReimportingTheSameFileChangesNothing()
    {
        using var host = CliTestHost.Create();
        await SeedInstruments(host, "AAA", "BBB");
        var dir = TempDir(); var file = Path.Combine(dir, "d.csv"); await File.WriteAllTextAsync(file, Bhav);
        try
        {
            await Run(host, "import-delivery", "--file", file);
            await Run(host, "import-delivery", "--file", file);

            Assert.Equal(1, (await host.Repo<IDeliveryDataRepository>().GetRangeAsync("AAA", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))).Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DryRun_WritesNothing()
    {
        using var host = CliTestHost.Create();
        await SeedInstruments(host, "AAA");
        var dir = TempDir(); var file = Path.Combine(dir, "d.csv"); await File.WriteAllTextAsync(file, Bhav);
        try
        {
            Assert.Equal(0, await Run(host, "import-delivery", "--file", file, "--dry-run"));
            Assert.Null(await host.Repo<IDeliveryDataRepository>().GetLatestDateAsync());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExitCodes_DistinguishCleanPartialAndFailedImports()
    {
        using var host = CliTestHost.Create();
        await SeedInstruments(host, "AAA");
        var dir = TempDir();
        try
        {
            var partial = Path.Combine(dir, "partial.csv"); await File.WriteAllTextAsync(partial, "Symbol,TradeDate,DeliveryPercent\nAAA,2026-10-07,40\nAAA,2026-10-08,oops\n");
            var garbage = Path.Combine(dir, "garbage.csv"); await File.WriteAllTextAsync(garbage, "X,Y\n1,2\n");

            Assert.Equal(2, await Run(host, "import-delivery", "--file", partial));            // some rows imported, one rejected
            Assert.Equal(1, await Run(host, "import-delivery", "--file", garbage));            // unrecognised layout: nothing imported
            Assert.Equal(1, await Run(host, "import-delivery", "--file", Path.Combine(dir, "missing.csv")));
            Assert.Equal(1, await Run(host, "import-delivery"));                               // neither --file nor --dir
            Assert.Equal(1, await Run(host, "import-delivery", "--file", partial, "--dir", dir));   // both
            Assert.Equal(1, await Run(host, "import-delivery", "--file"));                     // flag without a value
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Directory_ImportsEveryCsv_InNameOrder()
    {
        using var host = CliTestHost.Create();
        await SeedInstruments(host, "AAA");
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a_0701.csv"), "Symbol,TradeDate,DeliveryPercent\nAAA,2026-10-07,40\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "b_0801.csv"), "Symbol,TradeDate,DeliveryPercent\nAAA,2026-10-08,45\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "ignored");

            Assert.Equal(0, await Run(host, "import-delivery", "--dir", dir));
            Assert.Equal(2, (await host.Repo<IDeliveryDataRepository>().GetRangeAsync("AAA", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))).Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
