using TradingPlatform.Core.Configuration;
using TradingPlatform.Risk.Drawdown;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.RiskTests;

public class PortfolioRiskGateTests
{
    private static readonly RiskOptions Options = new()
    {
        DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList(),
        DailyLossLimitPercent = 2m,
        WeeklyLossLimitPercent = 4m,
        MonthlyLossLimitPercent = 8m,
        WeeklyLossLimitRiskReductionPercent = 50m,
        MonthlyLossLimitRiskReductionPercent = 75m,
    };

    private static DateOnly D(int day) => new DateOnly(2026, 1, 5).AddDays(day); // starts on a Monday

    private static DrawdownState Drawdown(decimal nav, decimal peak = 1_000_000m) =>
        DrawdownEngine.Evaluate(
            DrawdownEngine.Evaluate(null, D(0), peak, Options).State,
            D(1), nav, Options).State;

    [Fact]
    public void NoBreachesAnywhere_FullDeployableCapital_NoBlock()
    {
        var dd = Drawdown(990_000m); // 1% down, FullAggression tier
        var loss = PeriodicLossLimitEngine.Evaluate(null, D(1), 990_000m, Options);

        var result = PortfolioRiskGate.Evaluate(dd, loss, Options);

        Assert.Equal(100m, result.DeployableCapitalPercent);
        Assert.False(result.BlocksNewEntries);
        Assert.Equal(0, result.BlockingReasons.Count);
    }

    [Fact]
    public void DrawdownTierReduction_TakesEffect_WhenItIsTheTightest()
    {
        var dd = Drawdown(780_000m); // 22% down -> ReduceRisk40 tier (60% deployable)
        var loss = PeriodicLossLimitEngine.Evaluate(null, D(1), 780_000m, Options); // no period breach yet

        var result = PortfolioRiskGate.Evaluate(dd, loss, Options);

        Assert.Equal(60m, result.DeployableCapitalPercent);
        Assert.Equal(1, result.BindingConstraints.Count);
        Assert.True(result.BindingConstraints.Contains(nameof(PortfolioRiskGateResult.DrawdownBasedDeployablePercent)));
        Assert.False(result.BlocksNewEntries); // reduced, not blocked — ReduceRisk40 isn't the shutdown tier
    }

    [Fact]
    public void WeeklyBreach_TakesEffect_WhenTighterThanDrawdown()
    {
        var dd = Drawdown(995_000m); // barely any drawdown -> 100% deployable on that axis
        var s1 = PeriodicLossLimitEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var loss = PeriodicLossLimitEngine.Evaluate(s1, D(1), 950_000m, Options); // -5% this week, breaches 4%

        var result = PortfolioRiskGate.Evaluate(dd, loss, Options);

        Assert.Equal(50m, result.DeployableCapitalPercent); // 100 - WeeklyLossLimitRiskReductionPercent
        Assert.Equal(1, result.BindingConstraints.Count);
        Assert.True(result.BindingConstraints.Contains(nameof(PortfolioRiskGateResult.WeeklyBasedDeployablePercent)));
        Assert.False(result.BlocksNewEntries); // weekly is a reduction, never a block
    }

    [Fact]
    public void MonthlyBreach_IsDeeperCutThanWeekly_WhenBothBreach()
    {
        var dd = Drawdown(995_000m);
        var s1 = PeriodicLossLimitEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var loss = PeriodicLossLimitEngine.Evaluate(s1, D(1), 900_000m, Options); // -10%, breaches both weekly (4%) and monthly (8%)

        var result = PortfolioRiskGate.Evaluate(dd, loss, Options);

        Assert.Equal(25m, result.DeployableCapitalPercent); // 100 - MonthlyLossLimitRiskReductionPercent(75), the tighter of the two
        Assert.Equal(1, result.BindingConstraints.Count);
        Assert.True(result.BindingConstraints.Contains(nameof(PortfolioRiskGateResult.MonthlyBasedDeployablePercent)));
    }

    [Fact]
    public void DailyLimitBreach_BlocksNewEntries_EvenWithNoDrawdown()
    {
        var dd = Drawdown(999_000m); // negligible drawdown
        var s1 = PeriodicLossLimitEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var loss = PeriodicLossLimitEngine.Evaluate(s1, D(0), 975_000m, Options); // -2.5% same day, breaches daily

        var result = PortfolioRiskGate.Evaluate(dd, loss, Options);

        Assert.True(result.BlocksNewEntries);
        Assert.Equal(1, result.BlockingReasons.Count);
        Assert.True(result.BlockingReasons.Contains("DailyLossLimitBreached"));
        // Daily is a full block, not a size reduction — it doesn't touch DeployableCapitalPercent.
        Assert.Equal(100m, result.DeployableCapitalPercent);
    }

    [Fact]
    public void TradingShutdown_BlocksNewEntries_AndSurfacesManualResetRequirement()
    {
        var dd = Drawdown(580_000m); // 42% -> TradingShutdown tier
        var loss = PeriodicLossLimitEngine.Evaluate(null, D(1), 580_000m, Options);

        var result = PortfolioRiskGate.Evaluate(dd, loss, Options);

        Assert.True(result.BlocksNewEntries);
        Assert.True(result.RequiresManualReset);
        Assert.Equal(1, result.BlockingReasons.Count);
        Assert.True(result.BlockingReasons.Contains("TradingShutdown"));
        Assert.Equal(0m, result.DeployableCapitalPercent); // TradingShutdown tier's RiskReductionPercent is 100
    }

    [Fact]
    public void BothDailyAndShutdown_ListsBothBlockingReasons()
    {
        var dd = Drawdown(580_000m); // shutdown tier
        var s1 = PeriodicLossLimitEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var loss = PeriodicLossLimitEngine.Evaluate(s1, D(0), 975_000m, Options); // also breaches daily

        var result = PortfolioRiskGate.Evaluate(dd, loss, Options);

        Assert.Equal(2, result.BlockingReasons.Count);
        Assert.True(result.BlockingReasons.Contains("TradingShutdown"));
        Assert.True(result.BlockingReasons.Contains("DailyLossLimitBreached"));
    }
}
