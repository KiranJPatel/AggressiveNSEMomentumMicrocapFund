using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Scanner.Scheduling;

/// <summary>
/// Executes one full scan cycle (gathering current data for the whole
/// universe and calling ScannerEngine.Scan) and does whatever the host
/// wants with the result (persist, publish, etc.). Deliberately a seam,
/// not an implementation: assembling live inputs for ScannerEngine.Scan —
/// fetching current bars for the whole universe (Tranche 2's Yahoo
/// provider), current surveillance/classification snapshots (Tranche 1's
/// repositories), and the live portfolio context (Tranche 15's live
/// orchestration, which doesn't exist yet) — is an Application-layer
/// wiring job for whichever tranche builds the actual live-trading host
/// process, not this one. ScannerBackgroundService's own job is strictly
/// the scheduling: knowing WHEN to call this, not HOW it's implemented.
/// </summary>
public interface IScanRunner
{
    Task<IReadOnlyList<ScannerResult>> RunScanAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Spec §7.1: a scheduled background service that runs a scan every
/// ScannerOptions.IntervalMinutes while the market is open (per
/// MarketHoursCalculator), and sleeps until the next open otherwise —
/// never busy-polling outside market hours. Runs via ASP.NET Core's
/// BackgroundService/IHostedService (available through the
/// Microsoft.AspNetCore.App framework reference already on this project,
/// spec §1.3's NuGet-optional constraint — no external package needed).
/// </summary>
public sealed class ScannerBackgroundService : BackgroundService
{
    private readonly IScanRunner _scanRunner;
    private readonly IOptions<ScannerOptions> _options;
    private readonly ILogger<ScannerBackgroundService> _logger;
    private readonly TimeProvider _timeProvider;

    public ScannerBackgroundService(IScanRunner scanRunner, IOptions<ScannerOptions> options, ILogger<ScannerBackgroundService> logger, TimeProvider? timeProvider = null)
    {
        _scanRunner = scanRunner;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow();
            var options = _options.Value;

            if (!MarketHoursCalculator.IsMarketOpen(now, options))
            {
                var wait = MarketHoursCalculator.TimeUntilNextOpen(now, options);
                _logger.LogInformation("Market closed — next scan window opens in {Wait}.", wait);
                await DelayAsync(wait, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var results = await _scanRunner.RunScanAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Scan cycle complete: {Count} ranked candidates.", results.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single scan cycle's failure (a data-source outage, a transient error) must never
                // crash the whole hosted service — spec's whole premise is unattended operation; the
                // NEXT cycle gets a fresh chance rather than the service going silently dark.
                _logger.LogError(ex, "Scan cycle failed — will retry next interval.");
            }

            await DelayAsync(TimeSpan.FromMinutes(options.IntervalMinutes), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) return;
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown — ExecuteAsync's loop condition handles it.
        }
    }
}
