namespace TradingPlatform.Data.Http;

/// <summary>
/// Hand-rolled retry-with-exponential-backoff (Polly is a NuGet package and
/// this environment has no NuGet access — see docs/gap-log.md). Retries on
/// any exception the caller's predicate accepts, with backoff doubling each
/// attempt up to a configured ceiling, plus up to 20% jitter so concurrent
/// callers don't all retry in lockstep.
/// </summary>
public sealed class ExponentialBackoffRetryPolicy
{
    private readonly int _maxRetries;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Random _random = new();

    public ExponentialBackoffRetryPolicy(int maxRetries, TimeSpan initialDelay, TimeSpan maxDelay)
    {
        if (maxRetries < 0) throw new ArgumentOutOfRangeException(nameof(maxRetries));
        _maxRetries = maxRetries;
        _initialDelay = initialDelay;
        _maxDelay = maxDelay;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> action,
        Func<Exception, bool> shouldRetry,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _maxRetries && shouldRetry(ex))
            {
                lastException = ex;
                var delay = ComputeDelay(attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        // Final attempt (attempt == _maxRetries) is allowed to throw normally, uncaught, by the loop above.
        // We only reach here if _maxRetries == 0 and the first attempt already threw and wasn't retried,
        // or defensively if the loop somehow falls through.
        throw lastException ?? new InvalidOperationException("Retry policy exhausted with no recorded exception.");
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var baseDelayMs = _initialDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var cappedMs = Math.Min(baseDelayMs, _maxDelay.TotalMilliseconds);
        var jitterFraction = 1.0 + (_random.NextDouble() * 0.2 - 0.1); // +/-10%
        return TimeSpan.FromMilliseconds(cappedMs * jitterFraction);
    }
}
