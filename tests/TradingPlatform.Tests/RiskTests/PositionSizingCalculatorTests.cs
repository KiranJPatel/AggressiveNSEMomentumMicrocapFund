using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Risk.Sizing;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.RiskTests;

public class PositionSizingCalculatorTests
{
    private static readonly RiskOptions RiskOptions = new()
    {
        RiskPercentPerTrade = 1.0m,
        MaxParticipationPercentOfAdtv = 2m,
        PortfolioHeatMaxPercent = 8m,
        MaxCorrelatedClusterExposurePercent = 25m,
        ExceptionalPositionScoreThreshold = 85m,
        MicrocapMinTechnicalScore = 80m,
        MicrocapMinFundamentalScore = 60m,
    };

    /// <summary>Same as RiskOptions but with RiskPercentPerTrade loosened way up, so tests can isolate a DIFFERENT constraint as the binding one without RiskBasedQty getting there first.</summary>
    private static readonly RiskOptions LooseRiskOptions = new()
    {
        RiskPercentPerTrade = 50m,
        MaxParticipationPercentOfAdtv = 2m,
        PortfolioHeatMaxPercent = 8m,
        MaxCorrelatedClusterExposurePercent = 25m,
        ExceptionalPositionScoreThreshold = 85m,
        MicrocapMinTechnicalScore = 80m,
        MicrocapMinFundamentalScore = 60m,
    };

    private static readonly PortfolioOptions PortfolioOptions = new()
    {
        MaxSinglePositionPercent = 15m,
        ExceptionalPositionPercent = 20m,
        MaxSectorExposurePercent = 25m,
        MaxMicrocapExposurePercent = 35m,
        MinimumCashPercent = 10m,
    };

    /// <summary>A "roomy" baseline where nothing binds tightly except RiskBasedQty — every other headroom is generous. NAV=10,00,000; Entry=100; Stop=95 (RiskPerShare=5).</summary>
    private static PositionSizingInputs Baseline() => new()
    {
        Symbol = "STK",
        EntryPrice = 100m,
        StopPrice = 95m,
        Nav = 1_000_000m,
        CashBalance = 1_000_000m,
        AverageDailyTradedValue = 100_000_000m, // huge — liquidity never binds
        StockScoreTotalScore = 50m,
        MarketCapBucket = MarketCapBucket.Mid,
        CurrentSectorExposureValue = 0m,
        CurrentMicrocapExposureValue = 0m,
        CurrentPortfolioHeatValue = 0m,
        CorrelatedClusterExposureValue = 0m,
        DeployableCapitalPercent = 100m,
    };

    [Fact]
    public void RiskPerShareNegative_Rejects()
    {
        var b = Baseline();
        var inputs = new PositionSizingInputs
        {
            Symbol = b.Symbol, EntryPrice = 100m, StopPrice = 105m, // stop ABOVE entry -> negative risk per share
            Nav = b.Nav, CashBalance = b.CashBalance, AverageDailyTradedValue = b.AverageDailyTradedValue,
            StockScoreTotalScore = b.StockScoreTotalScore, MarketCapBucket = b.MarketCapBucket,
            CurrentSectorExposureValue = b.CurrentSectorExposureValue, CurrentMicrocapExposureValue = b.CurrentMicrocapExposureValue,
            CurrentPortfolioHeatValue = b.CurrentPortfolioHeatValue, CorrelatedClusterExposureValue = b.CorrelatedClusterExposureValue,
            DeployableCapitalPercent = b.DeployableCapitalPercent,
        };

        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);

