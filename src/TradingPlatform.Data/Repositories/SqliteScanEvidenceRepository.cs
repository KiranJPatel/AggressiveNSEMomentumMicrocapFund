using System.Text.Json;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteScanEvidenceRepository : IScanEvidenceRepository
{
    private readonly SqliteConnection _connection;

    public SqliteScanEvidenceRepository(SqliteConnection connection) => _connection = connection;

    public Task UpsertAsync(IReadOnlyList<ScanEvidence> evidence, CancellationToken cancellationToken = default)
    {
        if (evidence.Count == 0) return Task.CompletedTask;
        var now = DateTimeOffset.UtcNow;
        using var tx = _connection.BeginTransaction();
        foreach (var e in evidence)
        {
            var s = e.Score;
            _connection.Execute(
                """
                INSERT INTO StockScores
                    (Symbol, AsOfDate, TotalScore, TechnicalScore, FundamentalScore, RelativeStrengthScore, VolumeScore, PriceMomentumScore,
                     EarningsAccelerationScore, VcpScore, RevenueAccelerationScore, TrendStructureScore, InstitutionalActivityScore, SectorScore,
                     DataQualityScore, CreatedAt)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                ON CONFLICT(Symbol, AsOfDate) DO UPDATE SET
                    TotalScore = excluded.TotalScore, TechnicalScore = excluded.TechnicalScore, FundamentalScore = excluded.FundamentalScore,
                    RelativeStrengthScore = excluded.RelativeStrengthScore, VolumeScore = excluded.VolumeScore, PriceMomentumScore = excluded.PriceMomentumScore,
                    EarningsAccelerationScore = excluded.EarningsAccelerationScore, VcpScore = excluded.VcpScore,
                    RevenueAccelerationScore = excluded.RevenueAccelerationScore, TrendStructureScore = excluded.TrendStructureScore,
                    InstitutionalActivityScore = excluded.InstitutionalActivityScore, SectorScore = excluded.SectorScore,
                    DataQualityScore = excluded.DataQualityScore, CreatedAt = excluded.CreatedAt;
                """,
                s.Symbol, s.AsOfDate, s.TotalScore, s.TechnicalScore, s.FundamentalScore, s.RelativeStrengthScore, s.VolumeScore, s.PriceMomentumScore,
                s.EarningsAccelerationScore, s.VcpScore, s.RevenueAccelerationScore, s.TrendStructureScore, s.InstitutionalActivityScore, s.SectorScore,
                s.DataQualityScore, now);

            if (e.Vcp is { } v)
                _connection.Execute(
                    """
                    INSERT INTO VcpPatterns
                        (Symbol, AsOfDate, VcpScore, NumberOfContractions, ContractionRatiosJson, PivotPrice, BaseLengthDays, AverageVolumeDuringBase, VolumeDryUpScore, CreatedAt)
                    VALUES (?,?,?,?,?,?,?,?,?,?)
                    ON CONFLICT(Symbol, AsOfDate) DO UPDATE SET
                        VcpScore = excluded.VcpScore, NumberOfContractions = excluded.NumberOfContractions, ContractionRatiosJson = excluded.ContractionRatiosJson,
                        PivotPrice = excluded.PivotPrice, BaseLengthDays = excluded.BaseLengthDays, AverageVolumeDuringBase = excluded.AverageVolumeDuringBase,
                        VolumeDryUpScore = excluded.VolumeDryUpScore, CreatedAt = excluded.CreatedAt;
                    """,
                    v.Symbol, v.AsOfDate, v.VcpScore, v.NumberOfContractions, JsonSerializer.Serialize(v.ContractionRatios), v.PivotPrice, v.BaseLengthDays,
                    v.AverageVolumeDuringBase, v.VolumeDryUpScore, now);

            var i = e.Indicators;
            _connection.Execute(
                """
                INSERT INTO Indicators
                    (Symbol, Timestamp, Timeframe, HmaFast, HmaSlow, Supertrend, Sma20, Sma50, Sma200, Rsi, Macd, MacdSignal, Adx, Atr, AtrPercent,
                     HistoricalVolatility, VolumeSma, RelativeVolume, CreatedAt)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                ON CONFLICT(Symbol, Timestamp, Timeframe) DO UPDATE SET
                    HmaFast = excluded.HmaFast, HmaSlow = excluded.HmaSlow, Supertrend = excluded.Supertrend, Sma20 = excluded.Sma20, Sma50 = excluded.Sma50,
                    Sma200 = excluded.Sma200, Rsi = excluded.Rsi, Macd = excluded.Macd, MacdSignal = excluded.MacdSignal, Adx = excluded.Adx, Atr = excluded.Atr,
                    AtrPercent = excluded.AtrPercent, HistoricalVolatility = excluded.HistoricalVolatility, VolumeSma = excluded.VolumeSma,
                    RelativeVolume = excluded.RelativeVolume, CreatedAt = excluded.CreatedAt;
                """,
                i.Symbol, i.Timestamp, i.Timeframe, i.HmaFast, i.HmaSlow, i.Supertrend, i.Sma20, i.Sma50, i.Sma200, i.Rsi, i.Macd, i.MacdSignal, i.Adx, i.Atr,
                i.AtrPercent, i.HistoricalVolatility, i.VolumeSma, i.RelativeVolume, now);
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<StockScore?> GetLatestScoreAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult(_connection.QuerySingleOrDefault("SELECT * FROM StockScores WHERE Symbol = ? ORDER BY AsOfDate DESC LIMIT 1;", row => new StockScore
        {
            Id = row.GetInt64("Id"), Symbol = row.GetString("Symbol"), AsOfDate = row.GetDateOnly("AsOfDate"),
            TotalScore = row.GetDecimal("TotalScore"), TechnicalScore = row.GetDecimal("TechnicalScore"), FundamentalScore = row.GetDecimal("FundamentalScore"),
            RelativeStrengthScore = row.GetDecimal("RelativeStrengthScore"), VolumeScore = row.GetDecimal("VolumeScore"), PriceMomentumScore = row.GetDecimal("PriceMomentumScore"),
            EarningsAccelerationScore = row.GetDecimal("EarningsAccelerationScore"), VcpScore = row.GetDecimal("VcpScore"),
            RevenueAccelerationScore = row.GetDecimal("RevenueAccelerationScore"), TrendStructureScore = row.GetDecimal("TrendStructureScore"),
            InstitutionalActivityScore = row.GetDecimal("InstitutionalActivityScore"), SectorScore = row.GetDecimal("SectorScore"),
            DataQualityScore = row.GetDecimal("DataQualityScore"), CreatedAt = row.GetDateTimeOffset("CreatedAt"),
        }, symbol));

    public Task<VcpPattern?> GetLatestVcpAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult(_connection.QuerySingleOrDefault("SELECT * FROM VcpPatterns WHERE Symbol = ? ORDER BY AsOfDate DESC LIMIT 1;", row => new VcpPattern
        {
            Id = row.GetInt64("Id"), Symbol = row.GetString("Symbol"), AsOfDate = row.GetDateOnly("AsOfDate"), VcpScore = row.GetDecimal("VcpScore"),
            NumberOfContractions = row.GetInt32("NumberOfContractions"),
            ContractionRatios = JsonSerializer.Deserialize<List<decimal>>(row.GetString("ContractionRatiosJson")) ?? new List<decimal>(),
            PivotPrice = row.GetDecimal("PivotPrice"), BaseLengthDays = row.GetInt32("BaseLengthDays"),
            AverageVolumeDuringBase = row.GetDecimal("AverageVolumeDuringBase"), VolumeDryUpScore = row.GetDecimal("VolumeDryUpScore"),
            CreatedAt = row.GetDateTimeOffset("CreatedAt"),
        }, symbol));

    public Task<IndicatorSnapshot?> GetLatestIndicatorsAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult(_connection.QuerySingleOrDefault("SELECT * FROM Indicators WHERE Symbol = ? ORDER BY Timestamp DESC LIMIT 1;", row => new IndicatorSnapshot
        {
            Id = row.GetInt64("Id"), Symbol = row.GetString("Symbol"), Timestamp = row.GetDateTimeOffset("Timestamp"), Timeframe = row.GetEnum<Timeframe>("Timeframe"),
            HmaFast = row.GetNullableDecimal("HmaFast"), HmaSlow = row.GetNullableDecimal("HmaSlow"), Supertrend = row.GetNullableDecimal("Supertrend"),
            Sma20 = row.GetNullableDecimal("Sma20"), Sma50 = row.GetNullableDecimal("Sma50"), Sma200 = row.GetNullableDecimal("Sma200"),
            Rsi = row.GetNullableDecimal("Rsi"), Macd = row.GetNullableDecimal("Macd"), MacdSignal = row.GetNullableDecimal("MacdSignal"), Adx = row.GetNullableDecimal("Adx"),
            Atr = row.GetNullableDecimal("Atr"), AtrPercent = row.GetNullableDecimal("AtrPercent"), HistoricalVolatility = row.GetNullableDecimal("HistoricalVolatility"),
            VolumeSma = row.GetNullableDecimal("VolumeSma"), RelativeVolume = row.GetNullableDecimal("RelativeVolume"), CreatedAt = row.GetDateTimeOffset("CreatedAt"),
        }, symbol));
}
