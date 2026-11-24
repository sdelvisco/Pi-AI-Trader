using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.HeadlineNewsPipeline.Tests
{
    /// <summary>
    /// IAlpacaNewsSource test double so PollCycleRunner's dedup/ordering/
    /// multi-symbol/failure-continuation logic can be tested directly,
    /// without routing every case through HTTP mocking — mirrors
    /// Intelligence.Tests' FakeLlmClient, which exists for the same reason
    /// relative to LlmSentimentModule.
    /// </summary>
    public class FakeAlpacaNewsSource : IAlpacaNewsSource
    {
        // Not readonly: multi-cycle tests swap in a new batch between
        // RunOnceAsync() calls via Articles.
        private IReadOnlyList<AlpacaNewsArticle> _articles;

        /// <summary>Replaces the articles returned by the next
        /// GetNewsSinceAsync() call (used by multi-cycle tests).</summary>
        public IReadOnlyList<AlpacaNewsArticle> Articles
        {
            set => _articles = value ?? throw new ArgumentNullException(nameof(value));
        }

        public DateTime? LastRequestedSinceUtc { get; private set; }
        public IReadOnlyCollection<string> LastRequestedSymbols { get; private set; }

        public FakeAlpacaNewsSource(IReadOnlyList<AlpacaNewsArticle> articles)
        {
            _articles = articles ?? throw new ArgumentNullException(nameof(articles));
        }

        public Task<IReadOnlyList<AlpacaNewsArticle>> GetNewsSinceAsync(
            IReadOnlyCollection<string> symbols, DateTime sinceUtc, CancellationToken cancellationToken)
        {
            LastRequestedSymbols = symbols;
            LastRequestedSinceUtc = sinceUtc;
            return Task.FromResult(_articles);
        }
    }
}
