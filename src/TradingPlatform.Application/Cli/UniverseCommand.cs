using System.Globalization;
using System.Text.Json;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Application.Cli;

/// <summary>Everything `universe` needs, bundled so the command is testable without the whole host.</summary>
public sealed record UniverseServices(
    SqliteConnection Db, IInstrumentRepository Instruments, IUniverseClassificationRepository Classifications, ISurveillanceRepository Surveillance,
    SqliteFundamentalRepository Fundamentals, SqliteInstitutionalActivityRepository Institutional, IHistoricalBarRepository Bars,
    IEngineStateRepository State, ScannerOptions Scanner);

/// <summary>
/// `dotnet run -- universe <action>` — the ONE place the stock universe is managed in SQLite: details, market-cap classification and
/// surveillance (ASM/GSM), plus the bulk loaders for fundamentals / institutional activity. (These used to be separate `import --kind`
/// invocations; `import` still works as an alias.)
///
///   universe import       --file universe.csv [--kind universe|marketcap|classification|surveillance|fundamentals|institutional]
///                         [--on-duplicate first|last|error] [--deactivate-missing] [--dry-run]
///   universe list         [--bucket Small] [--inactive|--all] [--search text] [--limit N]
///   universe show         --symbol X
///   universe stats
///   universe activate|deactivate   --symbol X | --symbols A,B
///   universe classify     --symbol X --bucket Large|Mid|Small|Micro [--rank N] [--sector S] [--from yyyy-MM-dd]
///                         | --file classification.csv
///                         | --auto --large-min N --mid-min N --small-min N [--from yyyy-MM-dd]      (₹ crore thresholds, no defaults)
///   universe surveillance --symbol X --flags ASM|GSM|none [--as-of yyyy-MM-dd]
///                         | --file surveillance.csv
///                         | [--asm-file f] [--gsm-file f] [--t2t-file f] [--as-of d] [--assume-unlisted-clear]
///                         | --clear-all [--as-of d]
/// </summary>
public static class UniverseCommand
{
    public static async Task<int> RunAsync(string[] args, UniverseServices s, Microsoft.Extensions.Logging.ILogger logger, TextWriter? output = null, TextWriter? error = null, DateOnly? today = null, CancellationToken ct = default)
    {
        var o = output ?? Console.Out; var e = error ?? Console.Error;
        // "Today" is the MARKET's date (IST): a status recorded at 7am IST must be dated today even though UTC is still yesterday.
        var todayDate = today ?? RealtimeBarOverlay.MarketDate(DateTimeOffset.UtcNow, s.Scanner);
        var action = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        var rest = args.Skip(2).ToArray();
        try
        {
            return action switch
            {
                "import" => await ImportAsync(rest, s, logger, e, ct),
                "list" => await ListAsync(rest, s, o, e, todayDate, ct),
                "show" => await ShowAsync(rest, s, o, e, todayDate, ct),
                "stats" => await StatsAsync(s, o, todayDate, ct),
                "activate" => await SetActiveAsync(rest, s, true, o, e, ct),
                "deactivate" => await SetActiveAsync(rest, s, false, o, e, ct),
                "classify" => await ClassifyAsync(rest, s, logger, o, e, todayDate, ct),
                "surveillance" => await SurveillanceAsync(rest, s, logger, o, e, todayDate, ct),
                _ => Usage(e, action),
            };
        }
        catch (FormatException ex) { e.WriteLine(ex.Message); return 1; }
    }

    private static int Usage(TextWriter e, string action)
    {
        if (action.Length > 0) e.WriteLine($"Unknown universe action '{action}'.");
        e.WriteLine("Usage: universe import|list|show|stats|activate|deactivate|classify|surveillance  (see docs/data-import.md)");
        return 1;
    }

    // ------------------------------------------------------------------ helpers
    private static string? Flag(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[i + 1] : null; }
    private static bool Has(string[] a, string name) => a.Contains(name);

    private static DateOnly DateFlag(string[] a, string name, DateOnly fallback)
    {
        if (Has(a, name) && Flag(a, name) is null) throw new FormatException($"{name} requires a value (yyyy-MM-dd).");
        var v = Flag(a, name);
        if (v is null) return fallback;
        return DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new FormatException($"{name} '{v}' must be yyyy-MM-dd.");
    }

