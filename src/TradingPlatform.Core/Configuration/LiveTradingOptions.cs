using TradingPlatform.Core.Enums;

namespace TradingPlatform.Core.Configuration;

/// <summary>
/// Binds to config section "LiveTrading" (spec §6.1: three modes —
/// Backtest, Paper, Live — with Live requiring BOTH Mode=Live AND an
/// explicit AllowLiveTrading flag, so a config typo alone can never
/// accidentally enable real order placement). Defaults are deliberately
/// the safest possible: Paper mode, live trading NOT allowed.
/// </summary>
public sealed class LiveTradingOptions
{
    public const string SectionName = "LiveTrading";

    public TradingMode Mode { get; set; } = TradingMode.Paper;

    /// <summary>Must be explicitly set true, IN ADDITION to Mode=Live, before any real broker order is ever placed — see LiveModeGate.</summary>
    public bool AllowLiveTrading { get; set; } = false;

    /// <summary>Spec §6.4 names "repeated order rejection" as a kill-switch source but not a threshold — resolved: N consecutive rejections in a row.</summary>
    public int RejectionConsecutiveThreshold { get; set; } = 3;

    /// <summary>Spec §6.4 names "abnormal slippage" as a kill-switch source but not a threshold — resolved: bps beyond which a fill counts as abnormal, and how many consecutive abnormal fills trip the switch.</summary>
    public decimal AbnormalSlippageThresholdBps { get; set; } = 100m;
    public int AbnormalSlippageConsecutiveThreshold { get; set; } = 3;

    /// <summary>Cash reconciliation tolerance (spec §6.4 treats any mismatch as dirty; a tiny rupee-level tolerance absorbs harmless rounding, not real discrepancies).</summary>
    public decimal CashReconciliationToleranceRupees { get; set; } = 1m;
}
