using System.Globalization;
using Microsoft.Extensions.Logging;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Cli;

public enum DeliveryFileFormat { Unknown, NseBhavcopy, Generic }

public sealed record DeliveryParseResult(
    DeliveryFileFormat Format, int RowsRead, IReadOnlyList<DeliveryRecord> Records,
    int SkippedOtherSeries, int SkippedNoDeliveryFigure, int SkippedNotInUniverse, IReadOnlyList<string> Rejected, string? FatalError);

/// <summary>
/// Parses delivery-percent files (spec §3.4 "if a reliable source is added later"). Two layouts, detected from the header:
///  - NSE "full bhavcopy" (sec_bhavdata_full_DDMMYYYY.csv): SYMBOL, SERIES, DATE1, TTL_TRD_QNTY, DELIV_QTY, DELIV_PER. Only SERIES = EQ rows are
///    kept (the trade-for-trade BE series is 100% delivery by rule, which says nothing about conviction); a "-" delivery figure is skipped and counted.
///  - Generic: Symbol, TradeDate (or AsOfDate / Date), DeliveryPercent [, TradedQuantity, DeliveredQuantity]; the percent may be left blank when
///    both quantities are given, and is then DeliveredQuantity / TradedQuantity x 100.
/// A row that cannot be parsed is REJECTED and listed — never guessed — and does not stop the rest of the file. The NSE layout here is written from
/// the documented column names and has NOT been run against a real NSE download.
/// </summary>
public static class DeliveryCsvParser
{
    private static readonly string[] DateFormats = { "dd-MMM-yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy" };

    public static DeliveryParseResult Parse(string csvText, string source, ISet<string>? universe)
    {
        CsvTable table;
        try { table = CsvTable.Parse(csvText); }
        catch (FormatException ex) { return Fail($"not a readable CSV: {ex.Message}"); }

        DeliveryFileFormat format;
        if (table.HasColumn("DELIV_PER") && table.HasColumn("SYMBOL") && table.HasColumn("DATE1")) format = DeliveryFileFormat.NseBhavcopy;
        else if (table.HasColumn("Symbol") && (table.HasColumn("TradeDate") || table.HasColumn("AsOfDate") || table.HasColumn("Date"))
                 && (table.HasColumn("DeliveryPercent") || (table.HasColumn("TradedQuantity") && table.HasColumn("DeliveredQuantity")))) format = DeliveryFileFormat.Generic;
        else return Fail("unrecognised layout. Expected NSE bhavcopy columns (SYMBOL, SERIES, DATE1, DELIV_PER, ...) or generic columns (Symbol, TradeDate, DeliveryPercent).");

        var records = new List<DeliveryRecord>();
        var rejected = new List<string>();
        int otherSeries = 0, noFigure = 0, notInUniverse = 0;
        var seen = new HashSet<string>();

        foreach (var row in table.Rows)
        {
            string Rej(string why) => $"line {row.Line}: {why}";
            var symbol = (format == DeliveryFileFormat.NseBhavcopy ? row.Get("SYMBOL") : row.Get("Symbol"))?.ToUpperInvariant();
            if (symbol is null) { rejected.Add(Rej("blank symbol")); continue; }

            if (format == DeliveryFileFormat.NseBhavcopy && row.Get("SERIES") is { } series && !series.Equals("EQ", StringComparison.OrdinalIgnoreCase)) { otherSeries++; continue; }
            if (universe is not null && !universe.Contains(symbol)) { notInUniverse++; continue; }

            var dateText = format == DeliveryFileFormat.NseBhavcopy ? row.Get("DATE1") : (row.Get("TradeDate") ?? row.Get("AsOfDate") ?? row.Get("Date"));
            if (!TryDate(dateText, out var date)) { rejected.Add(Rej($"{symbol}: date '{dateText}' is not dd-MMM-yyyy, yyyy-MM-dd or dd-MM-yyyy")); continue; }

            var tradedText = format == DeliveryFileFormat.NseBhavcopy ? row.Get("TTL_TRD_QNTY") : row.Get("TradedQuantity");
            var deliveredText = format == DeliveryFileFormat.NseBhavcopy ? row.Get("DELIV_QTY") : row.Get("DeliveredQuantity");
            long? traded = TryLong(tradedText), delivered = TryLong(deliveredText);

            var percentText = format == DeliveryFileFormat.NseBhavcopy ? row.Get("DELIV_PER") : row.Get("DeliveryPercent");
            decimal percent;
            if (percentText is null || percentText == "-")
            {
                if (traded is > 0 && delivered is not null && format == DeliveryFileFormat.Generic) percent = (decimal)delivered.Value / traded.Value * 100m;
                else { noFigure++; continue; }
            }
            else if (!decimal.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)) { rejected.Add(Rej($"{symbol}: delivery percent '{percentText}' is not a number")); continue; }

            if (percent is < 0m or > 100m) { rejected.Add(Rej($"{symbol}: delivery percent {percent} is outside 0-100")); continue; }
            if (!seen.Add($"{symbol}|{date:yyyy-MM-dd}")) { rejected.Add(Rej($"duplicate {symbol} {date:yyyy-MM-dd} in file")); continue; }

            records.Add(new DeliveryRecord { Symbol = symbol, TradeDate = date, TotalTradedQuantity = traded, DeliverableQuantity = delivered, DeliveryPercent = decimal.Round(percent, 4), Source = source, CreatedAt = DateTimeOffset.UtcNow });
        }
        return new DeliveryParseResult(format, table.Rows.Count, records, otherSeries, noFigure, notInUniverse, rejected, null);

