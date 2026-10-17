using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Breakout;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class BreakoutEngineTests
{
    private static readonly StrategyOptions StrategyOptions = new() { BreakoutVolumeMultiplier = 1.5m, BreakoutMinAdx = 20m };
    private static readonly RsOptions RsOptions = new() { SubstantialOutperformanceScoreThreshold = 70m };

    private static IndicatorSnapshot GoodSnapshot(decimal? relativeVolume = 2.0m, decimal? adx = 25m, decimal? hmaFast = 105m, decimal? hmaSlow = 100m) => new()
    {
        Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily,
        RelativeVolume = relativeVolume, Adx = adx, HmaFast = hmaFast, HmaSlow = hmaSlow
    };

    private static BreakoutEvaluation EvaluateGoodCase(
        decimal close = 110m, decimal pivot = 100m, IndicatorSnapshot? indicators = null,
        decimal rsScore = 80m, RegimeClassification regime = RegimeClassification.Normal) =>
        BreakoutEngine.Evaluate(close, pivot, indicators ?? GoodSnapshot(), rsScore, regime, StrategyOptions, RsOptions);

    [Fact]
    public void AllConditionsPass_SignalFires()
    {
        var result = EvaluateGoodCase();
        Assert.True(result.IsBreakout);
        Assert.True(result.Conditions.All(c => c.Passed));
    }

    [Fact]
    public void ClosePastPivot_Fails_WhenCloseBelowPivot()
    {
        var result = EvaluateGoodCase(close: 95m, pivot: 100m);
        Assert.False(result.IsBreakout);
        Assert.False(result.Conditions.Single(c => c.Name == "ClosePastPivot").Passed);
    }

    [Fact]
    public void ClosePastPivot_Fails_WhenCloseExactlyAtPivot()
    {
        // Strictly greater-than, per spec's literal "Close > Pivot" — exactly at pivot doesn't count.
        var result = EvaluateGoodCase(close: 100m, pivot: 100m);
        Assert.False(result.Conditions.Single(c => c.Name == "ClosePastPivot").Passed);
    }

    [Fact]
    public void Volume_Fails_WhenBelowRequiredMultiplier()
    {
        var indicators = GoodSnapshot(relativeVolume: 1.2m); // below 1.5x
        var result = EvaluateGoodCase(indicators: indicators);
        Assert.False(result.IsBreakout);
        Assert.False(result.Conditions.Single(c => c.Name == "Volume").Passed);
    }

    [Fact]
    public void Volume_Passes_WhenExactlyAtRequiredMultiplier()
    {
        var indicators = GoodSnapshot(relativeVolume: 1.5m);
        var result = EvaluateGoodCase(indicators: indicators);
        Assert.True(result.Conditions.Single(c => c.Name == "Volume").Passed);
    }

    [Fact]
    public void Volume_FailsClosed_WhenUnavailable()
    {
        var indicators = GoodSnapshot(relativeVolume: null);
        var result = EvaluateGoodCase(indicators: indicators);
        Assert.False(result.IsBreakout);
        Assert.False(result.Conditions.Single(c => c.Name == "Volume").Passed);
    }

    [Fact]
    public void RelativeStrength_Fails_WhenBelowThreshold()
    {
        var result = EvaluateGoodCase(rsScore: 65m);
        Assert.False(result.IsBreakout);
        Assert.False(result.Conditions.Single(c => c.Name == "RelativeStrength").Passed);
    }

    [Fact]
    public void RelativeStrength_Passes_WhenExactlyAtThreshold()
    {
        var result = EvaluateGoodCase(rsScore: 70m);
        Assert.True(result.Conditions.Single(c => c.Name == "RelativeStrength").Passed);
    }

    [Fact]
    public void Adx_Fails_WhenBelowMinimum()
    {
        var indicators = GoodSnapshot(adx: 15m);
        var result = EvaluateGoodCase(indicators: indicators);
        Assert.False(result.IsBreakout);
        Assert.False(result.Conditions.Single(c => c.Name == "Adx").Passed);
    }

    [Fact]
    public void Adx_FailsClosed_WhenUnavailable()
    {
        var indicators = GoodSnapshot(adx: null);
        var result = EvaluateGoodCase(indicators: indicators);
        Assert.False(result.Conditions.Single(c => c.Name == "Adx").Passed);
    }

    [Fact]
    public void PriceAboveHmaFast_Fails_WhenCloseBelowHmaFast()
    {
        var indicators = GoodSnapshot(hmaFast: 115m); // close=110 < 115
        var result = EvaluateGoodCase(indicators: indicators);
        Assert.False(result.IsBreakout);
        Assert.False(result.Conditions.Single(c => c.Name == "PriceAboveHmaFast").Passed);
    }

    [Fact]
    public void HmaFastAboveSlow_Fails_WhenFastBelowSlow()
    {
        var indicators = GoodSnapshot(hmaFast: 95m, hmaSlow: 100m);
        var result = EvaluateGoodCase(indicators: indicators);
        Assert.False(result.IsBreakout);
        Assert.False(result.Conditions.Single(c => c.Name == "HmaFastAboveSlow").Passed);
    }

    [Fact]
    public void HmaFastAboveSlow_FailsClosed_WhenEitherUnavailable()
    {
        var indicators = GoodSnapshot(hmaSlow: null);
        var result = EvaluateGoodCase(indicators: indicators);
        Assert.False(result.Conditions.Single(c => c.Name == "HmaFastAboveSlow").Passed);
    }

    [Fact]
    public void Regime_Fails_WhenEmergency()
    {
        var result = EvaluateGoodCase(regime: RegimeClassification.Emergency);
        Assert.False(result.IsBreakout);
        Assert.False(result.Conditions.Single(c => c.Name == "RegimeAllowsNewPositions").Passed);
    }

    [Fact]
    public void Regime_Passes_ForEveryNonEmergencyClassification()
    {
        foreach (var regime in new[] { RegimeClassification.Aggressive, RegimeClassification.Normal, RegimeClassification.Cautious, RegimeClassification.Defensive })
        {
            var result = EvaluateGoodCase(regime: regime);
            Assert.True(result.Conditions.Single(c => c.Name == "RegimeAllowsNewPositions").Passed, $"Regime {regime} should permit new positions");
        }
    }

    [Fact]
    public void SingleFailingCondition_FailsTheWholeSignal_ANDGate()
    {
        // Everything good except ADX slightly low — the whole thing must fail, not "mostly pass".
        var indicators = GoodSnapshot(adx: 19.9m);
        var result = EvaluateGoodCase(indicators: indicators);

        Assert.False(result.IsBreakout);
        Assert.Equal(6, result.Conditions.Count(c => c.Passed)); // 6 of 7 passed, but AND-gate still fails overall
    }

    [Fact]
    public void EvaluationReturnsAllSevenNamedConditions()
    {
        var result = EvaluateGoodCase();
        var expectedNames = new[] { "ClosePastPivot", "Volume", "RelativeStrength", "Adx", "PriceAboveHmaFast", "HmaFastAboveSlow", "RegimeAllowsNewPositions" };
        foreach (var name in expectedNames)
            Assert.True(result.Conditions.Any(c => c.Name == name), $"Missing condition: {name}");
        Assert.Equal(7, result.Conditions.Count);
    }
}
