using System.Globalization;
using Microsoft.Extensions.Logging;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Application.Cli;

public enum DuplicatePolicy { Error, First, Last }

public sealed record ImportOptions(DuplicatePolicy OnDuplicate = DuplicatePolicy.First, bool DeactivateMissing = false);

public sealed record ImportResult(string Kind, int Rows, int Written, IReadOnlyList<string> Errors, IReadOnlyList<string>? Warnings = null, int Deactivated = 0)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// `dotnet run -- import --kind universe|classification|surveillance|fundamentals|institutional --file x.csv [--dry-run]`
/// (spec §2: configurable CSV/SQLite universe + manual/CSV ingestion for sector, surveillance, fundamentals and
/// institutional activity — none of which Yahoo provides reliably). Rules:
///  * ALL-OR-NOTHING: every row is validated first; if any row is bad, NOTHING is written and every error is listed with its line number.
///  * NEVER FABRICATE: blank optional values stay null/Unknown; a blank growth figure is stored as Unavailable, not 0.
///  * Re-importing the same file is idempotent (upserts keyed on Symbol[+date]).
/// Column reference: docs/data-import.md.
/// </summary>
public static class ImportCommand
{
    public static readonly string[] Kinds = { "universe", "classification", "surveillance", "fundamentals", "institutional", "marketcap" };

    public static async Task<int> RunAsync(string[] args, SqliteConnection db, IInstrumentRepository instruments, IUniverseClassificationRepository classifications,
        ISurveillanceRepository surveillance, SqliteFundamentalRepository fundamentals, SqliteInstitutionalActivityRepository institutional, ILogger logger, CancellationToken ct = default)
    {
        var kind = ReadFlag(args, "--kind")?.ToLowerInvariant();
        var file = ReadFlag(args, "--file");
        var dryRun = args.Contains("--dry-run");
        var policyText = ReadFlag(args, "--on-duplicate")?.ToLowerInvariant() ?? "first";
        if (!Enum.TryParse<DuplicatePolicy>(policyText, true, out var policy)) { Console.Error.WriteLine($"--on-duplicate '{policyText}' must be error|first|last."); return 1; }
        var importOptions = new ImportOptions(policy, args.Contains("--deactivate-missing"));
        if (kind is null || !Kinds.Contains(kind) || string.IsNullOrWhiteSpace(file))
        {
            Console.Error.WriteLine($"Usage: import --kind {string.Join("|", Kinds)} --file <path.csv> [--dry-run]");
            return 1;
        }
        if (!File.Exists(file)) { Console.Error.WriteLine($"File not found: {file}"); return 1; }

        CsvTable table;
        try { table = CsvTable.Parse(await File.ReadAllTextAsync(file, ct)); }
        catch (FormatException ex) { Console.Error.WriteLine($"Cannot parse {file}: {ex.Message}"); return 1; }

        var result = await ImportAsync(kind, table, Path.GetFileName(file), dryRun, db, instruments, classifications, surveillance, fundamentals, institutional, ct, importOptions);
        foreach (var w in (result.Warnings ?? Array.Empty<string>()).Take(30)) Console.Error.WriteLine("  warning: " + w);
        if ((result.Warnings?.Count ?? 0) > 30) Console.Error.WriteLine($"  … and {result.Warnings!.Count - 30} more warning(s).");
        foreach (var e in result.Errors.Take(50)) Console.Error.WriteLine("  " + e);
        if (result.Errors.Count > 50) Console.Error.WriteLine($"  … and {result.Errors.Count - 50} more.");
        if (!result.Ok) { Console.Error.WriteLine($"import {kind}: {result.Errors.Count} error(s) in {result.Rows} row(s) — NOTHING was written."); return 1; }
        Console.WriteLine(dryRun ? $"import {kind}: {result.Rows} row(s) valid (dry run — nothing written)." : $"import {kind}: {result.Written} row(s) written{(result.Deactivated > 0 ? $", {result.Deactivated} instrument(s) deactivated" : "")}{((result.Warnings?.Count ?? 0) > 0 ? $", {result.Warnings!.Count} warning(s)" : "")}.");
        logger.LogInformation("import: kind={Kind} rows={Rows} written={Written} dryRun={DryRun}.", kind, result.Rows, result.Written, dryRun);
        return 0;
    }

