namespace TradingPlatform.Strategy;

/// <summary>
/// Shared tanh-based squashing from an unbounded signal to a 0-100 score,
/// 50 = neutral (spec §0.4: every scale/threshold here is a documented,
/// configurable resolved ambiguity, not a hardcoded magic number — the
/// SCALE parameter is what's configurable per call site; the squashing
/// shape itself is deliberately the same symmetric tanh curve used
/// throughout Strategy (RelativeStrengthCalculator, MarketRegimeEngine,
/// and the scoring calculators below) so a "score of 50" always means the
/// same thing everywhere in this codebase: exactly neutral.
/// </summary>
internal static class ScoreNormalization
{
    public static decimal SquashToScore(decimal rawSignal, decimal scale)
    {
        if (scale <= 0) return 50m;
        var tanh = Math.Tanh((double)(rawSignal / scale));
        return 50m + 50m * (decimal)tanh;
    }
}
