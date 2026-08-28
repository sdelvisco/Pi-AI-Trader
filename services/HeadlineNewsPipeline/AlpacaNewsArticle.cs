using System;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Plain internal model for one Alpaca News API article. Deliberately
    /// separate from PiAiTrader.Intelligence.SignalRequest/Signal — this
    /// type only knows about Alpaca's response shape, the same way
    /// AzureLlmClient keeps its raw JSON envelope separate from
    /// LlmSentimentModule's sentiment-specific schema. Mapping one of these
    /// onto a SignalRequest (one per in-universe tagged symbol) is
    /// PollCycleRunner's job, not this type's.
    /// </summary>
    public class AlpacaNewsArticle
    {
        /// <summary>Alpaca's own monotonically-increasing numeric article
        /// ID. This is the sole basis for this pipeline's dedup high-water
        /// mark — see HighWaterMarkStore.</summary>
        public long Id { get; set; }

        /// <summary>The headline text itself — the only field this
        /// pipeline currently sends to the LLM for scoring (article body,
        /// summary, etc. are not used this session).</summary>
        public string Headline { get; set; }

        /// <summary>When Alpaca created this article record, in UTC.
        /// Becomes SignalRequest.AsOfUtc for every signal derived from this
        /// article.</summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>Every ticker Alpaca tagged this article with — not yet
        /// filtered down to this pipeline's trading universe. That
        /// filtering happens in PollCycleRunner against TickerUniverse.</summary>
        public string[] Symbols { get; set; }
    }
}
