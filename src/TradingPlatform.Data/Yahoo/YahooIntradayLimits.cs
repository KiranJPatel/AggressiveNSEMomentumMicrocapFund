using TradingPlatform.Core.Enums;

namespace TradingPlatform.Data.Yahoo;

/// <summary>How far back Yahoo serves each intraday interval, and the interval token it expects (spec §7.3). A margin below Yahoo's stated limit (60 / 730 days) is kept so a request never lands exactly on the boundary.</summary>
public static class YahooIntradayLimits
{
    public static int MaxHistoryDays(Timeframe tf) => tf switch
    {
        Timeframe.M5 or Timeframe.M15 => 59,
        Timeframe.H1 => 729,
        _ => throw new ArgumentException($"{tf} is not an intraday timeframe."),
    };

    public static string IntervalToken(Timeframe tf) => tf switch
    {
        Timeframe.M5 => "5m",
        Timeframe.M15 => "15m",
        Timeframe.H1 => "60m",
        _ => throw new ArgumentException($"{tf} is not an intraday timeframe."),
    };

    public static bool TryParse(string? text, out Timeframe tf)
    {
        tf = default;
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "60m" or "1h" or "h1": tf = Timeframe.H1; return true;
            case "15m" or "m15": tf = Timeframe.M15; return true;
            case "5m" or "m5": tf = Timeframe.M5; return true;
            default: return false;
        }
    }
}