    private static IReadOnlyList<string> SymbolsFrom(string[] a)
    {
        var list = new List<string>();
        if (Flag(a, "--symbol") is { } one) list.Add(one);
        if (Flag(a, "--symbols") is { } many) list.AddRange(many.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return list.Select(x => x.ToUpperInvariant()).Distinct().ToList();
    }

    private static string Money(decimal? v) => v is null ? "—" : v.Value.ToString("N0", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ import (delegates to the all-or-nothing validated importer)
    private static async Task<int> ImportAsync(string[] rest, UniverseServices s, Microsoft.Extensions.Logging.ILogger logger, TextWriter e, CancellationToken ct)
    {
        var args = rest.ToList();
        if (!args.Contains("--kind")) { args.Insert(0, "universe"); args.Insert(0, "--kind"); }
        return await ImportCommand.RunAsync(args.ToArray(), s.Db, s.Instruments, s.Classifications, s.Surveillance, s.Fundamentals, s.Institutional, logger, ct);
    }

    // ------------------------------------------------------------------ list / show / stats
    private static async Task<int> ListAsync(string[] a, UniverseServices s, TextWriter o, TextWriter e, DateOnly today, CancellationToken ct)
    {
        MarketCapBucket? bucketFilter = null;
        if (Flag(a, "--bucket") is { } b) { if (!Enum.TryParse<MarketCapBucket>(b, true, out var bk) || !Enum.IsDefined(bk)) { e.WriteLine($"--bucket '{b}' must be Large|Mid|Small|Micro|Unknown."); return 1; } bucketFilter = bk; }
        var limit = 50;
        if (Flag(a, "--limit") is { } l && (!int.TryParse(l, out limit) || limit < 1)) { e.WriteLine($"--limit '{l}' must be a positive integer."); return 1; }
        var search = Flag(a, "--search");

        var all = await s.Instruments.GetAllAsync(ct);
        var coverage = (await s.Bars.GetCoverageAsync(Timeframe.Daily, ct)).ToDictionary(c => c.Symbol, StringComparer.OrdinalIgnoreCase);
        var rows = new List<(Instrument I, MarketCapBucket Bucket, BarCoverage? Cov)>();
        foreach (var i in all)
        {
            if (Has(a, "--inactive") ? i.Active : (!Has(a, "--all") && !i.Active)) continue;
            if (search is not null && !new[] { i.Symbol, i.CompanyName, i.Industry ?? "", i.Sector ?? "" }.Any(f => f.Contains(search, StringComparison.OrdinalIgnoreCase))) continue;
            var cls = await s.Classifications.GetClassificationAsync(i.Symbol, today, ct);
            var bucket = cls?.MarketCapBucket ?? MarketCapBucket.Unknown;
            if (bucketFilter is { } want && bucket != want) continue;
            coverage.TryGetValue(i.Symbol, out var cov);
            rows.Add((i, bucket, cov));
        }
        o.WriteLine($"{"Symbol",-14}{"Act",-4}{"Bucket",-9}{"Industry",-28}{"MarketCap",14}{"AvgTurnover",16}  Bars (last)");
        foreach (var (i, bucket, cov) in rows.Take(limit))
            o.WriteLine($"{i.Symbol,-14}{(i.Active ? "yes" : "no"),-4}{bucket,-9}{Trunc(i.Industry ?? i.Sector ?? "—", 26),-28}{Money(i.MarketCap),14}{Money(i.AverageDailyTurnover),16}  {(cov is null ? "none" : $"{cov.BarCount} ({cov.LastDate:yyyy-MM-dd})")}");
        o.WriteLine($"{Math.Min(limit, rows.Count)} of {rows.Count} matching instrument(s) shown.");
        return 0;
    }

    private static string Trunc(string v, int n) => v.Length <= n ? v : v[..(n - 1)] + "…";

    private static async Task<int> ShowAsync(string[] a, UniverseServices s, TextWriter o, TextWriter e, DateOnly today, CancellationToken ct)
    {
        var sym = Flag(a, "--symbol")?.ToUpperInvariant();
        if (sym is null) { e.WriteLine("universe show requires --symbol."); return 1; }
        var i = await s.Instruments.GetBySymbolAsync(sym, ct);
        if (i is null) { e.WriteLine($"{sym} is not in the universe."); return 1; }
        o.WriteLine($"{i.Symbol} — {i.CompanyName}   [{(i.Active ? "active" : "INACTIVE")}]");
        o.WriteLine($"  NSE {i.NseSymbol}   Yahoo {i.YahooSymbol}   ISIN {i.Isin ?? "—"}   Exchange {i.Exchange}");
        o.WriteLine($"  Sector {i.Sector ?? "—"}   Industry {i.Industry ?? "—"}   MarketCap {Money(i.MarketCap)}   AvgDailyTurnover {Money(i.AverageDailyTurnover)}");
        var hist = await s.Classifications.GetHistoryAsync(sym, ct);
        o.WriteLine(hist.Count == 0 ? "  Classification: none (Unknown)" : "  Classification history:");
        foreach (var h in hist) o.WriteLine($"    {h.EffectiveFrom:yyyy-MM-dd}..{(h.EffectiveTo is { } t ? t.ToString("yyyy-MM-dd") : "open"),-10} {h.MarketCapBucket}{(h.MarketCapRank is { } r ? $" (rank {r})" : "")}{(h.Sector is { } sec ? $"  {sec}" : "")}");
        var surv = await s.Surveillance.GetMostRecentAsync(sym, ct);
        var current = await s.Surveillance.GetLatestStatusAsync(sym, today, s.Scanner.SurveillanceMaxStalenessDays, ct);
        o.WriteLine(surv is null ? "  Surveillance: never recorded (Unknown — fails closed)"
            : $"  Surveillance: latest {surv.AsOfDate:yyyy-MM-dd} = {(surv.Flags == RiskFlags.None ? "clear" : surv.Flags.ToString())} ({surv.Source}); status for today: {current.Availability}");
        var cov = (await s.Bars.GetCoverageAsync(Timeframe.Daily, ct)).FirstOrDefault(c => c.Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase));
        o.WriteLine(cov is null ? "  Bars: none cached — run `download`" : $"  Bars: {cov.BarCount} daily bars {cov.FirstDate:yyyy-MM-dd}..{cov.LastDate:yyyy-MM-dd}");
        return 0;
    }

    private static async Task<int> StatsAsync(UniverseServices s, TextWriter o, DateOnly today, CancellationToken ct)
    {
        var all = await s.Instruments.GetAllAsync(ct);
        var active = all.Where(i => i.Active).ToList();
        var coverage = (await s.Bars.GetCoverageAsync(Timeframe.Daily, ct)).ToDictionary(c => c.Symbol, StringComparer.OrdinalIgnoreCase);
        o.WriteLine($"Instruments: {all.Count} total, {active.Count} active, {all.Count - active.Count} inactive");
        o.WriteLine($"  with Sector {active.Count(i => i.Sector is not null)}, with Industry {active.Count(i => i.Industry is not null)}, with ISIN {active.Count(i => i.Isin is not null)}, with MarketCap {active.Count(i => i.MarketCap is > 0)}, with turnover {active.Count(i => i.AverageDailyTurnover is > 0)}");

        var buckets = new Dictionary<MarketCapBucket, int>();
        int unsurv = 0, avail = 0;
        foreach (var i in active)
        {
            var b = (await s.Classifications.GetClassificationAsync(i.Symbol, today, ct))?.MarketCapBucket ?? MarketCapBucket.Unknown;
            buckets[b] = buckets.GetValueOrDefault(b) + 1;
            var st = await s.Surveillance.GetLatestStatusAsync(i.Symbol, today, s.Scanner.SurveillanceMaxStalenessDays, ct);
            if (st.Availability == DataAvailability.Unknown) unsurv++; else avail++;
        }
        o.WriteLine("Classification today: " + string.Join(", ", Enum.GetValues<MarketCapBucket>().Select(b => $"{b} {buckets.GetValueOrDefault(b)}")));
        o.WriteLine($"Surveillance (window {s.Scanner.SurveillanceMaxStalenessDays}d): {avail} known, {unsurv} UNKNOWN{(unsurv > 0 ? "  <- these are rejected by the scanner until a status is recorded" : "")}");

        var withBars = active.Count(i => coverage.ContainsKey(i.Symbol));
        var lastDates = active.Where(i => coverage.ContainsKey(i.Symbol)).Select(i => coverage[i.Symbol].LastDate).ToList();
        o.WriteLine($"Bars: {withBars} of {active.Count} active symbols cached" + (lastDates.Count > 0 ? $"; newest bar {lastDates.Max():yyyy-MM-dd}, {lastDates.Count(d => d < lastDates.Max().AddDays(-5))} stale (>5d behind)" : ""));

        var last = await s.State.GetAsync(DownloadService.LastDownloadStateKey, ct);
        if (last is not null)
        {
            var d = JsonSerializer.Deserialize<DownloadSummary>(last);
            if (d is not null) o.WriteLine($"Last download: {d.StartedAt:yyyy-MM-dd HH:mm}Z — {d.Downloaded} downloaded, {d.UpToDate} up to date, {d.Failed} failed{(d.Aborted ? " (ABORTED)" : "")}");
        }
        else o.WriteLine("Last download: never — run `download`");
        return 0;
    }

    private static async Task<int> SetActiveAsync(string[] a, UniverseServices s, bool active, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var syms = SymbolsFrom(a);
        if (syms.Count == 0) { e.WriteLine($"universe {(active ? "activate" : "deactivate")} requires --symbol X or --symbols A,B."); return 1; }
        var missing = new List<string>();
        foreach (var sym in syms) if (!await s.Instruments.SetActiveAsync(sym, active, ct)) missing.Add(sym);
        o.WriteLine($"{syms.Count - missing.Count} instrument(s) {(active ? "activated" : "deactivated")}.");
        if (missing.Count > 0) { e.WriteLine($"Not in the universe: {string.Join(", ", missing)}"); return 1; }
        return 0;
    }

    // ------------------------------------------------------------------ classify
    private static async Task<int> ClassifyAsync(string[] a, UniverseServices s, Microsoft.Extensions.Logging.ILogger logger, TextWriter o, TextWriter e, DateOnly today, CancellationToken ct)
    {
        if (Flag(a, "--file") is { } file) return await ImportCommand.RunAsync(new[] { "--kind", "classification", "--file", file }.Concat(a.Where(x => x == "--dry-run")).ToArray(), s.Db, s.Instruments, s.Classifications, s.Surveillance, s.Fundamentals, s.Institutional, logger, ct);

        var from = DateFlag(a, "--from", today);
        if (Has(a, "--auto"))
        {
            // Absolute ₹-crore cut-offs, REQUIRED: AMFI revises them semi-annually, so a baked-in default would silently go stale — and ranking
            // within this (partial) universe instead would label everything "Large". MarketCap must be in ₹ crore.
            decimal? Cut(string name) { var v = Flag(a, name); if (v is null) return null; if (!decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0) throw new FormatException($"{name} '{v}' must be a positive number (₹ crore)."); return d; }
            var large = Cut("--large-min"); var mid = Cut("--mid-min"); var small = Cut("--small-min");
            if (large is null || mid is null || small is null) { e.WriteLine("universe classify --auto requires --large-min, --mid-min and --small-min (market cap in ₹ crore; use the current AMFI cut-offs). Below --small-min = Micro."); return 1; }
            if (!(large > mid && mid > small)) { e.WriteLine("Cut-offs must satisfy --large-min > --mid-min > --small-min."); return 1; }

            var counts = new Dictionary<MarketCapBucket, int>(); int skipped = 0, written = 0;
            using var tx = s.Db.BeginTransaction();
            try
            {
                foreach (var i in await s.Instruments.GetActiveAsync(ct))
                {
                    if (i.MarketCap is not > 0) { skipped++; continue; }
                    var bucket = i.MarketCap >= large ? MarketCapBucket.Large : i.MarketCap >= mid ? MarketCapBucket.Mid : i.MarketCap >= small ? MarketCapBucket.Small : MarketCapBucket.Micro;
                    await WriteClassificationAsync(s, i, bucket, null, i.Sector, from, ct);
                    counts[bucket] = counts.GetValueOrDefault(bucket) + 1; written++;
                }
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
            o.WriteLine($"Classified {written} instrument(s) effective {from:yyyy-MM-dd}: " + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
            if (skipped > 0) o.WriteLine($"{skipped} active instrument(s) have NO market cap and were left unclassified (Unknown). Load market caps with: universe import --kind marketcap --file <Symbol,MarketCap csv>");
            return 0;
        }

        var sym = Flag(a, "--symbol")?.ToUpperInvariant();
        var bucketText = Flag(a, "--bucket");
        if (sym is null || bucketText is null) { e.WriteLine("universe classify needs --symbol X --bucket Large|Mid|Small|Micro, or --file, or --auto (see --help)."); return 1; }
        if (!Enum.TryParse<MarketCapBucket>(bucketText, true, out var bkt) || !Enum.IsDefined(bkt)) { e.WriteLine($"--bucket '{bucketText}' must be Large|Mid|Small|Micro|Unknown."); return 1; }
        int? rank = null;
        if (Flag(a, "--rank") is { } r) { if (!int.TryParse(r, out var ri) || ri <= 0) { e.WriteLine($"--rank '{r}' must be a positive integer."); return 1; } rank = ri; }
        var inst = await s.Instruments.GetBySymbolAsync(sym, ct);
        if (inst is null) { e.WriteLine($"{sym} is not in the universe — `universe import` it first."); return 1; }
        await WriteClassificationAsync(s, inst, bkt, rank, Flag(a, "--sector") ?? inst.Sector, from, ct);
        o.WriteLine($"{sym}: {bkt} effective {from:yyyy-MM-dd}.");
        return 0;
    }

    private static async Task WriteClassificationAsync(UniverseServices s, Instrument i, MarketCapBucket bucket, int? rank, string? sector, DateOnly from, CancellationToken ct)
    {
        s.Db.Execute("DELETE FROM UniverseClassificationSnapshots WHERE Symbol = ? AND EffectiveFrom = ?;", i.Symbol, from); // idempotent per effective date
        await s.Classifications.UpsertAsync(new UniverseClassificationSnapshot
        {
            InstrumentId = i.Id, Symbol = i.Symbol, MarketCapBucket = bucket, MarketCapRank = rank, Sector = sector, EffectiveFrom = from, IsUnknown = bucket == MarketCapBucket.Unknown,
        }, ct);
    }

    // ------------------------------------------------------------------ surveillance
    private static async Task<int> SurveillanceAsync(string[] a, UniverseServices s, Microsoft.Extensions.Logging.ILogger logger, TextWriter o, TextWriter e, DateOnly today, CancellationToken ct)
    {
        if (Flag(a, "--file") is { } file) return await ImportCommand.RunAsync(new[] { "--kind", "surveillance", "--file", file }.Concat(a.Where(x => x == "--dry-run")).ToArray(), s.Db, s.Instruments, s.Classifications, s.Surveillance, s.Fundamentals, s.Institutional, logger, ct);

        var asOf = DateFlag(a, "--as-of", today);
        if (asOf > today) { e.WriteLine($"--as-of {asOf:yyyy-MM-dd} is in the future."); return 1; }
        var source = Flag(a, "--source") ?? "universe-cli";
        var active = (await s.Instruments.GetActiveAsync(ct)).Select(i => i.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Single symbol.
        if (Flag(a, "--symbol") is { } one)
        {
            var flagsText = Flag(a, "--flags");
            if (flagsText is null) { e.WriteLine("universe surveillance --symbol X needs --flags ASM|GSM|TradeToTrade|…|none."); return 1; }
            if (!TryFlags(flagsText, out var flags, out var bad)) { e.WriteLine($"unknown flag '{bad}' (valid: {string.Join("|", Enum.GetNames<RiskFlags>().Where(n => n != "None"))}|none)."); return 1; }
            if (await s.Instruments.GetBySymbolAsync(one, ct) is null) { e.WriteLine($"{one.ToUpperInvariant()} is not in the universe."); return 1; }
            await s.Surveillance.UpsertAsync(new SurveillanceStatus { Symbol = one.ToUpperInvariant(), AsOfDate = asOf, Flags = flags, Availability = DataAvailability.Available, Source = source }, ct);
            o.WriteLine($"{one.ToUpperInvariant()}: {(flags == RiskFlags.None ? "clear" : flags.ToString())} as of {asOf:yyyy-MM-dd}.");
            return 0;
        }

        // Whole-universe forms. Every form that marks symbols CLEAR is an explicit assertion by the operator — never a default.
        var flagged = new Dictionary<string, RiskFlags>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var (opt, flag) in new[] { ("--asm-file", RiskFlags.Asm), ("--gsm-file", RiskFlags.Gsm), ("--t2t-file", RiskFlags.TradeToTrade) })
        {
            if (Has(a, opt) && Flag(a, opt) is null) { e.WriteLine($"{opt} requires a file path."); return 1; }
            if (Flag(a, opt) is not { } path) continue;
            if (!File.Exists(path)) { e.WriteLine($"File not found: {path}"); return 1; }
            foreach (var sym in ReadSymbols(path))
            {
                if (!active.Contains(sym)) { warnings.Add($"{sym} in {Path.GetFileName(path)} is not an active universe symbol (ignored)."); continue; }
                flagged[sym] = flagged.GetValueOrDefault(sym) | flag;
            }
        }
        var clearAll = Has(a, "--clear-all");
        var assumeClear = Has(a, "--assume-unlisted-clear");
        if (flagged.Count == 0 && !clearAll) { e.WriteLine("Nothing to do: give --symbol X --flags …, --file, --asm-file/--gsm-file/--t2t-file [--assume-unlisted-clear], or --clear-all."); return 1; }
        if (clearAll && flagged.Count > 0) { e.WriteLine("--clear-all cannot be combined with list files (it would contradict them)."); return 1; }
        if (flagged.Count > 0 && !assumeClear) o.WriteLine("Note: only the LISTED symbols were recorded; the rest stay Unknown (and are rejected) — add --assume-unlisted-clear if the lists are complete.");

        var rows = new List<SurveillanceStatus>();
        foreach (var (sym, f) in flagged) rows.Add(new SurveillanceStatus { Symbol = sym.ToUpperInvariant(), AsOfDate = asOf, Flags = f, Availability = DataAvailability.Available, Source = source });
        if (clearAll || assumeClear)
            foreach (var sym in active.Where(x => !flagged.ContainsKey(x)))
                rows.Add(new SurveillanceStatus { Symbol = sym.ToUpperInvariant(), AsOfDate = asOf, Flags = RiskFlags.None, Availability = DataAvailability.Available, Source = source });

        using var tx = s.Db.BeginTransaction();
        try { foreach (var r in rows) await s.Surveillance.UpsertAsync(r, ct); tx.Commit(); } catch { tx.Rollback(); throw; }
        foreach (var w in warnings.Take(20)) e.WriteLine("  warning: " + w);
        o.WriteLine($"Surveillance as of {asOf:yyyy-MM-dd}: {flagged.Count} flagged, {rows.Count - flagged.Count} marked clear, {warnings.Count} warning(s).");
        return 0;
    }

    /// <summary>Reads symbols from either a CSV with a Symbol column (the NSE list exports) or a plain one-symbol-per-line file.</summary>
    internal static IReadOnlyList<string> ReadSymbols(string path)
    {
        var text = File.ReadAllText(path);
        var lines = text.Split('\n').Select(l => l.Trim().TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return Array.Empty<string>();
        if (lines[0].Contains(','))
        {
            var table = CsvTable.Parse(text);
            var col = table.Headers.FirstOrDefault(h => h.Equals("Symbol", StringComparison.OrdinalIgnoreCase)) ?? throw new FormatException($"{Path.GetFileName(path)} has commas but no 'Symbol' column.");
            return table.Rows.Select(r => r.Get(col)).Where(x => x is not null).Select(x => x!.ToUpperInvariant()).Distinct().ToList();
        }
        var skipHeader = lines[0].Equals("symbol", StringComparison.OrdinalIgnoreCase);
        return lines.Skip(skipHeader ? 1 : 0).Select(l => l.ToUpperInvariant()).Distinct().ToList();
    }

    private static bool TryFlags(string raw, out RiskFlags flags, out string? bad)
    {
        flags = RiskFlags.None; bad = null;
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase) || raw.Equals("clear", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var part in raw.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<RiskFlags>(part, true, out var f) || !Enum.IsDefined(f) || f == RiskFlags.None) { bad = part; return false; }
            flags |= f;
        }
        return true;
    }
}
