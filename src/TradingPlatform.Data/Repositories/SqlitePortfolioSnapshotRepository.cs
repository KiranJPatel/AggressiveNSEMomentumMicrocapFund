using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqlitePortfolioSnapshotRepository : IPortfolioSnapshotRepository
{
    private readonly SqliteConnection _connection;

    public SqlitePortfolioSnapshotRepository(SqliteConnection connection) => _connection = connection;

    public Task UpsertAsync(PortfolioSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        // AsOfDate is UNIQUE — ON CONFLICT lets a re-run for the same day
        // (e.g. an EOD job re-executed after a fix) update in place instead
        // of failing the insert.
        _connection.Execute(
            """
            INSERT INTO PortfolioSnapshots
                (AsOfDate, Nav, CashBalance, PeakNav, DrawdownPercent, PortfolioHeatPercent, OpenPositionCount, Regime, CreatedAt)
            VALUES (?,?,?,?,?,?,?,?,?)
            ON CONFLICT(AsOfDate) DO UPDATE SET
                Nav = excluded.Nav, CashBalance = excluded.CashBalance, PeakNav = excluded.PeakNav,
                DrawdownPercent = excluded.DrawdownPercent, PortfolioHeatPercent = excluded.PortfolioHeatPercent,
                OpenPositionCount = excluded.OpenPositionCount, Regime = excluded.Regime;
            """,
            snapshot.AsOfDate, snapshot.Nav, snapshot.CashBalance, snapshot.PeakNav, snapshot.DrawdownPercent,
            snapshot.PortfolioHeatPercent, snapshot.OpenPositionCount, snapshot.Regime, now);
        return Task.CompletedTask;
    }

    public Task<PortfolioSnapshot?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var result = _connection.QuerySingleOrDefault(
            "SELECT * FROM PortfolioSnapshots ORDER BY AsOfDate DESC LIMIT 1;", Map);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<PortfolioSnapshot>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query(
            "SELECT * FROM PortfolioSnapshots WHERE AsOfDate >= ? AND AsOfDate <= ? ORDER BY AsOfDate;",
            Map, from, to);
        return Task.FromResult(result);
    }

    private static PortfolioSnapshot Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        AsOfDate = row.GetDateOnly("AsOfDate"),
        Nav = row.GetDecimal("Nav"),
        CashBalance = row.GetDecimal("CashBalance"),
        PeakNav = row.GetDecimal("PeakNav"),
        DrawdownPercent = row.GetDecimal("DrawdownPercent"),
        PortfolioHeatPercent = row.GetDecimal("PortfolioHeatPercent"),
        OpenPositionCount = row.GetInt32("OpenPositionCount"),
        Regime = row.GetNullableEnum<RegimeClassification>("Regime"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
    };
}
