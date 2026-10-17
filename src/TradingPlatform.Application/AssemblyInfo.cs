using System.Runtime.CompilerServices;

// TradingPlatform.Application has no test project of its own (it's the CLI
// entry point / composition root); its argument-parsing helpers are exposed
// as `internal` rather than `public` (they're not part of any external API)
// and made visible here specifically so TradingPlatform.Tests can unit-test
// them directly, rather than only through a full RunAsync integration test
// that would need 10+ repository mocks per command.
[assembly: InternalsVisibleTo("TradingPlatform.Tests")]
