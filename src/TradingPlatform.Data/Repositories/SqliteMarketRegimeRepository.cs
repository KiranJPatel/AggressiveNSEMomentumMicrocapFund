using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteMarketRegimeRepository : IMarketRegimeRepository
{
    private readonly SqliteConnection _connection;

    public SqliteMarketRegimeRepository(SqliteConnection connection) => _connection = connection;

    public Task UpsertAsync(MarketRegimeSnapshot s, CancellationToken cancellationToken = default)
    {
        _connection.Execute(
            """
            INSERT INTO MarketRegimes
                (AsOfDate, RegimeScore, Classification, DeployableCapitalPercent, NiftyTrendScore, BreadthScore, AdvanceDeclineRatio,
                 PercentUniverseAbove50Dma, PercentUniverseAbove200Dma, SmallMidRelativeStrength, VolatilityScore, BreakoutSuccessRate, WeeklyBias, CreatedAt)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(AsOfDate) DO UPDATE SET
                RegimeScore = excluded.RegimeScore, Classification = excluded.Classification,
                DeployableCapitalPercent = excluded.DeployableCapitalPercent, NiftyTrendScore = excluded.NiftyTrendScore,
                BreadthScore = excluded.BreadthScore, AdvanceDeclineRatio = excluded.AdvanceDeclineRatio,
                PercentUniverseAbove50Dma = excluded.PercentUniverseAbove50Dma, PercentUniverseAbove200Dma = excluded.PercentUniverseAbove200Dma,
                SmallMidRelativeStrength = excluded.SmallMidRelativeStrength, VolatilityScore = excluded.VolatilityScore,
                BreakoutSuccessRate = excluded.BreakoutSuccessRate, WeeklyBias = excluded.WeeklyBias, CreatedAt = excluded.CreatedAt;
            """,
            s.AsOfDate, s.RegimeScore, s.Classification, s.DeployableCapitalPercent, s.NiftyTrendScore, s.BreadthScore, s.AdvanceDeclineRatio,
            s.PercentUniverseAbove50Dma, s.PercentUniverseAbove200Dma, s.SmallMidRelativeStrength, s.VolatilityScore, s.BreakoutSuccessRate, s.WeeklyBias,
            DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task<MarketRegimeSnapshot?> GetLatestAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_connection.QuerySingleOrDefault("SELECT * FROM MarketRegimes ORDER BY AsOfDate DESC LIMIT 1;", Map));

    public Task<IReadOnlyList<MarketRegimeSnapshot>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        => Task.FromResult(_connection.Query("SELECT * FROM MarketRegimes WHERE AsOfDate >= ? AND AsOfDate <= ? ORDER BY AsOfDate;", Map, from, to));

    private static MarketRegimeSnapshot Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        AsOfDate = row.GetDateOnly("AsOfDate"),
        RegimeScore = row.GetInt32("RegimeScore"),
        Classification = row.GetEnum<RegimeClassification>("Classification"),
        DeployableCapitalPercent = row.GetDecimal("DeployableCapitalPercent"),
        NiftyTrendScore = row.GetNullableDecimal("NiftyTrendScore"),
        BreadthScore = row.GetNullableDecimal("BreadthScore"),
        AdvanceDeclineRatio = row.GetNullableDecimal("AdvanceDeclineRatio"),
        PercentUniverseAbove50Dma = row.GetNullableDecimal("PercentUniverseAbove50Dma"),
        PercentUniverseAbove200Dma = row.GetNullableDecimal("PercentUniverseAbove200Dma"),
        SmallMidRelativeStrength = row.GetNullableDecimal("SmallMidRelativeStrength"),
        VolatilityScore = row.GetNullableDecimal("VolatilityScore"),
        BreakoutSuccessRate = row.GetNullableDecimal("BreakoutSuccessRate"),
        WeeklyBias = row.GetNullableEnum<WeeklyBias>("WeeklyBias"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
    };
}
