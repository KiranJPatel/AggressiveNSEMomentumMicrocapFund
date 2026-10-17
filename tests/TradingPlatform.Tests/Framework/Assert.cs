namespace TradingPlatform.Tests.Framework;

/// <summary>Minimal hand-rolled assertion library covering what the Tranche 0-N test suites need.</summary>
public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new AssertionFailedException(message ?? "Expected condition to be true.");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) throw new AssertionFailedException(message ?? "Expected condition to be false.");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionFailedException(message ?? $"Expected <{expected}> but got <{actual}>.");
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new AssertionFailedException(message ?? $"Expected value to differ from <{notExpected}>.");
    }

    public static void NotNull(object? value, string? message = null)
    {
        if (value is null) throw new AssertionFailedException(message ?? "Expected non-null value.");
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null) throw new AssertionFailedException(message ?? $"Expected null but got <{value}>.");
    }

    /// <summary>Asserts the sequence has exactly one element and returns it, mirroring xUnit's Assert.Single.</summary>
    public static T Single<T>(IEnumerable<T> collection, string? message = null)
    {
        using var enumerator = collection.GetEnumerator();
        if (!enumerator.MoveNext())
            throw new AssertionFailedException(message ?? "Expected exactly one element but the sequence was empty.");
        var value = enumerator.Current;
        if (enumerator.MoveNext())
            throw new AssertionFailedException(message ?? "Expected exactly one element but the sequence had more than one.");
        return value;
    }

    public static void Empty<T>(IEnumerable<T> collection, string? message = null)
    {
        if (collection.Any())
            throw new AssertionFailedException(message ?? "Expected an empty sequence but it had elements.");
    }

    public static void InRange(decimal value, decimal low, decimal high, string? message = null)
    {
        if (value < low || value > high)
            throw new AssertionFailedException(message ?? $"Expected <{value}> to be within [{low}, {high}].");
    }

    public static void Throws<TException>(Action action, string? message = null) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionFailedException(message ?? $"Expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}");
        }
        throw new AssertionFailedException(message ?? $"Expected {typeof(TException).Name} but no exception was thrown.");
    }
}
