using System.Text.Json;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Reporting.Export;
using TradingPlatform.Risk.Analytics;

namespace TradingPlatform.Reporting.Reports;

/// <summary>Directly mirrors spec 8.1's ASCII-art backtest summary field-for-field.</summary>
public sealed record BacktestReport(
    decimal InitialCapital, decimal FinalCapital, decimal NetReturnPercent, decimal? Cagr,
    decimal? MaxDrawdownPercent, decimal? Sharpe, decimal? Sortino,
    decimal? WinRatePercent, decimal? ProfitFactor, decimal? ExpectancyR, int TradeCount,
    decimal? WorstMonthPercent, decimal? BestMonthPercent, decimal? AverageWinnerR, decimal? AverageLoserR,
    decimal? PortfolioHeatAveragePercent, decimal? PortfolioHeatMaxPercent,
    decimal RegimeBullPercent, decimal RegimeNormalPercent, decimal RegimeBearPercent,
    bool SurvivorshipBiased,
    // --- spec §8.2 minimum research report additions (trailing + defaulted so older call sites/serialized shapes keep working) ---
    int? WorstYear = null, decimal? WorstYearPercent = null, bool WorstYearIsPartial = false,
    int? DrawdownPeakToTroughDays = null, int? DrawdownRecoveryDays = null, int LongestUnderwaterDays = 0, bool StillUnderwater = false,
    bool Rejected = false, IReadOnlyList<string>? VerdictNotes = null)
{
    /// <param name="maxAcceptableDrawdownPercent">Spec §5.7: a strategy whose max drawdown exceeds this is REJECTED however high its CAGR (default 40%).</param>
    /// <summary>Below this many trades the headline statistics say little; matches the AntiOverfittingChecker's default floor.</summary>
    public const int MinimumMeaningfulTrades = 30;

    public static BacktestReport Build(BacktestRunResult result, decimal initialCapital, decimal maxAcceptableDrawdownPercent = 40m)
    {
        var run = result.Run;
        var finalCapital = run.FinalCapital ?? initialCapital;
        var netReturnPercent = initialCapital > 0 ? (finalCapital - initialCapital) / initialCapital * 100m : 0m;

        var analytics = TradeAnalyticsCalculator.Calculate(result.Trades);

        var (worstMonth, bestMonth) = ComputeMonthlyReturnExtremes(result.DailySnapshots, initialCapital);

        var timing = EquityCurveStatistics.ComputeDrawdownTiming(result.DailySnapshots);
        var years = EquityCurveStatistics.ComputeYearReturns(result.DailySnapshots, initialCapital);
        var worstYear = years.Count > 0 ? years.OrderBy(y => y.ReturnPercent).First() : null;

        // Spec §8.2 acceptance. One in-sample run can only fail on drawdown; it can never PROVE out-of-sample robustness — say so every time.
        var notes = new List<string>();
        var rejected = run.MaxDrawdownPercent is { } dd && dd > maxAcceptableDrawdownPercent;
        if (rejected) notes.Add($"REJECTED: max drawdown {run.MaxDrawdownPercent:F1}% exceeds the {maxAcceptableDrawdownPercent:F0}% ceiling (spec §5.7) — high CAGR does not redeem this.");
        if (result.EntryExecution is { } ee)
        {
            notes.Add($"Entry execution: {ee.Entries} Stage-1 entries filled on average {ee.MeanFillVsPlannedEntryPercent:+0.00;-0.00}% versus the planned (pivot) entry; " +
                      (ee.GuardEnabled ? $"gap-risk guard scaled {ee.Resized} entr{(ee.Resized == 1 ? "y" : "ies")} down and cancelled {ee.Cancelled} to keep risk at the planned budget."
                                       : "gap-risk guard is OFF (Risk:ResizeEntryForGapRisk=false), so positions carried the risk of their actual fill price."));
        }
        if (result.OrphanedAddOnOrdersCancelled > 0)
            notes.Add($"{result.OrphanedAddOnOrdersCancelled} pyramid add-on order(s) were cancelled because their position had already been exited before the add-on could fill.");
        if (result.Refinement is { } rf)
        {
            var share = rf.Attempted > 0 ? 100m * rf.FallbackToDailyOpen / rf.Attempted : 0m;
            notes.Add($"Entry refinement {rf.Mode}: {rf.Attempted} entries attempted — executed {rf.Executed}" +
                      (rf.MeanExecutionVsDailyOpenPercent is { } m ? $" (mean fill {m:+0.00;-0.00}% vs the daily open)" : "") +
                      $", breakout not confirmed {rf.SkippedNoConfirmation}, confirmed too late {rf.SkippedNoLaterBar}, fell back to the daily open {rf.FallbackToDailyOpen}, skipped for missing intraday data {rf.SkippedNoIntradayData}.");
            if (share > 50m)
                notes.Add($"WARNING: {share:F0}% of entries had no cached intraday bars and used the daily open — this run is mostly NOT refined (Yahoo serves only ~59 days of 15m and ~729 days of 60m).");
        }
        var tradeCount = run.TradeCount ?? result.Trades.Count;
        if (tradeCount < MinimumMeaningfulTrades)
            notes.Add($"WARNING: only {tradeCount} trade(s) — fewer than {MinimumMeaningfulTrades}, so win rate, expectancy, Sharpe and drawdown are statistically weak (same floor as the spec §5.7 check used by walkforward/optimize).");
        notes.Add("Out-of-sample behaviour is NOT assessed by a single backtest: run 'walkforward' (and 'montecarlo') before trusting these numbers.");

        var heatValues = result.DailySnapshots.Select(s => s.PortfolioHeatPercent).ToList();
        decimal? heatAverage = heatValues.Count > 0 ? heatValues.Average() : null;
        decimal? heatMax = heatValues.Count > 0 ? heatValues.Max() : null;

        var regimeDays = result.DailySnapshots.Where(s => s.Regime is not null).ToList();
        var totalRegimeDays = regimeDays.Count;
        decimal BucketPercent(string bucket) => totalRegimeDays > 0
            ? regimeDays.Count(s => RegimeBucketMapper.ToReportBucket(s.Regime!.Value) == bucket) / (decimal)totalRegimeDays * 100m
            : 0m;

        return new BacktestReport(
            InitialCapital: initialCapital,
            FinalCapital: finalCapital,
            NetReturnPercent: netReturnPercent,
            Cagr: run.Cagr,
            MaxDrawdownPercent: run.MaxDrawdownPercent,
            Sharpe: run.Sharpe,
            Sortino: run.Sortino,
            WinRatePercent: run.WinRatePercent,
            ProfitFactor: run.ProfitFactor,
            ExpectancyR: run.ExpectancyR,
            TradeCount: run.TradeCount ?? analytics.TradeCount,
            WorstMonthPercent: worstMonth,
            BestMonthPercent: bestMonth,
            AverageWinnerR: analytics.AverageWinnerR,
            AverageLoserR: analytics.AverageLoserR,
            PortfolioHeatAveragePercent: heatAverage,
            PortfolioHeatMaxPercent: heatMax,
            RegimeBullPercent: BucketPercent("Bull"),
            RegimeNormalPercent: BucketPercent("Normal"),
            RegimeBearPercent: BucketPercent("Bear"),
            SurvivorshipBiased: run.SurvivorshipBiased,
            WorstYear: worstYear?.Year, WorstYearPercent: worstYear?.ReturnPercent, WorstYearIsPartial: worstYear?.Partial ?? false,
            DrawdownPeakToTroughDays: timing.PeakToTroughDays, DrawdownRecoveryDays: timing.TroughToRecoveryDays,
            LongestUnderwaterDays: timing.LongestUnderwaterDays, StillUnderwater: timing.StillUnderwater,
            Rejected: rejected, VerdictNotes: notes);
    }

    /// <summary>Monthly return = this month's last snapshot's NAV vs the PRECEDING month's last snapshot's NAV (or InitialCapital, for the very first month).</summary>
    private static (decimal? Worst, decimal? Best) ComputeMonthlyReturnExtremes(IReadOnlyList<Core.Models.PortfolioSnapshot> snapshots, decimal initialCapital)
    {
        if (snapshots.Count == 0) return (null, null);

        var byMonth = snapshots.GroupBy(s => (s.AsOfDate.Year, s.AsOfDate.Month)).OrderBy(g => g.Key).ToList();
        var monthlyReturns = new List<decimal>();
        var priorMonthEndNav = initialCapital;

        foreach (var month in byMonth)
        {
            var monthEndNav = month.OrderBy(s => s.AsOfDate).Last().Nav;
            if (priorMonthEndNav > 0) monthlyReturns.Add((monthEndNav - priorMonthEndNav) / priorMonthEndNav * 100m);
            priorMonthEndNav = monthEndNav;
        }

        return monthlyReturns.Count == 0 ? (null, null) : (monthlyReturns.Min(), monthlyReturns.Max());
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Directly mirrors spec §8.1's ASCII-art layout, including Indian lakh/crore digit grouping for rupee amounts (₹10,00,000, not ₹1,000,000) — the format spec itself uses.</summary>
    public string ToConsoleText()
    {
        var sb = new System.Text.StringBuilder();
        const string rule = "========================================================";

        sb.AppendLine(rule);
        sb.AppendLine(" AGGRESSIVE NSE MOMENTUM FUND");
        sb.AppendLine(rule);
        sb.AppendLine($"Initial Capital : {Rupees(InitialCapital),-18} Final Capital   : {Rupees(FinalCapital)}");
        sb.AppendLine($"Net Return      : {Pct(NetReturnPercent),-18} CAGR            : {Pct(Cagr)}");
        sb.AppendLine($"Max Drawdown    : {Pct(MaxDrawdownPercent),-18} Sharpe          : {Num(Sharpe),-9} Sortino  : {Num(Sortino)}");
        sb.AppendLine($"Win Rate        : {Pct(WinRatePercent),-18} Profit Factor   : {Num(ProfitFactor),-9} Expectancy: {Num(ExpectancyR)} R");
        sb.AppendLine($"Trades          : {TradeCount}");
        sb.AppendLine();
        sb.AppendLine($"Worst Month : {Pct(WorstMonthPercent)}   Best Month : {Pct(BestMonthPercent)}   Avg Winner : {Num(AverageWinnerR)}R   Avg Loser : {Num(AverageLoserR)}R");
        sb.AppendLine($"Worst Year  : {(WorstYearPercent is null ? "n/a" : $"{Pct(WorstYearPercent)} ({WorstYear}{(WorstYearIsPartial ? ", partial year" : "")})")}");
        sb.AppendLine($"Drawdown timing — peak→trough: {(DrawdownPeakToTroughDays is null ? "n/a" : DrawdownPeakToTroughDays + " days")}   recovery: {(DrawdownRecoveryDays is { } rd ? rd + " days" : (DrawdownPeakToTroughDays is null ? "n/a" : "NOT RECOVERED"))}   longest underwater: {LongestUnderwaterDays} days{(StillUnderwater ? " (still underwater at end)" : "")}");
        sb.AppendLine($"Portfolio Heat — Average: {Pct(PortfolioHeatAveragePercent)}  Maximum: {Pct(PortfolioHeatMaxPercent)}");
        sb.AppendLine($"Regime — Bull: {Pct(RegimeBullPercent)}  Normal: {Pct(RegimeNormalPercent)}  Bear: {Pct(RegimeBearPercent)}");
        if (SurvivorshipBiased)
            sb.AppendLine("[SURVIVORSHIP-BIASED — see §5.6: today's active universe was applied retroactively, not the genuine point-in-time constituent list]");
        foreach (var note in VerdictNotes ?? Array.Empty<string>()) sb.AppendLine($"[{(Rejected && note.StartsWith("REJECTED") ? "VERDICT" : "NOTE")}] {note}");
        sb.AppendLine(rule);

        return sb.ToString();
    }

    private static string Pct(decimal? v) => v is null ? "n/a" : $"{v.Value:F2}%";
    private static string Num(decimal? v) => v is null ? "n/a" : $"{v.Value:F2}";

    /// <summary>Indian digit grouping (lakh/crore: last 3 digits, then groups of 2) — .NET's built-in "N0"/"C0" formatters use Western 3-digit grouping even under an en-IN culture in some runtimes, so this is done by hand for reliability.</summary>
    private static string Rupees(decimal amount)
    {
        var negative = amount < 0;
        var whole = (long)Math.Round(Math.Abs(amount), 0, MidpointRounding.AwayFromZero);
        var digits = whole.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string grouped;
        if (digits.Length <= 3)
        {
            grouped = digits;
        }
        else
        {
            var lastThree = digits[^3..];
            var remainder = digits[..^3];
            var groups = new List<string>();
            for (var i = remainder.Length; i > 0; i -= 2)
                groups.Insert(0, remainder[Math.Max(0, i - 2)..i]);
            grouped = string.Join(",", groups) + "," + lastThree;
        }

        return (negative ? "-₹" : "₹") + grouped;
    }

    public string ToCsv()
    {
        var headers = new[]
        {
            "InitialCapital", "FinalCapital", "NetReturnPercent", "Cagr", "MaxDrawdownPercent", "Sharpe", "Sortino",
            "WinRatePercent", "ProfitFactor", "ExpectancyR", "TradeCount", "WorstMonthPercent", "BestMonthPercent",
            "AverageWinnerR", "AverageLoserR", "PortfolioHeatAveragePercent", "PortfolioHeatMaxPercent",
            "RegimeBullPercent", "RegimeNormalPercent", "RegimeBearPercent", "SurvivorshipBiased",
            "WorstYear", "WorstYearPercent", "WorstYearIsPartial", "DrawdownPeakToTroughDays", "DrawdownRecoveryDays", "LongestUnderwaterDays", "StillUnderwater", "Rejected",
        };
        var row = new[]
        {
            F(InitialCapital), F(FinalCapital), F(NetReturnPercent), F(Cagr), F(MaxDrawdownPercent), F(Sharpe), F(Sortino),
            F(WinRatePercent), F(ProfitFactor), F(ExpectancyR), TradeCount.ToString(), F(WorstMonthPercent), F(BestMonthPercent),
            F(AverageWinnerR), F(AverageLoserR), F(PortfolioHeatAveragePercent), F(PortfolioHeatMaxPercent),
            F(RegimeBullPercent), F(RegimeNormalPercent), F(RegimeBearPercent), SurvivorshipBiased.ToString(),
            WorstYear?.ToString() ?? "n/a", F(WorstYearPercent), WorstYearIsPartial.ToString(), DrawdownPeakToTroughDays?.ToString() ?? "n/a", DrawdownRecoveryDays?.ToString() ?? "n/a",
            LongestUnderwaterDays.ToString(), StillUnderwater.ToString(), Rejected.ToString(),
        };
        return CsvWriter.Write(headers, new[] { row });
    }

    public string ToHtml()
    {
        var summary = new List<(string, string)>
        {
            ("Initial Capital", "Rs " + InitialCapital.ToString("N0")), ("Final Capital", "Rs " + FinalCapital.ToString("N0")),
            ("Net Return", $"{NetReturnPercent:F1}%"), ("CAGR", F(Cagr) + "%"),
            ("Max Drawdown", F(MaxDrawdownPercent) + "%"), ("Sharpe", F(Sharpe)), ("Sortino", F(Sortino)),
            ("Win Rate", F(WinRatePercent) + "%"), ("Profit Factor", F(ProfitFactor)), ("Expectancy", F(ExpectancyR) + " R"),
            ("Trades", TradeCount.ToString()),
            ("Worst Month", F(WorstMonthPercent) + "%"), ("Best Month", F(BestMonthPercent) + "%"),
            ("Avg Winner", F(AverageWinnerR) + " R"), ("Avg Loser", F(AverageLoserR) + " R"),
            ("Worst Year", WorstYearPercent is null ? "n/a" : $"{F(WorstYearPercent)}% ({WorstYear}{(WorstYearIsPartial ? ", partial" : "")})"),
            ("Drawdown timing", $"peak-to-trough {DrawdownPeakToTroughDays?.ToString() ?? "n/a"} d, recovery {(DrawdownRecoveryDays is { } rd ? rd + " d" : (DrawdownPeakToTroughDays is null ? "n/a" : "NOT RECOVERED"))}, longest underwater {LongestUnderwaterDays} d"),
            ("Portfolio Heat (avg / max)", $"{F(PortfolioHeatAveragePercent)}% / {F(PortfolioHeatMaxPercent)}%"),
            ("Regime (Bull / Normal / Bear)", $"{RegimeBullPercent:F0}% / {RegimeNormalPercent:F0}% / {RegimeBearPercent:F0}%"),
        };
        foreach (var note in VerdictNotes ?? Array.Empty<string>()) summary.Add((Rejected && note.StartsWith("REJECTED") ? "VERDICT" : "Note", note));
        if (SurvivorshipBiased) summary.Add(("SURVIVORSHIP-BIASED", "see spec section 5.6 - universe was not confirmed point-in-time"));

        return HtmlTableRenderer.Render("Aggressive NSE Momentum Fund - Backtest Report", summary, null, null);
    }

    private static string F(decimal? value) => value?.ToString("F2") ?? "n/a";
}
