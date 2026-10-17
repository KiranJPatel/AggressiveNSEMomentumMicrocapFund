using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;

namespace TradingPlatform.Execution.Orchestration;

/// <summary>
/// Spec §6.1: real broker order placement requires BOTH Mode=Live AND an
/// explicit AllowLiveTrading=true — codified as its own tiny, named,
/// tested unit rather than an inline condition scattered wherever it's
/// checked, since this is exactly the kind of single-flag-typo-away-from-
/// real-money-orders logic that deserves to be impossible to get subtly
/// wrong at each call site.
/// </summary>
public static class LiveModeGate
{
    public static bool CanPlaceLiveOrders(LiveTradingOptions options) => options.Mode == TradingMode.Live && options.AllowLiveTrading;

    /// <summary>Which IExecutionProvider a given mode should route through — Live only when the dual gate above clears; Live mode WITHOUT AllowLiveTrading falls back to Paper rather than silently doing nothing, so misconfiguration fails toward a safe, visible simulation instead of a mysteriously inert system.</summary>
    public static TradingMode EffectiveExecutionMode(LiveTradingOptions options) =>
        options.Mode == TradingMode.Live && !options.AllowLiveTrading ? TradingMode.Paper : options.Mode;
}
