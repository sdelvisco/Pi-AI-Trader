using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Live smoke test — makes ONE real call to the real Azure AI Foundry
    /// endpoint (real network traffic, real inference cost). Tagged
    /// [Trait("Category", "LiveSmoke")] so it is explicitly excluded from
    /// the normal test run and never runs automatically or in CI:
    ///
    ///   Normal/CI run (never hits the network, never costs money):
    ///     dotnet test --filter Category!=LiveSmoke
    ///
    ///   Deliberately run this one test by hand:
    ///     dotnet test --filter Category=LiveSmoke
    ///
    /// AzureLlmClient reads its three required env vars directly from the
    /// process environment (see its own class comment) — it does not read
    /// any file itself. Before running this test manually, populate
    /// AZURE_LLM_ENDPOINT / AZURE_LLM_API_KEY / AZURE_LLM_DEPLOYMENT_NAME in
    /// your shell, e.g. on the Pi (or a dev machine with an equivalent
    /// local file):
    ///     set -a; source /etc/tradingpi/azure.env; set +a
    ///     dotnet test --filter Category=LiveSmoke
    /// </summary>
    public class AzureLlmClientLiveSmokeTest
    {
        [Fact]
        [Trait("Category", "LiveSmoke")]
        public async Task GenerateSignalAsync_RealAzureEndpoint_ProducesSignalForAppleEarningsHeadline()
        {
            using (var azureClient = new AzureLlmClient())
            {
                var module = new LlmSentimentModule(azureClient);
                var request = new SignalRequest
                {
                    Symbol = "AAPL",
                    InputText = "Apple beats Q3 earnings expectations",
                    AsOfUtc = DateTime.UtcNow
                };

                var signal = await module.GenerateSignalAsync(request, CancellationToken.None);

                // Printed for manual inspection, per this session's spec —
                // this test's real point is the human reading this output,
                // not the (deliberately loose) assertions below.
                Console.WriteLine("=== AzureLlmClient live smoke test result ===");
                Console.WriteLine($"Symbol:       {signal.Symbol}");
                Console.WriteLine($"Direction:    {signal.Direction}");
                Console.WriteLine($"RawScore:     {signal.RawScore}");
                Console.WriteLine($"Confidence:   {signal.Confidence}");
                Console.WriteLine($"SourceWeight: {signal.SourceWeight}");
                Console.WriteLine($"SourceModule: {signal.SourceModule}");
                Console.WriteLine($"TimestampUtc: {signal.TimestampUtc:o}");
                Console.WriteLine($"Rationale:    {signal.Rationale}");

                Assert.NotNull(signal);
                Assert.Equal("AAPL", signal.Symbol);
            }
        }
    }
}
