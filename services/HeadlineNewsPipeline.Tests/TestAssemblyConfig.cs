using Xunit;

// AlpacaNewsClientTests manipulates process-global state (ALPACA_KEY_ID/
// ALPACA_SECRET_KEY env vars) and restores it via IDisposable teardown
// after each test — only safe if tests don't run concurrently with each
// other, so parallelization is disabled for this whole (small) assembly,
// matching Intelligence.Tests' own TestAssemblyConfig.cs.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
