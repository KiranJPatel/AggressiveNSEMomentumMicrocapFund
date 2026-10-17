namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>Institutional/delivery-volume activity source (spec §3.2), explicitly nullable-with-reason.</summary>
public interface IInstitutionalActivityProvider
{
    Task<InstitutionalActivitySnapshot> GetActivityAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default);
}
