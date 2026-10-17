using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using TradingPlatform.Api;
using TradingPlatform.Application.Api;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Core.Interfaces;
using Microsoft.Extensions.Options;
using TradingPlatform.Scanner;
using TradingPlatform.Scanner.Scheduling;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// Starts the spec Section 7.5 REST API surface (Tranche 17) as a real,
/// runnable Kestrel host - `dotnet run -- serve` genuinely responds to
/// every route today: the 8 from spec §7.5, plus /api/metrics (Tranche
/// 21), plus /api/instruments, /api/chart/{symbol}, and
/// /api/backtests/{id}/detail (added alongside the HTML dashboard). As of
/// Tranche 18, every route is backed by a real repository-backed
/// SqliteQueryServices implementation (constructed from the SAME
/// repository instances/SqliteConnection the rest of the process uses,
/// passed in rather than opening a second connection to the same
/// database file) — see that class's XML doc for the honest caveat about
/// what's genuinely populated today versus what's correct-but-currently-
/// empty pending activity from `paper`/`live`.
/// Listens on ASPNETCORE_URLS (standard ASP.NET Core convention, default
/// http://localhost:5000) rather than a bespoke config section, since
/// this is exactly what that environment variable is for. Also serves
/// static files from wwwroot/ (the dashboard's index.html) if present.
/// </summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(
        string[] args, IServiceProvider commandProvider,
        IConfiguration configuration, ILogger logger,
        IPortfolioSnapshotRepository portfolioSnapshots, IPositionRepository positions, IOrderRepository orders,
        ISignalRepository signals, IScannerResultRepository scannerResults, IBacktestRunRepository backtestRuns,
        IDrawdownEventRepository drawdownEvents, IRiskEventRepository riskEvents, ITradeRepository trades,
        IInstrumentRepository instruments, IHistoricalBarRepository bars, IUniverseClassificationRepository classifications,
        ISurveillanceRepository surveillance,
        RiskOptions riskOptions, PortfolioOptions portfolioOptions, IndicatorOptions indicatorOptions,
        VcpOptions vcpOptions, RsOptions rsOptions, BenchmarkOptions benchmarkOptions)
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiSecurity.ApiKeyEnvVar);
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (ApiSecurity.StartupViolation(urls, apiKey, args.Contains("--allow-unauthenticated")) is { } violation)
        {
            Console.Error.WriteLine(violation);
            return 1;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // AppContext.BaseDirectory (the built DLL's own folder), not
            // Directory.GetCurrentDirectory() — `dotnet run --project X`
            // does not reliably leave the process CWD at X, so the default
            // WebRootPath ("{CWD}/wwwroot") would be wrong whenever `serve`
            // is invoked from anywhere other than the project folder itself
            // (every example in README.md runs it from the repo root).
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Configuration.AddConfiguration(configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        // Enums (OrderState, PyramidStage, RegimeClassification, ExitReason,
        // MarketCapBucket, ...) serialize as readable strings ("Filled", not
        // a raw integer) — the default is integer-valued, which every JSON
        // consumer of this API (starting with the dashboard) would otherwise
        // have to hand-maintain an enum-value lookup table to decode.
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        builder.Services.AddSingleton<IPortfolioQueryService>(new SqlitePortfolioQueryService(portfolioSnapshots));
        builder.Services.AddSingleton<IPositionQueryService>(new SqlitePositionQueryService(positions));
        builder.Services.AddSingleton<IOrderQueryService>(new SqliteOrderQueryService(orders));
        builder.Services.AddSingleton<ISignalQueryService>(new SqliteSignalQueryService(signals));
        builder.Services.AddSingleton<IScannerQueryService>(new SqliteScannerQueryService(scannerResults));
        builder.Services.AddSingleton<IRiskQueryService>(new SqliteRiskQueryService(drawdownEvents, portfolioSnapshots, riskEvents, riskOptions));
        builder.Services.AddSingleton<IPerformanceQueryService>(new SqlitePerformanceQueryService(trades, portfolioSnapshots, portfolioOptions, indicatorOptions));
        builder.Services.AddSingleton<IBacktestQueryService>(new SqliteBacktestQueryService(backtestRuns));
        builder.Services.AddSingleton<IMetricsQueryService>(new SqliteMetricsQueryService(portfolioSnapshots, signals, orders, positions));
        builder.Services.AddSingleton<IInstrumentQueryService>(new SqliteInstrumentQueryService(instruments));
        builder.Services.AddSingleton<IChartQueryService>(new SqliteChartQueryService(instruments, bars, classifications, surveillance, indicatorOptions, vcpOptions, rsOptions, benchmarkOptions));

        // Spec §7.1: the market-hours scanner as a real hosted background service (opt-in via --scheduler so a plain
        // `serve` stays a pure read-only dashboard/API). Cancellation + per-cycle fault isolation live in
        // ScannerBackgroundService itself; this just supplies the IScanRunner it was always missing.
        var schedulerEnabled = args.Contains("--scheduler");
        if (schedulerEnabled)
        {
            var scannerOptions = commandProvider.GetRequiredService<IOptions<ScannerOptions>>();
            var engineOptions = new ScannerEngineOptions
            {
                Scanner = scannerOptions.Value, Indicator = indicatorOptions, Vcp = vcpOptions, Rs = rsOptions,
                StockScoring = commandProvider.GetRequiredService<IOptions<StockScoringOptions>>().Value,
                Strategy = commandProvider.GetRequiredService<IOptions<StrategyOptions>>().Value,
                MarketRegime = commandProvider.GetRequiredService<IOptions<MarketRegimeOptions>>().Value,
                Risk = riskOptions, Portfolio = portfolioOptions,
            };
            builder.Services.AddSingleton(scannerOptions);
            builder.Services.AddSingleton<IScanRunner>(new ScanRunner(
                instruments, bars, classifications, surveillance, scannerResults, engineOptions, benchmarkOptions, logger,
                commandProvider.GetService<INotificationService>(),
                commandProvider.GetService<IFundamentalDataProvider>(), commandProvider.GetService<IInstitutionalActivityProvider>(),
                commandProvider.GetService<IMarketRegimeRepository>(), commandProvider.GetService<ITradeRepository>(), commandProvider.GetService<IScanEvidenceRepository>(), commandProvider.GetService<IDeliveryDataRepository>()));
            builder.Services.AddHostedService<ScannerBackgroundService>();
            // Spec §7.1 pre-/post-market jobs (data refresh, validation, regime, reconciliation / performance, DB snapshot, daily report).
            builder.Services.AddSingleton(new DailyJobsRunner(commandProvider, logger));
            builder.Services.AddHostedService<DailyJobsBackgroundService>();
        }

        var app = builder.Build();

        // Read-only effective configuration for the dashboard's Settings page — never accepts writes, never emits secrets.
        app.MapGet("/api/settings", () => Microsoft.AspNetCore.Http.Results.Ok(SettingsSnapshot.Build(configuration)));
        if (!string.IsNullOrEmpty(apiKey))
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api")
                    && !ApiSecurity.IsAuthorized(apiKey, context.Request.Headers[ApiSecurity.HeaderName].ToString()))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = $"Missing or invalid {ApiSecurity.HeaderName} header." });
                    return;
                }
                await next();
            });
        }
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapTradingPlatformApi();

        logger.LogInformation("REST API starting (spec Section 7.5) — routes backed by real SQLite-backed query services (Tranche 18/21/dashboard).");
        Console.WriteLine("API listening — GET /api/portfolio /positions /orders /signals /scanner /risk /performance /backtests /metrics /instruments /chart/{symbol}");
        Console.WriteLine("Dashboard (if wwwroot/index.html exists): open the same address in a browser.");
        Console.WriteLine(schedulerEnabled ? "Scanner scheduler ENABLED (--scheduler): scanning every ScannerOptions.IntervalMinutes during market hours." : "Scanner scheduler off (add --scheduler to enable the market-hours scan loop).");
        Console.WriteLine(string.IsNullOrEmpty(apiKey)
            ? "API access: open (loopback only). Set TRADING_API_KEY to require an X-Api-Key header on /api/*."
            : "API access: /api/* requires the X-Api-Key header (TRADING_API_KEY).");
        Console.WriteLine("Press Ctrl+C to stop.");

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
