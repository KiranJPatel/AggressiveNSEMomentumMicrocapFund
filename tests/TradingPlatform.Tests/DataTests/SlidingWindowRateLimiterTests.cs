using System.Diagnostics;
using TradingPlatform.Data.Http;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

public class SlidingWindowRateLimiterTests
{
    [Fact]
    public async Task AllowsUpToLimit_WithoutBlocking()
    {
        var limiter = new SlidingWindowRateLimiter(3, TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();

        await limiter.WaitAsync();
        await limiter.WaitAsync();
        await limiter.WaitAsync();

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"First 3 calls under the limit should not block; took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task BlocksTheCallOverTheLimit_UntilWindowExpires()
    {
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.FromMilliseconds(300));

        await limiter.WaitAsync();
        await limiter.WaitAsync();

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync(); // 3rd call must wait for the window to slide
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 200, $"3rd call should have been throttled; only waited {sw.ElapsedMilliseconds}ms.");
    }
}
