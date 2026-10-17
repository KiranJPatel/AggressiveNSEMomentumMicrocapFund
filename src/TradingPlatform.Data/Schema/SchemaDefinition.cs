namespace TradingPlatform.Data.Schema;

/// <summary>
/// Full SQLite schema (spec §7.2). Every statement is idempotent
/// (CREATE TABLE/INDEX IF NOT EXISTS) so SchemaInitializer can run it on
/// every startup safely. Column types follow SqliteRow/SqliteValueConverter's
/// conventions: decimal/DateTimeOffset/DateOnly are all TEXT (never REAL —
/// see SqliteRow's class remarks on why money is never stored as a double),
/// enums and bool are INTEGER.
///
/// Table set = spec §7.2's minimum list, plus SurveillanceStatuses/
/// FundamentalSnapshots/InstitutionalActivitySnapshots/StockScores/
/// VcpPatterns to back the Core models and provider interfaces already
/// defined in Tranche 0 (ISurveillanceDataProvider, IFundamentalDataProvider,
/// IInstitutionalActivityProvider) that the spec says are "manual/CSV/SQLite
/// ingestion initially" (§2.3, §3.2) but doesn't enumerate a table name for.
/// </summary>
public static class SchemaDefinition
{
    public const int CurrentVersion = 1;

