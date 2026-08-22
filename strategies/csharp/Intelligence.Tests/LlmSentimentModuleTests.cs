using System;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Logging;
using Xunit;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Unit tests for LlmSentimentModule. All of these go through
    /// FakeLlmClient, never AzureLlmClient/HTTP — this module's own logic
    /// (parsing/validating/mapping the sentiment JSON) is what's under test
    /// here, not the transport.
    ///
    /// Tests that need to observe what was logged install a
    /// CapturingLogHandler via Log.LogHandler and restore the previous
    /// handler afterward (IDisposable pattern, same as
    /// AzureLlmClientTests' env-var save/restore) since QuantConnect.Logging.Log
    /// is a process-global static.
    /// </summary>
    public class LlmSentimentModuleTests : IDisposable
    {
        private readonly ILogHandler _savedLogHandler;

        public LlmSentimentModuleTests()
        {
            _savedLogHandler = Log.LogHandler;
        }

        public void Dispose()
        {
            Log.LogHandler = _savedLogHandler;
        }

        private static SignalRequest MakeRequest(string symbol = "AAPL", string headline = "Apple beats Q3 earnings expectations")
        {
            return new SignalRequest
            {
                Symbol = symbol,
                InputText = headline,
                AsOfUtc = new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc)
            };
        }

        [Fact]
        public async Task GenerateSignalAsync_WellFormedResponse_MapsAllFieldsCorrectly()
        {
            const string response = "{\"ticker\":\"AAPL\",\"sentiment_score\":0.72,\"confidence\":0.88," +
                "\"direction\":\"bullish\",\"rationale\":\"Strong earnings beat expectations\"}";
            var module = new LlmSentimentModule(new FakeLlmClient(response));
            var request = MakeRequest();

            var signal = await module.GenerateSignalAsync(request, CancellationToken.None);

            Assert.Equal("AAPL", signal.Symbol);
            Assert.Equal(SignalDirection.Bullish, signal.Direction);
            Assert.Equal(0.72, signal.RawScore);
            Assert.Equal(0.88, signal.Confidence);
            Assert.Equal(LlmSentimentModule.HeadlineSourceWeight, signal.SourceWeight);
            Assert.Equal(1.0, signal.SourceWeight);
            Assert.Equal("LlmSentimentModule:Headline", signal.SourceModule);
            Assert.Equal(request.AsOfUtc, signal.TimestampUtc);
            Assert.Equal("Strong earnings beat expectations", signal.Rationale);
        }

        [Fact]
        public async Task GenerateSignalAsync_LowConfidenceResponse_IsStillReturnedUnfiltered()
        {
            // Per this session's explicit decision: no confidence-based
            // filtering happens in this module.
            const string response = "{\"ticker\":\"AAPL\",\"sentiment_score\":0.1,\"confidence\":0.02," +
                "\"direction\":\"neutral\",\"rationale\":\"Ambiguous relevance\"}";
            var module = new LlmSentimentModule(new FakeLlmClient(response));

            var signal = await module.GenerateSignalAsync(MakeRequest(), CancellationToken.None);

            Assert.NotNull(signal);
            Assert.Equal(0.02, signal.Confidence);
        }

        [Fact]
        public async Task GenerateSignalAsync_MalformedJson_ThrowsLlmResponseFormatException()
        {
            var module = new LlmSentimentModule(new FakeLlmClient("not valid json at all"));

            await Assert.ThrowsAsync<LlmResponseFormatException>(
                () => module.GenerateSignalAsync(MakeRequest(), CancellationToken.None));
        }

        [Fact]
        public async Task GenerateSignalAsync_MissingSentimentScore_ThrowsLlmResponseFormatException()
        {
            const string response = "{\"ticker\":\"AAPL\",\"confidence\":0.5,\"direction\":\"bullish\",\"rationale\":\"x\"}";
            var module = new LlmSentimentModule(new FakeLlmClient(response));

            var ex = await Assert.ThrowsAsync<LlmResponseFormatException>(
                () => module.GenerateSignalAsync(MakeRequest(), CancellationToken.None));
            Assert.Contains("sentiment_score", ex.Message);
        }

        [Fact]
        public async Task GenerateSignalAsync_MissingConfidence_ThrowsLlmResponseFormatException()
        {
            const string response = "{\"ticker\":\"AAPL\",\"sentiment_score\":0.5,\"direction\":\"bullish\",\"rationale\":\"x\"}";
            var module = new LlmSentimentModule(new FakeLlmClient(response));

            var ex = await Assert.ThrowsAsync<LlmResponseFormatException>(
                () => module.GenerateSignalAsync(MakeRequest(), CancellationToken.None));
            Assert.Contains("confidence", ex.Message);
        }

        [Fact]
        public async Task GenerateSignalAsync_MissingRationale_ThrowsLlmResponseFormatException()
        {
            const string response = "{\"ticker\":\"AAPL\",\"sentiment_score\":0.5,\"confidence\":0.5,\"direction\":\"bullish\"}";
            var module = new LlmSentimentModule(new FakeLlmClient(response));

            var ex = await Assert.ThrowsAsync<LlmResponseFormatException>(
                () => module.GenerateSignalAsync(MakeRequest(), CancellationToken.None));
            Assert.Contains("rationale", ex.Message);
        }

        [Fact]
        public async Task GenerateSignalAsync_UnrecognizedDirection_ThrowsLlmResponseFormatException()
        {
            const string response = "{\"ticker\":\"AAPL\",\"sentiment_score\":0.5,\"confidence\":0.5," +
                "\"direction\":\"very bullish\",\"rationale\":\"x\"}";
            var module = new LlmSentimentModule(new FakeLlmClient(response));

            await Assert.ThrowsAsync<LlmResponseFormatException>(
                () => module.GenerateSignalAsync(MakeRequest(), CancellationToken.None));
        }

        [Fact]
        public async Task GenerateSignalAsync_SentimentScoreOutOfRange_ThrowsLlmResponseFormatException()
        {
            const string response = "{\"ticker\":\"AAPL\",\"sentiment_score\":3.5,\"confidence\":0.5," +
                "\"direction\":\"bullish\",\"rationale\":\"x\"}";
            var module = new LlmSentimentModule(new FakeLlmClient(response));

            var ex = await Assert.ThrowsAsync<LlmResponseFormatException>(
                () => module.GenerateSignalAsync(MakeRequest(), CancellationToken.None));
            Assert.Contains("sentiment_score", ex.Message);
        }

        [Fact]
        public async Task GenerateSignalAsync_ConfidenceOutOfRange_ThrowsLlmResponseFormatException()
        {
            const string response = "{\"ticker\":\"AAPL\",\"sentiment_score\":0.5,\"confidence\":1.5," +
                "\"direction\":\"bullish\",\"rationale\":\"x\"}";
            var module = new LlmSentimentModule(new FakeLlmClient(response));

            await Assert.ThrowsAsync<LlmResponseFormatException>(
                () => module.GenerateSignalAsync(MakeRequest(), CancellationToken.None));
        }

        [Fact]
        public async Task GenerateSignalAsync_DirectionScoreMismatch_LogsWarningButStillReturnsSignal()
        {
            // Positive score but "bearish" direction — a deliberate
            // disagreement that must be logged, not filtered/corrected.
            const string response = "{\"ticker\":\"AAPL\",\"sentiment_score\":0.6,\"confidence\":0.7," +
                "\"direction\":\"bearish\",\"rationale\":\"Contradictory signal for test\"}";
            var capturingHandler = new CapturingLogHandler();
            Log.LogHandler = capturingHandler;

            var module = new LlmSentimentModule(new FakeLlmClient(response));
            var signal = await module.GenerateSignalAsync(MakeRequest(), CancellationToken.None);

            // Still a valid, usable Signal — this module never filters or
            // corrects based on the mismatch.
            Assert.Equal(SignalDirection.Bearish, signal.Direction);
            Assert.Equal(0.6, signal.RawScore);

            Assert.Contains(capturingHandler.TraceMessages, m =>
                m.Contains("WARNING") && m.Contains("bearish", StringComparison.OrdinalIgnoreCase) && m.Contains("0.6"));
        }

        [Fact]
        public async Task GenerateSignalAsync_TickerMismatch_FallsBackToRequestSymbolAndLogsWarning()
        {
            const string response = "{\"ticker\":\"MSFT\",\"sentiment_score\":0.3,\"confidence\":0.5," +
                "\"direction\":\"neutral\",\"rationale\":\"x\"}";
            var capturingHandler = new CapturingLogHandler();
            Log.LogHandler = capturingHandler;

            var module = new LlmSentimentModule(new FakeLlmClient(response));
            var signal = await module.GenerateSignalAsync(MakeRequest(symbol: "AAPL"), CancellationToken.None);

            // Falls back to the request's symbol rather than trusting the
            // model's mismatched ticker.
            Assert.Equal("AAPL", signal.Symbol);
            Assert.Contains(capturingHandler.TraceMessages, m =>
                m.Contains("WARNING") && m.Contains("MSFT") && m.Contains("AAPL"));
        }

        [Fact]
        public async Task GenerateSignalAsync_TickerOmitted_FallsBackToRequestSymbol()
        {
            const string response = "{\"sentiment_score\":0.1,\"confidence\":0.2,\"direction\":\"neutral\",\"rationale\":\"no ticker field\"}";
            var module = new LlmSentimentModule(new FakeLlmClient(response));

            var signal = await module.GenerateSignalAsync(MakeRequest(symbol: "TSLA"), CancellationToken.None);

            Assert.Equal("TSLA", signal.Symbol);
        }

        [Fact]
        public async Task GenerateSignalAsync_RationaleIsLoggedAtInfoLevel()
        {
            const string response = "{\"ticker\":\"AAPL\",\"sentiment_score\":0.4,\"confidence\":0.6," +
                "\"direction\":\"bullish\",\"rationale\":\"Rationale for logging test\"}";
            var capturingHandler = new CapturingLogHandler();
            Log.LogHandler = capturingHandler;

            var module = new LlmSentimentModule(new FakeLlmClient(response));
            await module.GenerateSignalAsync(MakeRequest(), CancellationToken.None);

            Assert.Contains(capturingHandler.TraceMessages, m => m.Contains("Rationale for logging test"));
        }
    }
}
