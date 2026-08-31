namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// The four ways SignalAggregator can combine a symbol's recent Signals
    /// into one AggregatedSignal. See SignalAggregator's own class comment
    /// for the precise math behind each mode -- this enum is just the
    /// selector, read fresh from the shared config file on every rebalance
    /// (see AggregatorConfigReader) so a web-portal change takes effect
    /// without restarting lean-trader.
    /// </summary>
    public enum AggregationMode
    {
        WeightedVote,
        ConfidenceWeighted,
        ConsensusOnly,
        CapitalSplit
    }
}
