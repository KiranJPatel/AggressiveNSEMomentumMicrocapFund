using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Scanner.Scheduling;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ScannerTests;

public class ScannerBackgroundServiceTests
{
    /// <summary>Fires timers immediately (ignoring dueTime) so tests never wait on real wall-clock time — MarketHoursCalculatorTests already thoroughly covers real timing correctness.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public DateTimeOffset FixedNow { get; set; }
        public override DateTimeOffset GetUtcNow() => FixedNow;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private sealed class FakeScanRunner : IScanRunner
    {
        public int CallCount;
        public Task<IReadOnlyList<ScannerResult>> RunScanAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult<IReadOnlyList<ScannerResult>>(Array.Empty<ScannerResult>());
        }
    }

    // 2026-03-02 11:00 IST is a Monday during market hours.
    private static readonly DateTimeOffset OpenMarketInstant = new DateTimeOffset(2026, 3, 2, 11, 0, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();
    // Saturday — always closed.
    private static readonly DateTimeOffset ClosedMarketInstant = new DateTimeOffset(2026, 3, 7, 11, 0, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();

    private static IOptions<ScannerOptions> Options(int intervalMinutes = 15) => Microsoft.Extensions.Options.Options.Create(new ScannerOptions
    {
        IntervalMinutes = intervalMinutes, MarketOpenLocalTime = "09:15", MarketCloseLocalTime = "15:30", MarketTimeZoneId = "Asia/Kolkata",
    });

    [Fact]
    public async Task WhenMarketOpen_CallsScanRunner()
    {
        var runner = new FakeScanRunner();
        var timeProvider = new ImmediateTimeProvider { FixedNow = OpenMarketInstant };
        var service = new TradingPlatform.Scanner.Scheduling.ScannerBackgroundService(runner, Options(), NullLogger<TradingPlatform.Scanner.Scheduling.ScannerBackgroundService>.Instance, timeProvider);

        using var cts = new CancellationTokenSource();
        var startTask = service.StartAsync(cts.Token);
        await Task.Delay(200); // give the immediate-timer loop a few cycles to run
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Assert.True(runner.CallCount > 0);
    }

    [Fact]
    public async Task WhenMarketClosed_NeverCallsScanRunner()
    {
        var runner = new FakeScanRunner();
        var timeProvider = new ImmediateTimeProvider { FixedNow = ClosedMarketInstant };
        var service = new TradingPlatform.Scanner.Scheduling.ScannerBackgroundService(runner, Options(), NullLogger<TradingPlatform.Scanner.Scheduling.ScannerBackgroundService>.Instance, timeProvider);

        using var cts = new CancellationTokenSource();
        var startTask = service.StartAsync(cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task ScanRunnerThrows_ServiceKeepsRunning_NeverCrashes()
    {
        var callCount = 0;
        var timeProvider = new ImmediateTimeProvider { FixedNow = OpenMarketInstant };
        var throwingRunner = new ThrowingScanRunner(() => callCount++);
        var service = new TradingPlatform.Scanner.Scheduling.ScannerBackgroundService(throwingRunner, Options(), NullLogger<TradingPlatform.Scanner.Scheduling.ScannerBackgroundService>.Instance, timeProvider);

        using var cts = new CancellationTokenSource();
        var startTask = service.StartAsync(cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Assert.True(callCount > 1, "The service must keep retrying on later cycles even after a scan cycle throws.");
    }

    private sealed class ThrowingScanRunner : IScanRunner
    {
        private readonly Action _onCall;
        public ThrowingScanRunner(Action onCall) => _onCall = onCall;
        public Task<IReadOnlyList<ScannerResult>> RunScanAsync(CancellationToken cancellationToken)
        {
            _onCall();
            throw new InvalidOperationException("simulated data-source failure");
        }
    }
}
