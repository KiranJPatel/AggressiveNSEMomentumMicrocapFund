namespace TradingPlatform.Tests.Framework;

/// <summary>
/// Hand-rolled stand-in for xunit's [Fact]. xunit/NUnit/MSTest are all
/// NuGet packages and this environment has no NuGet access (see
/// docs/gap-log.md), so the test framework itself is hand-rolled: a
/// reflection-based runner (see TestRunner.cs) discovers and invokes every
/// method tagged with this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute
{
    public string? Skip { get; init; }
}