    public static async Task<ImportResult> ImportAsync(string kind, CsvTable table, string sourceName, bool dryRun, SqliteConnection db,
        IInstrumentRepository instruments, IUniverseClassificationRepository classifications, ISurveillanceRepository surveillance,
        SqliteFundamentalRepository fundamentals, SqliteInstitutionalActivityRepository institutional, CancellationToken ct = default, ImportOptions? options = null)
    {
        options ??= new ImportOptions();
        var warnings = new List<string>();
        var errors = new List<string>();
        var writes = new List<Func<Task>>();

        var required = kind switch
        {
            "universe" => new[] { "Symbol", "CompanyName" },
            "marketcap" => new[] { "Symbol", "MarketCap" },
            "classification" => new[] { "Symbol", "MarketCapBucket", "EffectiveFrom" },
            "surveillance" => new[] { "Symbol", "AsOfDate" },
            "fundamentals" => new[] { "Symbol", "AsOfDate" },
            _ => new[] { "Symbol", "AsOfDate" },
        };
        foreach (var col in required.Where(c => !table.HasColumn(c)))
            errors.Add($"Missing required column '{col}'.");
        if (errors.Count > 0) return new ImportResult(kind, table.Rows.Count, 0, errors);

        var seen = new HashSet<string>();
        var rowsToProcess = kind is "universe" or "marketcap" ? ResolveDuplicates(table, options.OnDuplicate, errors, warnings) : table.Rows;
        foreach (var row in rowsToProcess)
        {
            string Err(string msg) => $"line {row.Line}: {msg}";
            var symbol = row.Get("Symbol")?.ToUpperInvariant();
            if (symbol is null) { errors.Add(Err("Symbol is blank.")); continue; }
            if (row.FieldCount != row.HeaderCount) { errors.Add(Err($"expected {row.HeaderCount} fields but found {row.FieldCount}.")); continue; }

            switch (kind)
            {
                case "universe":
                {
                    if (!TryDecimal(row, "MarketCap", out var mcap, errors, row.Line) | !TryDecimal(row, "AverageDailyTurnover", out var turnover, errors, row.Line)) break;
                    // 0 is how exporters write "unknown" — storing it would read as a real market cap / zero turnover.
                    if (mcap is 0m) mcap = null;
                    if (turnover is 0m) turnover = null;
                    int? lot = null;
                    if (row.Get("LotSize") is { } ls) { if (!int.TryParse(ls, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) || l <= 0) { errors.Add(Err($"LotSize '{ls}' is not a positive integer.")); break; } lot = l; }
                    bool active = true;
                    if (row.Get("Active") is { } a) { if (!TryBool(a, out active)) { errors.Add(Err($"Active '{a}' is not true/false/1/0/yes/no.")); break; } }
                    var instrument = new Instrument
                    {
                        Symbol = symbol, NseSymbol = (row.Get("NseSymbol") ?? symbol).ToUpperInvariant(), YahooSymbol = row.Get("YahooSymbol") ?? symbol + ".NS",
                        Isin = row.Get("Isin"), CompanyName = row.Get("CompanyName") ?? symbol, Sector = NullIfNa(row.Get("Sector")), Industry = NullIfNa(row.Get("Industry")),
                        MarketCap = mcap, AverageDailyTurnover = turnover, LotSize = lot, Exchange = row.Get("Exchange") ?? "NSE", Active = active,
                    };
                    writes.Add(async () => await instruments.UpsertAsync(instrument, ct));
                    break;
                }
                case "marketcap":
                {
                    if (!TryDecimal(row, "MarketCap", out var cap, errors, row.Line) | !TryDecimal(row, "AverageDailyTurnover", out var turn, errors, row.Line)) break;
                    if (cap is null or <= 0m) { errors.Add(Err("MarketCap must be a positive number.")); break; }
                    if (await instruments.GetBySymbolAsync(symbol, ct) is null) { errors.Add(Err($"{symbol} is not in the universe — import the universe first.")); break; }
                    var capValue = cap; var turnValue = turn is 0m ? null : turn;
                    writes.Add(async () => await instruments.UpdateMarketDataAsync(symbol, capValue, turnValue, ct));
                    break;
                }
                case "classification":
                {
                    if (!Enum.TryParse<MarketCapBucket>(row.Get("MarketCapBucket"), true, out var bucket) || !Enum.IsDefined(bucket)) { errors.Add(Err($"MarketCapBucket '{row.Get("MarketCapBucket")}' must be Large|Mid|Small|Micro|Unknown.")); break; }
                    if (!TryDate(row.Get("EffectiveFrom"), out var from)) { errors.Add(Err($"EffectiveFrom '{row.Get("EffectiveFrom")}' must be yyyy-MM-dd.")); break; }
                    DateOnly? to = null;
                    if (row.Get("EffectiveTo") is { } t) { if (!TryDate(t, out var td)) { errors.Add(Err($"EffectiveTo '{t}' must be yyyy-MM-dd.")); break; } if (td < from) { errors.Add(Err("EffectiveTo is before EffectiveFrom.")); break; } to = td; }
                    int? rank = null;
                    if (row.Get("MarketCapRank") is { } r) { if (!int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ri) || ri <= 0) { errors.Add(Err($"MarketCapRank '{r}' is not a positive integer.")); break; } rank = ri; }
                    var instrument = await instruments.GetBySymbolAsync(symbol, ct);
                    if (instrument is null) { errors.Add(Err($"{symbol} is not in the universe — import the universe first.")); break; }
                    var snap = new UniverseClassificationSnapshot { InstrumentId = instrument.Id, Symbol = symbol, MarketCapBucket = bucket, MarketCapRank = rank, Sector = row.Get("Sector"), EffectiveFrom = from, EffectiveTo = to, IsUnknown = bucket == MarketCapBucket.Unknown };
                    writes.Add(async () => { db.Execute("DELETE FROM UniverseClassificationSnapshots WHERE Symbol = ? AND EffectiveFrom = ?;", symbol, from); await classifications.UpsertAsync(snap, ct); });
                    break;
                }
                case "surveillance":
                {
                    if (!TryDate(row.Get("AsOfDate"), out var asOf)) { errors.Add(Err($"AsOfDate '{row.Get("AsOfDate")}' must be yyyy-MM-dd.")); break; }
                    if (!seen.Add($"{symbol}|{asOf}")) { errors.Add(Err($"duplicate {symbol} {asOf:yyyy-MM-dd} in file.")); break; }
                    if (!TryFlags(row.Get("Flags"), out var flags, out var bad)) { errors.Add(Err($"unknown flag '{bad}' (valid: {string.Join("|", Enum.GetNames<RiskFlags>().Where(n => n != "None"))}, separated by | or ;).")); break; }
                    var status = new SurveillanceStatus { Symbol = symbol, AsOfDate = asOf, Flags = flags, Availability = DataAvailability.Available, Source = row.Get("Source") ?? $"csv-import:{sourceName}" };
                    writes.Add(async () => await surveillance.UpsertAsync(status, ct));
                    break;
                }
                case "fundamentals":
                {
                    if (!TryDate(row.Get("AsOfDate"), out var asOf)) { errors.Add(Err($"AsOfDate '{row.Get("AsOfDate")}' must be yyyy-MM-dd.")); break; }
                    if (!seen.Add($"{symbol}|{asOf}")) { errors.Add(Err($"duplicate {symbol} {asOf:yyyy-MM-dd} in file.")); break; }
                    if (!TryDecimal(row, "EarningsGrowthPercent", out var eg, errors, row.Line) | !TryDecimal(row, "RevenueGrowthPercent", out var rg, errors, row.Line)) break;
                    var snap = new FundamentalSnapshot
                    {
                        Symbol = symbol, AsOfDate = asOf,
                        EarningsGrowthPercent = eg, EarningsGrowthAvailability = eg is null ? DataAvailability.Unavailable : DataAvailability.Available,
                        RevenueGrowthPercent = rg, RevenueGrowthAvailability = rg is null ? DataAvailability.Unavailable : DataAvailability.Available,
                    };
                    writes.Add(async () => await fundamentals.UpsertAsync(snap, ct));
                    break;
                }
                default: // institutional
                {
                    if (!TryDate(row.Get("AsOfDate"), out var asOf)) { errors.Add(Err($"AsOfDate '{row.Get("AsOfDate")}' must be yyyy-MM-dd.")); break; }
                    if (!seen.Add($"{symbol}|{asOf}")) { errors.Add(Err($"duplicate {symbol} {asOf:yyyy-MM-dd} in file.")); break; }
                    if (!TryDecimal(row, "ActivityScore", out var score, errors, row.Line)) break;
                    var snap = new InstitutionalActivitySnapshot { Symbol = symbol, AsOfDate = asOf, ActivityScore = score, Availability = score is null ? DataAvailability.Unavailable : DataAvailability.Available };
                    writes.Add(async () => await institutional.UpsertAsync(snap, ct));
                    break;
                }
            }
        }

