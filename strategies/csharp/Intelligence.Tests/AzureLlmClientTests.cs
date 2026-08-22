using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Unit tests for AzureLlmClient. Every HTTP call is routed through
    /// FakeHttpMessageHandler — no real network call is ever made here, per
    /// this session's explicit requirement.
    ///
    /// AzureLlmClient reads its three env vars at construction time, so
    /// each test saves/restores the process's existing values in the
    /// constructor/Dispose (xUnit instantiates a fresh test class per test
    /// method and calls Dispose after each one), rather than leaking
    /// test-only env var values into any other test that happens to run in
    /// this process afterward.
    /// </summary>
    public class AzureLlmClientTests : IDisposable
    {
        private const string EndpointVar = "AZURE_LLM_ENDPOINT";
        private const string ApiKeyVar = "AZURE_LLM_API_KEY";
        private const string DeploymentVar = "AZURE_LLM_DEPLOYMENT_NAME";

        private readonly string _savedEndpoint;
        private readonly string _savedApiKey;
        private readonly string _savedDeployment;

        public AzureLlmClientTests()
        {
            _savedEndpoint = Environment.GetEnvironmentVariable(EndpointVar);
            _savedApiKey = Environment.GetEnvironmentVariable(ApiKeyVar);
            _savedDeployment = Environment.GetEnvironmentVariable(DeploymentVar);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EndpointVar, _savedEndpoint);
            Environment.SetEnvironmentVariable(ApiKeyVar, _savedApiKey);
            Environment.SetEnvironmentVariable(DeploymentVar, _savedDeployment);
        }

        private static void SetAllEnvVars(
            string endpoint = "https://example-foundry.services.ai.azure.com",
            string apiKey = "test-api-key",
            string deployment = "Llama-4-Scout-17B-16E-Instruct")
        {
            Environment.SetEnvironmentVariable(EndpointVar, endpoint);
            Environment.SetEnvironmentVariable(ApiKeyVar, apiKey);
            Environment.SetEnvironmentVariable(DeploymentVar, deployment);
        }

        [Fact]
        public async Task CompleteJsonAsync_SuccessfulRoundTrip_ReturnsMessageContentAndSendsExpectedRequest()
        {
            SetAllEnvVars();

            const string modelContent = "{\"ticker\":\"AAPL\",\"sentiment_score\":0.5}";
            var responseJson = "{\"choices\":[{\"message\":{\"content\":" +
                Newtonsoft.Json.JsonConvert.ToString(modelContent) + "}}]}";

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AzureLlmClient(httpClient))
            {
                var result = await client.CompleteJsonAsync("system prompt", "user prompt", CancellationToken.None);

                Assert.Equal(modelContent, result);

                // Auth: Bearer <api key>, per Azure AI Foundry's v1 API
                // surface (see AzureLlmClient's class comment / DEVIATIONS.md).
                Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization.Scheme);
                Assert.Equal("test-api-key", handler.LastRequest.Headers.Authorization.Parameter);

                Assert.Equal(
                    "https://example-foundry.services.ai.azure.com/openai/v1/chat/completions",
                    handler.LastRequest.RequestUri.ToString());

                // Request body carries the deployment name as "model", and
                // both prompts as separate chat messages.
                Assert.Contains("\"model\":\"Llama-4-Scout-17B-16E-Instruct\"", handler.LastRequestBody);
                Assert.Contains("\"role\":\"system\"", handler.LastRequestBody);
                Assert.Contains("system prompt", handler.LastRequestBody);
                Assert.Contains("\"role\":\"user\"", handler.LastRequestBody);
                Assert.Contains("user prompt", handler.LastRequestBody);
            }
        }

        [Fact]
        public void Constructor_MissingEndpoint_ThrowsLlmConfigurationException()
        {
            Environment.SetEnvironmentVariable(EndpointVar, null);
            Environment.SetEnvironmentVariable(ApiKeyVar, "key");
            Environment.SetEnvironmentVariable(DeploymentVar, "deployment");

            var ex = Assert.Throws<LlmConfigurationException>(() => new AzureLlmClient());
            Assert.Contains(EndpointVar, ex.Message);
        }

        [Fact]
        public void Constructor_MissingApiKey_ThrowsLlmConfigurationException()
        {
            Environment.SetEnvironmentVariable(EndpointVar, "https://example.azure.com");
            Environment.SetEnvironmentVariable(ApiKeyVar, "");
            Environment.SetEnvironmentVariable(DeploymentVar, "deployment");

            var ex = Assert.Throws<LlmConfigurationException>(() => new AzureLlmClient());
            Assert.Contains(ApiKeyVar, ex.Message);
        }

        [Fact]
        public void Constructor_MissingDeploymentName_ThrowsLlmConfigurationException()
        {
            Environment.SetEnvironmentVariable(EndpointVar, "https://example.azure.com");
            Environment.SetEnvironmentVariable(ApiKeyVar, "key");
            Environment.SetEnvironmentVariable(DeploymentVar, "   ");

            var ex = Assert.Throws<LlmConfigurationException>(() => new AzureLlmClient());
            Assert.Contains(DeploymentVar, ex.Message);
        }

        [Fact]
        public async Task CompleteJsonAsync_NonSuccessStatusCode_ThrowsLlmRequestException()
        {
            SetAllEnvVars();

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"message\":\"invalid api key\"}}", Encoding.UTF8, "application/json")
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AzureLlmClient(httpClient))
            {
                var ex = await Assert.ThrowsAsync<LlmRequestException>(
                    () => client.CompleteJsonAsync("s", "u", CancellationToken.None));
                Assert.Contains("401", ex.Message);
            }
        }

        [Fact]
        public async Task CompleteJsonAsync_MalformedResponseBody_ThrowsLlmRequestException()
        {
            SetAllEnvVars();

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("this is not json", Encoding.UTF8, "application/json")
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AzureLlmClient(httpClient))
            {
                await Assert.ThrowsAsync<LlmRequestException>(
                    () => client.CompleteJsonAsync("s", "u", CancellationToken.None));
            }
        }

        [Fact]
        public async Task CompleteJsonAsync_ResponseMissingMessageContent_ThrowsLlmRequestException()
        {
            SetAllEnvVars();

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[]}", Encoding.UTF8, "application/json")
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AzureLlmClient(httpClient))
            {
                await Assert.ThrowsAsync<LlmRequestException>(
                    () => client.CompleteJsonAsync("s", "u", CancellationToken.None));
            }
        }

        [Fact]
        public async Task CompleteJsonAsync_NetworkFailure_ThrowsLlmRequestException()
        {
            SetAllEnvVars();

            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("simulated DNS failure"));

            using (var httpClient = new HttpClient(handler))
            using (var client = new AzureLlmClient(httpClient))
            {
                await Assert.ThrowsAsync<LlmRequestException>(
                    () => client.CompleteJsonAsync("s", "u", CancellationToken.None));
            }
        }
    }
}
