namespace TradingPlatform.Data.Validation;

public enum DataQualitySeverity { Info, Warning, Error }

/// <summary>
/// A flagged data quality observation (spec §5.3: "never silently fix
/// suspicious data — flag it"). Bars are never dropped or altered because
/// of an issue found here; the bar is still persisted as-received, and the
/// issue is logged (via ISystemEventWriter, category "DataQuality") for a
/// human or a later automated corporate-action reconciliation pass to review.
/// </summary>
public sealed record DataQualityIssue(string Symbol, DateTimeOffset Timestamp, DataQualitySeverity Severity, string IssueType, string Description);
