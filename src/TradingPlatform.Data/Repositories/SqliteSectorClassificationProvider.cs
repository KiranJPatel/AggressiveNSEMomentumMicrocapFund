using TradingPlatform.Core.Interfaces;

namespace TradingPlatform.Data.Repositories;

/// <summary>
/// ISectorClassificationProvider (spec §2.2): the sector effective on the date from the point-in-time classification
/// snapshots, falling back to the instrument's own Sector column. Null (never a guess) when neither is known.
/// </summary>
public sealed class SqliteSectorClassificationProvider : ISectorClassificationProvider
{
    private readonly IUniverseClassificationRepository _classifications;
    private readonly IInstrumentRepository _instruments;

    public SqliteSectorClassificationProvider(IUniverseClassificationRepository classifications, IInstrumentRepository instruments)
    { _classifications = classifications; _instruments = instruments; }

    public async Task<string?> GetSectorAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default)
    {
        var snapshot = await _classifications.GetClassificationAsync(symbol, asOfDate, cancellationToken);
        if (!string.IsNullOrWhiteSpace(snapshot?.Sector)) return snapshot!.Sector;
        var instrument = await _instruments.GetBySymbolAsync(symbol, cancellationToken);
        return string.IsNullOrWhiteSpace(instrument?.Sector) ? null : instrument!.Sector;
    }
}
