using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteInstrumentRepository : IInstrumentRepository
{
    private readonly SqliteConnection _connection;

    public SqliteInstrumentRepository(SqliteConnection connection) => _connection = connection;

    public Task<Instrument?> GetBySymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var result = _connection.QuerySingleOrDefault(
            "SELECT * FROM Instruments WHERE Symbol = ?;", Map, symbol);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Instrument>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var result = _connection.Query(
            "SELECT * FROM Instruments WHERE Active = 1 ORDER BY Symbol;", Map);
        return Task.FromResult(result);
    }

    public Task<long> UpsertAsync(Instrument instrument, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var found = _connection.TryQuerySingleValue(
            "SELECT Id FROM Instruments WHERE Symbol = ?;", row => row.GetInt64("Id"), out var id, instrument.Symbol);

        if (found)
        {
            _connection.Execute(
                """
                UPDATE Instruments SET NseSymbol=?, YahooSymbol=?, Isin=?, CompanyName=?, Sector=?, Industry=?,
                    MarketCap=?, AverageDailyTurnover=?, LotSize=?, Exchange=?, Active=?, UpdatedAt=?
                WHERE Id=?;
                """,
                instrument.NseSymbol, instrument.YahooSymbol, instrument.Isin, instrument.CompanyName,
                instrument.Sector, instrument.Industry, instrument.MarketCap, instrument.AverageDailyTurnover,
                instrument.LotSize, instrument.Exchange, instrument.Active, now, id);
            return Task.FromResult(id);
        }

        var newId = _connection.ExecuteInsert(
            """
            INSERT INTO Instruments
                (Symbol, NseSymbol, YahooSymbol, Isin, CompanyName, Sector, Industry, MarketCap,
                 AverageDailyTurnover, LotSize, Exchange, Active, CreatedAt, UpdatedAt)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?);
            """,
            instrument.Symbol, instrument.NseSymbol, instrument.YahooSymbol, instrument.Isin,
            instrument.CompanyName, instrument.Sector, instrument.Industry, instrument.MarketCap,
            instrument.AverageDailyTurnover, instrument.LotSize, instrument.Exchange, instrument.Active,
            now, now);
        return Task.FromResult(newId);
    }

    public Task<IReadOnlyList<Instrument>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_connection.Query("SELECT * FROM Instruments ORDER BY Symbol;", Map));

    public Task<bool> SetActiveAsync(string symbol, bool active, CancellationToken cancellationToken = default)
    {
        var exists = _connection.TryQuerySingleValue("SELECT Id FROM Instruments WHERE Symbol = ?;", r => r.GetInt64("Id"), out _, symbol);
        if (exists) _connection.Execute("UPDATE Instruments SET Active = ?, UpdatedAt = ? WHERE Symbol = ?;", active, DateTimeOffset.UtcNow, symbol);
        return Task.FromResult(exists);
    }

    public Task UpdateDerivedMetricsAsync(string symbol, decimal? averageDailyTurnover, CancellationToken cancellationToken = default)
    {
        _connection.Execute("UPDATE Instruments SET AverageDailyTurnover = ?, UpdatedAt = ? WHERE Symbol = ?;", averageDailyTurnover, DateTimeOffset.UtcNow, symbol);
        return Task.CompletedTask;
    }

    public Task UpdateMarketDataAsync(string symbol, decimal? marketCap, decimal? averageDailyTurnover, CancellationToken cancellationToken = default)
    {
        _connection.Execute(
            "UPDATE Instruments SET MarketCap = COALESCE(?, MarketCap), AverageDailyTurnover = COALESCE(?, AverageDailyTurnover), UpdatedAt = ? WHERE Symbol = ?;",
            marketCap, averageDailyTurnover, DateTimeOffset.UtcNow, symbol);
        return Task.CompletedTask;
    }

    private static Instrument Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        Symbol = row.GetString("Symbol"),
        NseSymbol = row.GetString("NseSymbol"),
        YahooSymbol = row.GetString("YahooSymbol"),
        Isin = row.GetNullableString("Isin"),
        CompanyName = row.GetString("CompanyName"),
        Sector = row.GetNullableString("Sector"),
        Industry = row.GetNullableString("Industry"),
        MarketCap = row.GetNullableDecimal("MarketCap"),
        AverageDailyTurnover = row.GetNullableDecimal("AverageDailyTurnover"),
        LotSize = row.GetNullableInt32("LotSize"),
        Exchange = row.GetString("Exchange"),
        Active = row.GetBoolean("Active"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
        UpdatedAt = row.GetDateTimeOffset("UpdatedAt"),
    };
}
