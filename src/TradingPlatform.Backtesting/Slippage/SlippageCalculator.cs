using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Slippage;

/// <summary>ExecutionPrice already has slippage baked in (ReferencePrice adjusted against the trade's own side); SlippageAmount/SlippageBps are retained separately for reporting/audit.</summary>
public sealed record SlippageResult(decimal ExecutionPrice, decimal SlippageAmount, decimal SlippageBps);

/// <summary>
/// Implements spec §5.4's three slippage models. Every model produces a
/// bps figure applied AGAINST the trade — added to price for a Buy,
/// subtracted for a Sell — so slippage always costs the trade, never
/// improves it (a backtest that lets slippage randomly help is exactly
/// the kind of optimistic bias spec §5.7 warns against).
///
/// See BacktestOptions.PercentageSlippageOfRangePercent's XML doc for why
/// "Percentage" is defined as a volatility-scaled (day-range-based) model
/// rather than a redundant restatement of FixedBps.
/// </summary>
public static class SlippageCalculator
{
    public static SlippageResult Calculate(
        decimal referencePrice,
        OrderSide side,
        decimal quantity,
        HistoricalBar executionBar,
        decimal averageDailyTradedValue,
        MarketCapBucket marketCapBucket,
        BacktestOptions options)
    {
        var bps = options.SlippageModel switch
        {
            SlippageModelType.FixedBps => options.FixedSlippageBps,
            SlippageModelType.Percentage => PercentageOfRangeBps(executionBar, options),
            SlippageModelType.LiquidityBased => LiquidityBasedBps(referencePrice, quantity, averageDailyTradedValue, marketCapBucket, options),
            _ => options.FixedSlippageBps,
        };

        var slippageAmount = referencePrice * bps / 10000m;
        var executionPrice = side == OrderSide.Buy ? referencePrice + slippageAmount : referencePrice - slippageAmount;
        return new SlippageResult(executionPrice, slippageAmount, bps);
    }

    private static decimal PercentageOfRangeBps(HistoricalBar bar, BacktestOptions options)
    {
        if (bar.Close <= 0) return options.FixedSlippageBps; // defensive only; Close is never legitimately <= 0
        var range = bar.High - bar.Low;
        var rangeSlippage = range * options.PercentageSlippageOfRangePercent / 100m;
        return rangeSlippage / bar.Close * 10000m;
    }

    /// <summary>
    /// Spec §5.4: LiquidityBased is the backtest default, and "dynamically
    /// widens for microcaps as liquidity deteriorates." Modeled as a floor
    /// bps plus a participation-ratio-scaled component (how much of the
    /// day's traded value this order itself represents — the closer to
    /// 100%, the more the order itself would have moved the market),
    /// further multiplied for microcaps.
    /// </summary>
    private static decimal LiquidityBasedBps(decimal referencePrice, decimal quantity, decimal averageDailyTradedValue, MarketCapBucket marketCapBucket, BacktestOptions options)
    {
        var orderValue = referencePrice * quantity;
        var participationRatio = averageDailyTradedValue > 0
            ? orderValue / averageDailyTradedValue
            : 1m; // no ADTV data at all -> treat as maximally illiquid, never as frictionless

        var participationBps = Math.Min(participationRatio, 1m) * options.LiquidityBasedMaxParticipationBpsScaling;
        var microcapMultiplier = marketCapBucket == MarketCapBucket.Micro ? options.LiquidityBasedMicrocapMultiplier : 1m;

        return (options.LiquidityBasedMinBps + participationBps) * microcapMultiplier;
    }
}
