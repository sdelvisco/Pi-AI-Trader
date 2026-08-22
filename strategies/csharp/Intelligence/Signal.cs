using System;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// A single trading signal emitted by any intelligence module (LLM, ML,
    /// RL, etc). This is the common currency the future Signal Aggregator
    /// (out of scope for this session) will consume regardless of source —
    /// every <see cref="IIntelligenceModule"/> implementation, no matter how
    /// different its internals, must be able to produce one of these.
    /// </summary>
    public class Signal
    {
        /// <summary>The ticker this signal applies to.</summary>
        public string Symbol { get; set; }

        /// <summary>Coarse bullish/bearish/neutral read. See
        /// <see cref="SignalDirection"/> for why this is kept independent
        /// of <see cref="RawScore"/> rather than derived from it.</summary>
        public SignalDirection Direction { get; set; }

        /// <summary>Continuous sentiment/prediction magnitude, -1.0 to 1.0.</summary>
        public double RawScore { get; set; }

        /// <summary>Model's self-reported confidence in this signal, 0.0 to 1.0.
        /// Kept separate from RawScore deliberately: a strongly-worded headline
        /// can still be low-confidence (sarcasm, ambiguous ticker reference),
        /// and collapsing the two into one number would lose that distinction
        /// for confidence-weighted aggregation later. This session's modules
        /// (LlmSentimentModule) deliberately do NOT filter signals by
        /// confidence — every successfully parsed response becomes a Signal,
        /// and confidence-based filtering/weighting is left entirely to the
        /// future Signal Aggregator, which can see confidence across all
        /// sources at once instead of each module making that call in
        /// isolation.</summary>
        public double Confidence { get; set; }

        /// <summary>Weight assigned to this signal's source type for
        /// aggregation purposes (e.g. 1.0 for Headline, 2.8 for Article, 4.5
        /// for Filing, per the project's existing weighting scheme). Set by
        /// the module that produced the signal, not computed here — this
        /// class is a plain data carrier and has no aggregation logic of its
        /// own.</summary>
        public double SourceWeight { get; set; }

        /// <summary>e.g. "LlmSentimentModule:Headline" — identifies which
        /// module and task type produced this signal, for logging/debugging
        /// when signals from many different sources are mixed together
        /// downstream.</summary>
        public string SourceModule { get; set; }

        /// <summary>When this signal is considered "as of". See
        /// LlmSentimentModule.GenerateSignalAsync for the reasoning behind
        /// which timestamp (request time vs. generation time) populates
        /// this field for headline-scoring signals specifically.</summary>
        public DateTime TimestampUtc { get; set; }

        /// <summary>Short human-readable justification for this signal.
        /// Always populated for headline scoring (never null/empty) — this
        /// project wants LLM rationale logged for later review, so a
        /// missing rationale is treated as a malformed response rather than
        /// silently allowed through (see LlmSentimentModule).</summary>
        public string Rationale { get; set; }
    }
}
