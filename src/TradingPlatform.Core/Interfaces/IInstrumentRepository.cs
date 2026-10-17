namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>Local SQLite-backed universe/instrument store (spec §2.2, §7.2).</summary>
public interface IInstrumentRepository
{
    Task<Instrument?> GetBySymbolAsync(string symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Instrument>> GetActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts if new (by Symbol), otherwise updates. Returns the row's Id.</summary>
    Task<long> UpsertAsync(Instrument instrument, CancellationToken cancellationToken = default);

    /// <summary>Every instrument, active AND inactive, by symbol (universe management; trading code uses GetActiveAsync).</summary>
    Task<IReadOnlyList<Instrument>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Activates/deactivates a symbol WITHOUT touching any other column. Returns false when the symbol does not exist.</summary>
    Task<bool> SetActiveAsync(string symbol, bool active, CancellationToken cancellationToken = default);

    /// <summary>Updates only the columns derived from downloaded bars (currently AverageDailyTurnover), leaving user-supplied data alone.</summary>
    Task UpdateDerivedMetricsAsync(string symbol, decimal? averageDailyTurnover, CancellationToken cancellationToken = default);

    /// <summary>Updates only MarketCap (and optionally AverageDailyTurnover when supplied) — the partial-update path for `universe import --kind marketcap`.</summary>
    Task UpdateMarketDataAsync(string symbol, decimal? marketCap, decimal? averageDailyTurnover, CancellationToken cancellationToken = default);
}
