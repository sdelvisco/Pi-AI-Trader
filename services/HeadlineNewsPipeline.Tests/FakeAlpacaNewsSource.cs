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
        private readonly IReadOnlyList<AlpacaNewsArticle> _articles;

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