        DeliveryParseResult Fail(string msg) => new(DeliveryFileFormat.Unknown, 0, Array.Empty<DeliveryRecord>(), 0, 0, 0, Array.Empty<string>(), msg);
    }

    private static bool TryDate(string? text, out DateOnly date)
    {
        date = default;
        return text is not null && DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static long? TryLong(string? text) =>
        text is not null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>`dotnet run -- import-delivery --file F.csv | --dir D [--all-symbols] [--dry-run]` — loads delivery % into the DeliveryData table.</summary>
public static class DeliveryImportCommand
{
    public static async Task<int> RunAsync(string[] args, IDeliveryDataRepository deliveries, IInstrumentRepository instruments, ILogger logger, CancellationToken ct = default)
    {
        string? Flag(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }
        var file = Flag("--file"); var dir = Flag("--dir");
        var dryRun = args.Contains("--dry-run"); var allSymbols = args.Contains("--all-symbols");
        if (args.Contains("--file") && file is null || args.Contains("--dir") && dir is null) { Console.Error.WriteLine("--file/--dir need a path."); return 1; }
        if ((file is null) == (dir is null)) { Console.Error.WriteLine("Usage: import-delivery (--file <path.csv> | --dir <folder>) [--all-symbols] [--dry-run]"); return 1; }

        List<string> files;
        if (file is not null) files = new List<string> { file };
        else
        {
            if (!Directory.Exists(dir)) { Console.Error.WriteLine($"Directory not found: {dir}"); return 1; }
            files = Directory.GetFiles(dir!, "*.csv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) { Console.Error.WriteLine($"No .csv files in {dir}"); return 1; }
        }
        foreach (var f in files) if (!File.Exists(f)) { Console.Error.WriteLine($"File not found: {f}"); return 1; }

        ISet<string>? universe = null;
        if (!allSymbols) universe = (await instruments.GetActiveAsync(ct)).Select(i => i.Symbol.ToUpperInvariant()).ToHashSet();

        int imported = 0, rejectedTotal = 0, unusable = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var result = DeliveryCsvParser.Parse(await File.ReadAllTextAsync(f, ct), Path.GetFileName(f), universe);
            if (result.FatalError is not null) { Console.Error.WriteLine($"{Path.GetFileName(f)}: {result.FatalError}"); unusable++; continue; }

            if (!dryRun) await deliveries.UpsertRangeAsync(result.Records, ct);
            imported += result.Records.Count; rejectedTotal += result.Rejected.Count;
            Console.WriteLine($"{Path.GetFileName(f),-40} {result.Format,-11} rows {result.RowsRead,5}  imported {result.Records.Count,5}  non-EQ {result.SkippedOtherSeries,4}  no figure {result.SkippedNoDeliveryFigure,4}  outside universe {result.SkippedNotInUniverse,5}  rejected {result.Rejected.Count}");
            foreach (var r in result.Rejected.Take(5)) Console.WriteLine($"    REJECTED {r}");
        }

        Console.WriteLine($"{(dryRun ? "DRY RUN — nothing written. " : "")}Imported {imported:N0} delivery record(s) from {files.Count - unusable} of {files.Count} file(s); {rejectedTotal} row(s) rejected.");
        if (universe is not null) Console.WriteLine("Only symbols in the active universe were kept (use --all-symbols to keep every EQ symbol).");
        logger.LogInformation("import-delivery: {Imported} records, {Rejected} rejected, {Unusable} unusable file(s).", imported, rejectedTotal, unusable);
        return imported == 0 ? 1 : (rejectedTotal > 0 || unusable > 0 ? 2 : 0);
    }
}
