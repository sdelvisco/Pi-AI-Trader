using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// IIntelligenceModule implementation covering headline scoring only —
    /// the sole task type built this session. Article sentiment, SEC filing
    /// scoring, and signal narrative generation are explicitly out of scope
    /// (see this session's prompt) and are not stubbed out here; a future
    /// session can add sibling task-type methods (or sibling modules) as
    /// needed without this class needing to change shape today.
    ///
    /// This module owns everything the raw LLM client (ILlmClient) doesn't:
    /// the sentiment-scoring system prompt, the user-prompt format, parsing
    /// the model's JSON response, validating it, and mapping it onto the
    /// shared Signal type.
    ///
    /// Logging note: QuantConnect.Logging.Log only exposes Trace/Debug/Error
    /// static methods — there is no dedicated "Warning" level in LEAN's
    /// logger. Non-fatal sanity-check warnings in this class therefore use
    /// Log.Trace with an explicit "WARNING:" prefix (informational — the
    /// Signal is still returned), while Log.Error is reserved for lines
    /// that accompany a thrown exception (a genuine failure).
    /// </summary>
    public class LlmSentimentModule : IIntelligenceModule
    {
        /// <summary>Weight assigned to headline-sourced signals in the
        /// project's existing source-weighting scheme. Named explicitly
        /// (rather than left as a magic 1.0 literal) so Article (2.8) and
        /// Filing (4.5) weights — both out of scope this session — can be
        /// added as sibling named constants later without anyone having to
        /// go hunting for where "1.0" came from.</summary>
        public const double HeadlineSourceWeight = 1.0;

        private const double MinScore = -1.0;
        private const double MaxScore = 1.0;
        private const double MinConfidence = 0.0;
        private const double MaxConfidence = 1.0;

        // Verbatim per this session's spec. Only change this text if
        // testing reveals it needs tightening, and log any such change in
        // DEVIATIONS.md — this is documented as a deliberate, reviewed
        // prompt, not a starting draft to be casually edited.
        private const string SystemPrompt =
            "You are a financial sentiment scoring engine. Given a news headline and a stock ticker, " +
            "output ONLY a JSON object with exactly these fields: ticker, sentiment_score (-1.0 to 1.0), " +
            "confidence (0.0 to 1.0), direction (bullish/bearish/neutral), rationale (brief, under 150 characters). " +
            "Do not include any text outside the JSON object. If the headline has no clear relevance to the ticker, " +
            "set confidence low and direction neutral.";

        private readonly ILlmClient _llmClient;

        /// <inheritdoc/>
        public string ModuleName => "LlmSentimentModule";

        public LlmSentimentModule(ILlmClient llmClient)
        {
            _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        }

        /// <inheritdoc/>
        public async Task<Signal> GenerateSignalAsync(SignalRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // Simple, clearly-delimited user prompt per this session's spec —
            // no attempt at more elaborate prompt engineering here.
            var userPrompt = $"Ticker: {request.Symbol}\nHeadline: {request.InputText}\n";

            var rawResponse = await _llmClient.CompleteJsonAsync(SystemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);

            JObject parsed;
            try
            {
                parsed = JObject.Parse(rawResponse);
            }
            catch (JsonException ex)
            {
                // Log the raw offending text before throwing, per this
                // session's explicit requirement — this is exactly the kind
                // of quiet data-pipeline issue this project's retrospectives
                // keep tracing outages back to, so it must be visible in the
                // log even though we refuse to guess at a Signal here.
                Log.Error(
                    $"LlmSentimentModule: LLM response for '{request.Symbol}' was not valid JSON. Raw response: {rawResponse}");
                throw new LlmResponseFormatException(
                    $"LLM headline-scoring response for '{request.Symbol}' was not valid JSON.", ex);
            }

            // sentiment_score, confidence, direction, and rationale are all
            // required — any missing/out-of-range field throws rather than
            // being guessed at. ticker is handled separately below: it has
            // its own documented fallback-to-request-symbol behavior instead
            // of being a hard failure.
            var sentimentScore = RequireRangedDouble(parsed, "sentiment_score", MinScore, MaxScore, request.Symbol, rawResponse);
            var confidence = RequireRangedDouble(parsed, "confidence", MinConfidence, MaxConfidence, request.Symbol, rawResponse);
            var directionText = RequireNonEmptyString(parsed, "direction", request.Symbol, rawResponse);
            var rationale = RequireNonEmptyString(parsed, "rationale", request.Symbol, rawResponse);
            var direction = ParseDirection(directionText, request.Symbol, rawResponse);

            var symbol = ResolveSymbol(parsed, request);

            // Sanity-check logging only, never filtering/correcting: if the
            // model's own direction disagrees with the sign of its own
            // score, that's worth a human noticing later, but it is still a
            // valid, usable signal — the future Signal Aggregator (not this
            // module) is where any actual filtering/weighting policy
            // belongs.
            if ((direction == SignalDirection.Bullish && sentimentScore < 0) ||
                (direction == SignalDirection.Bearish && sentimentScore > 0))
            {
                Log.Trace(
                    $"LlmSentimentModule: WARNING: direction/score disagreement for {symbol} — " +
                    $"direction={direction}, sentiment_score={sentimentScore}, rationale=\"{rationale}\"");
            }

            // This project explicitly wants LLM rationale logged for later
            // review independent of wherever the resulting Signal object
            // ends up (e.g. even if a downstream consumer never persists
            // it) — so log it here at Info level in addition to storing it
            // on the Signal.
            Log.Trace($"LlmSentimentModule: {symbol} rationale: {rationale}");

            return new Signal
            {
                Symbol = symbol,
                Direction = direction,
                RawScore = sentimentScore,
                Confidence = confidence,
                SourceWeight = HeadlineSourceWeight,
                SourceModule = "LlmSentimentModule:Headline",
                // request.AsOfUtc (the headline's own timestamp), not
                // DateTime.UtcNow (when scoring happened to run) — see
                // SignalRequest.AsOfUtc's doc comment. This keeps a signal's
                // timestamp meaningful if the same headline is ever re-scored
                // later (backtesting/replay), rather than drifting to
                // whenever the LLM call actually executed.
                TimestampUtc = request.AsOfUtc,
                Rationale = rationale,
            };
        }

        /// <summary>Resolves Signal.Symbol per this session's fallback rule:
        /// prefer the model's own reported ticker, but fall back to the
        /// request's symbol whenever the model omitted it or reported a
        /// different one — logging a warning on disagreement either way, so
        /// a systematically confused model is visible without ever being
        /// silently trusted over the actual input.</summary>
        private static string ResolveSymbol(JObject parsed, SignalRequest request)
        {
            string reportedTicker = null;
            var tickerToken = parsed["ticker"];
            if (tickerToken != null && tickerToken.Type != JTokenType.Null)
            {
                try
                {
                    reportedTicker = tickerToken.Type == JTokenType.String ? tickerToken.Value<string>() : tickerToken.ToString();
                }
                catch (Exception)
                {
                    reportedTicker = null;
                }
            }

            if (string.IsNullOrWhiteSpace(reportedTicker))
            {
                Log.Trace(
                    $"LlmSentimentModule: WARNING: LLM response omitted 'ticker' for request symbol " +
                    $"'{request.Symbol}'. Falling back to the request's symbol.");
                return request.Symbol;
            }

            if (!string.Equals(reportedTicker.Trim(), request.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                Log.Trace(
                    $"LlmSentimentModule: WARNING: LLM-reported ticker '{reportedTicker}' disagrees with " +
                    $"request symbol '{request.Symbol}'. Falling back to the request's symbol.");
                return request.Symbol;
            }

            return request.Symbol;
        }

        private static SignalDirection ParseDirection(string directionText, string symbol, string rawResponse)
        {
            switch (directionText.Trim().ToLowerInvariant())
            {
                case "bullish":
                    return SignalDirection.Bullish;
                case "bearish":
                    return SignalDirection.Bearish;
                case "neutral":
                    return SignalDirection.Neutral;
                default:
                    Log.Error(
                        $"LlmSentimentModule: LLM response for '{symbol}' had unrecognized direction " +
                        $"'{directionText}' (expected bullish/bearish/neutral). Raw response: {rawResponse}");
                    throw new LlmResponseFormatException(
                        $"LLM headline-scoring response field 'direction' was '{directionText}'; " +
                        "expected one of: bullish, bearish, neutral.");
            }
        }

        private static double RequireRangedDouble(JObject parsed, string field, double min, double max, string symbol, string rawResponse)
        {
            var token = parsed[field];
            if (token == null || token.Type == JTokenType.Null)
            {
                Log.Error(
                    $"LlmSentimentModule: LLM response for '{symbol}' was missing required field '{field}'. " +
                    $"Raw response: {rawResponse}");
                throw new LlmResponseFormatException(
                    $"LLM headline-scoring response was missing required field '{field}'.");
            }

            // Explicit ok/value pair (rather than chaining TryParse's out
            // parameter through a separately-stored bool) so every code path
            // definitely assigns `value` before it's used below — simpler
            // for the compiler's definite-assignment analysis to prove, and
            // simpler to read.
            var value = 0.0;
            var ok = false;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<double>();
                ok = true;
            }
            else if (token.Type == JTokenType.String)
            {
                ok = double.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }

            if (!ok)
            {
                Log.Error(
                    $"LlmSentimentModule: LLM response for '{symbol}' field '{field}' was not numeric " +
                    $"(got '{token}'). Raw response: {rawResponse}");
                throw new LlmResponseFormatException(
                    $"LLM headline-scoring response field '{field}' was not a number (got '{token}').");
            }

            if (value < min || value > max)
            {
                Log.Error(
                    $"LlmSentimentModule: LLM response for '{symbol}' field '{field}' = {value} is outside " +
                    $"the expected range [{min}, {max}]. Raw response: {rawResponse}");
                throw new LlmResponseFormatException(
                    $"LLM headline-scoring response field '{field}' = {value} is outside the expected range [{min}, {max}].");
            }

            return value;
        }

        private static string RequireNonEmptyString(JObject parsed, string field, string symbol, string rawResponse)
        {
            var token = parsed[field];
            string value = null;
            if (token != null && token.Type != JTokenType.Null)
            {
                try
                {
                    // Prefer JValue's own string coercion for scalar types
                    // (string/number/bool); fall back to null (treated as
                    // "missing" below) rather than letting an unexpected
                    // exception type (e.g. from a JSON array/object in this
                    // field) escape this method.
                    value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
                }
                catch (Exception)
                {
                    value = null;
                }
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                Log.Error(
                    $"LlmSentimentModule: LLM response for '{symbol}' was missing required field '{field}'. " +
                    $"Raw response: {rawResponse}");
                throw new LlmResponseFormatException(
                    $"LLM headline-scoring response was missing required field '{field}'.");
            }
            return value;
        }
    }
}
