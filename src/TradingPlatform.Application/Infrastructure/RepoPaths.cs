namespace TradingPlatform.Application.Infrastructure;

/// <summary>
/// Resolves the repository root by walking up from the executing assembly's
/// directory until TradingPlatform.sln is found. This lets the tool be run
/// via `dotnet run`, a published exe, or from any working directory and
/// still find /config, /data, /logs, /reports, /backtests, /exports (spec §7.5)
/// relative to the repo root rather than the current directory.
/// </summary>
public static class RepoPaths
{
    public static string RepoRoot { get; } = Resolve();

    private static string Resolve()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TradingPlatform.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        // Fallback: current directory (e.g. a future standalone deployment
        // without the .sln present). Callers still get a stable, absolute path.
        return Directory.GetCurrentDirectory();
    }

    public static string Combine(string relative) => Path.Combine(RepoRoot, relative);
}
