using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy;
using TradingPlatform.Strategy.Entry;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

/// <summary>
/// Spec §7.4 parity: the shared decision logic must give identical decisions given identical inputs, whichever
/// execution context calls it. Backtest uses StrategyStepEngine directly; paper/live use StagedAddPlanner (which wraps the
/// SAME Stage2/Stage3 evaluators and the SAME StagedEntryCalculator split). These tests pin that the two paths cannot
/// drift: same trigger decision, same stage, same quantity, for the same bars/indicators/position state.
/// </summary>
public class ParityTests
{
    private static readonly StrategyOptions Strategy = new();
    private static readonly RsOptions Rs = new();
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static HistoricalBar Bar(int day, decimal o, decimal h, decimal l, decimal c) =>
        new() { Symbol = "ABC", Timestamp = T0.AddDays(day), Open = o, High = h, Low = l, Close = c, Volume = 100_000, Source = "test" };

    private static IndicatorSnapshot Ind(decimal? fast, decimal? slow, decimal? adx = null) =>
        new() { Symbol = "ABC", HmaFast = fast, HmaSlow = slow, Adx = adx };

    private sealed record Scenario(string Name, PyramidStage Stage, decimal CurrentStop, decimal Stage1Price, decimal? Stage2Price, DateTimeOffset? Stage2At,
        IReadOnlyList<HistoricalBar> Bars, IndicatorSnapshot Indicators, bool ExpectAdd);

    private static IEnumerable<Scenario> Scenarios()
    {
        var s1 = new[] { Bar(0, 100, 101, 99, 100), Bar(1, 104, 108, 103, 106) };
        yield return new("Stage2 all conditions pass", PyramidStage.Stage1Initial, 95m, 100m, null, null, s1, Ind(105m, 100m), true);
        yield return new("Stage2 price below stage1 entry (never add to a loser)", PyramidStage.Stage1Initial, 90m, 100m, null, null,
            new[] { Bar(0, 100, 101, 99, 100), Bar(1, 98, 99, 96, 97) }, Ind(105m, 100m), false);
        yield return new("Stage2 trend indicators missing (fails closed)", PyramidStage.Stage1Initial, 95m, 100m, null, null, s1, Ind(null, null), false);
        yield return new("Stage2 stop violated by bar low", PyramidStage.Stage1Initial, 104m, 100m, null, null, s1, Ind(105m, 100m), false);
        yield return new("Stage2 move too small", PyramidStage.Stage1Initial, 95m, 100m, null, null,
            new[] { Bar(0, 100, 101, 99, 100), Bar(1, 100, 101.5m, 100, 101) }, Ind(105m, 100m), false);

        var t2 = T0.AddDays(10);
        var s3 = new[] { Bar(10, 118, 120, 117, 119), Bar(11, 123, 125, 122, 124), Bar(12, 124, 124, 119, 123), Bar(13, 122, 126, 121, 125) };
        yield return new("Stage3 retest + reclaim + momentum", PyramidStage.Stage2Add, 100m, 100m, 119m, t2, s3, Ind(122m, 115m, 30m), true);
        yield return new("Stage3 no pullback yet", PyramidStage.Stage2Add, 100m, 100m, 119m, t2,
            new[] { Bar(10, 118, 120, 117, 119), Bar(11, 123, 125, 124, 124.5m), Bar(12, 125, 127, 126, 126.5m) }, Ind(122m, 115m, 30m), false);
        yield return new("Stage3 momentum indicators missing (fails closed)", PyramidStage.Stage2Add, 100m, 100m, 119m, t2, s3, Ind(null, null, null), false);
        yield return new("Stage3 support broken", PyramidStage.Stage2Add, 122m, 100m, 119m, t2, s3, Ind(122m, 115m, 30m), false);
    }

    private static (StrategyStepResult Engine, AddPlan? Planner) Run(Scenario s, decimal total)
    {
        var position = new Position { Symbol = "ABC", Quantity = 40, AverageEntryPrice = s.Stage1Price, CurrentStop = s.CurrentStop, HighestStageFilled = s.Stage, IsOpen = true };
        var staging = new PositionStaging(total, s.Stage1Price, T0.AddDays(-5), s.Stage2Price, s.Stage2At);

        // Backtest path: the shared StrategyStepEngine.
        var engine = StrategyStepEngine.Evaluate(new StrategyStepInputs
        {
            Symbol = "ABC", Bars = s.Bars, IndicatorSnapshots = new[] { s.Indicators }, RelativeStrengthScore = 80m, Regime = RegimeClassification.Aggressive,
            TotalTargetQuantity = total,
            ExistingPosition = new StagedPositionState
            {
                Symbol = "ABC", HighestStageFilled = s.Stage, CurrentStop = s.CurrentStop, Stage1EntryPrice = s.Stage1Price, Stage1EntryTimestamp = T0.AddDays(-5),
                Stage2EntryPrice = s.Stage2Price, Stage2EntryTimestamp = s.Stage2At,
            },
        }, Strategy, Rs);

        // Paper/live path.
        var planner = StagedAddPlanner.Plan(position, staging, s.Bars, s.Indicators, Strategy);
        return (engine, planner);
    }

    [Fact]
    public void PaperLivePlanner_AndBacktestEngine_AgreeOnEveryScenario()
    {
        foreach (var s in Scenarios())
        {
            var (engine, planner) = Run(s, 100m);
            var engineAdds = engine.Decision is StrategyStepDecision.AddStage2 or StrategyStepDecision.AddStage3;

            Assert.True(engineAdds == s.ExpectAdd, $"[{s.Name}] engine decision {engine.Decision}, expected add={s.ExpectAdd}.");
            Assert.True((planner is not null) == engineAdds, $"[{s.Name}] planner/engine disagree: engine={engine.Decision}, planner={(planner is null ? "none" : planner.Stage.ToString())}.");
            if (planner is not null)
            {
                Assert.Equal(engine.Stage, planner.Stage);
                Assert.Equal(engine.SuggestedQuantity, planner.Quantity);
            }
        }
    }

    [Fact]
    public void Decision_IsDeterministic_AcrossRepeatedCalls_NoHiddenClockOrState()
    {
        foreach (var s in Scenarios())
        {
            var first = Run(s, 100m);
            for (var i = 0; i < 3; i++)
            {
                var again = Run(s, 100m);
                Assert.Equal(first.Engine.Decision, again.Engine.Decision);
                Assert.Equal(first.Engine.SuggestedQuantity, again.Engine.SuggestedQuantity);
                Assert.Equal(first.Planner?.Quantity, again.Planner?.Quantity);
            }
        }
    }

    [Fact]
    public void StageQuantities_SumExactlyToTheTarget_ForAnyTotal()
    {
        foreach (var total in new[] { 10, 37, 100, 2500 })
        {
            var (s1, s2, s3) = StagedEntryCalculator.Split(total, Strategy);
            Assert.Equal(total, s1 + s2 + s3);
            Assert.True(s1 >= 0 && s2 >= 0 && s3 >= 0);
        }
    }
}
