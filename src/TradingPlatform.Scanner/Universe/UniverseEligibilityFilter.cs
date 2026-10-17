using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Scanner.Universe;

public sealed record EligibilityCheck(string Name, bool Passed, string Detail);
public sealed record EligibilityResult(bool IsEligible, IReadOnlyList<EligibilityCheck> Checks);

/// <summary>
/// Implements spec §2.3's universe eligibility filter: "Active,
/// AverageDailyTurnover &gt;= threshold, Price &gt;= minimum, sufficient
/// historical data, not permanently suspended, not invalid Yahoo symbol"
/// plus the surveillance risk-flag check named immediately after it.
///
/// "Not permanently suspended" has no separate field on Instrument beyond
/// Active — resolved as: Active already covers suspension (a suspended
/// instrument is, definitionally, not currently active), so there is no
/// distinct check for it, rather than inventing a field spec doesn't
/// otherwise mention.
///
/// The surveillance check is deliberately strict: BOTH an explicit risk
/// flag AND an Unknown availability status disqualify. Spec §2.3 states
/// "Unknown status stays Unknown, never defaults to Safe" — treating an
/// unresolved surveillance status as good enough to trade would be
/// exactly that silent default this platform is built to avoid.
/// </summary>
public static class UniverseEligibilityFilter
{
    public static EligibilityResult Evaluate(Instrument instrument, decimal currentPrice, int historicalBarCount, SurveillanceStatus surveillance, ScannerOptions options)
    {
        var checks = new List<EligibilityCheck>
        {
            new("Active", instrument.Active, instrument.Active ? "Active." : "Instrument is inactive or permanently suspended."),
            new("ValidYahooSymbol", !string.IsNullOrWhiteSpace(instrument.YahooSymbol), string.IsNullOrWhiteSpace(instrument.YahooSymbol) ? "YahooSymbol is missing/blank." : "Mapped."),
            new("SufficientHistoricalData", historicalBarCount >= options.MinimumHistoricalBarsRequired,
                $"{historicalBarCount} bars available, {options.MinimumHistoricalBarsRequired} required."),
            new("MinimumPrice", currentPrice >= options.MinimumPrice, $"Price {currentPrice:F2} vs minimum {options.MinimumPrice:F2}."),
            new("MinimumTurnover", (instrument.AverageDailyTurnover ?? 0m) >= options.MinimumAverageDailyTurnover,
                $"AverageDailyTurnover {(instrument.AverageDailyTurnover ?? 0m):F0} vs minimum {options.MinimumAverageDailyTurnover:F0}."),
            new("SurveillanceClear", surveillance.Availability == DataAvailability.Available && surveillance.Flags == RiskFlags.None,
                surveillance.Availability != DataAvailability.Available
                    ? "Surveillance status is Unknown — never treated as Safe."
                    : surveillance.Flags != RiskFlags.None ? $"Flagged: {surveillance.Flags}." : "Clear."),
        };

        return new EligibilityResult(checks.All(c => c.Passed), checks);
    }
}
