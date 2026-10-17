using System.Reflection;

namespace TradingPlatform.Tests.Framework;

/// <summary>
/// Reflection-based test runner: discovers every public method tagged
/// [Fact] on public classes with a public parameterless constructor,
/// invokes each (awaiting Task/Task&lt;T&gt; results), and reports a
/// pass/fail summary. Returns non-zero on any failure so `dotnet run`
/// (this project has no `dotnet test` support target — see docs/gap-log.md
/// on why xunit/MSTest are unavailable) fails the way a CI step expects.
/// </summary>
public static class TestRunner
{
    public static async Task<int> RunAllAsync(Assembly assembly)
    {
        var testMethods = assembly.GetTypes()
            .Where(t => t.IsClass && t.IsPublic && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
                .Select(m => (Type: t, Method: m)))
            .OrderBy(x => x.Type.FullName)
            .ThenBy(x => x.Method.Name)
            .ToList();

        Console.WriteLine($"Discovered {testMethods.Count} test(s) in {assembly.GetName().Name}.");
        Console.WriteLine();

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<(string Name, string Message)>();

        foreach (var (type, method) in testMethods)
        {
            var fullName = $"{type.Name}.{method.Name}";
            var attr = method.GetCustomAttribute<FactAttribute>()!;

            if (attr.Skip is not null)
            {
                Console.WriteLine($"  SKIP  {fullName}  ({attr.Skip})");
                skipped++;
                continue;
            }

            try
            {
                var instance = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException($"Could not construct {type.FullName}");

                var result = method.Invoke(instance, null);
                if (result is Task task) await task;

                Console.WriteLine($"  PASS  {fullName}");
                passed++;
            }
            catch (Exception ex)
            {
                var actual = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;
                Console.WriteLine($"  FAIL  {fullName}");
                Console.WriteLine($"        {actual!.GetType().Name}: {actual.Message}");
                failed++;
                failures.Add((fullName, actual.Message));
            }
        }

        Console.WriteLine();
        Console.WriteLine("========================================================");
        Console.WriteLine($" {passed} passed, {failed} failed, {skipped} skipped ({testMethods.Count} total)");
        Console.WriteLine("========================================================");

        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var (name, message) in failures)
                Console.WriteLine($"  - {name}: {message}");
        }

        return failed > 0 ? 1 : 0;
    }
}
