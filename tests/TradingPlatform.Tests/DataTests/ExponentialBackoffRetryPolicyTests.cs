using TradingPlatform.Data.Http;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

public class ExponentialBackoffRetryPolicyTests
{
    [Fact]
    public async Task SucceedsOnFirstAttempt_NoRetryNeeded()
    {
        var policy = new ExponentialBackoffRetryPolicy(3, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5));
        var attempts = 0;

        var result = await policy.ExecuteAsync(
            (_, _) => { attempts++; return Task.FromResult(42); },
            _ => true);

        Assert.Equal(42, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetriesUpToMaxRetries_ThenSucceeds()
    {
        var policy = new ExponentialBackoffRetryPolicy(3, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5));
        var attempts = 0;

        var result = await policy.ExecuteAsync(
            (attempt, _) =>
            {
                attempts++;
                if (attempt < 2) throw new InvalidOperationException("transient");
                return Task.FromResult("ok");
            },
            _ => true);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts); // attempts 0, 1 fail; attempt 2 succeeds
    }

    [Fact]
    public async Task ExhaustsRetries_ThrowsLastException()
    {
        var policy = new ExponentialBackoffRetryPolicy(2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5));
        var attempts = 0;

        await AssertThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync<int>(
            (_, _) => { attempts++; throw new InvalidOperationException($"fail {attempts}"); },
            _ => true));

        Assert.Equal(3, attempts); // initial + 2 retries
    }

    [Fact]
    public async Task NonRetryablePredicate_ThrowsImmediately_NoRetries()
    {
        var policy = new ExponentialBackoffRetryPolicy(5, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5));
        var attempts = 0;

        await AssertThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync<int>(
            (_, _) => { attempts++; throw new InvalidOperationException("permanent"); },
            _ => false)); // never retry

        Assert.Equal(1, attempts);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new AssertionFailedException($"Expected {typeof(TException).Name} to be thrown.");
    }
}