    public static readonly IReadOnlyList<string> Statements = new List<string>
    {
        // --- bookkeeping ---
        """
        CREATE TABLE IF NOT EXISTS SchemaVersion (
            Version INTEGER NOT NULL,
            AppliedAt TEXT NOT NULL
        );
        """,

        // --- universe & classification (spec §2.2) ---
        """
        CREATE TABLE IF NOT EXISTS Instruments (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL UNIQUE,
            NseSymbol TEXT NOT NULL,
            YahooSymbol TEXT NOT NULL,
            Isin TEXT NULL,
            CompanyName TEXT NOT NULL,
            Sector TEXT NULL,
            Industry TEXT NULL,
            MarketCap TEXT NULL,
            AverageDailyTurnover TEXT NULL,
            LotSize INTEGER NULL,
            Exchange TEXT NOT NULL,
            Active INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_Instruments_Active ON Instruments(Active);",

        """
        CREATE TABLE IF NOT EXISTS UniverseClassificationSnapshots (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            InstrumentId INTEGER NOT NULL,
            Symbol TEXT NOT NULL,
            MarketCapBucket INTEGER NOT NULL,
            MarketCapRank INTEGER NULL,
            Sector TEXT NULL,
            EffectiveFrom TEXT NOT NULL,
            EffectiveTo TEXT NULL,
            IsUnknown INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (InstrumentId) REFERENCES Instruments(Id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_UniverseClassificationSnapshots_Symbol_EffectiveFrom ON UniverseClassificationSnapshots(Symbol, EffectiveFrom);",

        // --- market data (spec §5.2) ---
        """
        CREATE TABLE IF NOT EXISTS HistoricalBars (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            Timestamp TEXT NOT NULL,
            [Open] TEXT NOT NULL,
            High TEXT NOT NULL,
            Low TEXT NOT NULL,
            [Close] TEXT NOT NULL,
            AdjustedClose TEXT NOT NULL,
            Volume INTEGER NOT NULL,
            Source TEXT NOT NULL,
            Timeframe INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            UNIQUE (Symbol, Timestamp, Timeframe)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_HistoricalBars_Symbol_Timestamp ON HistoricalBars(Symbol, Timestamp);",

        """
        CREATE TABLE IF NOT EXISTS Indicators (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            Timestamp TEXT NOT NULL,
            Timeframe INTEGER NOT NULL,
            HmaFast TEXT NULL, HmaSlow TEXT NULL, Supertrend TEXT NULL,
            Sma20 TEXT NULL, Sma50 TEXT NULL, Sma200 TEXT NULL,
            Rsi TEXT NULL, Macd TEXT NULL, MacdSignal TEXT NULL, Adx TEXT NULL,
            Atr TEXT NULL, AtrPercent TEXT NULL, HistoricalVolatility TEXT NULL,
            VolumeSma TEXT NULL, RelativeVolume TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UNIQUE (Symbol, Timestamp, Timeframe)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_Indicators_Symbol_Timestamp ON Indicators(Symbol, Timestamp);",

        // --- delivery % (spec §3.4) — one statement per list element: SQLite runs only the first statement of a string ---
        """
        CREATE TABLE IF NOT EXISTS DeliveryData (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            TradeDate TEXT NOT NULL,
            TotalTradedQuantity INTEGER NULL,
            DeliverableQuantity INTEGER NULL,
            DeliveryPercent TEXT NOT NULL,
            Source TEXT NOT NULL DEFAULT '',
            CreatedAt TEXT NOT NULL,
            UNIQUE (Symbol, TradeDate)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_DeliveryData_Symbol_TradeDate ON DeliveryData(Symbol, TradeDate);",

        // --- regime & scoring (spec §3.1-§3.5) ---
        """
        CREATE TABLE IF NOT EXISTS MarketRegimes (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AsOfDate TEXT NOT NULL UNIQUE,
            RegimeScore INTEGER NOT NULL,
            Classification INTEGER NOT NULL,
            DeployableCapitalPercent TEXT NOT NULL,
            NiftyTrendScore TEXT NULL, BreadthScore TEXT NULL, AdvanceDeclineRatio TEXT NULL,
            PercentUniverseAbove50Dma TEXT NULL, PercentUniverseAbove200Dma TEXT NULL,
            SmallMidRelativeStrength TEXT NULL, VolatilityScore TEXT NULL, BreakoutSuccessRate TEXT NULL, WeeklyBias INTEGER NULL,
            CreatedAt TEXT NOT NULL
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS StockScores (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            AsOfDate TEXT NOT NULL,
            TotalScore TEXT NOT NULL, TechnicalScore TEXT NOT NULL, FundamentalScore TEXT NOT NULL,
            RelativeStrengthScore TEXT NOT NULL, VolumeScore TEXT NOT NULL, PriceMomentumScore TEXT NOT NULL,
            EarningsAccelerationScore TEXT NOT NULL, VcpScore TEXT NOT NULL, RevenueAccelerationScore TEXT NOT NULL,
            TrendStructureScore TEXT NOT NULL, InstitutionalActivityScore TEXT NOT NULL, SectorScore TEXT NOT NULL,
            DataQualityScore TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UNIQUE (Symbol, AsOfDate)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_StockScores_Symbol_AsOfDate ON StockScores(Symbol, AsOfDate);",

        """
        CREATE TABLE IF NOT EXISTS VcpPatterns (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            AsOfDate TEXT NOT NULL,
            VcpScore TEXT NOT NULL,
            NumberOfContractions INTEGER NOT NULL,
            ContractionRatiosJson TEXT NOT NULL,
            PivotPrice TEXT NOT NULL,
            BaseLengthDays INTEGER NOT NULL,
            AverageVolumeDuringBase TEXT NOT NULL,
            VolumeDryUpScore TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UNIQUE (Symbol, AsOfDate)
        );
        """,

        // --- ingestion-backed provider data (spec §2.3, §3.2) ---
        """
        CREATE TABLE IF NOT EXISTS SurveillanceStatuses (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            AsOfDate TEXT NOT NULL,
            Flags INTEGER NOT NULL,
            Availability INTEGER NOT NULL,
            Source TEXT NULL,
            UNIQUE (Symbol, AsOfDate)
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS FundamentalSnapshots (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            AsOfDate TEXT NOT NULL,
            EarningsGrowthPercent TEXT NULL, EarningsGrowthAvailability INTEGER NOT NULL,
            RevenueGrowthPercent TEXT NULL, RevenueGrowthAvailability INTEGER NOT NULL,
            UNIQUE (Symbol, AsOfDate)
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS InstitutionalActivitySnapshots (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            AsOfDate TEXT NOT NULL,
            ActivityScore TEXT NULL,
            Availability INTEGER NOT NULL,
            UNIQUE (Symbol, AsOfDate)
        );
        """,

        // --- signals & orders (spec §6.3) ---
        """
        CREATE TABLE IF NOT EXISTS Signals (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            GeneratedAt TEXT NOT NULL,
            Stage INTEGER NOT NULL,
            PivotPrice TEXT NOT NULL, SuggestedEntry TEXT NOT NULL, SuggestedStop TEXT NOT NULL,
            RiskPerShare TEXT NOT NULL, SuggestedPositionQty TEXT NOT NULL,
            SignalToOrderLag INTEGER NOT NULL,
            StrategyVersion TEXT NOT NULL, ConfigurationVersion TEXT NOT NULL,
            BindingConstraints TEXT NULL,
            CreatedAt TEXT NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_Signals_Symbol ON Signals(Symbol);",

        """
        CREATE TABLE IF NOT EXISTS Orders (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            SignalId INTEGER NULL,
            Symbol TEXT NOT NULL,
            Side INTEGER NOT NULL,
            Type INTEGER NOT NULL,
            State INTEGER NOT NULL,
            Quantity TEXT NOT NULL,
            LimitPrice TEXT NULL,
            StopPrice TEXT NULL,
            BrokerOrderId TEXT NULL,
            RejectionReason TEXT NULL,
            CorrelationId TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (SignalId) REFERENCES Signals(Id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_Orders_SignalId ON Orders(SignalId);",
        "CREATE INDEX IF NOT EXISTS IX_Orders_Symbol ON Orders(Symbol);",

        """
        CREATE TABLE IF NOT EXISTS OrderEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            OrderId INTEGER NOT NULL,
            FromState INTEGER NOT NULL,
            ToState INTEGER NOT NULL,
            Detail TEXT NULL,
            Timestamp TEXT NOT NULL,
            FOREIGN KEY (OrderId) REFERENCES Orders(Id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_OrderEvents_OrderId ON OrderEvents(OrderId);",

        """
        CREATE TABLE IF NOT EXISTS Fills (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            OrderId INTEGER NOT NULL,
            Symbol TEXT NOT NULL,
            Quantity TEXT NOT NULL,
            Price TEXT NOT NULL,
            SlippageAmount TEXT NOT NULL,
            Commission TEXT NOT NULL,
            FilledAt TEXT NOT NULL,
            FOREIGN KEY (OrderId) REFERENCES Orders(Id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_Fills_OrderId ON Fills(OrderId);",

        // --- positions & portfolio (spec §4.2-§4.5) ---
        """
        CREATE TABLE IF NOT EXISTS Positions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            Quantity TEXT NOT NULL,
            AverageEntryPrice TEXT NOT NULL,
            InitialStop TEXT NOT NULL,
            CurrentStop TEXT NOT NULL,
            InitialRiskAmount TEXT NOT NULL,
            RiskPerShare TEXT NOT NULL,
            HighestStageFilled INTEGER NOT NULL,
            IsOpen INTEGER NOT NULL,
            OpenedAt TEXT NOT NULL,
            ClosedAt TEXT NULL,
            CloseReason INTEGER NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_Positions_Symbol_IsOpen ON Positions(Symbol, IsOpen);",

        """
        CREATE TABLE IF NOT EXISTS PortfolioSnapshots (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AsOfDate TEXT NOT NULL UNIQUE,
            Nav TEXT NOT NULL,
            CashBalance TEXT NOT NULL,
            PeakNav TEXT NOT NULL,
            DrawdownPercent TEXT NOT NULL,
            PortfolioHeatPercent TEXT NOT NULL,
            OpenPositionCount INTEGER NOT NULL,
            Regime INTEGER NULL,
            CreatedAt TEXT NOT NULL
        );
        """,

        // --- trade journal (spec §7.2, immutable) ---
        """
        CREATE TABLE IF NOT EXISTS Trades (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            SignalId INTEGER NULL,
            EntryPrice TEXT NOT NULL,
            InitialStop TEXT NOT NULL,
            InitialRiskAmount TEXT NOT NULL,
            ExitPrice TEXT NOT NULL,
            ExitReason INTEGER NOT NULL,
            RealizedPnl TEXT NOT NULL,
            RMultiple TEXT NOT NULL,
            MaximumFavorableExcursion TEXT NOT NULL,
            MaximumAdverseExcursion TEXT NOT NULL,
            RegimeScoreAtEntry INTEGER NOT NULL,
            StockScoreAtEntry TEXT NOT NULL,
            RiskAtEntryPercent TEXT NOT NULL,
            StrategyVersion TEXT NOT NULL,
            ConfigurationVersion TEXT NOT NULL,
            EntryAt TEXT NOT NULL,
            ExitAt TEXT NOT NULL,
            FOREIGN KEY (SignalId) REFERENCES Signals(Id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_Trades_Symbol ON Trades(Symbol);",
        "CREATE INDEX IF NOT EXISTS IX_Trades_EntryAt ON Trades(EntryAt);",

        """
        CREATE TABLE IF NOT EXISTS TradeLegs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TradeId INTEGER NOT NULL,
            Stage INTEGER NOT NULL,
            Quantity TEXT NOT NULL,
            Price TEXT NOT NULL,
            FilledAt TEXT NOT NULL,
            FOREIGN KEY (TradeId) REFERENCES Trades(Id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_TradeLegs_TradeId ON TradeLegs(TradeId);",

        // --- risk & drawdown (spec §4.2, §4.5) ---
        """
        CREATE TABLE IF NOT EXISTS RiskEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            EventType TEXT NOT NULL,
            Description TEXT NOT NULL,
            Symbol TEXT NULL,
            Timestamp TEXT NOT NULL,
            CorrelationId TEXT NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_RiskEvents_Timestamp ON RiskEvents(Timestamp);",

        """
        CREATE TABLE IF NOT EXISTS DrawdownEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            DrawdownPercent TEXT NOT NULL,
            TierName TEXT NOT NULL,
            RiskReductionPercent TEXT NOT NULL,
            TradingShutdown INTEGER NOT NULL,
            Timestamp TEXT NOT NULL
        );
        """,

        // --- scanner (spec §7.1) ---
        """
        CREATE TABLE IF NOT EXISTS ScannerResults (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ScanTimestamp TEXT NOT NULL,
            Rank INTEGER NOT NULL,
            Symbol TEXT NOT NULL,
            Price TEXT NOT NULL,
            MarketCap TEXT NULL,
            Sector TEXT NULL,
            Score TEXT NOT NULL,
            RelativeStrengthScore TEXT NOT NULL,
            MomentumScore TEXT NOT NULL,
            VolumeScore TEXT NOT NULL,
            VcpScore TEXT NOT NULL,
            Adx TEXT NULL, Rsi TEXT NULL, Atr TEXT NULL,
            PivotPrice TEXT NOT NULL,
            BreakoutStatus TEXT NOT NULL,
            DistanceFromPivotPercent TEXT NOT NULL,
            SuggestedEntry TEXT NOT NULL,
            SuggestedStop TEXT NOT NULL,
            Risk TEXT NOT NULL,
            SuggestedPositionSize TEXT NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_ScannerResults_ScanTimestamp ON ScannerResults(ScanTimestamp);",

        // --- backtesting & optimization (spec §5.5, §7.6) ---
        """
        CREATE TABLE IF NOT EXISTS BacktestRuns (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            StrategyVersion TEXT NOT NULL,
            ConfigurationHash TEXT NOT NULL,
            DataVersion TEXT NOT NULL,
            StartDate TEXT NOT NULL,
            EndDate TEXT NOT NULL,
            Status INTEGER NOT NULL,
            SurvivorshipBiased INTEGER NOT NULL,
            FinalCapital TEXT NULL, Cagr TEXT NULL, MaxDrawdownPercent TEXT NULL,
            Sharpe TEXT NULL, Sortino TEXT NULL, WinRatePercent TEXT NULL,
            ProfitFactor TEXT NULL, ExpectancyR TEXT NULL, TradeCount INTEGER NULL,
            StartedAt TEXT NOT NULL,
            CompletedAt TEXT NULL
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS BacktestTrades (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            BacktestRunId INTEGER NOT NULL,
            TradeId INTEGER NOT NULL,
            FOREIGN KEY (BacktestRunId) REFERENCES BacktestRuns(Id),
            FOREIGN KEY (TradeId) REFERENCES Trades(Id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_BacktestTrades_BacktestRunId ON BacktestTrades(BacktestRunId);",

        // Deliberately NOT the same tables live/paper trading writes to
        // (PortfolioSnapshots/DrawdownEvents, added Tranche 1/10): those carry
        // a live portfolio's actual day-by-day history, and
        // PortfolioSnapshots.AsOfDate is UNIQUE across the whole table, so a
        // multi-year backtest replaying historical dates would either collide
        // with a genuine live snapshot on the same calendar date or silently
        // overwrite it. A backtest's internal daily NAV/drawdown series
        // belongs to that BacktestRun, not to the live portfolio's timeline —
        // hence its own BacktestRunId-scoped tables (Tranche 18), additive to
        // the existing schema rather than a breaking change to the live ones.
        """
        CREATE TABLE IF NOT EXISTS BacktestSnapshots (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            BacktestRunId INTEGER NOT NULL,
            AsOfDate TEXT NOT NULL,
            Nav TEXT NOT NULL,
            CashBalance TEXT NOT NULL,
            PeakNav TEXT NOT NULL,
            DrawdownPercent TEXT NOT NULL,
            PortfolioHeatPercent TEXT NOT NULL,
            OpenPositionCount INTEGER NOT NULL,
            Regime INTEGER NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (BacktestRunId) REFERENCES BacktestRuns(Id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_BacktestSnapshots_BacktestRunId_AsOfDate ON BacktestSnapshots(BacktestRunId, AsOfDate);",

        """
        CREATE TABLE IF NOT EXISTS BacktestDrawdownEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            BacktestRunId INTEGER NOT NULL,
            DrawdownPercent TEXT NOT NULL,
            TierName TEXT NOT NULL,
            RiskReductionPercent TEXT NOT NULL,
            TradingShutdown INTEGER NOT NULL,
            Timestamp TEXT NOT NULL,
            FOREIGN KEY (BacktestRunId) REFERENCES BacktestRuns(Id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_BacktestDrawdownEvents_BacktestRunId ON BacktestDrawdownEvents(BacktestRunId);",

        // Tranche 20: a tiny generic key-value store for the pure engine
        // state records that must survive between trading-cycle
        // invocations (DrawdownState, LossLimitState, KillSwitchState —
        // see TradingPlatform.Risk.Drawdown/Execution.KillSwitch). Each
        // is a small, self-contained record with no need to be queried
        // relationally, so a JSON-blob-per-key table is simpler and
        // safer than three new tables with dozens of individually-typed
        // columns duplicating what the record already defines — this is
        // exactly the SqliteEngineStateRepository seam spec §6.5's
        // restart-recovery ("load DB state") needs.
        """
        CREATE TABLE IF NOT EXISTS EngineState (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS OptimizationRuns (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            BatchId TEXT NULL,
            ParameterSetJson TEXT NOT NULL,
            CompositeScoreBreakdownJson TEXT NOT NULL,
            CompositeScore TEXT NOT NULL,
            IsOutOfSample INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL
        );
        """,

        // --- system log (spec §7.2) ---
        """
        CREATE TABLE IF NOT EXISTS SystemEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Level TEXT NOT NULL,
            Category TEXT NOT NULL,
            Message TEXT NOT NULL,
            Exception TEXT NULL,
            CorrelationId TEXT NULL,
            Timestamp TEXT NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_SystemEvents_Timestamp ON SystemEvents(Timestamp);",
    };
}
