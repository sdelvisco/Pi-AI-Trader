using Xunit;

// A handful of these tests manipulate process-global state (env vars in
// AzureLlmClientTests, QuantConnect.Logging.Log.LogHandler in
// LlmSentimentModuleTests) and restore it via IDisposable teardown after
// each test. That save/restore is only safe if tests don't run
// concurrently with each other, so parallelization is disabled for this
// whole (small) assembly rather than reasoning about per-class isolation.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
