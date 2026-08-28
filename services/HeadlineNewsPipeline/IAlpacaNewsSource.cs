using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Narrow seam between PollCycleRunner and AlpacaNewsClient, so unit
    /// tests covering dedup/ordering/multi-symbol/failure-continuation
    /// logic can fake the news source directly instead of routing through
    /// HTTP mocking for every test — the same layering AzureLlmClient/
    /// ILlmClient established between HTTP concerns and the module that
    /// consumes them.
    /// </summary>
    public interface IAlpacaNewsSource
    {
        /// <summary>Fetches every article created at or after
        /// <paramref name="sinceUtc"/> for <paramref name="symbols"/>,
        /// across as many pages as the API returns, in no particular
        /// guaranteed order — callers must sort/filter by Id themselves.
        /// Throws AlpacaRequestException/AlpacaResponseFormatException on
        /// failure; never returns null.</summary>
        Task<IReadOnlyList<AlpacaNewsArticle>> GetNewsSinceAsync(
            IReadOnlyCollection<string> symbols, DateTime sinceUtc, CancellationToken cancellationToken);
    }
}
