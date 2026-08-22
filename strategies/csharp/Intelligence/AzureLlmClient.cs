using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// ILlmClient implementation for Azure AI Foundry's OpenAI-compatible
    /// chat-completions endpoint. Talks only in raw request/response JSON —
    /// see ILlmClient for why it deliberately knows nothing about
    /// sentiment-specific (or any other task-specific) schema.
    ///
    /// Configuration (endpoint, API key, deployment name) is read from
    /// three environment variables at construction time, never hardcoded
    /// and never logged. On the Pi these are expected to arrive via
    /// systemd's EnvironmentFile mechanism pointed at
    /// /etc/tradingpi/azure.env — the exact same mechanism
    /// services/lean-trader.service already uses for
    /// /etc/tradingpi/alpaca.env (see that file's header comment). This
    /// class does not read any file itself; it only ever reads
    /// Environment.GetEnvironmentVariable, so whatever process launches it
    /// (systemd on the Pi, a local .env-loading shim on a dev machine, or a
    /// test harness setting variables directly) is free to populate the
    /// process environment however is appropriate for that context.
    /// </summary>
    public class AzureLlmClient : ILlmClient, IDisposable
    {
        private const string EndpointEnvVar = "AZURE_LLM_ENDPOINT";
        private const string ApiKeyEnvVar = "AZURE_LLM_API_KEY";
        private const string DeploymentNameEnvVar = "AZURE_LLM_DEPLOYMENT_NAME";

        // Azure AI Foundry's newer, OpenAI-SDK-compatible "v1" API surface.
        // Deliberately NOT the classic
        // "/openai/deployments/{deployment}/chat/completions?api-version=..."
        // shape (which encodes the deployment in the URL and requires an
        // api-version query string) — the v1 surface instead takes the
        // deployment/model name in the request body's "model" field, which
        // matches the exact request shape this session's spec calls for.
        private const string ChatCompletionsPath = "/openai/v1/chat/completions";

        // Network failures should surface quickly rather than hang a
        // rebalance/scoring pass indefinitely; 30s is generous for a single
        // short chat-completion call.
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _deploymentName;

        /// <param name="httpClient">Optional injected HttpClient, so unit
        /// tests can supply one backed by a fake HttpMessageHandler instead
        /// of making real network calls. This codebase has no
        /// IHttpClientFactory/DI container set up anywhere (it's a LEAN
        /// algorithm process, not an ASP.NET host), so a plain injected
        /// instance — defaulting to a freshly constructed one owned by this
        /// client — is the simplest fit. When null, this client creates and
        /// owns its own HttpClient and disposes it in Dispose().</param>
        public AzureLlmClient(HttpClient httpClient = null)
        {
            // Fail loudly at construction time, not silently at first call —
            // a missing credential should stop startup immediately rather
            // than surface as a mysterious failure the first time a headline
            // needs scoring.
            _endpoint = ReadRequiredEnvVar(EndpointEnvVar);
            _apiKey = ReadRequiredEnvVar(ApiKeyEnvVar);
            _deploymentName = ReadRequiredEnvVar(DeploymentNameEnvVar);

            _ownsHttpClient = httpClient == null;
            _httpClient = httpClient ?? new HttpClient { Timeout = DefaultTimeout };
        }

        private static string ReadRequiredEnvVar(string variableName)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                // Never include the value itself in the exception message —
                // for AZURE_LLM_API_KEY that would leak a credential into
                // logs/exception trackers; for the other two it's simply
                // pointless since we already know the value is empty/missing.
                throw new LlmConfigurationException(
                    $"AzureLlmClient is missing required environment variable '{variableName}'. " +
                    "Expected it to be populated via the systemd EnvironmentFile at " +
                    "/etc/tradingpi/azure.env (see AzureLlmClient's class comment), or set " +
                    "directly in the process environment for local/dev/test use.");
            }
            return value;
        }

        /// <inheritdoc/>
        public async Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(systemPrompt))
            {
                throw new ArgumentException("systemPrompt must not be null or empty.", nameof(systemPrompt));
            }
            if (string.IsNullOrEmpty(userPrompt))
            {
                throw new ArgumentException("userPrompt must not be null or empty.", nameof(userPrompt));
            }

            // Standard OpenAI-compatible chat-completions request shape, per
            // this session's spec. Built with JObject/JArray (Newtonsoft)
            // rather than string interpolation so prompt text is properly
            // JSON-escaped regardless of what characters it contains.
            var requestBody = new JObject
            {
                ["model"] = _deploymentName,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JObject { ["role"] = "user", ["content"] = userPrompt }
                }
            };

            var requestUri = _endpoint.TrimEnd('/') + ChatCompletionsPath;

            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUri))
            {
                request.Content = new StringContent(requestBody.ToString(Formatting.None), Encoding.UTF8, "application/json");

                // Azure AI Foundry's "/openai/v1/..." surface (unlike the
                // classic "api-key: ..." header used by the older
                // "/openai/deployments/{deployment}/..." surface) accepts the
                // API key as a Bearer token in the Authorization header —
                // this is deliberate on Microsoft's part, so the vanilla
                // OpenAI SDK (which always sends Authorization: Bearer) works
                // against Azure endpoints unmodified. Confirmed against
                // current Microsoft Foundry documentation for this specific
                // path rather than assumed; see DEVIATIONS.md.
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The HttpClient's own Timeout elapsed rather than the
                    // caller cancelling — surface this as a request failure,
                    // not as cancellation, so callers can tell "the caller
                    // gave up" apart from "the network/service was slow".
                    Log.Error("AzureLlmClient: request to Azure AI Foundry endpoint timed out.");
                    throw new LlmRequestException(
                        $"Request to Azure AI Foundry LLM endpoint timed out after {DefaultTimeout.TotalSeconds}s.");
                }
                catch (HttpRequestException ex)
                {
                    Log.Error($"AzureLlmClient: network failure calling Azure AI Foundry endpoint: {ex.Message}");
                    throw new LlmRequestException("Network failure while calling the Azure AI Foundry LLM endpoint.", ex);
                }

                using (response)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        // Log the full body (may contain a useful Azure error
                        // message) but keep the exception message itself
                        // short — the body could be arbitrarily large or,
                        // for a 401, could theoretically echo request
                        // details back.
                        Log.Error(
                            $"AzureLlmClient: Azure AI Foundry endpoint returned {(int)response.StatusCode} " +
                            $"{response.StatusCode}. Response body: {responseBody}");
                        throw new LlmRequestException(
                            $"Azure AI Foundry endpoint returned non-success status {(int)response.StatusCode} ({response.StatusCode}).");
                    }

                    JObject envelope;
                    try
                    {
                        envelope = JObject.Parse(responseBody);
                    }
                    catch (JsonException ex)
                    {
                        Log.Error($"AzureLlmClient: response body was not valid JSON. Raw body: {responseBody}");
                        throw new LlmRequestException("Azure AI Foundry response body was not valid JSON.", ex);
                    }

                    // Extract choices[0].message.content. This is the only
                    // part of the HTTP envelope this client understands —
                    // everything else (usage stats, finish_reason, etc.) is
                    // intentionally ignored, per ILlmClient's minimal
                    // contract. Note: JArray's indexer throws
                    // ArgumentOutOfRangeException for an out-of-bounds
                    // index even under the null-conditional operator (`?[0]`
                    // only protects against the array itself being null,
                    // not against it being empty) — so `choices` must be
                    // bounds-checked explicitly before indexing into it,
                    // unlike a JObject property lookup which safely returns
                    // null for a missing key.
                    var choices = envelope["choices"] as JArray;
                    var content = choices != null && choices.Count > 0
                        ? choices[0]["message"]?["content"]?.Value<string>()
                        : null;
                    if (string.IsNullOrEmpty(content))
                    {
                        Log.Error(
                            "AzureLlmClient: response JSON did not contain choices[0].message.content. " +
                            $"Raw body: {responseBody}");
                        throw new LlmRequestException(
                            "Azure AI Foundry response JSON did not contain a usable choices[0].message.content field.");
                    }

                    return content;
                }
            }
        }

        /// <summary>Disposes the HttpClient only if this instance created it
        /// itself — an injected HttpClient (e.g. shared or owned by a test)
        /// is the caller's responsibility, not ours.</summary>
        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
