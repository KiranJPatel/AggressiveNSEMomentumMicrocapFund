using TradingPlatform.Core.Configuration;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CoreTests;

public class PortfolioOptionsTests
{
    [Fact]
    public void InitialCapital_MatchesSpecifiedCapital()
    {
        var o = new PortfolioOptions();
        Assert.Equal(1_000_000m, o.InitialCapital);
    }

    [Fact]
    public void MaxSinglePositionPercent_DoesNotExceedExceptionalPositionPercent()
    {
        var o = new PortfolioOptions();
        Assert.True(o.MaxSinglePositionPercent <= o.ExceptionalPositionPercent);
    }
}
