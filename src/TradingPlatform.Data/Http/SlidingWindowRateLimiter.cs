namespace TradingPlatform.Data.Http;

/// <summary>
/// Hand-rolled sliding-window rate limiter (Polly's rate-limit policies are
/// also NuGet-only — see docs/gap-log.md). Tracks recent call timestamps in
/// a queue; WaitAsync blocks only long enough to stay under the configured
/// requests-per-minute ceiling.
/// </summary>
public sealed class SlidingWindowRateLimiter
{
    private readonly int _maxRequestsPerWindow;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _callTimestamps = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SlidingWindowRateLimiter(int maxRequestsPerWindow, TimeSpan window)
    {
        if (maxRequestsPerWindow <= 0) throw new ArgumentOutOfRangeException(nameof(maxRequestsPerWindow));
        _maxRequestsPerWindow = maxRequestsPerWindow;
        _window = window;
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TimeSpan? delay = null;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                while (_callTimestamps.Count > 0 && now - _callTimestamps.Peek() > _window)
                    _callTimestamps.Dequeue();

                if (_callTimestamps.Count < _maxRequestsPerWindow)
                {
                    _callTimestamps.Enqueue(now);
                    return;
                }

                delay = _window - (now - _callTimestamps.Peek());
            }
            finally
            {
                _gate.Release();
            }

            if (delay is { } d && d > TimeSpan.Zero)
                await Task.Delay(d, cancellationToken).ConfigureAwait(false);
        }
    }
}
