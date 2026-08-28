using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PiAiTrader.HeadlineNewsPipeline.Tests
{
    /// <summary>
    /// Unit tests for AlpacaNewsClient. Every HTTP call is routed through
    /// FakeHttpMessageHandler — no real network call is ever made here.
    /// Mirrors Intelligence.Tests/AzureLlmClientTests.cs's env-var
    /// save/restore pattern since AlpacaNewsClient also reads its
    /// credentials at construction time.
    /// </summary>
    public class AlpacaNewsClientTests : IDisposable
    {
        private const string KeyIdVar = "ALPACA_KEY_ID";
        private const string SecretKeyVar = "ALPACA_SECRET_KEY";

        private readonly string _savedKeyId;
        private readonly string _savedSecretKey;

        public AlpacaNewsClientTests()
        {
            _savedKeyId = Environment.GetEnvironmentVariable(KeyIdVar);
            _savedSecretKey = Environment.GetEnvironmentVariable(SecretKeyVar);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(KeyIdVar, _savedKeyId);
            Environment.SetEnvironmentVariable(SecretKeyVar, _savedSecretKey);
        }

        private static void SetAllEnvVars(string keyId = "test-key-id", string secretKey = "test-secret-key")
        {
            Environment.SetEnvironmentVariable(KeyIdVar, keyId);
            Environment.SetEnvironmentVariable(SecretKeyVar, secretKey);
        }

        [Fact]
        public void Constructor_MissingKeyId_ThrowsAlpacaConfigurationException()
        {
            Environment.SetEnvironmentVariable(KeyIdVar, null);
            Environment.SetEnvironmentVariable(SecretKeyVar, "test-secret-key");

            Assert.Throws<AlpacaConfigurationException>(() => new AlpacaNewsClient());
        }

        [Fact]
        public void Constructor_MissingSecretKey_ThrowsAlpacaConfigurationException()
        {
            Environment.SetEnvironmentVariable(KeyIdVar, "test-key-id");
            Environment.SetEnvironmentVariable(SecretKeyVar, null);

            Assert.Throws<AlpacaConfigurationException>(() => new AlpacaNewsClient());
        }

        [Fact]
        public async Task GetNewsSinceAsync_MultiHeadlineResponse_ParsesAllFieldsAndSendsAuthHeaders()
        {
            SetAllEnvVars();

            const string body = @"{
                ""news"": [
                    { ""id"": 100, ""headline"": ""Fed holds rates steady"", ""created_at"": ""2026-08-28T14:00:00Z"", ""symbols"": [""SPY"", ""AGG""] },
                    { ""id"": 101, ""headline"": ""NVIDIA beats earnings"", ""created_at"": ""2026-08-28T14:05:00Z"", ""symbols"": [""NVDA""] }
                ],
                ""next_page_token"": null
            }";

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AlpacaNewsClient(httpClient))
            {
                var articles = await client.GetNewsSinceAsync(
                    new[] { "SPY", "AGG", "NVDA" }, new DateTime(2026, 8, 28, 13, 0, 0, DateTimeKind.Utc), CancellationToken.None);

                Assert.Equal(2, articles.Count);
                Assert.Equal(100, articles[0].Id);
                Assert.Equal("Fed holds rates steady", articles[0].Headline);
                Assert.Equal(new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc), articles[0].CreatedAtUtc);
                Assert.Equal(new[] { "SPY", "AGG" }, articles[0].Symbols);
                Assert.Equal(101, articles[1].Id);

                Assert.Equal("test-key-id", handler.LastRequest.Headers.GetValues("APCA-API-KEY-ID").Single());
                Assert.Equal("test-secret-key", handler.LastRequest.Headers.GetValues("APCA-API-SECRET-KEY").Single());
                Assert.Contains("symbols=SPY%2CAGG%2CNVDA", handler.LastRequest.RequestUri.ToString());
            }
        }

        [Fact]
        public async Task GetNewsSinceAsync_MultiPageResponse_DrainsAllPagesViaPageToken()
        {
            SetAllEnvVars();

            const string page1 = @"{
                ""news"": [ { ""id"": 1, ""headline"": ""H1"", ""created_at"": ""2026-08-28T14:00:00Z"", ""symbols"": [""SPY""] } ],
                ""next_page_token"": ""abc123""
            }";
            const string page2 = @"{
                ""news"": [ { ""id"": 2, ""headline"": ""H2"", ""created_at"": ""2026-08-28T14:01:00Z"", ""symbols"": [""SPY""] } ],
                ""next_page_token"": null
            }";

            var callCount = 0;
            var handler = new FakeHttpMessageHandler(request =>
            {
                callCount++;
                var body = request.RequestUri.ToString().Contains("page_token") ? page2 : page1;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AlpacaNewsClient(httpClient))
            {
                var articles = await client.GetNewsSinceAsync(
                    new[] { "SPY" }, DateTime.UtcNow, CancellationToken.None);

                Assert.Equal(2, callCount);
                Assert.Equal(2, articles.Count);
                Assert.Equal(1, articles[0].Id);
                Assert.Equal(2, articles[1].Id);
            }
        }

        [Fact]
        public async Task GetNewsSinceAsync_NonSuccessStatusCode_ThrowsAlpacaRequestException()
        {
            SetAllEnvVars();

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"message\":\"unauthorized\"}", Encoding.UTF8, "application/json")
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AlpacaNewsClient(httpClient))
            {
                await Assert.ThrowsAsync<AlpacaRequestException>(
                    () => client.GetNewsSinceAsync(new[] { "SPY" }, DateTime.UtcNow, CancellationToken.None));
            }
        }

        [Fact]
        public async Task GetNewsSinceAsync_NetworkFailure_ThrowsAlpacaRequestException()
        {
            SetAllEnvVars();

            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("simulated DNS failure"));

            using (var httpClient = new HttpClient(handler))
            using (var client = new AlpacaNewsClient(httpClient))
            {
                await Assert.ThrowsAsync<AlpacaRequestException>(
                    () => client.GetNewsSinceAsync(new[] { "SPY" }, DateTime.UtcNow, CancellationToken.None));
            }
        }

        [Fact]
        public async Task GetNewsSinceAsync_MalformedJsonBody_ThrowsAlpacaResponseFormatException()
        {
            SetAllEnvVars();

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("this is not json", Encoding.UTF8, "application/json")
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AlpacaNewsClient(httpClient))
            {
                await Assert.ThrowsAsync<AlpacaResponseFormatException>(
                    () => client.GetNewsSinceAsync(new[] { "SPY" }, DateTime.UtcNow, CancellationToken.None));
            }
        }

        [Fact]
        public async Task GetNewsSinceAsync_ResponseMissingNewsArray_ThrowsAlpacaResponseFormatException()
        {
            SetAllEnvVars();

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"foo\":\"bar\"}", Encoding.UTF8, "application/json")
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AlpacaNewsClient(httpClient))
            {
                await Assert.ThrowsAsync<AlpacaResponseFormatException>(
                    () => client.GetNewsSinceAsync(new[] { "SPY" }, DateTime.UtcNow, CancellationToken.None));
            }
        }

        [Fact]
        public async Task GetNewsSinceAsync_NewsItemMissingHeadline_ThrowsAlpacaResponseFormatException()
        {
            SetAllEnvVars();

            const string body = @"{
                ""news"": [ { ""id"": 1, ""created_at"": ""2026-08-28T14:00:00Z"", ""symbols"": [""SPY""] } ]
            }";

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });

            using (var httpClient = new HttpClient(handler))
            using (var client = new AlpacaNewsClient(httpClient))
            {
                await Assert.ThrowsAsync<AlpacaResponseFormatException>(
                    () => client.GetNewsSinceAsync(new[] { "SPY" }, DateTime.UtcNow, CancellationToken.None));
            }
        }
    }
}
