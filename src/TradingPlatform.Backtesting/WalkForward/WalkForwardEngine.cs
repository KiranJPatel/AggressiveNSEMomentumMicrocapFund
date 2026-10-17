using TradingPlatform.Backtesting.Engine;

namespace TradingPlatform.Backtesting.WalkForward;

/// <summary>One window's three sub-period results, all run with the SAME parameter set (the caller's runBacktest delegate closes over whichever parameters are being evaluated).</summary>
public sealed record WalkForwardWindowResult(WalkForwardWindow Window, BacktestRunResult TrainResult, BacktestRunResult ValidateResult, BacktestRunResult TestResult);

/// <summary>
/// Runs a caller-supplied backtest function across every window's Train,
/// Validate, and Test sub-periods (spec §5.5). Deliberately decoupled
/// from BacktestEngine/BacktestEngineOptions directly — takes a
/// Func&lt;DateOnly,DateOnly,BacktestRunResult&gt; delegate instead — so this
/// engine has zero knowledge of what's actually being parameterized or
/// how; ParameterGridSearchEngine supplies a different delegate per
/// candidate parameter set, closing over the specific
/// BacktestEngineOptions/BacktestRunRequest construction that's its own
/// concern, not this one's.
/// </summary>
public static class WalkForwardEngine
{
    public static IReadOnlyList<WalkForwardWindowResult> Run(IReadOnlyList<WalkForwardWindow> windows, Func<DateOnly, DateOnly, BacktestRunResult> runBacktest)
    {
        var results = new List<WalkForwardWindowResult>();
        foreach (var window in windows)
        {
            var train = runBacktest(window.TrainStart, window.TrainEnd);
            var validate = runBacktest(window.ValidateStart, window.ValidateEnd);
            var test = runBacktest(window.TestStart, window.TestEnd);
            results.Add(new WalkForwardWindowResult(window, train, validate, test));
        }
        return results;
    }
}