        // Opt-in: instruments present in the DB but ABSENT from the file stop trading (data is kept; reversible with `universe activate`).
        var toDeactivate = new List<string>();
        if (kind == "universe" && options.DeactivateMissing)
        {
            var inFile = rowsToProcess.Select(r => r.Get("Symbol")?.ToUpperInvariant()).Where(x => x is not null).ToHashSet();
            toDeactivate = (await instruments.GetActiveAsync(ct)).Select(i => i.Symbol).Where(sym => !inFile.Contains(sym.ToUpperInvariant())).ToList();
        }

        if (errors.Count > 0) return new ImportResult(kind, table.Rows.Count, 0, errors, warnings);
        if (dryRun) return new ImportResult(kind, table.Rows.Count, 0, errors, warnings, toDeactivate.Count);

        using var tx = db.BeginTransaction();
        try
        {
            foreach (var w in writes) await w();
            foreach (var sym in toDeactivate) await instruments.SetActiveAsync(sym, false, ct);
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
        return new ImportResult(kind, table.Rows.Count, writes.Count, errors, warnings, toDeactivate.Count);
    }

    private static string? NullIfNa(string? v) => v is null || v.Equals("NA", StringComparison.OrdinalIgnoreCase) || v.Equals("N/A", StringComparison.OrdinalIgnoreCase) ? null : v;

