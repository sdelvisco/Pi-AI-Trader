using System.Collections.Generic;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Combines a symbol's recent Signals into one AggregatedSignal. The
    /// caller (DualMomentumV2, via SignalsFileReader) is responsible for
    /// narrowing the input down to one symbol and the desired lookback
    /// window before calling this -- Aggregate() itself does no filtering,
    /// only combination.
    /// </summary>
    public interface ISignalAggregator
    {
        /// <summary>
        /// Combines the given recent signals (already filtered to one
        /// symbol and the caller's chosen lookback window) into one
        /// AggregatedSignal using the given mode. An empty signals
        /// collection is valid input and must return a neutral,
        /// zero-score, zero-confidence, zero-contributing-count result --
        /// never throw for this case, since this is the normal "no recent
        /// news for this ticker" case DualMomentumV2's fail-safe behavior
        /// depends on.
        /// </summary>
        AggregatedSignal Aggregate(string symbol, IEnumerable<Signal> recentSignals, AggregationMode mode);
    }
}
