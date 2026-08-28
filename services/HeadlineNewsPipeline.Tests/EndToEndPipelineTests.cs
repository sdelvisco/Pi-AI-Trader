using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PiAiTrader.Intelligence;
using Xunit;

namespace PiAiTrader.HeadlineNewsPipeline.Tests
{
    /// <summary>
    /// End-to-end test wiring the *real* AlpacaNewsClient and the *real*
    /// AzureLlmClient/LlmSentimentModule together through PollCycleRunner —
    /// satisfying this session's explicit requirement to mock HTTP for both
    /// the Alpaca side and the underlying Azure calls, in one test that
    /// proves the whole pipeline works with zero real network calls, not
    /// just its individual pieces in isolation (PollCycleRunnerTests
    /// already covers orchestration logic against lightweight fakes;
    /// AlpacaNewsClientTests already covers the Alpaca HTTP parsing in
    /// depth). Env vars for both clients are saved/restored per test.
    /// </summary>
    public class EndToEndPipelineTests : IDisposable
    {
        private readonly string _savedAlpacaKeyId;
        private readonly string _savedAlpacaSecretKey;
        private readonly string _savedAzureEndpoint;
        private readonly string _savedAzureApiKey;
        private readonly string _savedAzureDeployment;
        private readonly string _tempDir;

        public EndToEndPipelineTests()
        {
            _savedAlpacaKeyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID");
            _savedAlpacaSecretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY");
            _savedAzureEndpoint = Environment.GetEnvironmentVariable("AZURE_LLM_ENDPOINT");
            _savedAzureApiKey = Environment.GetEnvironmentVariable("AZURE_LLM_API_KEY");
            _savedAzureDeployment = Environment.GetEnvironmentVariable("AZURE_LLM_DEPLOYMENT_NAME");

            Environment.SetEnvironmentVariable("ALPACA_KEY_ID", "test-key-id");
            Environment.SetEnvironmentVariable("ALPACA_SECRET_KEY", "test-secret-key");
            Environment.SetEnvironmentVariable("AZURE_LLM_ENDPOINT", "https://example-foundry.services.ai.azure.com");
            Environment.SetEnvironmentVariable("AZURE_LLM_API_KEY", "test-azure-key");
            Environment.SetEnvironmentVariable("AZURE_LLM_DEPLOYMENT_NAME", "test-deployment");

            _tempDir = Path.Combine(Path.GetTempPath(), "hnp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ALPACA_KEY_ID", _savedAlpacaKeyId);
            Environment.SetEnvironmentVariable("ALPACA_SECRET_KEY", _savedAlpacaSecretKey);
            Environment.SetEnvironmentVariable("AZURE_LLM_ENDPOINT", _savedAzureEndpoint);
            Environment.SetEnvironmentVariable("AZURE_LLM_API_KEY", _savedAzureApiKey);
            Environment.SetEnvironmentVariable("AZURE_LLM_DEPLOYMENT_NAME", _savedAzureDeployment);
            Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public async Task RunOnceAsync_RealAlpacaAndAzureClientsOverMockedHttp_ProducesExpectedSignals()
        {
            const string alpacaBody = @"{
                ""news"": [
                    { ""id"": 555, ""headline"": ""NVIDIA and Microsoft announce AI partnership"", ""created_at"": ""2026-08-28T14:00:00Z"", ""symbols"": [""NVDA"", ""MSFT"", ""TSLA""] }
                ],
                ""next_page_token"": null
            }";
            var alpacaHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(alpacaBody, Encoding.UTF8, "application/json")
            });

            var azureHandler = new FakeHttpMessageHandler(request =>
            {
                var requestBody = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var ticker = requestBody.Contains("NVDA") ? "NVDA" : "MSFT";
                var modelContent = JsonConvert.SerializeObject(new
                {
                    ticker,
                    sentiment_score = 0.6,
                    confidence = 0.8,
                    direction = "bullish",
                    rationale = "Positive partnership news.",
                });
                var envelope = "{\"choices\":[{\"message\":{\"content\":" +
                    JsonConvert.ToString(modelContent) + "}}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(envelope, Encoding.UTF8, "application/json")
                };
            });

            var stateStore = new HighWaterMarkStore(Path.Combine(_tempDir, "state.json"));
            var signalsPath = Path.Combine(_tempDir, "signals.jsonl");
            var signalWriter = new SignalWriter(signalsPath);
            var universe = new[] { "NVDA", "MSFT" }; // TSLA deliberately excluded

            using (var alpacaHttpClient = new HttpClient(alpacaHandler))
            using (var azureHttpClient = new HttpClient(azureHandler))
            using (var newsSource = new AlpacaNewsClient(alpacaHttpClient))
            using (var llmClient = new AzureLlmClient(azureHttpClient))
            {
                var intelligenceModule = new LlmSentimentModule(llmClient);
                var runner = new PollCycleRunner(
                    newsSource, intelligenceModule, stateStore, signalWriter, new FakePacer(), universe);

                await runner.RunOnceAsync(CancellationToken.None);
            }

            var lines = File.ReadAllLines(signalsPath);
            Assert.Equal(2, lines.Length);
            Assert.Contains(lines, l => l.Contains("\"Symbol\":\"NVDA\""));
            Assert.Contains(lines, l => l.Contains("\"Symbol\":\"MSFT\""));
            Assert.DoesNotContain(lines, l => l.Contains("TSLA"));

            Assert.Equal(555, stateStore.Load().LastProcessedId);
        }
    }
}
