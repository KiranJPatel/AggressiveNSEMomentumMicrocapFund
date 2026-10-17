using System.Globalization;

namespace TradingPlatform.Application.Cli;

/// <summary>Parser for `optimize`'s sweep flags. Separate from the command so every malformed input is unit-testable and nothing is silently ignored.</summary>
internal static class OptimizeArguments
{
    internal sealed record Sweep(string Name, IReadOnlyList<decimal> Values);

    internal sealed record Result(IReadOnlyList<Sweep> Parameters, int MaxCombinations, int Top, string? Error);

    private const int DefaultMaxCombinations = 200;
    private const int DefaultTop = 15;

    internal static Result Parse(string[] args)
    {
        var sweeps = new List<Sweep>();
        var maxCombinations = DefaultMaxCombinations;
        var top = DefaultTop;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--param":
                {
                    if (i + 1 >= args.Length) return Fail("--param needs a value like Name=v1,v2,v3.");
                    var spec = args[++i];
                    var eq = spec.IndexOf('=');
                    if (eq <= 0 || eq == spec.Length - 1) return Fail($"--param '{spec}' is not in Name=v1,v2,v3 form.");
                    var name = spec[..eq].Trim();
                    var def = Backtesting.Selection.OptimizerParameterCatalog.Find(name);
                    if (def is null)
                        return Fail($"Unknown parameter '{name}'. Run 'optimize --list-params' for the sweepable names.");
                    if (sweeps.Any(s => s.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase))) return Fail($"Parameter '{def.Name}' was given more than once.");

                    var values = new List<decimal>();
                    foreach (var token in spec[(eq + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                            return Fail($"--param {def.Name}: '{token}' is not a number.");
                        values.Add(v);
                    }
                    if (values.Count == 0) return Fail($"--param {def.Name} has no values.");
                    sweeps.Add(new Sweep(def.Name, values.Distinct().OrderBy(v => v).ToList()));
                    break;
                }
                case "--max-combinations":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxCombinations) || maxCombinations < 1)
                        return Fail("--max-combinations needs a positive integer.");
                    break;
                case "--top":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out top) || top < 1)
                        return Fail("--top needs a positive integer.");
                    break;
            }
        }
        return new Result(sweeps, maxCombinations, top, null);
    }

    private static Result Fail(string message) => new(Array.Empty<Sweep>(), DefaultMaxCombinations, DefaultTop, message);
}
