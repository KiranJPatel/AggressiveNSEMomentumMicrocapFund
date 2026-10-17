using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Cli;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Application.Logging;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Data.Yahoo;
using TradingPlatform.Execution.Zerodha;

var validCommands = new[] { "scan", "backtest", "optimize", "walkforward", "montecarlo", "paper", "live", "portfolio", "reconcile", "env", "fetch", "serve", "killswitch", "import", "universe", "download", "download-intraday", "import-delivery", "zerodha-login" };

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

var command = args[0];
if (!validCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Unknown command: '{command}'");
    Console.Error.WriteLine();
    PrintUsage();
    return 1;
}

// --- Configuration: config/appsettings.json (+ optional environment overlay + env vars) ---
var configDir = RepoPaths.Combine("config");
var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";

var configuration = new ConfigurationBuilder()
    .SetBasePath(configDir)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

// --- Ensure configured directories exist (spec §7.5) ---
var pathsOptions = new PathsOptions();
configuration.GetSection(PathsOptions.SectionName).Bind(pathsOptions);
foreach (var dir in new[] { pathsOptions.Data, pathsOptions.Logs, pathsOptions.Reports, pathsOptions.Backtests, pathsOptions.Exports })
{
    Directory.CreateDirectory(RepoPaths.Combine(dir));
}

// --- SQLite: open connection + apply schema before anything else touches the DB (spec §7.2) ---
var databaseOptions = new DatabaseOptions();
configuration.GetSection(DatabaseOptions.SectionName).Bind(databaseOptions);

using var sqliteConnection = new SqliteConnection(RepoPaths.Combine(databaseOptions.DatabasePath));
try
{
    SchemaInitializer.Initialize(sqliteConnection);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Database schema initialization failed: {ex.Message}");
    return 1;
}

var systemEventWriter = new SqliteSystemEventWriter(sqliteConnection);

// --- DI + logging ---
var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);

services.Configure<PortfolioOptions>(configuration.GetSection(PortfolioOptions.SectionName));
services.Configure<RiskOptions>(configuration.GetSection(RiskOptions.SectionName));
services.Configure<StrategyOptions>(configuration.GetSection(StrategyOptions.SectionName));
services.Configure<MarketRegimeOptions>(configuration.GetSection(MarketRegimeOptions.SectionName));
services.Configure<YahooFinanceOptions>(configuration.GetSection(YahooFinanceOptions.SectionName));
services.Configure<ZerodhaOptions>(configuration.GetSection(ZerodhaOptions.SectionName));
services.Configure<BacktestOptions>(configuration.GetSection(BacktestOptions.SectionName));
services.Configure<ScannerOptions>(configuration.GetSection(ScannerOptions.SectionName));
services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
services.Configure<RealtimeOptions>(configuration.GetSection(RealtimeOptions.SectionName));
services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
services.Configure<PathsOptions>(configuration.GetSection(PathsOptions.SectionName));
// Tranche 18: registered so BacktestCommand/ScanCommand can resolve
// BacktestEngineOptions/ScannerEngineOptions in full via DI instead of
// each command hand-constructing default sub-options (which would
// silently ignore anything the operator configured in appsettings.json).
services.Configure<IndicatorOptions>(configuration.GetSection(IndicatorOptions.SectionName));
services.Configure<VcpOptions>(configuration.GetSection(VcpOptions.SectionName));
services.Configure<RsOptions>(configuration.GetSection(RsOptions.SectionName));
services.Configure<StockScoringOptions>(configuration.GetSection(StockScoringOptions.SectionName));
services.Configure<ExitOptions>(configuration.GetSection(ExitOptions.SectionName));
services.Configure<LiveTradingOptions>(configuration.GetSection(LiveTradingOptions.SectionName));
services.Configure<BenchmarkOptions>(configuration.GetSection(BenchmarkOptions.SectionName));

