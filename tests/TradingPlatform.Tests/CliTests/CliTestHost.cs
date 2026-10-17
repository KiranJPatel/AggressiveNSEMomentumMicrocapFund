using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Tests.CliTests;

/// <summary>
/// Builds a real DI container for CLI command integration tests: the real
/// config/appsettings.json (same file Program.cs itself binds — so a test
/// using this host is exercising the same configuration path as an actual
/// CLI invocation, not a hand-picked set of test-only option values that
/// could quietly drift from what's actually shipped) plus a real,
/// schema-initialized temp SQLite database (same convention as
/// TradingRepositoriesTests.OpenInitializedTempDb). Dispose to close the
/// connection and delete the temp file.
/// </summary>
public sealed class CliTestHost : IDisposable
{
    public SqliteConnection Db { get; }
    public IServiceProvider Provider { get; }
    private readonly string _dbPath;

    private CliTestHost(SqliteConnection db, IServiceProvider provider, string dbPath)
    {
        Db = db;
        Provider = provider;
        _dbPath = dbPath;
    }

    public static CliTestHost Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tp_cli_test_{Guid.NewGuid():N}.db");
        var db = new SqliteConnection(dbPath);
        SchemaInitializer.Initialize(db);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(RepoPaths.Combine("config"))
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        // Same Configure<T> calls as Program.cs — see the comment on this
        // class for why that duplication is deliberate, not an oversight.
        services.Configure<PortfolioOptions>(configuration.GetSection(PortfolioOptions.SectionName));
        services.Configure<RiskOptions>(configuration.GetSection(RiskOptions.SectionName));
        services.Configure<StrategyOptions>(configuration.GetSection(StrategyOptions.SectionName));
        services.Configure<MarketRegimeOptions>(configuration.GetSection(MarketRegimeOptions.SectionName));
        services.Configure<BacktestOptions>(configuration.GetSection(BacktestOptions.SectionName));
        services.Configure<ScannerOptions>(configuration.GetSection(ScannerOptions.SectionName));
        services.Configure<PathsOptions>(configuration.GetSection(PathsOptions.SectionName));
        services.Configure<IndicatorOptions>(configuration.GetSection(IndicatorOptions.SectionName));
        services.Configure<VcpOptions>(configuration.GetSection(VcpOptions.SectionName));
        services.Configure<RsOptions>(configuration.GetSection(RsOptions.SectionName));
        services.Configure<StockScoringOptions>(configuration.GetSection(StockScoringOptions.SectionName));
        services.Configure<ExitOptions>(configuration.GetSection(ExitOptions.SectionName));
        services.Configure<BenchmarkOptions>(configuration.GetSection(BenchmarkOptions.SectionName));

        services.AddSingleton<IInstrumentRepository>(new SqliteInstrumentRepository(db));
        services.AddSingleton<IHistoricalBarRepository>(new SqliteHistoricalBarRepository(db));
        services.AddSingleton<IUniverseClassificationRepository>(new SqliteUniverseClassificationRepository(db));
        services.AddSingleton<ISurveillanceRepository>(new SqliteSurveillanceRepository(db));
        services.AddSingleton<IPositionRepository>(new SqlitePositionRepository(db));
        services.AddSingleton<ITradeRepository>(new SqliteTradeRepository(db));
        services.AddSingleton<IDrawdownEventRepository>(new SqliteDrawdownEventRepository(db));
        services.AddSingleton<IPortfolioSnapshotRepository>(new SqlitePortfolioSnapshotRepository(db));
        services.AddSingleton<IScannerResultRepository>(new SqliteScannerResultRepository(db));
        services.AddSingleton<IBacktestRunRepository>(new SqliteBacktestRunRepository(db));
        services.AddSingleton<IOrderRepository>(new SqliteOrderRepository(db));
        services.AddSingleton<IEngineStateRepository>(new SqliteEngineStateRepository(db));
        services.AddSingleton<IRiskEventRepository>(new SqliteRiskEventRepository(db));
        services.AddSingleton<ISignalRepository>(new SqliteSignalRepository(db));
        services.AddSingleton<IMarketRegimeRepository>(new SqliteMarketRegimeRepository(db));
        services.AddSingleton<IDeliveryDataRepository>(new SqliteDeliveryDataRepository(db));
        services.AddSingleton<IScanEvidenceRepository>(new SqliteScanEvidenceRepository(db));
        services.AddSingleton<IOptimizationRunRepository>(new SqliteOptimizationRunRepository(db));

        var provider = services.BuildServiceProvider();
        return new CliTestHost(db, provider, dbPath);
    }

    public IOptions<T> Options<T>() where T : class, new() => Provider.GetRequiredService<IOptions<T>>();
    public T Repo<T>() where T : notnull => Provider.GetRequiredService<T>();
    public static Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;

    public void Dispose()
    {
        Db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }
}