    /// <summary>
    /// One row per symbol. A repeated symbol is resolved by policy — never silently: First/Last keep one row and emit a WARNING that
    /// names exactly which columns differed between the copies; Error rejects the file. Different symbols sharing an ISIN are also
    /// reported (kept, since ISIN is not this table's key — but it usually means a data error or a corporate-action rename).
    /// </summary>
    private static IReadOnlyList<CsvRow> ResolveDuplicates(CsvTable table, DuplicatePolicy policy, List<string> errors, List<string> warnings)
    {
        var bySymbol = new Dictionary<string, CsvRow>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var row in table.Rows)
        {
            var sym = row.Get("Symbol");
            if (sym is null) { bySymbol[$"\0blank{row.Line}"] = row; order.Add($"\0blank{row.Line}"); continue; } // blank symbol: surfaced later by the main loop
            if (!bySymbol.TryGetValue(sym, out var kept)) { bySymbol[sym] = row; order.Add(sym); continue; }

            var differing = table.Headers.Where(h => !string.Equals(kept.Get(h), row.Get(h), StringComparison.Ordinal)).ToList();
            var detail = differing.Count == 0 ? "identical" : "differs in " + string.Join(", ", differing.Select(h => $"{h} ('{kept.Get(h)}' vs '{row.Get(h)}')"));
            switch (policy)
            {
                case DuplicatePolicy.Error: errors.Add($"line {row.Line}: duplicate symbol {sym} (first seen on line {kept.Line}; {detail})."); break;
                case DuplicatePolicy.First: warnings.Add($"line {row.Line}: duplicate symbol {sym} ignored — kept the first (line {kept.Line}); {detail}."); break;
                case DuplicatePolicy.Last: bySymbol[sym] = row; warnings.Add($"line {row.Line}: duplicate symbol {sym} — using this LATER row over line {kept.Line}; {detail}."); break;
            }
        }
        var rows = order.Select(k => bySymbol[k]).ToList();
        foreach (var g in rows.Where(r => r.Get("Isin") is not null).GroupBy(r => r.Get("Isin")!).Where(g => g.Count() > 1))
            warnings.Add($"ISIN {g.Key} is shared by {string.Join(" and ", g.Select(r => r.Get("Symbol")))} (kept both; check for a rename or a data error).");
        return rows;
    }

    private static bool TryDate(string? s, out DateOnly d) { d = default; return s is not null && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d); }
    private static bool TryBool(string s, out bool b)
    {
        switch (s.ToLowerInvariant()) { case "true": case "1": case "yes": case "y": b = true; return true; case "false": case "0": case "no": case "n": b = false; return true; default: b = false; return false; }
    }
    /// <summary>Blank -> null (OK). Non-blank must parse as an invariant decimal; failure appends an error and returns false.</summary>
    private static bool TryDecimal(CsvRow row, string column, out decimal? value, List<string> errors, int line)
    {
        value = null; var raw = row.Get(column);
        if (raw is null) return true;
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { value = d; return true; }
        errors.Add($"line {line}: {column} '{raw}' is not a number."); return false;
    }
    private static bool TryFlags(string? raw, out RiskFlags flags, out string? bad)
    {
        flags = RiskFlags.None; bad = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        foreach (var part in raw.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<RiskFlags>(part, true, out var f) || !Enum.IsDefined(f)) { bad = part; return false; }
            flags |= f;
        }
        return true;
    }
    private static string? ReadFlag(string[] args, string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}
