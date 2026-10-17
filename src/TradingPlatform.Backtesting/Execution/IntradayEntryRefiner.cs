using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Execution;

public enum RefinementOutcome
{
    /// <summary>An hourly bar confirmed the breakout and a later bar provides the execution price.</summary>
    Executed,
    /// <summary>No hourly bar that session closed above the level (with the required volume): the breakout did not hold — the entry is cancelled.</summary>
    NoConfirmation,
    /// <summary>Confirmed, but on the last bar of the session — nothing later to execute on today.</summary>
    NoLaterBarToExecute,
    /// <summary>No intraday bars cached for the session: refinement could not be evaluated at all.</summary>
    NoIntradayData,
}

public sealed record RefinementResult(RefinementOutcome Outcome, decimal? ExecutionPrice, DateTimeOffset? TriggerBarStart, DateTimeOffset? ExecutionTime, bool UsedQuarterHour, string Reason);

public sealed record RefinementStats(
    SignalToOrderLag Mode, int Attempted, int Executed, int SkippedNoConfirmation, int SkippedNoLaterBar, int FallbackToDailyOpen, int SkippedNoIntradayData,
    decimal? MeanExecutionVsDailyOpenPercent);

/// <summary>Mutable counter the engine fills while a run executes.</summary>
public sealed class RefinementTally
{
    public int Attempted, Executed, NoConfirmation, NoLaterBar, Fallback, SkippedNoData;
    private decimal _vsOpenSum; private int _vsOpenCount;

    public void RecordExecution(decimal executionPrice, decimal dailyOpen)
    {
        Executed++;
        if (dailyOpen > 0m) { _vsOpenSum += (executionPrice / dailyOpen - 1m) * 100m; _vsOpenCount++; }
    }

    public RefinementStats ToStats(SignalToOrderLag mode) =>
        new(mode, Attempted, Executed, NoConfirmation, NoLaterBar, Fallback, SkippedNoData, _vsOpenCount > 0 ? _vsOpenSum / _vsOpenCount : null);
}

/// <summary>
/// Spec §3.8 / §7.3: the daily signal stays the decision; intraday bars only refine WHEN the order executes on the next session.
///   60m  = entry refinement: the entry is confirmed by the first hourly bar that CLOSES above the breakout level (by the configured buffer)
///          on at least the configured multiple of the average hourly volume; if no hourly bar does, the breakout failed to hold and no order is placed.
///   15m  = execution: after the confirming hourly bar completes, the order fills at the open of the first 15m bar that starts at/after its end.
/// With Refinement60m the order fills at the open of the hourly bar after the confirming one. Pure, deterministic, and it only ever uses bars
/// of the session being filled plus a volume baseline the caller computed from EARLIER sessions — no look-ahead.
/// </summary>
public static class IntradayEntryRefiner
{
    private static readonly TimeSpan SessionClose = new(15, 30, 0);

    public static RefinementResult Refine(
        SignalToOrderLag mode, decimal breakoutLevel,
        IReadOnlyList<HistoricalBar>? hourlySession, IReadOnlyList<HistoricalBar>? quarterHourSession,
        decimal? baselineHourlyVolume, decimal confirmBufferPercent, decimal minTriggerRelativeVolume)
    {
        if (mode == SignalToOrderLag.NextOpen)
            throw new ArgumentException("NextOpen needs no intraday refinement.", nameof(mode));
        if (hourlySession is null || hourlySession.Count == 0)
            return new RefinementResult(RefinementOutcome.NoIntradayData, null, null, null, false, "no hourly bars cached for the session");

        var hours = hourlySession.OrderBy(b => b.Timestamp).ToList();
        var threshold = breakoutLevel * (1m + confirmBufferPercent / 100m);

        var triggerIndex = -1;
        for (var i = 0; i < hours.Count; i++)
        {
            if (hours[i].Close <= threshold) continue;
            // Volume condition only when a baseline exists: with no earlier sessions to compare against it cannot be evaluated, and refusing
            // every entry for lack of history would make the refinement unusable on a fresh cache.
            if (baselineHourlyVolume is { } baseline && baseline > 0m && hours[i].Volume < baseline * minTriggerRelativeVolume) continue;
            triggerIndex = i;
            break;
        }
        if (triggerIndex < 0)
            return new RefinementResult(RefinementOutcome.NoConfirmation, null, null, null, false,
                $"no hourly close above {threshold:F2}{(baselineHourlyVolume is null ? "" : $" on >= {minTriggerRelativeVolume:0.##}x average hourly volume")}");

        var trigger = hours[triggerIndex];
        var triggerEnd = EndOf(trigger);

        if (mode == SignalToOrderLag.Refinement15m && quarterHourSession is { Count: > 0 })
        {
            var next = quarterHourSession.OrderBy(b => b.Timestamp).FirstOrDefault(b => b.Timestamp >= triggerEnd);
            return next is null
                ? new RefinementResult(RefinementOutcome.NoLaterBarToExecute, null, trigger.Timestamp, null, true, "confirmed on the last hourly bar: no 15m bar left to execute on")
                : new RefinementResult(RefinementOutcome.Executed, next.Open, trigger.Timestamp, next.Timestamp, true, "hourly confirmation, 15m execution");
        }

        // Refinement60m — or Refinement15m without 15m data for the session, which falls back to the next hourly open (reported in Reason).
        if (triggerIndex + 1 >= hours.Count)
            return new RefinementResult(RefinementOutcome.NoLaterBarToExecute, null, trigger.Timestamp, null, false, "confirmed on the last hourly bar: no later bar to execute on");
        var exec = hours[triggerIndex + 1];
        return new RefinementResult(RefinementOutcome.Executed, exec.Open, trigger.Timestamp, exec.Timestamp, false,
            mode == SignalToOrderLag.Refinement15m ? "hourly confirmation; no 15m bars cached, executed at the next hourly open" : "hourly confirmation, next-hour open");
    }

    /// <summary>End of an hourly candle: start + 60 minutes, but never after the 15:30 close (the 15:15 candle only covers 15 minutes).</summary>
    private static DateTimeOffset EndOf(HistoricalBar hourly)
    {
        var plus60 = hourly.Timestamp.AddMinutes(60);
        var close = new DateTimeOffset(hourly.Timestamp.Date + SessionClose, hourly.Timestamp.Offset);
        return plus60 < close ? plus60 : close;
    }
}
