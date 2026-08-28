using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Polls Alpaca's News API (REST, not the websocket/real-time stream —
    /// out of scope for this session, see DEVIATIONS.md) for headlines
    /// tagged against this pipeline's trading universe.
    ///
    /// Configuration (key ID, secret key) is read from the process
    /// environment at construction time, never hardcoded and never logged —
    /// same pattern as PiAiTrader.Intelligence.AzureLlmClient. On the Pi
    /// these arrive via systemd's EnvironmentFile mechanism pointed at
    /// /etc/tradingpi/alpaca.env, exactly like lean-trader.service already
    /// uses (see that unit file's header comment) — this class does not
    /// read any file itself, only Environment.GetEnvironmentVariable.
    /// </summary>
    public class AlpacaNewsClient : IAlpacaNewsSource, IDisposable
    {
        private const string KeyIdEnvVar = "ALPACA_KEY_ID";
        private const string SecretKeyEnvVar = "ALPACA_SECRET_KEY";

        // Per this session's spec — the confirmed-working News API endpoint
        // for this project. Not sourced from ALPACA_DATA_URL (unlike the
        // Python side's broader data-API usage): the prompt names this
        // exact URL, and introducing a second, optional configuration knob
        // for a single fixed endpoint would be unused complexity.
        private const string NewsEndpoint = "https://data.alpaca.markets/v1beta1/news";

        // Alpaca's documented maximum/default page size for this endpoint.
        // Requesting the max page size minimizes the number of round trips
        // needed to drain a poll window.
        private const int PageSize = 50;

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly string _keyId;
        private readonly string _secretKey;

        /// <param name="httpClient">Optional injected HttpClient so unit
        /// tests can supply one backed by a fake HttpMessageHandler instead
        /// of making real network calls — mirrors AzureLlmClient's
        /// constructor. When null, this client creates and owns its own
        /// HttpClient and disposes it in Dispose().</param>
        public AlpacaNewsClient(HttpClient httpClient = null)
        {
            _keyId = ReadRequiredEnvVar(KeyIdEnvVar);
            _secretKey = ReadRequiredEnvVar(SecretKeyEnvVar);

            _ownsHttpClient = httpClient == null;
            _httpClient = httpClient ?? new HttpClient { Timeout = DefaultTimeout };
        }

        private static string ReadRequiredEnvVar(string variableName)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new AlpacaConfigurationException(
                    $"AlpacaNewsClient is missing required environment variable '{variableName}'. " +
                    "Expected it to be populated via the systemd EnvironmentFile at " +
                    "/etc/tradingpi/alpaca.env (see lean-trader.service's header comment), or set " +
                    "directly in the process environment for local/dev/test use.");
            }
            return value;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AlpacaNewsArticle>> GetNewsSinceAsync(
            IReadOnlyCollection<string> symbols, DateTime sinceUtc, CancellationToken cancellationToken)
        {
            if (symbols == null || symbols.Count == 0)
            {
                throw new ArgumentException("symbols must not be null or empty.", nameof(symbols));
            }

            var symbolsParam = string.Join(",", symbols);
            var startParam = sinceUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var articles = new List<AlpacaNewsArticle>();
            string pageToken = null;

            // Drain every page: Alpaca returns next_page_token whenever
            // there are more results than fit in one page, and an
            // absent/null token when the caller has reached the end — per
            // this session's requirement not to silently miss headlines
            // between polls by only reading the first page.
            do
            {
                var (pageArticles, nextPageToken) = await FetchPageAsync(
                    symbolsParam, startParam, pageToken, cancellationToken).ConfigureAwait(false);
                articles.AddRange(pageArticles);
                pageToken = nextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return articles;
        }

        private async Task<(List<AlpacaNewsArticle> Articles, string NextPageToken)> FetchPageAsync(
            string symbolsParam, string startParam, string pageToken, CancellationToken cancellationToken)
        {
            var requestUri = new StringBuilder(NewsEndpoint)
                .Append("?symbols=").Append(Uri.EscapeDataString(symbolsParam))
                .Append("&start=").Append(Uri.EscapeDataString(startParam))
                .Append("&sort=ASC")
                .Append("&limit=").Append(PageSize)
                .ToString();

            if (!string.IsNullOrEmpty(pageToken))
            {
                requestUri += "&page_token=" + Uri.EscapeDataString(pageToken);
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, requestUri))
            {
                // Header names confirmed via this project's own manual curl
                // test against the real Alpaca endpoint, per this session's
                // spec.
                request.Headers.Add("APCA-API-KEY-ID", _keyId);
                request.Headers.Add("APCA-API-SECRET-KEY", _secretKey);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.Error("AlpacaNewsClient: request to Alpaca News API timed out.");
                    throw new AlpacaRequestException(
                        $"Request to Alpaca News API timed out after {DefaultTimeout.TotalSeconds}s.");
                }
                catch (HttpRequestException ex)
                {
                    Log.Error($"AlpacaNewsClient: network failure calling Alpaca News API: {ex.Message}");
                    throw new AlpacaRequestException("Network failure while calling the Alpaca News API.", ex);
                }

                using (response)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        Log.Error(
                            $"AlpacaNewsClient: Alpaca News API returned {(int)response.StatusCode} " +
                            $"{response.StatusCode}. Response body: {responseBody}");
                        throw new AlpacaRequestException(
                            $"Alpaca News API returned non-success status {(int)response.StatusCode} ({response.StatusCode}).");
                    }

                    return ParseResponseBody(responseBody);
                }
            }
        }

        private static (List<AlpacaNewsArticle> Articles, string NextPageToken) ParseResponseBody(string responseBody)
        {
            JObject envelope;
            try
            {
                // Newtonsoft's default JsonTextReader auto-detects
                // ISO8601-looking string values (like created_at) and
                // silently converts them to JTokenType.Date tokens instead
                // of leaving them as JTokenType.String — which would make
                // ParseArticle's explicit "is this actually a string"
                // format check below reject perfectly valid responses.
                // Disabling DateParseHandling keeps every field exactly as
                // Alpaca sent it, so this client parses created_at itself
                // with an explicit, known format rather than trusting
                // Newtonsoft's own date-detection heuristic.
                using (var stringReader = new StringReader(responseBody))
                using (var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None })
                {
                    envelope = JObject.Load(jsonReader);
                }
            }
            catch (JsonException ex)
            {
                Log.Error($"AlpacaNewsClient: response body was not valid JSON. Raw body: {responseBody}");
                throw new AlpacaResponseFormatException("Alpaca News API response body was not valid JSON.", ex);
            }

            var newsArray = envelope["news"] as JArray;
            if (newsArray == null)
            {
                Log.Error(
                    $"AlpacaNewsClient: response JSON did not contain a 'news' array. Raw body: {responseBody}");
                throw new AlpacaResponseFormatException(
                    "Alpaca News API response JSON did not contain the expected top-level 'news' array.");
            }

            var articles = new List<AlpacaNewsArticle>(newsArray.Count);
            foreach (var item in newsArray)
            {
                articles.Add(ParseArticle(item, responseBody));
            }

            var nextPageToken = envelope["next_page_token"]?.Type == JTokenType.String
                ? envelope["next_page_token"].Value<string>()
                : null;

            return (articles, nextPageToken);
        }

        private static AlpacaNewsArticle ParseArticle(JToken item, string rawResponseForLogging)
        {
            var idToken = item["id"];
            if (idToken == null || idToken.Type != JTokenType.Integer)
            {
                Log.Error(
                    $"AlpacaNewsClient: news item missing/non-numeric 'id'. Raw body: {rawResponseForLogging}");
                throw new AlpacaResponseFormatException("Alpaca News API item was missing a numeric 'id' field.");
            }

            var headlineToken = item["headline"];
            if (headlineToken == null || headlineToken.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(headlineToken.Value<string>()))
            {
                Log.Error(
                    $"AlpacaNewsClient: news item {idToken.Value<long>()} missing 'headline'. " +
                    $"Raw body: {rawResponseForLogging}");
                throw new AlpacaResponseFormatException(
                    $"Alpaca News API item {idToken.Value<long>()} was missing a non-empty 'headline' field.");
            }

            var createdAtToken = item["created_at"];
            if (createdAtToken == null || createdAtToken.Type != JTokenType.String ||
                !DateTime.TryParse(
                    createdAtToken.Value<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdAtUtc))
            {
                Log.Error(
                    $"AlpacaNewsClient: news item {idToken.Value<long>()} had missing/unparseable 'created_at'. " +
                    $"Raw body: {rawResponseForLogging}");
                throw new AlpacaResponseFormatException(
                    $"Alpaca News API item {idToken.Value<long>()} had a missing or unparseable 'created_at' field.");
            }

            var symbolsToken = item["symbols"] as JArray;
            if (symbolsToken == null)
            {
                Log.Error(
                    $"AlpacaNewsClient: news item {idToken.Value<long>()} missing 'symbols' array. " +
                    $"Raw body: {rawResponseForLogging}");
                throw new AlpacaResponseFormatException(
                    $"Alpaca News API item {idToken.Value<long>()} was missing the expected 'symbols' array.");
            }

            var symbols = new string[symbolsToken.Count];
            for (var i = 0; i < symbolsToken.Count; i++)
            {
                symbols[i] = symbolsToken[i].Value<string>();
            }

            return new AlpacaNewsArticle
            {
                Id = idToken.Value<long>(),
                Headline = headlineToken.Value<string>(),
                CreatedAtUtc = createdAtUtc,
                Symbols = symbols,
            };
        }

        /// <summary>Disposes the HttpClient only if this instance created it
        /// itself — mirrors AzureLlmClient.Dispose.</summary>
        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
