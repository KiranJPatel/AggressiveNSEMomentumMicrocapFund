namespace TradingPlatform.Backtesting.WalkForward;

/// <summary>One rolling Train/Validate/Test window (spec §5.5, e.g. "3y/6m/6m"). The three sub-periods are contiguous and non-overlapping WITHIN one window; consecutive windows step forward by StepMonths, which may make windows overlap each other (a rolling walk-forward, not a strictly partitioned one) — that's the whole point of "rolling," and is what lets a single multi-year dataset yield several independent out-of-sample Test periods instead of just one.</summary>
public sealed record WalkForwardWindow(
    DateOnly TrainStart, DateOnly TrainEnd,
    DateOnly ValidateStart, DateOnly ValidateEnd,
    DateOnly TestStart, DateOnly TestEnd);

/// <summary>
/// Generates spec §5.5's rolling walk-forward windows. "Never optimize on
/// the full dataset and report those results as out-of-sample" (spec
/// §5.5) is enforced structurally by this shape: Test is always the LAST
/// sub-period of a window that a parameter search never gets to see while
/// choosing parameters from Train/Validate — see ParameterGridSearchEngine,
/// which is built to only ever be handed Train+Validate data at
/// selection time.
/// </summary>
public static class WalkForwardWindowGenerator
{
    /// <summary>
    /// stepMonths defaults to testMonths — i.e. consecutive windows'
    /// Test periods are adjacent and non-overlapping, covering the full
    /// dataset exactly once between them (the most common, simplest
    /// walk-forward cadence). A caller wanting overlapping/more-frequent
    /// re-optimization can pass a smaller stepMonths explicitly.
    /// </summary>
    public static IReadOnlyList<WalkForwardWindow> GenerateWindows(
        DateOnly overallStart, DateOnly overallEnd,
        int trainYears, int validateMonths, int testMonths, int? stepMonths = null)
    {
        if (overallStart >= overallEnd) throw new ArgumentException("overallStart must be before overallEnd.");
        if (trainYears <= 0 || validateMonths <= 0 || testMonths <= 0) throw new ArgumentException("trainYears/validateMonths/testMonths must all be positive.");

        var step = stepMonths ?? testMonths;
        if (step <= 0) throw new ArgumentException("stepMonths must be positive.");

        var windows = new List<WalkForwardWindow>();
        var trainStart = overallStart;

        while (true)
        {
            var trainEnd = trainStart.AddYears(trainYears);
            var validateStart = trainEnd.AddDays(1);
            var validateEnd = validateStart.AddMonths(validateMonths).AddDays(-1);
            var testStart = validateEnd.AddDays(1);
            var testEnd = testStart.AddMonths(testMonths).AddDays(-1);

            if (testEnd > overallEnd) break;

            windows.Add(new WalkForwardWindow(trainStart, trainEnd, validateStart, validateEnd, testStart, testEnd));
            trainStart = trainStart.AddMonths(step);
        }

        return windows;
    }
}
