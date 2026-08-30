using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PiAiTrader.Intelligence;

namespace PiAiTrader.HeadlineNewsPipeline.Tests
{
    /// <summary>
    /// IIntelligenceModule test double for PollCycleRunner's tests — lets a
    /// test configure specific symbols to throw for (simulating a
    /// GenerateSignalAsync failure) while everything else succeeds, and
    /// records every request it was called with so tests can assert exact
    /// call counts/order.
    /// </summary>
    public class FakeIntelligenceModule : IIntelligenceModule
    {
        private readonly HashSet<string> _symbolsToFail;

        public string ModuleName => "FakeIntelligenceModule";

        public List<SignalRequest> Requests { get; } = new List<SignalRequest>();

        /// <summary>Optional hook invoked at the start of GenerateSignalAsync,
        /// before this fake does anything else — lets a test observe
        /// external state (e.g. the on-disk high-water-mark file) exactly
        /// as it stood right before a given request was scored.</summary>
        public Action<SignalRequest> OnBeforeGenerate { get; set; }

        public FakeIntelligenceModule(IEnumerable<string> symbolsToFail = null)
        {
            _symbolsToFail = new HashSet<string>(symbolsToFail ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        public Task<Signal> GenerateSignalAsync(SignalRequest request, CancellationToken cancellationToken)
        {
            OnBeforeGenerate?.Invoke(request);
            Requests.Add(request);

            if (_symbolsToFail.Contains(request.Symbol))
            {
                throw new InvalidOperationException($"Simulated GenerateSignalAsync failure for '{request.Symbol}'.");
            }

            return Task.FromResult(new Signal
            {
                Symbol = request.Symbol,
                Direction = SignalDirection.Neutral,
                RawScore = 0.0,
                Confidence = 1.0,
                SourceWeight = LlmSentimentModule.HeadlineSourceWeight,
                SourceModule = "FakeIntelligenceModule:Headline",
                TimestampUtc = request.AsOfUtc,
                Rationale = "fake rationale",
            });
        }
    }
}
