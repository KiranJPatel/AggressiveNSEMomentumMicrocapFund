using TradingPlatform.Core.Enums;
using TradingPlatform.Data.Yahoo;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

/// <summary>
/// All fixture JSON here is hand-built from Yahoo's documented/known chart
/// API shape — this sandbox cannot reach query1.finance.yahoo.com to record
/// a real response (see docs/gap-log.md and
/// YahooFinanceOptions.AdapterVerifiedAgainstLiveResponse).
/// </summary>
public class YahooChartResponseParserTests
{
    private const string ValidTwoDayFixture = """
    {
      "chart": {
        "result": [
          {
            "meta": { "currency": "INR", "symbol": "RELIANCE.NS", "exchangeName": "NSI", "gmtoffset": 19800 },
            "timestamp": [1767330900, 1767417300],
            "indicators": {
              "quote": [
                {
                  "open": [1400.0, 1410.5],
                  "high": [1415.0, 1420.0],
                  "low": [1395.0, 1405.0],
                  "close": [1410.0, 1418.0],
                  "volume": [1000000, 1100000]
                }
              ],
              "adjclose": [ { "adjclose": [1410.0, 1418.0] } ]
            }
          }
        ],
        "error": null
      }
    }
    """;

    private const string ErrorFixture = """
    { "chart": { "result": null, "error": { "code": "Not Found", "description": "No data found, symbol may be delisted" } } }
    """;

    private const string NullFieldFixture = """
    {
      "chart": {
        "result": [
          {
            "meta": { "currency": "INR", "symbol": "XYZ.NS" },
            "timestamp": [1767330900, 1767417300],
            "indicators": {
              "quote": [
                {
                  "open": [100.0, null],
                  "high": [105.0, null],
                  "low": [99.0, null],
                  "close": [104.0, null],
                  "volume": [50000, null]
                }
              ],
              "adjclose": [ { "adjclose": [104.0, null] } ]
            }
          }
        ],
        "error": null
      }
    }
    """;

    [Fact]
    public void ValidFixture_ParsesExpectedBarCount_AndValues()
    {
        var result = YahooChartResponseParser.Parse(ValidTwoDayFixture, "RELIANCE", "Yahoo", Timeframe.Daily);

        Assert.Equal(2, result.Bars.Count);
        Assert.Equal(0, result.Warnings.Count);

        var first = result.Bars[0];
        Assert.Equal("RELIANCE", first.Symbol);
        Assert.Equal(1400.0m, first.Open);
        Assert.Equal(1415.0m, first.High);
        Assert.Equal(1395.0m, first.Low);
        Assert.Equal(1410.0m, first.Close);
        Assert.Equal(1410.0m, first.AdjustedClose);
        Assert.Equal(1000000L, first.Volume);
        Assert.Equal("Yahoo", first.Source);
        Assert.Equal(Timeframe.Daily, first.Timeframe);
    }

    [Fact]
    public void ErrorFixture_Throws()
    {
        Assert.Throws<YahooResponseException>(() =>
            YahooChartResponseParser.Parse(ErrorFixture, "DELISTED", "Yahoo", Timeframe.Daily));
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        Assert.Throws<YahooResponseException>(() =>
            YahooChartResponseParser.Parse("{ not valid json !!", "RELIANCE", "Yahoo", Timeframe.Daily));
    }

    [Fact]
    public void NullFieldEntries_AreSkipped_NotCrashed_AndWarned()
    {
        var result = YahooChartResponseParser.Parse(NullFieldFixture, "XYZ", "Yahoo", Timeframe.Daily);

        Assert.Equal(1, result.Bars.Count); // second candle (all nulls) skipped
        Assert.Equal(1, result.Warnings.Count);
    }

    [Fact]
    public void Timestamps_NormalizeToIstMidnight_DeterministicRegardlessOfEpochConvention()
    {
        var result = YahooChartResponseParser.Parse(ValidTwoDayFixture, "RELIANCE", "Yahoo", Timeframe.Daily);

        foreach (var bar in result.Bars)
        {
            Assert.Equal(TimeSpan.FromHours(5.5), bar.Timestamp.Offset);
            Assert.Equal(new TimeOnly(0, 0), TimeOnly.FromDateTime(bar.Timestamp.DateTime));
        }
    }

    [Fact]
    public void EmptyResultArray_ReturnsNoBarsAndAWarning_DoesNotThrow()
    {
        const string fixture = """{ "chart": { "result": [], "error": null } }""";
        var result = YahooChartResponseParser.Parse(fixture, "NODATA", "Yahoo", Timeframe.Daily);

        Assert.Equal(0, result.Bars.Count);
        Assert.Equal(1, result.Warnings.Count);
    }
}
