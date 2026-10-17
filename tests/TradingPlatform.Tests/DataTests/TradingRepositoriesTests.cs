using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

/// <summary>
/// Round-trip tests for the nine repositories Tranche 1 deferred (Signal,
/// Order/OrderEvent/Fill, Position, Trade/TradeLeg, RiskEvent,
/// DrawdownEvent, PortfolioSnapshot, ScannerResult, BacktestRun) and built
/// in Tranche 18 — see docs/gap-log.md. Follows RepositoryTests.cs's exact
/// per-test-file temp-DB convention (also duplicated in
/// YahooFinanceHistoricalDataProviderTests.cs) rather than a shared helper.
/// </summary>
public class TradingRepositoriesTests
{
    private static SqliteConnection OpenInitializedTempDb()
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db"));
        SchemaInitializer.Initialize(db);
        return db;
    }

    // ---------------- Signal ----------------

    [Fact]
    public async Task SignalRepository_Insert_ThenGetRecentAndBySymbol_RoundTrips()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteSignalRepository(db);

        var signal = new Signal
        {
            Symbol = "RELIANCE",
            GeneratedAt = DateTimeOffset.UtcNow,
            Stage = PyramidStage.Stage1Initial,
            PivotPrice = 100m,
            SuggestedEntry = 101m,
            SuggestedStop = 95m,
            RiskPerShare = 6m,
            SuggestedPositionQty = 50m,
            SignalToOrderLag = SignalToOrderLag.NextOpen,
            StrategyVersion = "v1",
            ConfigurationVersion = "cfg1",
        };

        var id = await repo.InsertAsync(signal);
        Assert.True(id > 0);

        var recent = await repo.GetRecentAsync(10);
        Assert.Single(recent);
        Assert.Equal("RELIANCE", recent[0].Symbol);
        Assert.Equal(100m, recent[0].PivotPrice);

        var bySymbol = await repo.GetBySymbolAsync("RELIANCE", 10);
        Assert.Single(bySymbol);
        var noneForOther = await repo.GetBySymbolAsync("INFY", 10);
        Assert.Empty(noneForOther);
    }

    // ---------------- Order / OrderEvent / Fill ----------------

    [Fact]
    public async Task OrderRepository_Insert_RecordStateTransition_CreatesEventAndUpdatesState()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteOrderRepository(db);

        var order = new Order
        {
            Symbol = "TCS",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            State = OrderState.Created,
            Quantity = 10m,
            CorrelationId = Guid.NewGuid().ToString(),
        };
        var id = await repo.InsertAsync(order);

        await repo.RecordStateTransitionAsync(id, OrderState.Created, OrderState.Submitted, brokerOrderId: "BRK123", rejectionReason: null, detail: "sent to broker", DateTimeOffset.UtcNow);

        var fetched = await repo.GetByIdAsync(id);
        Assert.NotNull(fetched);
        Assert.Equal(OrderState.Submitted, fetched!.State);
        Assert.Equal("BRK123", fetched.BrokerOrderId);

        var events = await repo.GetEventsAsync(id);
        Assert.Single(events);
        Assert.Equal(OrderState.Created, events[0].FromState);
        Assert.Equal(OrderState.Submitted, events[0].ToState);

        // A later transition that supplies no brokerOrderId must not clobber the one already recorded (COALESCE).
        await repo.RecordStateTransitionAsync(id, OrderState.Submitted, OrderState.Acknowledged, brokerOrderId: null, rejectionReason: null, detail: null, DateTimeOffset.UtcNow);
        var afterSecondTransition = await repo.GetByIdAsync(id);
        Assert.Equal("BRK123", afterSecondTransition!.BrokerOrderId);
        Assert.Equal(2, (await repo.GetEventsAsync(id)).Count);
    }

    [Fact]
    public async Task OrderRepository_RecordFill_RoundTrips_AndOpenOrdersExcludesTerminalStates()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteOrderRepository(db);

        var openOrder = new Order { Symbol = "TCS", Side = OrderSide.Buy, Type = OrderType.Market, State = OrderState.Open, Quantity = 10m, CorrelationId = Guid.NewGuid().ToString() };
        var filledOrder = new Order { Symbol = "INFY", Side = OrderSide.Buy, Type = OrderType.Market, State = OrderState.Filled, Quantity = 5m, CorrelationId = Guid.NewGuid().ToString() };
        var openId = await repo.InsertAsync(openOrder);
        await repo.InsertAsync(filledOrder);

        var fillId = await repo.RecordFillAsync(new Fill { OrderId = openId, Symbol = "TCS", Quantity = 10m, Price = 250.5m, SlippageAmount = 0.5m, Commission = 2m, FilledAt = DateTimeOffset.UtcNow });
        Assert.True(fillId > 0);

        var fills = await repo.GetFillsAsync(openId);
        Assert.Single(fills);
        Assert.Equal(250.5m, fills[0].Price);

        var openOrders = await repo.GetOpenOrdersAsync();
        Assert.Single(openOrders);
        Assert.Equal("TCS", openOrders[0].Symbol);
    }

    // ---------------- Position ----------------

    [Fact]
    public async Task PositionRepository_Upsert_ThenUpdate_ThenClose_RoundTrips()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqlitePositionRepository(db);

        var position = new Position
        {
            Symbol = "HDFCBANK",
            Quantity = 20m,
            AverageEntryPrice = 1500m,
            InitialStop = 1450m,
            CurrentStop = 1450m,
            InitialRiskAmount = 1000m,
            RiskPerShare = 50m,
            HighestStageFilled = PyramidStage.Stage1Initial,
            IsOpen = true,
            OpenedAt = DateTimeOffset.UtcNow,
        };

        var id = await repo.UpsertAsync(position);
        Assert.True(id > 0);

        var open = await repo.GetOpenBySymbolAsync("HDFCBANK");
        Assert.NotNull(open);
        Assert.Equal(1450m, open!.CurrentStop);

        // Trail the stop up (Id != 0 -> update in place, not a second row).
        open.CurrentStop = 1475m;
        open.HighestStageFilled = PyramidStage.Stage2Add;
        await repo.UpsertAsync(open);

        var allOpen = await repo.GetOpenAsync();
        Assert.Single(allOpen);
        Assert.Equal(1475m, allOpen[0].CurrentStop);

        await repo.CloseAsync(id, DateTimeOffset.UtcNow, ExitReason.TrailingStopHit);
        Assert.Empty(await repo.GetOpenAsync());
        Assert.Null(await repo.GetOpenBySymbolAsync("HDFCBANK"));
    }

    // ---------------- Trade / TradeLeg ----------------

    [Fact]
    public async Task TradeRepository_Insert_WithLegs_RoundTrips_AndLiveTradesExcludesBacktestLinked()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteTradeRepository(db);

        var trade = BuildTrade("WIPRO");
        var legs = new List<TradeLeg>
        {
            new() { Stage = PyramidStage.Stage1Initial, Quantity = 40m, Price = 400m, FilledAt = trade.EntryAt },
            new() { Stage = PyramidStage.Stage2Add, Quantity = 30m, Price = 410m, FilledAt = trade.EntryAt.AddDays(1) },
        };

        var tradeId = await repo.InsertAsync(trade, legs);
        Assert.True(tradeId > 0);

        var bySymbol = await repo.GetBySymbolAsync("WIPRO");
        Assert.Single(bySymbol);

        var fetchedLegs = await repo.GetLegsAsync(tradeId);
        Assert.Equal(2, fetchedLegs.Count);
        Assert.Equal(PyramidStage.Stage1Initial, fetchedLegs[0].Stage);

        // Still live before any BacktestTrades link exists.
        Assert.Single(await repo.GetLiveTradesAsync());

        // Simulate BacktestRunRepository.LinkTradesAsync's effect directly (that repo is tested separately).
        db.Execute("INSERT INTO BacktestRuns (StrategyVersion, ConfigurationHash, DataVersion, StartDate, EndDate, Status, SurvivorshipBiased, StartedAt) VALUES ('v1','h','d','2026-01-01','2026-01-31',1,1,?);", DateTimeOffset.UtcNow);
        db.Execute("INSERT INTO BacktestTrades (BacktestRunId, TradeId) VALUES (1, ?);", tradeId);

        Assert.Empty(await repo.GetLiveTradesAsync());
        Assert.Single(await repo.GetBySymbolAsync("WIPRO")); // still a real trade row either way
    }

    private static Trade BuildTrade(string symbol) => new()
    {
        Symbol = symbol,
        EntryPrice = 400m,
        InitialStop = 380m,
        InitialRiskAmount = 800m,
        ExitPrice = 420m,
        ExitReason = ExitReason.TrailingStopHit,
        RealizedPnl = 1400m,
        RMultiple = 1.75m,
        MaximumFavorableExcursion = 30m,
        MaximumAdverseExcursion = 5m,
        RegimeScoreAtEntry = 72,
        StockScoreAtEntry = 88m,
        RiskAtEntryPercent = 0.8m,
        StrategyVersion = "v1",
        ConfigurationVersion = "cfg1",
        EntryAt = new DateTimeOffset(2026, 2, 1, 9, 15, 0, TimeSpan.FromHours(5.5)),
        ExitAt = new DateTimeOffset(2026, 2, 10, 9, 15, 0, TimeSpan.FromHours(5.5)),
    };

    // ---------------- RiskEvent ----------------

    [Fact]
    public async Task RiskEventRepository_GetMostRecentByType_ReturnsLatest_NotFirst()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteRiskEventRepository(db);

        await repo.InsertAsync(new RiskEvent { EventType = "KillSwitchActivated", Description = "first", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10), CorrelationId = "c1" });
        await repo.InsertAsync(new RiskEvent { EventType = "KillSwitchActivated", Description = "second", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1), CorrelationId = "c2" });
        await repo.InsertAsync(new RiskEvent { EventType = "DailyLossLimitBreached", Description = "unrelated", Timestamp = DateTimeOffset.UtcNow, CorrelationId = "c3" });

        var latest = await repo.GetMostRecentByTypeAsync("KillSwitchActivated");
        Assert.NotNull(latest);
        Assert.Equal("second", latest!.Description);

        var none = await repo.GetMostRecentByTypeAsync("WeeklyLossLimitBreached");
        Assert.Null(none);

        var recent = await repo.GetRecentAsync(10);
        Assert.Equal(3, recent.Count);
    }

    // ---------------- DrawdownEvent ----------------

    [Fact]
    public async Task DrawdownEventRepository_GetMostRecent_ReturnsLatestTierTransition()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteDrawdownEventRepository(db);

        await repo.InsertAsync(new DrawdownEvent { DrawdownPercent = 12m, TierName = "ReduceRisk20", RiskReductionPercent = 20m, TradingShutdown = false, Timestamp = DateTimeOffset.UtcNow.AddDays(-1) });
        await repo.InsertAsync(new DrawdownEvent { DrawdownPercent = 22m, TierName = "ReduceRisk40", RiskReductionPercent = 40m, TradingShutdown = false, Timestamp = DateTimeOffset.UtcNow });

        var latest = await repo.GetMostRecentAsync();
        Assert.NotNull(latest);
        Assert.Equal("ReduceRisk40", latest!.TierName);

        Assert.Equal(2, (await repo.GetRecentAsync(10)).Count);
    }

    // ---------------- PortfolioSnapshot ----------------

    [Fact]
    public async Task PortfolioSnapshotRepository_Upsert_SameDateTwice_UpdatesInPlace_NotDuplicate()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqlitePortfolioSnapshotRepository(db);
        var date = new DateOnly(2026, 3, 15);

        await repo.UpsertAsync(new PortfolioSnapshot { AsOfDate = date, Nav = 1_010_000m, CashBalance = 200_000m, PeakNav = 1_010_000m, DrawdownPercent = 0m, PortfolioHeatPercent = 3m, OpenPositionCount = 2, Regime = RegimeClassification.Normal });
        await repo.UpsertAsync(new PortfolioSnapshot { AsOfDate = date, Nav = 1_015_000m, CashBalance = 195_000m, PeakNav = 1_015_000m, DrawdownPercent = 0m, PortfolioHeatPercent = 3.5m, OpenPositionCount = 2, Regime = RegimeClassification.Normal });

        var latest = await repo.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal(1_015_000m, latest!.Nav);

        var range = await repo.GetRangeAsync(date.AddDays(-5), date.AddDays(5));
        Assert.Single(range); // proves the second upsert updated in place rather than inserting a duplicate row
    }

    // ---------------- ScannerResult ----------------

    [Fact]
    public async Task ScannerResultRepository_InsertCycle_ThenGetLatest_OnlyReturnsMostRecentCycle()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteScannerResultRepository(db);

        var firstCycle = DateTimeOffset.UtcNow.AddHours(-1);
        var secondCycle = DateTimeOffset.UtcNow;

        await repo.InsertCycleAsync(new[] { BuildScannerResult("A", firstCycle, 1), BuildScannerResult("B", firstCycle, 2) });
        await repo.InsertCycleAsync(new[] { BuildScannerResult("C", secondCycle, 1) });

        var latest = await repo.GetLatestCycleAsync();
        Assert.Single(latest);
        Assert.Equal("C", latest[0].Symbol);
    }

    [Fact]
    public async Task ScannerResultRepository_GetLatestCycle_OnEmptyTable_ReturnsEmpty()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteScannerResultRepository(db);

        Assert.Empty(await repo.GetLatestCycleAsync());
    }

    private static ScannerResult BuildScannerResult(string symbol, DateTimeOffset scanTimestamp, int rank) => new()
    {
        ScanTimestamp = scanTimestamp,
        Rank = rank,
        Symbol = symbol,
        Price = 100m,
        Score = 80m,
        RelativeStrengthScore = 70m,
        MomentumScore = 65m,
        VolumeScore = 60m,
        VcpScore = 75m,
        PivotPrice = 105m,
        BreakoutStatus = "Forming",
        DistanceFromPivotPercent = -4.5m,
        SuggestedEntry = 106m,
        SuggestedStop = 98m,
        Risk = 8m,
        SuggestedPositionSize = 50m,
    };

    // ---------------- BacktestRun (+ link table + scoped snapshots/drawdowns) ----------------

    [Fact]
    public async Task BacktestRunRepository_FullLifecycle_StartLinkSnapshotsDrawdownsComplete_RoundTrips()
    {
        using var db = OpenInitializedTempDb();
        var runRepo = new SqliteBacktestRunRepository(db);
        var tradeRepo = new SqliteTradeRepository(db);

        var startedAt = DateTimeOffset.UtcNow;
        var run = new BacktestRun
        {
            StrategyVersion = "v1",
            ConfigurationHash = "hash1",
            DataVersion = "data1",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 3, 31),
            Status = BacktestRunStatus.Pending,
            SurvivorshipBiased = true,
            StartedAt = startedAt,
        };

        var runId = await runRepo.InsertStartedAsync(run);
        Assert.True(runId > 0);

        var runningFetch = await runRepo.GetRunAsync(runId);
        Assert.Equal(BacktestRunStatus.Running, runningFetch!.Status);

        var trade = BuildTrade("AXISBANK");
        var tradeId = await tradeRepo.InsertAsync(trade, Array.Empty<TradeLeg>());
        await runRepo.LinkTradesAsync(runId, new[] { tradeId });

        var linkedTrades = await runRepo.GetTradesAsync(runId);
        Assert.Single(linkedTrades);
        Assert.Equal("AXISBANK", linkedTrades[0].Symbol);

        var snapshots = new[]
        {
            new PortfolioSnapshot { AsOfDate = new DateOnly(2026, 1, 2), Nav = 1_000_000m, CashBalance = 1_000_000m, PeakNav = 1_000_000m, DrawdownPercent = 0m, PortfolioHeatPercent = 0m, OpenPositionCount = 0, Regime = RegimeClassification.Normal },
            new PortfolioSnapshot { AsOfDate = new DateOnly(2026, 1, 5), Nav = 1_050_000m, CashBalance = 900_000m, PeakNav = 1_050_000m, DrawdownPercent = 0m, PortfolioHeatPercent = 2m, OpenPositionCount = 1, Regime = RegimeClassification.Aggressive },
        };
        await runRepo.InsertSnapshotsAsync(runId, snapshots);
        var fetchedSnapshots = await runRepo.GetSnapshotsAsync(runId);
        Assert.Equal(2, fetchedSnapshots.Count);
        Assert.Equal(1_050_000m, fetchedSnapshots[1].Nav);

        var drawdownEvents = new[]
        {
            new DrawdownEvent { DrawdownPercent = 11m, TierName = "ReduceRisk20", RiskReductionPercent = 20m, TradingShutdown = false, Timestamp = startedAt },
        };
        await runRepo.InsertDrawdownEventsAsync(runId, drawdownEvents);
        Assert.Single(await runRepo.GetDrawdownEventsAsync(runId));

        run.Id = runId;
        run.Status = BacktestRunStatus.Completed;
        run.FinalCapital = 1_050_000m;
        run.Cagr = 15.2m;
        run.MaxDrawdownPercent = 5m;
        run.TradeCount = 1;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await runRepo.UpdateCompletedAsync(run);

        var completedFetch = await runRepo.GetRunAsync(runId);
        Assert.Equal(BacktestRunStatus.Completed, completedFetch!.Status);
        Assert.Equal(1_050_000m, completedFetch.FinalCapital);
        Assert.NotNull(completedFetch.CompletedAt);

        var allRuns = await runRepo.GetRunsAsync();
        Assert.Single(allRuns);

        // A second, independent run that fails must not disturb the first run's rows.
        var secondRun = new BacktestRun { StrategyVersion = "v1", ConfigurationHash = "hash2", DataVersion = "data1", StartDate = run.StartDate, EndDate = run.EndDate, Status = BacktestRunStatus.Pending, SurvivorshipBiased = true, StartedAt = DateTimeOffset.UtcNow };
        var secondRunId = await runRepo.InsertStartedAsync(secondRun);
        await runRepo.MarkFailedAsync(secondRunId, DateTimeOffset.UtcNow);

        var secondFetch = await runRepo.GetRunAsync(secondRunId);
        Assert.Equal(BacktestRunStatus.Failed, secondFetch!.Status);
        Assert.Equal(BacktestRunStatus.Completed, (await runRepo.GetRunAsync(runId))!.Status);
        Assert.Equal(2, (await runRepo.GetRunsAsync()).Count);
    }

    // ---------------- EngineState (Tranche 20) ----------------

    [Fact]
    public async Task EngineStateRepository_GetMissingKey_ReturnsNull()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteEngineStateRepository(db);

        Assert.Null(await repo.GetAsync("DrawdownState"));
    }

    [Fact]
    public async Task EngineStateRepository_SetThenGet_RoundTrips_AndOverwritesInPlace()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteEngineStateRepository(db);

        await repo.SetAsync("DrawdownState", """{"DrawdownPercent":5.5}""");
        Assert.Equal("""{"DrawdownPercent":5.5}""", await repo.GetAsync("DrawdownState"));

        // Same key set again must update in place, not add a second row (Key is PRIMARY KEY).
        await repo.SetAsync("DrawdownState", """{"DrawdownPercent":12.0}""");
        Assert.Equal("""{"DrawdownPercent":12.0}""", await repo.GetAsync("DrawdownState"));

        // A different key is independent.
        await repo.SetAsync("KillSwitchState", """{"IsActive":false}""");
        Assert.Equal("""{"IsActive":false}""", await repo.GetAsync("KillSwitchState"));
        Assert.Equal("""{"DrawdownPercent":12.0}""", await repo.GetAsync("DrawdownState"));
    }
}