// Tranche 1: real SQLite-backed writer (was NoOpSystemEventWriter in Tranche 0).
services.AddSingleton<ISystemEventWriter>(systemEventWriter);
services.AddSingleton(sqliteConnection);
services.AddSingleton<IInstrumentRepository>(new SqliteInstrumentRepository(sqliteConnection));
services.AddSingleton<IHistoricalBarRepository>(new SqliteHistoricalBarRepository(sqliteConnection));
services.AddSingleton<IUniverseClassificationRepository>(new SqliteUniverseClassificationRepository(sqliteConnection));
services.AddSingleton<ISurveillanceRepository>(new SqliteSurveillanceRepository(sqliteConnection));
// Tranche 18 — the repositories Tranche 1 deliberately deferred (see
// docs/gap-log.md). Same "new T(sqliteConnection) as singleton" pattern
// as the four above; all nine share the one process-lifetime connection.
services.AddSingleton<ISignalRepository>(new SqliteSignalRepository(sqliteConnection));
services.AddSingleton<IOrderRepository>(new SqliteOrderRepository(sqliteConnection));
services.AddSingleton<IPositionRepository>(new SqlitePositionRepository(sqliteConnection));
services.AddSingleton<ITradeRepository>(new SqliteTradeRepository(sqliteConnection));
services.AddSingleton<IRiskEventRepository>(new SqliteRiskEventRepository(sqliteConnection));
services.AddSingleton<IDrawdownEventRepository>(new SqliteDrawdownEventRepository(sqliteConnection));
services.AddSingleton<IPortfolioSnapshotRepository>(new SqlitePortfolioSnapshotRepository(sqliteConnection));
services.AddSingleton<IScannerResultRepository>(new SqliteScannerResultRepository(sqliteConnection));
services.AddSingleton<IBacktestRunRepository>(new SqliteBacktestRunRepository(sqliteConnection));
services.AddSingleton<IEngineStateRepository>(new SqliteEngineStateRepository(sqliteConnection));
services.AddSingleton<IMarketRegimeRepository>(new SqliteMarketRegimeRepository(sqliteConnection));
services.AddSingleton<IDeliveryDataRepository>(new SqliteDeliveryDataRepository(sqliteConnection));
services.AddSingleton<IScanEvidenceRepository>(new SqliteScanEvidenceRepository(sqliteConnection));
services.AddSingleton<IOptimizationRunRepository>(new SqliteOptimizationRunRepository(sqliteConnection));
// Tranche 2: Yahoo Finance historical data provider.
services.AddHttpClient(nameof(YahooHttpFetcher));
services.AddHttpClient(nameof(KiteHttpTransport));
services.AddSingleton(sp => sp.GetRequiredService<IOptions<YahooFinanceOptions>>().Value);
services.AddSingleton<YahooHttpFetcher>();
services.AddSingleton<IYahooHttpFetcher>(sp => sp.GetRequiredService<YahooHttpFetcher>());
services.AddSingleton<IYahooIntradayFetcher>(sp => sp.GetRequiredService<YahooHttpFetcher>()); // same instance => one shared rate limiter
services.AddSingleton<YahooFinanceHistoricalDataProvider>();
services.AddSingleton<IHistoricalMarketDataProvider>(sp => sp.GetRequiredService<YahooFinanceHistoricalDataProvider>());
services.AddSingleton<IBarDownloader>(sp => sp.GetRequiredService<YahooFinanceHistoricalDataProvider>());
services.AddSingleton<IIntradayBarDownloader>(sp => sp.GetRequiredService<YahooFinanceHistoricalDataProvider>());
services.AddSingleton<IFundamentalDataProvider>(sp => new SqliteFundamentalRepository(sqliteConnection));
services.AddSingleton<IInstitutionalActivityProvider>(sp => new SqliteInstitutionalActivityRepository(sqliteConnection));
services.AddSingleton<ISectorClassificationProvider>(sp => new SqliteSectorClassificationProvider(sp.GetRequiredService<IUniverseClassificationRepository>(), sp.GetRequiredService<IInstrumentRepository>()));
services.AddSingleton<INotificationService>(sp => new ConsoleNotificationService(sp.GetRequiredService<IOptions<NotificationOptions>>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Notifications")));

services.AddLogging(builder =>
{
    builder.ClearProviders();
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddConsole();
    builder.AddProvider(new RollingFileLoggerProvider(RepoPaths.Combine(pathsOptions.Logs)));
    builder.AddProvider(new SystemEventLoggerProvider(systemEventWriter));
});

await using var provider = services.BuildServiceProvider();
var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("TradingPlatform.Application");

logger.LogInformation("TradingPlatform starting. Command={Command} Environment={Environment}", command, environmentName);

// --- Validate bound configuration is sane before doing anything else (fail fast) ---
try
{
    ValidateConfiguration(provider);
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Configuration validation failed at startup");
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

// --- Command dispatch: every entry in validCommands is handled below.
// Wrapped in a single top-level try/catch so an unexpected exception deep
// inside any command (a bug, a corrupted row, an I/O failure not already
// caught closer to its source) surfaces as one logged, clean, non-zero
// exit rather than a raw unhandled-exception stack trace to the console.
// Deliberately does NOT attempt any command-specific automated recovery
// (e.g. an emergency flatten-positions attempt for 'live') — an exception
// that reaches this point is by definition an unanticipated failure mode,
// and issuing further automated trading actions into an unknown failure
// state is a materially worse default than stopping and alerting a human
// via the logs. See docs/gap-log.md's "Twenty-seventh item" for the
// reasoning this was deferred, then decided, on.
// Ctrl+C: the first press asks the running command to stop at its next safe point (never mid-order-submission or mid-transaction —
// a hard kill during a live order cycle can leave a broker order the journal does not know about); a second press force-quits.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cancellation.IsCancellationRequested) return; // second press: default behaviour, terminate now
    e.Cancel = true;
    cancellation.Cancel();
    Console.Error.WriteLine("Interrupt received — stopping at the next safe point (press Ctrl+C again to force quit).");
};
var cts = cancellation;

try
{
if (command.Equals("env", StringComparison.OrdinalIgnoreCase))
{
    return await EnvironmentCheckCommand.RunAsync(logger);
}

if (command.Equals("fetch", StringComparison.OrdinalIgnoreCase))
{
    var dataProvider = provider.GetRequiredService<IHistoricalMarketDataProvider>();
    return await FetchCommand.RunAsync(args, dataProvider, logger);
}

if (command.Equals("serve", StringComparison.OrdinalIgnoreCase))
{
    return await ServeCommand.RunAsync(
        args, provider, configuration, logger,
        provider.GetRequiredService<IPortfolioSnapshotRepository>(), provider.GetRequiredService<IPositionRepository>(),
        provider.GetRequiredService<IOrderRepository>(), provider.GetRequiredService<ISignalRepository>(),
        provider.GetRequiredService<IScannerResultRepository>(), provider.GetRequiredService<IBacktestRunRepository>(),
        provider.GetRequiredService<IDrawdownEventRepository>(), provider.GetRequiredService<IRiskEventRepository>(),
        provider.GetRequiredService<ITradeRepository>(),
        provider.GetRequiredService<IInstrumentRepository>(), provider.GetRequiredService<IHistoricalBarRepository>(),
        provider.GetRequiredService<IUniverseClassificationRepository>(), provider.GetRequiredService<ISurveillanceRepository>(),
        provider.GetRequiredService<IOptions<RiskOptions>>().Value, provider.GetRequiredService<IOptions<PortfolioOptions>>().Value,
        provider.GetRequiredService<IOptions<IndicatorOptions>>().Value, provider.GetRequiredService<IOptions<VcpOptions>>().Value,
        provider.GetRequiredService<IOptions<RsOptions>>().Value, provider.GetRequiredService<IOptions<BenchmarkOptions>>().Value);
}

if (command.Equals("universe", StringComparison.OrdinalIgnoreCase) || command.Equals("import", StringComparison.OrdinalIgnoreCase))
{
    // `import` is kept as an alias for `universe import` (it used to be a separate command).
    if (command.Equals("import", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("note: `import` is now `universe import` (same options); forwarding.");
        args = new[] { "universe", "import" }.Concat(args.Skip(1)).ToArray();
    }
    var universeServices = new UniverseServices(
        sqliteConnection, provider.GetRequiredService<IInstrumentRepository>(), provider.GetRequiredService<IUniverseClassificationRepository>(),
        provider.GetRequiredService<ISurveillanceRepository>(), new SqliteFundamentalRepository(sqliteConnection), new SqliteInstitutionalActivityRepository(sqliteConnection),
        provider.GetRequiredService<IHistoricalBarRepository>(), provider.GetRequiredService<IEngineStateRepository>(), provider.GetRequiredService<IOptions<ScannerOptions>>().Value);
    return await UniverseCommand.RunAsync(args, universeServices, logger);
}

if (command.Equals("download", StringComparison.OrdinalIgnoreCase))
{
    var downloadService = new DownloadService(
        provider.GetRequiredService<IInstrumentRepository>(), provider.GetRequiredService<IHistoricalBarRepository>(), provider.GetRequiredService<IBarDownloader>(),
        provider.GetRequiredService<IEngineStateRepository>(), provider.GetRequiredService<IOptions<BenchmarkOptions>>().Value,
        provider.GetRequiredService<IOptions<ScannerOptions>>().Value, logger);
    return await DownloadCommand.RunAsync(args, downloadService, provider.GetRequiredService<IOptions<ScannerOptions>>().Value, logger);
}

if (command.Equals("import-delivery", StringComparison.OrdinalIgnoreCase))
{
    return await DeliveryImportCommand.RunAsync(args, provider.GetRequiredService<IDeliveryDataRepository>(), provider.GetRequiredService<IInstrumentRepository>(), logger, cts.Token);
}

if (command.Equals("download-intraday", StringComparison.OrdinalIgnoreCase))
{
    var intradayDownloader = provider.GetRequiredService<IIntradayBarDownloader>();
    var intradayService = new IntradayDownloadService(provider.GetRequiredService<IInstrumentRepository>(), intradayDownloader,
        provider.GetRequiredService<IOptions<ScannerOptions>>().Value, logger);
    return await IntradayDownloadCommand.RunAsync(args, intradayService, intradayDownloader, logger, cts.Token);
}

if (command.Equals("killswitch", StringComparison.OrdinalIgnoreCase))
{
    return await KillSwitchCommand.RunAsync(
        args, provider.GetRequiredService<IEngineStateRepository>(), provider.GetRequiredService<IRiskEventRepository>(),
        provider.GetService<INotificationService>(), logger);
}

if (command.Equals("portfolio", StringComparison.OrdinalIgnoreCase))
{
    return await PortfolioCommand.RunAsync(
        provider.GetRequiredService<IPortfolioSnapshotRepository>(), provider.GetRequiredService<IPositionRepository>(),
        provider.GetRequiredService<IDrawdownEventRepository>(), logger);
}

if (command.Equals("scan", StringComparison.OrdinalIgnoreCase))
{
    return await ScanCommand.RunAsync(
        provider.GetRequiredService<IInstrumentRepository>(), provider.GetRequiredService<IHistoricalBarRepository>(),
        provider.GetRequiredService<IUniverseClassificationRepository>(), provider.GetRequiredService<ISurveillanceRepository>(),
        provider.GetRequiredService<IScannerResultRepository>(),
        provider.GetRequiredService<IOptions<ScannerOptions>>(), provider.GetRequiredService<IOptions<IndicatorOptions>>(),
        provider.GetRequiredService<IOptions<VcpOptions>>(), provider.GetRequiredService<IOptions<RsOptions>>(),
        provider.GetRequiredService<IOptions<StockScoringOptions>>(), provider.GetRequiredService<IOptions<StrategyOptions>>(),
        provider.GetRequiredService<IOptions<MarketRegimeOptions>>(), provider.GetRequiredService<IOptions<RiskOptions>>(),
        provider.GetRequiredService<IOptions<PortfolioOptions>>(), provider.GetRequiredService<IOptions<BenchmarkOptions>>(), logger,
        cts.Token, provider.GetService<IFundamentalDataProvider>(), provider.GetService<IInstitutionalActivityProvider>(), args.Contains("--explain"),
        provider.GetRequiredService<IMarketRegimeRepository>(), provider.GetRequiredService<ITradeRepository>(),
        provider.GetRequiredService<IScanEvidenceRepository>(), provider.GetRequiredService<IDeliveryDataRepository>());
}

if (command.Equals("backtest", StringComparison.OrdinalIgnoreCase))
{
    return await BacktestCommand.RunAsync(
        args, provider.GetRequiredService<IInstrumentRepository>(), provider.GetRequiredService<IHistoricalBarRepository>(),
        provider.GetRequiredService<IUniverseClassificationRepository>(), provider.GetRequiredService<ISurveillanceRepository>(),
        provider.GetRequiredService<IBacktestRunRepository>(), provider.GetRequiredService<ITradeRepository>(),
        provider.GetRequiredService<IOptions<IndicatorOptions>>(), provider.GetRequiredService<IOptions<VcpOptions>>(),
        provider.GetRequiredService<IOptions<RsOptions>>(), provider.GetRequiredService<IOptions<StockScoringOptions>>(),
        provider.GetRequiredService<IOptions<StrategyOptions>>(), provider.GetRequiredService<IOptions<MarketRegimeOptions>>(),
        provider.GetRequiredService<IOptions<ExitOptions>>(), provider.GetRequiredService<IOptions<RiskOptions>>(),
        provider.GetRequiredService<IOptions<PortfolioOptions>>(), provider.GetRequiredService<IOptions<BacktestOptions>>(),
        provider.GetRequiredService<IOptions<BenchmarkOptions>>(), provider.GetRequiredService<IOptions<PathsOptions>>(), logger, cts.Token, provider.GetRequiredService<IDeliveryDataRepository>());
}

if (command.Equals("walkforward", StringComparison.OrdinalIgnoreCase))
{
    return await WalkForwardCommand.RunAsync(
        args, provider.GetRequiredService<IInstrumentRepository>(), provider.GetRequiredService<IHistoricalBarRepository>(),
        provider.GetRequiredService<IUniverseClassificationRepository>(), provider.GetRequiredService<ISurveillanceRepository>(),
        provider.GetRequiredService<IBacktestRunRepository>(), provider.GetRequiredService<ITradeRepository>(),
        provider.GetRequiredService<IOptions<IndicatorOptions>>(), provider.GetRequiredService<IOptions<VcpOptions>>(),
        provider.GetRequiredService<IOptions<RsOptions>>(), provider.GetRequiredService<IOptions<StockScoringOptions>>(),
        provider.GetRequiredService<IOptions<StrategyOptions>>(), provider.GetRequiredService<IOptions<MarketRegimeOptions>>(),
        provider.GetRequiredService<IOptions<ExitOptions>>(), provider.GetRequiredService<IOptions<RiskOptions>>(),
        provider.GetRequiredService<IOptions<PortfolioOptions>>(), provider.GetRequiredService<IOptions<BacktestOptions>>(),
        provider.GetRequiredService<IOptions<BenchmarkOptions>>(), logger, cts.Token, provider.GetRequiredService<IDeliveryDataRepository>());
}

if (command.Equals("optimize", StringComparison.OrdinalIgnoreCase))
{
    return await OptimizeCommand.RunAsync(
        args, provider.GetRequiredService<IInstrumentRepository>(), provider.GetRequiredService<IHistoricalBarRepository>(),
        provider.GetRequiredService<IUniverseClassificationRepository>(), provider.GetRequiredService<ISurveillanceRepository>(),
        provider.GetRequiredService<IOptions<IndicatorOptions>>(), provider.GetRequiredService<IOptions<VcpOptions>>(),
        provider.GetRequiredService<IOptions<RsOptions>>(), provider.GetRequiredService<IOptions<StockScoringOptions>>(),
        provider.GetRequiredService<IOptions<StrategyOptions>>(), provider.GetRequiredService<IOptions<MarketRegimeOptions>>(),
        provider.GetRequiredService<IOptions<ExitOptions>>(), provider.GetRequiredService<IOptions<RiskOptions>>(),
        provider.GetRequiredService<IOptions<PortfolioOptions>>(), provider.GetRequiredService<IOptions<BacktestOptions>>(),
        provider.GetRequiredService<IOptions<BenchmarkOptions>>(), provider.GetRequiredService<IOptions<PathsOptions>>(), logger,
        cts.Token, provider.GetRequiredService<IOptimizationRunRepository>(), provider.GetRequiredService<IDeliveryDataRepository>());
}

if (command.Equals("montecarlo", StringComparison.OrdinalIgnoreCase))
{
    return await MonteCarloCommand.RunAsync(
        args, provider.GetRequiredService<IBacktestRunRepository>(), provider.GetRequiredService<IOptions<PortfolioOptions>>(),
        provider.GetRequiredService<IOptions<PathsOptions>>(), logger, cts.Token);
}

if (command.Equals("zerodha-login", StringComparison.OrdinalIgnoreCase))
{
    return await ZerodhaLoginCommand.RunAsync(args, provider.GetRequiredService<IOptions<ZerodhaOptions>>().Value, provider, logger, cts.Token);
}

if (command.Equals("reconcile", StringComparison.OrdinalIgnoreCase))
{
    return await ReconcileCommand.RunAsync(
        provider.GetRequiredService<IPositionRepository>(), provider.GetRequiredService<IPortfolioSnapshotRepository>(),
        provider.GetRequiredService<IRiskEventRepository>(), provider.GetRequiredService<IOptions<LiveTradingOptions>>().Value,
        provider.GetRequiredService<IOptions<ZerodhaOptions>>().Value, provider.GetRequiredService<IOptions<PortfolioOptions>>().Value,
        provider, logger, cts.Token);
}

if (command.Equals("paper", StringComparison.OrdinalIgnoreCase) || command.Equals("live", StringComparison.OrdinalIgnoreCase))
{
    return await TradeCycleCommand.RunAsync(
        args, command.Equals("paper", StringComparison.OrdinalIgnoreCase),
        provider.GetRequiredService<IInstrumentRepository>(), provider.GetRequiredService<IHistoricalBarRepository>(),
        provider.GetRequiredService<IUniverseClassificationRepository>(), provider.GetRequiredService<ISurveillanceRepository>(),
        provider.GetRequiredService<IPositionRepository>(), provider.GetRequiredService<IOrderRepository>(),
        provider.GetRequiredService<ITradeRepository>(), provider.GetRequiredService<ISignalRepository>(),
        provider.GetRequiredService<IRiskEventRepository>(), provider.GetRequiredService<IDrawdownEventRepository>(),
        provider.GetRequiredService<IPortfolioSnapshotRepository>(), provider.GetRequiredService<IEngineStateRepository>(),
        provider.GetRequiredService<IOptions<ScannerOptions>>(), provider.GetRequiredService<IOptions<IndicatorOptions>>(),
        provider.GetRequiredService<IOptions<VcpOptions>>(), provider.GetRequiredService<IOptions<RsOptions>>(),
        provider.GetRequiredService<IOptions<StockScoringOptions>>(), provider.GetRequiredService<IOptions<StrategyOptions>>(),
        provider.GetRequiredService<IOptions<MarketRegimeOptions>>(), provider.GetRequiredService<IOptions<ExitOptions>>(),
        provider.GetRequiredService<IOptions<RiskOptions>>(), provider.GetRequiredService<IOptions<PortfolioOptions>>(),
        provider.GetRequiredService<IOptions<BacktestOptions>>(), provider.GetRequiredService<IOptions<BenchmarkOptions>>(),
        provider.GetRequiredService<IOptions<LiveTradingOptions>>(), provider.GetRequiredService<IOptions<ZerodhaOptions>>(),
        provider, logger, cts.Token, provider.GetRequiredService<IMarketRegimeRepository>(), provider.GetRequiredService<IScanEvidenceRepository>(), provider.GetRequiredService<IDeliveryDataRepository>());
}

}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    logger.LogWarning("'{Command}' was cancelled by the user.", command);
    Console.Error.WriteLine($"'{command}' cancelled.");
    return 130;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Unhandled exception while running '{Command}'. Command aborted.", command);
    Console.Error.WriteLine($"Unexpected error while running '{command}': {ex.Message}");
    Console.Error.WriteLine("See the log files under the configured Logs directory for full details. No automated recovery action was taken.");
    return 1;
}

// Tranches 0-1 cover solution structure, domain models, configuration,
// logging, environment disclosure, and SQLite persistence. Tranche 18
// wired scan/backtest/portfolio/serve; Tranche 19 wired walkforward/
// optimize/montecarlo/reconcile; Tranche 20 wired paper/live — every
// command in validCommands is now genuinely implemented. See
// docs/gap-log.md for known simplifications within each.
logger.LogInformation("Command '{Command}' is recognized but not yet implemented. See docs/gap-log.md.", command);
Console.WriteLine($"'{command}' is wired into the CLI but not yet implemented — see docs/gap-log.md for the tranche that adds it.");
Console.WriteLine("Run 'dotnet run -- env' to see the current environment disclosure.");
return 0;

static void ValidateConfiguration(IServiceProvider provider)
{
    // Touching IOptions<T>.Value forces binding + throws if the section is malformed.
    _ = provider.GetRequiredService<IOptions<PortfolioOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<RiskOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<StrategyOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<MarketRegimeOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<YahooFinanceOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<ZerodhaOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<BacktestOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<ScannerOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<NotificationOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<IndicatorOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<VcpOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<RsOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<StockScoringOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<ExitOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<LiveTradingOptions>>().Value;
    _ = provider.GetRequiredService<IOptions<BenchmarkOptions>>().Value;
}

static void PrintUsage()
{
    Console.WriteLine("TradingPlatform - Aggressive NSE Momentum Fund (AggressiveMomentumV1)");
    Console.WriteLine();
    Console.WriteLine("Usage: dotnet run -- <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  scan          Run the universe scanner and print top-ranked candidates");
    Console.WriteLine("  backtest      Run a historical backtest (--from --to)");
    Console.WriteLine("  optimize      Grid search over RiskPercentPerTrade x StopAtrMultiplierMin (--from --to)");
    Console.WriteLine("  walkforward   Rolling train/validate/test windows (--from --to --train-years --validate-months --test-months --step-months)");
    Console.WriteLine("  montecarlo    Monte Carlo simulation over a completed backtest run (--run-id --iterations --seed)");
    Console.WriteLine("  paper         Run one paper-trading cycle (simulated fills, real signals; --acknowledge-drawdown-reset, --acknowledge-kill-switch-reset)");
    Console.WriteLine("  live          Run one live-trading cycle (requires Mode=Live AND AllowLiveTrading=true, else falls back to paper; --acknowledge-drawdown-reset, --acknowledge-kill-switch-reset)");
    Console.WriteLine("  portfolio     Print current portfolio/positions/risk summary");
    Console.WriteLine("  reconcile     Reconcile local state against the broker");
    Console.WriteLine("  download-intraday 60m/15m/5m bars into the cache for the multi-timeframe layers (--timeframe 60m|15m|5m --days --symbols)");
    Console.WriteLine("  import-delivery (--file F | --dir D) [--all-symbols --dry-run]   load NSE delivery % (sec_bhavdata_full or generic CSV)");
    Console.WriteLine("  zerodha-login Daily Kite session: print the login URL / exchange a request_token for the access token");
    Console.WriteLine("  env           Show environment disclosure: dependency reachability + active substitutions");
    Console.WriteLine("  fetch         Manually fetch/cache historical bars for one symbol (--symbol --from --to)");
    Console.WriteLine("  download      Download daily OHLC for the whole universe into the SQLite cache (incremental, resumable; --symbols --years --from --to --force)");
    Console.WriteLine("  universe      Manage the stock universe in SQLite: import (universe.csv), list, show, stats, activate/deactivate, classify, surveillance — see docs/data-import.md");
    Console.WriteLine("  import        (alias of `universe import`)");
    Console.WriteLine("  killswitch    status | activate --reason \"...\" | reset — manual kill switch control (spec §6.4)");
    Console.WriteLine("  serve         Start the REST API + dashboard (--scheduler also runs the market-hours scanner, spec §7.1) — GET /api/portfolio /positions /orders /signals /scanner /risk /performance /backtests");
    Console.WriteLine();
    Console.WriteLine("  -h, --help    Show this help");
}
