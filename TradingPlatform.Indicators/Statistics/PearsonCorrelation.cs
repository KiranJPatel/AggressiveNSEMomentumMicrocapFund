namespace TradingPlatform.Indicators.Statistics;

/// <summary>
/// Standard Pearson product-moment correlation coefficient:
///   r = Σ((x-x̄)(y-ȳ)) / sqrt(Σ(x-x̄)² × Σ(y-ȳ)²)
/// Used for the 60-day rolling correlation between position return series
/// (spec §4.2). A well-established, textbook statistical formula — no
/// resolved ambiguity here, unlike the indicator formulas in Tranche 3
/// that had rounding-convention choices to make.
/// </summary>
public static class PearsonCorrelation
{
    /// <summary>Both series must be the same length and already aligned by date. Returns null if fewer than 2 points or either series has zero variance (a correlation is undefined against a constant series).</summary>
    public static decimal? Calculate(IReadOnlyList<decimal> seriesX, IReadOnlyList<decimal> seriesY)
    {
        if (seriesX.Count != seriesY.Count) throw new ArgumentException("Series must be the same (already-aligned) length.");
        if (seriesX.Count < 2) return null;

        var meanX = seriesX.Average();
        var meanY = seriesY.Average();

        decimal covariance = 0, varianceX = 0, varianceY = 0;
        for (var i = 0; i < seriesX.Count; i++)
        {
            var dx = seriesX[i] - meanX;
            var dy = seriesY[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        if (varianceX == 0 || varianceY == 0) return null;

        var denominator = (double)varianceX * (double)varianceY;
        return covariance / (decimal)Math.Sqrt(denominator);
    }

    /// <summary>Converts a close-price series into simple period-over-period returns (length = prices.Count - 1).</summary>
    public static IReadOnlyList<decimal> ToReturns(IReadOnlyList<decimal> closes)
    {
        var returns = new List<decimal>(Math.Max(0, closes.Count - 1));
        for (var i = 1; i < closes.Count; i++)
        {
            if (closes[i - 1] != 0) returns.Add(closes[i] / closes[i - 1] - 1m);
        }
        return returns;
    }
}
