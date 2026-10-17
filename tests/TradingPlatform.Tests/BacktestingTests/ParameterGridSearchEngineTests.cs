using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class ParameterGridSearchEngineTests
{
    private sealed class FakeOptions
    {
        public decimal RiskPercent { get; set; } = 1m;
        public decimal AtrMultiplier { get; set; } = 2m;

        public FakeOptions Clone() => new() { RiskPercent = RiskPercent, AtrMultiplier = AtrMultiplier };
    }

    [Fact]
    public void Search_EnumeratesFullCartesianProduct()
    {
        var parameters = new[]
        {
            new GridParameter<FakeOptions>("RiskPercent", new decimal[] { 0.5m, 1m, 1.5m }, (o, v) => o.RiskPercent = v),
            new GridParameter<FakeOptions>("AtrMultiplier", new decimal[] { 2m, 2.5m }, (o, v) => o.AtrMultiplier = v),
        };

        var results = ParameterGridSearchEngine.Search(parameters, new FakeOptions(), o => o.Clone(), o => o.RiskPercent + o.AtrMultiplier);

        Assert.Equal(6, results.Count); // 3 x 2
    }

    [Fact]
    public void Search_AppliesEachDimensionCorrectly_ToTheClonedOptions()
    {
        var parameters = new[] { new GridParameter<FakeOptions>("RiskPercent", new decimal[] { 0.5m, 2m }, (o, v) => o.RiskPercent = v) };

        var results = ParameterGridSearchEngine.Search(parameters, new FakeOptions(), o => o.Clone(), o => o.RiskPercent);

        Assert.True(results.Any(r => r.Options.RiskPercent == 0.5m));
        Assert.True(results.Any(r => r.Options.RiskPercent == 2m));
    }

    [Fact]
    public void Search_ResultsAreRankedBestFirst()
    {
        var parameters = new[] { new GridParameter<FakeOptions>("RiskPercent", new decimal[] { 0.5m, 1m, 2m }, (o, v) => o.RiskPercent = v) };

        var results = ParameterGridSearchEngine.Search(parameters, new FakeOptions(), o => o.Clone(), o => o.RiskPercent); // score = the value itself

        Assert.Equal(2m, results[0].CompositeScore);
        Assert.Equal(0.5m, results[^1].CompositeScore);
    }

    [Fact]
    public void Search_DoesNotMutateTheBaselineOptions()
    {
        var baseline = new FakeOptions { RiskPercent = 1m };
        var parameters = new[] { new GridParameter<FakeOptions>("RiskPercent", new decimal[] { 5m }, (o, v) => o.RiskPercent = v) };

        ParameterGridSearchEngine.Search(parameters, baseline, o => o.Clone(), o => o.RiskPercent);

        Assert.Equal(1m, baseline.RiskPercent);
    }

    [Fact]
    public void Search_ParameterValuesDictionary_RecordsWhatWasSwept()
    {
        var parameters = new[] { new GridParameter<FakeOptions>("RiskPercent", new decimal[] { 1.25m }, (o, v) => o.RiskPercent = v) };
        var results = ParameterGridSearchEngine.Search(parameters, new FakeOptions(), o => o.Clone(), o => o.RiskPercent);

        Assert.Equal(1.25m, results[0].ParameterValues["RiskPercent"]);
    }

    [Fact]
    public void Search_NoParameters_Throws()
    {
        Assert.Throws<ArgumentException>(() => ParameterGridSearchEngine.Search(Array.Empty<GridParameter<FakeOptions>>(), new FakeOptions(), o => o.Clone(), o => 0m));
    }
}
