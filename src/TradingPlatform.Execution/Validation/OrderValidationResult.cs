namespace TradingPlatform.Execution.Validation;

/// <summary>One named pre-trade check's outcome — same "prove which constraint bound" pattern as PositionSizingResult.BindingConstraints (Tranche 8) and BreakoutEngine.Conditions (Tranche 6).</summary>
public sealed record OrderValidationCheck(string Name, bool Passed, string Detail);

/// <summary>
/// Full outcome of OrderValidationEngine.Validate — spec §6.3: "any failure
/// → reject with detailed reason." Checks always contains every APPLICABLE
/// check (side-dependent — see OrderValidationEngine), not just the first
/// failure, so a rejected order's full picture is auditable; RejectionReason
/// surfaces the FIRST failure for a human-readable single-line reason.
/// </summary>
public sealed record OrderValidationResult(bool IsValid, IReadOnlyList<OrderValidationCheck> Checks)
{
    public string? RejectionReason => Checks.FirstOrDefault(c => !c.Passed)?.Detail;
}
