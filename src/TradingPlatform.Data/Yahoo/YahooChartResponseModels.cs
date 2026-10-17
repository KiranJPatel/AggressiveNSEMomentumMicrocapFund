using System.Text.Json.Serialization;

namespace TradingPlatform.Data.Yahoo;

/// <summary>
/// Deserialization targets for Yahoo Finance's `/v8/finance/chart/{symbol}`
/// endpoint. Shape is implemented from documented/known structure — this
/// build environment cannot reach query1.finance.yahoo.com to verify
/// against a live response (see docs/gap-log.md and
/// YahooFinanceOptions.AdapterVerifiedAgainstLiveResponse). Field names use
/// [JsonPropertyName] rather than relying on case-insensitive matching, so
/// the mapping is explicit and greppable against Yahoo's actual JSON if/when
/// someone verifies it later.
/// </summary>
public sealed class YahooChartResponse
{
    [JsonPropertyName("chart")]
    public YahooChart? Chart { get; set; }
}

public sealed class YahooChart
{
    [JsonPropertyName("result")]
    public List<YahooChartResult>? Result { get; set; }

    [JsonPropertyName("error")]
    public YahooChartError? Error { get; set; }
}

public sealed class YahooChartError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class YahooChartResult
{
    [JsonPropertyName("meta")]
    public YahooChartMeta? Meta { get; set; }

    [JsonPropertyName("timestamp")]
    public List<long>? Timestamp { get; set; }

    [JsonPropertyName("indicators")]
    public YahooChartIndicators? Indicators { get; set; }
}

public sealed class YahooChartMeta
{
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("exchangeName")]
    public string? ExchangeName { get; set; }

    [JsonPropertyName("gmtoffset")]
    public long? GmtOffset { get; set; }
}

public sealed class YahooChartIndicators
{
    [JsonPropertyName("quote")]
    public List<YahooChartQuote>? Quote { get; set; }

    [JsonPropertyName("adjclose")]
    public List<YahooChartAdjClose>? AdjClose { get; set; }
}

public sealed class YahooChartQuote
{
    [JsonPropertyName("open")]
    public List<double?>? Open { get; set; }

    [JsonPropertyName("high")]
    public List<double?>? High { get; set; }

    [JsonPropertyName("low")]
    public List<double?>? Low { get; set; }

    [JsonPropertyName("close")]
    public List<double?>? Close { get; set; }

    [JsonPropertyName("volume")]
    public List<long?>? Volume { get; set; }
}

public sealed class YahooChartAdjClose
{
    [JsonPropertyName("adjclose")]
    public List<double?>? AdjClose { get; set; }
}
