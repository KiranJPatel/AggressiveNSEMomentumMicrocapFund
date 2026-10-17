using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

public class RepositoryTests
{
    private static SqliteConnection OpenInitializedTempDb()
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db"));
        SchemaInitializer.Initialize(db);
        return db;
    }

    [Fact]
    public async Task InstrumentRepository_Upsert_ThenGetBySymbol_RoundTrips()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteInstrumentRepository(db);

        var instrument = new Instrument
        {
            Symbol = "RELIANCE",
            NseSymbol = "RELIANCE",
            YahooSymbol = "RELIANCE.NS",
            CompanyName = "Reliance Industries Ltd.",
            Exchange = "NSE",
            Active = true,
            MarketCap = 1_500_000.5m,
        };

        await repo.UpsertAsync(instrument);
        var fetched = await repo.GetBySymbolAsync("RELIANCE");

        Assert.NotNull(fetched);
        Assert.Equal("Reliance Industries Ltd.", fetched!.CompanyName);
        Assert.Equal(1_500_000.5m, fetched.MarketCap);
        Assert.True(fetched.Active);
    }

    [Fact]
    public async Task InstrumentRepository_Upsert_SecondCallUpdatesInPlace_DoesNotDuplicate()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteInstrumentRepository(db);

        var instrument = new Instrument
        {
            Symbol = "TCS", NseSymbol = "TCS", YahooSymbol = "TCS.NS",
            CompanyName = "Tata Consultancy Services", Exchange = "NSE", Active = true,
        };
        await repo.UpsertAsync(instrument);

        instrument.CompanyName = "TCS Ltd (renamed)";
        await repo.UpsertAsync(instrument);

        var active = await repo.GetActiveAsync();
        Assert.Equal(1, active.Count(i => i.Symbol == "TCS"));
        Assert.Equal("TCS Ltd (renamed)", active.Single(i => i.Symbol == "TCS").CompanyName);
    }

    [Fact]
    public async Task HistoricalBarRepository_UpsertRange_ThenGetRange_RoundTrips()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteHistoricalBarRepository(db);

        var bars = new List<HistoricalBar>
        {
            new() { Symbol = "INFY", Timestamp = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), Open = 1500m, High = 1520m, Low = 1495m, Close = 1510m, AdjustedClose = 1510m, Volume = 100000, Source = "Yahoo", Timeframe = Timeframe.Daily },
            new() { Symbol = "INFY", Timestamp = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), Open = 1510m, High = 1535m, Low = 1505m, Close = 1530m, AdjustedClose = 1530m, Volume = 120000, Source = "Yahoo", Timeframe = Timeframe.Daily },
        };

        await repo.UpsertRangeAsync(bars);
        var fetched = await repo.GetRangeAsync("INFY", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), Timeframe.Daily);

        Assert.Equal(2, fetched.Count);
        Assert.Equal(1510m, fetched[0].Close);
        Assert.Equal(1530m, fetched[1].Close);
    }

    [Fact]
    public async Task HistoricalBarRepository_UpsertRange_CalledTwice_UpdatesInPlace_DoesNotDuplicate()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteHistoricalBarRepository(db);
        var ts = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        var bar = new HistoricalBar { Symbol = "INFY", Timestamp = ts, Open = 1500m, High = 1520m, Low = 1495m, Close = 1510m, AdjustedClose = 1510m, Volume = 100000, Source = "Yahoo", Timeframe = Timeframe.Daily };
        await repo.UpsertRangeAsync(new[] { bar });

        // Simulate a corrected re-fetch of the same bar (e.g. a corporate-action adjustment refresh).
        var corrected = new HistoricalBar { Symbol = "INFY", Timestamp = ts, Open = 1500m, High = 1520m, Low = 1495m, Close = 1512.5m, AdjustedClose = 1512.5m, Volume = 100500, Source = "Yahoo", Timeframe = Timeframe.Daily };
        await repo.UpsertRangeAsync(new[] { corrected });

        var fetched = await repo.GetRangeAsync("INFY", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), Timeframe.Daily);
        Assert.Equal(1, fetched.Count);
        Assert.Equal(1512.5m, fetched[0].Close);
    }

    [Fact]
    public async Task UniverseClassificationRepository_ResolvesSnapshotEffectiveOnDate_NotMostRecent()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteUniverseClassificationRepository(db);
        var now = DateTimeOffset.UtcNow;
        // Foreign keys are enforced (see SqliteConnection): the snapshot's parent instrument must exist.
        var instrumentId = await new SqliteInstrumentRepository(db).UpsertAsync(new Instrument
        {
            Symbol = "ADANIENT", NseSymbol = "ADANIENT", YahooSymbol = "ADANIENT.NS", CompanyName = "Adani Enterprises", Exchange = "NSE",
            Active = true, CreatedAt = now, UpdatedAt = now,
        });

        // Two non-overlapping periods for the same symbol — classic "was Mid, reclassified to Large" scenario.
        await repo.UpsertAsync(new UniverseClassificationSnapshot
        {
            InstrumentId = instrumentId, Symbol = "ADANIENT", MarketCapBucket = MarketCapBucket.Mid,
            EffectiveFrom = new DateOnly(2025, 1, 1), EffectiveTo = new DateOnly(2025, 6, 30), IsUnknown = false,
        });
        await repo.UpsertAsync(new UniverseClassificationSnapshot
        {
            InstrumentId = instrumentId, Symbol = "ADANIENT", MarketCapBucket = MarketCapBucket.Large,
            EffectiveFrom = new DateOnly(2025, 7, 1), EffectiveTo = null, IsUnknown = false,
        });

        var duringFirstPeriod = await repo.GetClassificationAsync("ADANIENT", new DateOnly(2025, 3, 15));
        var duringSecondPeriod = await repo.GetClassificationAsync("ADANIENT", new DateOnly(2026, 1, 1));

        Assert.NotNull(duringFirstPeriod);
        Assert.Equal(MarketCapBucket.Mid, duringFirstPeriod!.MarketCapBucket);

        Assert.NotNull(duringSecondPeriod);
        Assert.Equal(MarketCapBucket.Large, duringSecondPeriod!.MarketCapBucket);
    }

    [Fact]
    public async Task SurveillanceRepository_UnknownSymbol_ReturnsExplicitUnknown_NeverNullOrSafe()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteSurveillanceRepository(db);

        var status = await repo.GetStatusAsync("NEVER_INGESTED", new DateOnly(2026, 1, 1));

        Assert.Equal(DataAvailability.Unknown, status.Availability);
        Assert.Equal(RiskFlags.None, status.Flags);
    }

    [Fact]
    public async Task SurveillanceRepository_Upsert_ThenGet_RoundTripsFlags()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteSurveillanceRepository(db);

        await repo.UpsertAsync(new SurveillanceStatus
        {
            Symbol = "SUZLON", AsOfDate = new DateOnly(2026, 1, 1),
            Flags = RiskFlags.Asm | RiskFlags.LiquidityRisk,
            Availability = DataAvailability.Available,
            Source = "NSE-ASM-CSV"
        });

        var status = await repo.GetStatusAsync("SUZLON", new DateOnly(2026, 1, 1));

        Assert.Equal(DataAvailability.Available, status.Availability);
        Assert.True(status.Flags.HasFlag(RiskFlags.Asm));
        Assert.True(status.Flags.HasFlag(RiskFlags.LiquidityRisk));
        Assert.False(status.Flags.HasFlag(RiskFlags.Gsm));
    }

    [Fact]
    public async Task SystemEventWriter_Write_ThenQueryDirectly_Persists()
    {
        using var db = OpenInitializedTempDb();
        var writer = new SqliteSystemEventWriter(db);

        await writer.WriteAsync(new SystemEvent
        {
            Level = "Warning", Category = "Test", Message = "something happened",
            Timestamp = DateTimeOffset.UtcNow
        });

        var messages = db.Query("SELECT Message FROM SystemEvents;", row => row.GetString("Message"));
        Assert.Equal(1, messages.Count);
        Assert.Equal("something happened", messages[0]);
    }
}