        Assert.True(result.Rejected);
        Assert.Equal(0m, result.FinalQty);
        Assert.True(result.RejectionReason!.Contains("RiskPerShare"));
    }

    [Fact]
    public void RiskPerShareZero_Rejects()
    {
        var b = Baseline();
        var inputs = With(b, entryPrice: 100m, stopPrice: 100m);
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);
        Assert.True(result.Rejected);
    }

    [Fact]
    public void RiskBasedQty_BindsWhenNothingElseConstrains()
    {
        // Note: Baseline()'s 5-point stop (Entry=100, Stop=95) makes
        // RiskBasedQty=2000, which is actually WIDER than the 15%
        // MaxSinglePositionQty cap (1500) — MaxSinglePositionQty legitimately
        // binds tighter there, correctly reflecting that a very tight stop
        // implies an oversized risk-based position. To isolate RiskBasedQty
        // as the binding constraint here, widen the stop so RiskPerShare
        // produces a genuinely smaller share count than every other cap.
        var inputs = With(Baseline(), stopPrice: 90m); // RiskPerShare=10 -> RiskBasedQty=1000
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);

        // RiskAmount = 1,000,000 * 1% = 10,000; RiskPerShare = 10 -> RiskBasedQty = 1000
        Assert.Equal(1000m, result.RiskBasedQty);
        Assert.Equal(1000m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("RiskBasedQty"));
    }

    [Fact]
    public void MaxSinglePositionQty_BindsWhenTighter()
    {
        // Bump RiskPercentPerTrade way up so RiskBasedQty is huge, letting MaxSinglePosition bind instead.
        var looseRisk = LooseRiskOptions;
        var inputs = Baseline();

        var result = PositionSizingCalculator.Calculate(inputs, looseRisk, PortfolioOptions);

        // MaxSinglePositionQty = NAV*15% / EntryPrice = 150,000/100 = 1500
        Assert.Equal(1500m, result.MaxSinglePositionQty);
        Assert.Equal(1500m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("MaxSinglePositionQty"));
    }

    [Fact]
    public void ExceptionalScore_UsesExceptionalPositionPercent_NotMaxSingle()
    {
        var looseRisk = LooseRiskOptions;
        var b = Baseline();
        var inputs = With(b, stockScoreTotalScore: 90m); // >= ExceptionalPositionScoreThreshold (85)

        var result = PositionSizingCalculator.Calculate(inputs, looseRisk, PortfolioOptions);

        // ExceptionalPositionPercent = 20% -> NAV*20%/EntryPrice = 200,000/100 = 2000
        Assert.Equal(2000m, result.MaxSinglePositionQty);
    }

    [Fact]
    public void LiquidityConstrainedQty_BindsWhenTighter()
    {
        var looseRisk = LooseRiskOptions;
        var inputs = With(Baseline(), averageDailyTradedValue: 50_000m); // small ADTV

        var result = PositionSizingCalculator.Calculate(inputs, looseRisk, PortfolioOptions);

        // LiquidityConstrainedQty = 50,000*2% / 100 = 10
        Assert.Equal(10m, result.LiquidityConstrainedQty);
        Assert.Equal(10m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("LiquidityConstrainedQty"));
    }

    [Fact]
    public void SectorConstrainedQty_BindsWhenSectorAlreadyNearCap()
    {
        var looseRisk = LooseRiskOptions;
        // MaxSectorExposurePercent=25% of NAV=1,000,000 => 250,000 cap; already at 249,000 -> only 1,000 headroom -> 10 shares at price 100.
        var inputs = With(Baseline(), currentSectorExposureValue: 249_000m);

        var result = PositionSizingCalculator.Calculate(inputs, looseRisk, PortfolioOptions);

        Assert.Equal(10m, result.SectorConstrainedQty);
        Assert.Equal(10m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("SectorConstrainedQty"));
    }

    [Fact]
    public void MicrocapConstrainedQty_NotApplicable_ForNonMicrocap()
    {
        var inputs = Baseline(); // Mid bucket
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);
        Assert.False(result.BindingConstraints.Contains("MicrocapConstrainedQty"));
    }

    [Fact]
    public void MicrocapConstrainedQty_BindsWhenMicrocapExposureNearCap()
    {
        var looseRisk = LooseRiskOptions;
        var inputs = With(Baseline(),
            marketCapBucket: MarketCapBucket.Micro,
            currentMicrocapExposureValue: 349_000m, // cap 35% of 1,000,000 = 350,000; headroom 1,000 -> 10 shares
            technicalScore: 90m, fundamentalScore: 70m, hasCriticalSurveillanceFlag: false, hasSevereDataQualityIssue: false);

        var result = PositionSizingCalculator.Calculate(inputs, looseRisk, PortfolioOptions);

        Assert.Equal(10m, result.MicrocapConstrainedQty);
        Assert.Equal(10m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("MicrocapConstrainedQty"));
    }

    [Fact]
    public void CashConstrainedQty_BindsWhenCashNearMinimumReserve()
    {
        var looseRisk = LooseRiskOptions;
        // MinimumCashPercent=10% of NAV=1,000,000 => must keep 100,000 in reserve; CashBalance=101,000 -> headroom 1,000 -> 10 shares.
        var inputs = With(Baseline(), cashBalance: 101_000m);

        var result = PositionSizingCalculator.Calculate(inputs, looseRisk, PortfolioOptions);

        Assert.Equal(10m, result.CashConstrainedQty);
        Assert.Equal(10m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("CashConstrainedQty"));
    }

    [Fact]
    public void RegimeAdjustedQty_ScalesDownProportionallyToDeployableCapital()
    {
        var inputs = With(Baseline(), deployableCapitalPercent: 25m); // Defensive regime
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);

        // RiskBasedQty=2000 (as in the roomy baseline test) * 25% = 500
        Assert.Equal(500m, result.RegimeAdjustedQty);
        Assert.Equal(500m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("RegimeAdjustedQty"));
    }

    [Fact]
    public void PortfolioHeatConstrainedQty_BindsWhenHeatNearCap()
    {
        var looseRisk = LooseRiskOptions;
        // Heat cap = 8% of 1,000,000 = 80,000; already at 79,950 -> headroom 50; RiskPerShare=5 -> 10 shares.
        var inputs = With(Baseline(), currentPortfolioHeatValue: 79_950m);

        var result = PositionSizingCalculator.Calculate(inputs, looseRisk, PortfolioOptions);

        Assert.Equal(10m, result.PortfolioHeatConstrainedQty);
        Assert.Equal(10m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("PortfolioHeatConstrainedQty"));
    }

    [Fact]
    public void CorrelationConstrainedQty_BindsWhenClusterNearCap()
    {
        var looseRisk = LooseRiskOptions;
        // Cluster cap = 25% of 1,000,000 = 250,000; already at 249,000 -> headroom 1,000 -> 10 shares.
        var inputs = With(Baseline(), correlatedClusterExposureValue: 249_000m);

        var result = PositionSizingCalculator.Calculate(inputs, looseRisk, PortfolioOptions);

        Assert.Equal(10m, result.CorrelationConstrainedQty);
        Assert.Equal(10m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("CorrelationConstrainedQty"));
    }

    [Fact]
    public void ZeroHeadroom_ResultsInRejection()
    {
        var inputs = With(Baseline(), currentSectorExposureValue: 250_000m); // exactly at cap, zero headroom
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);

        Assert.True(result.Rejected);
        Assert.Equal(0m, result.FinalQty);
        Assert.True(result.RejectionReason!.Contains("SectorConstrainedQty"));
    }

    [Fact]
    public void MultipleConstraintsTiedAtSameValue_AllListedAsBinding()
    {
        var looseRisk = LooseRiskOptions;
        // Make sector AND correlation headroom both work out to exactly 10 shares.
        var inputs = With(Baseline(), currentSectorExposureValue: 249_000m, correlatedClusterExposureValue: 249_000m);

        var result = PositionSizingCalculator.Calculate(inputs, looseRisk, PortfolioOptions);

        Assert.Equal(10m, result.FinalQty);
        Assert.True(result.BindingConstraints.Contains("SectorConstrainedQty"));
        Assert.True(result.BindingConstraints.Contains("CorrelationConstrainedQty"));
        Assert.True(result.BindingConstraints.Count >= 2);
    }

    // --- Microcap gate ---

    [Fact]
    public void MicrocapGate_MissingTechnicalScore_Rejects()
    {
        var inputs = With(Baseline(), marketCapBucket: MarketCapBucket.Micro, technicalScore: null, fundamentalScore: 70m);
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);

        Assert.True(result.Rejected);
        Assert.True(result.RejectionReason!.Contains("TechnicalScore"));
    }

    [Fact]
    public void MicrocapGate_TechnicalScoreBelowThreshold_Rejects()
    {
        var inputs = With(Baseline(), marketCapBucket: MarketCapBucket.Micro, technicalScore: 79m, fundamentalScore: 70m);
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);
        Assert.True(result.Rejected);
    }

    [Fact]
    public void MicrocapGate_MissingFundamentalScore_Rejects()
    {
        var inputs = With(Baseline(), marketCapBucket: MarketCapBucket.Micro, technicalScore: 90m, fundamentalScore: null);
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);

        Assert.True(result.Rejected);
        Assert.True(result.RejectionReason!.Contains("FundamentalScore"));
    }

    [Fact]
    public void MicrocapGate_CriticalSurveillanceFlag_Rejects_EvenWithGoodScores()
    {
        var inputs = With(Baseline(), marketCapBucket: MarketCapBucket.Micro, technicalScore: 95m, fundamentalScore: 90m, hasCriticalSurveillanceFlag: true);
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);

        Assert.True(result.Rejected);
        Assert.True(result.RejectionReason!.Contains("surveillance"));
    }

    [Fact]
    public void MicrocapGate_SevereDataQualityIssue_Rejects_EvenWithGoodScores()
    {
        var inputs = With(Baseline(), marketCapBucket: MarketCapBucket.Micro, technicalScore: 95m, fundamentalScore: 90m, hasSevereDataQualityIssue: true);
        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);

        Assert.True(result.Rejected);
        Assert.True(result.RejectionReason!.Contains("data-quality"));
    }

    [Fact]
    public void MicrocapGate_AllConditionsSatisfied_ProceedsToSizing()
    {
        var inputs = With(Baseline(), marketCapBucket: MarketCapBucket.Micro, technicalScore: 90m, fundamentalScore: 70m,
            hasCriticalSurveillanceFlag: false, hasSevereDataQualityIssue: false);

        var result = PositionSizingCalculator.Calculate(inputs, RiskOptions, PortfolioOptions);

        Assert.False(result.Rejected);
        Assert.True(result.FinalQty > 0);
    }

    // --- helper: build a modified copy of a PositionSizingInputs (a plain class, no 'with' support) ---
    private static PositionSizingInputs With(
        PositionSizingInputs b,
        decimal? entryPrice = null, decimal? stopPrice = null, decimal? nav = null, decimal? cashBalance = null,
        decimal? averageDailyTradedValue = null, decimal? stockScoreTotalScore = null, MarketCapBucket? marketCapBucket = null,
        decimal? currentSectorExposureValue = null, decimal? currentMicrocapExposureValue = null,
        decimal? currentPortfolioHeatValue = null, decimal? correlatedClusterExposureValue = null,
        decimal? deployableCapitalPercent = null, decimal? technicalScore = null, decimal? fundamentalScore = null,
        bool? hasCriticalSurveillanceFlag = null, bool? hasSevereDataQualityIssue = null) => new()
    {
        Symbol = b.Symbol,
        EntryPrice = entryPrice ?? b.EntryPrice,
        StopPrice = stopPrice ?? b.StopPrice,
        Nav = nav ?? b.Nav,
        CashBalance = cashBalance ?? b.CashBalance,
        AverageDailyTradedValue = averageDailyTradedValue ?? b.AverageDailyTradedValue,
        StockScoreTotalScore = stockScoreTotalScore ?? b.StockScoreTotalScore,
        MarketCapBucket = marketCapBucket ?? b.MarketCapBucket,
        CurrentSectorExposureValue = currentSectorExposureValue ?? b.CurrentSectorExposureValue,
        CurrentMicrocapExposureValue = currentMicrocapExposureValue ?? b.CurrentMicrocapExposureValue,
        CurrentPortfolioHeatValue = currentPortfolioHeatValue ?? b.CurrentPortfolioHeatValue,
        CorrelatedClusterExposureValue = correlatedClusterExposureValue ?? b.CorrelatedClusterExposureValue,
        DeployableCapitalPercent = deployableCapitalPercent ?? b.DeployableCapitalPercent,
        TechnicalScore = technicalScore ?? b.TechnicalScore,
        FundamentalScore = fundamentalScore ?? b.FundamentalScore,
        HasCriticalSurveillanceFlag = hasCriticalSurveillanceFlag ?? b.HasCriticalSurveillanceFlag,
        HasSevereDataQualityIssue = hasSevereDataQualityIssue ?? b.HasSevereDataQualityIssue,
    };
}
