namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// The output of combining zero or more recent Signals for one symbol
    /// into a single actionable value. Symmetric across all four
    /// AggregationModes -- only the combination math inside
    /// SignalAggregator differs; callers (i.e. DualMomentumV2) consume this
    /// one shape regardless of which mode produced it.
    /// </summary>
    public class AggregatedSignal
    {
        public string Symbol { get; set; }

        public SignalDirection Direction { get; set; }

        /// <summary>Combined sentiment magnitude, -1.0 to 1.0. Zero when no
        /// signals contributed (the safe, no-adjustment default).</summary>
        public double CombinedScore { get; set; }

        public double CombinedConfidence { get; set; }

        public AggregationMode ModeUsed { get; set; }

        /// <summary>How many Signals actually contributed to this result.
        /// Zero is a valid, expected value (no recent signals for this
        /// symbol) and callers must treat that as "no adjustment", not an
        /// error. Note this reflects how many signals were fed into the
        /// aggregation, not how many ended up agreeing with the winning
        /// direction -- e.g. ConsensusOnly's forced-Neutral result on
        /// disagreement still has a nonzero ContributingSignalCount, since
        /// signals genuinely existed and were considered; only a literally
        /// empty input (no recent signals at all for this symbol) produces
        /// zero here.</summary>
        public int ContributingSignalCount { get; set; }
    }
}
