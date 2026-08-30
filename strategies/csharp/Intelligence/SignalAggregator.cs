using System;
using System.Collections.Generic;
using System.Linq;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Implements all four AggregationModes for combining a symbol's recent
    /// Signals into one AggregatedSignal.
    ///
    /// IMPORTANT -- CapitalSplit's real distinguishing behavior is NOT in
    /// this class. Aggregate() for CapitalSplit deliberately returns the
    /// exact same CombinedScore/CombinedConfidence as ConfidenceWeighted
    /// (see the ConfidenceWeighted-shared implementation below), because
    /// this method only ever sees one symbol at a time and capital split's
    /// whole point is to redistribute weight *relative to the other N-1
    /// selected tickers in the same rebalance* -- something only the
    /// position-sizing step in DualMomentumV2 (via PositionSizer, which
    /// receives all N tickers' AggregatedSignals at once) can see. Look
    /// there, not here, for the cohort-relative rescaling logic.
    /// </summary>
    public class SignalAggregator : ISignalAggregator
    {
        /// <inheritdoc/>
        public AggregatedSignal Aggregate(string symbol, IEnumerable<Signal> recentSignals, AggregationMode mode)
        {
            // A null or empty input is the normal "no recent news for this
            // ticker" case, not an error -- every mode must degrade to a
            // neutral, zero-score, zero-confidence, zero-contributing-count
            // result here so DualMomentumV2's fail-safe "no signals means no
            // adjustment" behavior has one single, uniform source.
            var signalList = (recentSignals ?? Enumerable.Empty<Signal>()).ToList();
            if (signalList.Count == 0)
            {
                return NeutralZero(symbol, mode, contributingSignalCount: 0);
            }

            switch (mode)
            {
                case AggregationMode.WeightedVote:
                    return WeightedVoteAggregate(symbol, signalList, AggregationMode.WeightedVote);

                case AggregationMode.ConfidenceWeighted:
                    return ConfidenceWeightedAggregate(symbol, signalList, AggregationMode.ConfidenceWeighted);

                case AggregationMode.ConsensusOnly:
                    return ConsensusOnlyAggregate(symbol, signalList);

                case AggregationMode.CapitalSplit:
                    // Per the class comment above: identical math to
                    // ConfidenceWeighted. The cohort-relative redistribution
                    // that actually distinguishes CapitalSplit happens one
                    // level up, in PositionSizer.
                    return ConfidenceWeightedAggregate(symbol, signalList, AggregationMode.CapitalSplit);

                default:
                    // Every defined AggregationMode is handled above; this
                    // branch only exists so an unrecognized enum value
                    // (e.g. from a future mode added to the enum but not
                    // yet wired in here) degrades safely to the project's
                    // documented default mode's math instead of throwing --
                    // aggregation must never be able to abort a rebalance.
                    return ConfidenceWeightedAggregate(symbol, signalList, AggregationMode.CapitalSplit);
            }
        }

        // =====================================================================
        // Weighted vote
        //
        // Each signal casts one vote for its own Direction, weighted by
        // SourceWeight -- a headline-sourced signal (SourceWeight 1.0) and a
        // (future) filing-sourced signal (SourceWeight 4.5) don't count
        // equally, since the project's own source-weighting scheme reflects
        // how much more reliable a filing's sentiment read is expected to be
        // than a single headline's. The Direction with the highest total
        // weight wins the vote. CombinedScore/CombinedConfidence are then
        // computed ONLY from the signals that agree with the winning
        // Direction -- disagreeing signals had their say in the vote itself,
        // but don't get to drag the winning side's magnitude/confidence
        // toward their own reading once they've lost.
        // =====================================================================
        private static AggregatedSignal WeightedVoteAggregate(string symbol, List<Signal> signalList, AggregationMode modeUsed)
        {
            var weightByDirection = signalList
                .GroupBy(s => s.Direction)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.SourceWeight));

            // OrderByDescending + First is a deterministic (stable-sort)
            // tie-break: ties fall to whichever direction's group was
            // encountered first in signalList. No stronger tie-break rule is
            // specified for this project, and a deterministic-if-arbitrary
            // choice is safer than a non-deterministic one for a system that
            // sizes real positions.
            var winningDirection = weightByDirection
                .OrderByDescending(kv => kv.Value)
                .First()
                .Key;

            var agreeing = signalList.Where(s => s.Direction == winningDirection).ToList();
            var agreeingWeightSum = agreeing.Sum(s => s.SourceWeight);

            var combinedScore = agreeingWeightSum > 0
                ? agreeing.Sum(s => s.RawScore * s.SourceWeight) / agreeingWeightSum
                : 0.0;
            var combinedConfidence = agreeingWeightSum > 0
                ? agreeing.Sum(s => s.Confidence * s.SourceWeight) / agreeingWeightSum
                : 0.0;

            return new AggregatedSignal
            {
                Symbol = symbol,
                Direction = winningDirection,
                CombinedScore = combinedScore,
                CombinedConfidence = combinedConfidence,
                ModeUsed = modeUsed,
                ContributingSignalCount = signalList.Count,
            };
        }

        // =====================================================================
        // Confidence-weighted (also the math CapitalSplit's Aggregate()
        // reuses -- see the class comment above)
        //
        // Weight per signal = SourceWeight x Confidence: a source that is
        // both inherently reliable (high SourceWeight) AND self-reports high
        // confidence in this particular read counts the most. Unlike
        // WeightedVote, EVERY signal contributes to CombinedScore here --
        // there is no "losing side" that gets excluded, since the whole
        // point of this mode is a smooth, continuous blend rather than a
        // binary winner. Direction is derived from the sign of the resulting
        // CombinedScore (not separately voted on), since a single continuous
        // score is this mode's primary output and the Direction field should
        // just describe it.
        // =====================================================================
        private static AggregatedSignal ConfidenceWeightedAggregate(string symbol, List<Signal> signalList, AggregationMode modeUsed)
        {
            double totalWeight = 0.0;
            double scoreWeightedSum = 0.0;
            double confidenceWeightedSum = 0.0;

            foreach (var s in signalList)
            {
                var weight = s.SourceWeight * s.Confidence;
                totalWeight += weight;
                scoreWeightedSum += s.RawScore * weight;
                confidenceWeightedSum += s.Confidence * weight;
            }

            var combinedScore = totalWeight > 0 ? scoreWeightedSum / totalWeight : 0.0;
            var combinedConfidence = totalWeight > 0 ? confidenceWeightedSum / totalWeight : 0.0;

            SignalDirection direction;
            if (combinedScore > 0) direction = SignalDirection.Bullish;
            else if (combinedScore < 0) direction = SignalDirection.Bearish;
            else direction = SignalDirection.Neutral;

            return new AggregatedSignal
            {
                Symbol = symbol,
                Direction = direction,
                CombinedScore = combinedScore,
                CombinedConfidence = combinedConfidence,
                ModeUsed = modeUsed,
                ContributingSignalCount = signalList.Count,
            };
        }

        // =====================================================================
        // Consensus-only
        //
        // The intentionally conservative mode: any dissent zeroes out the
        // adjustment entirely, rather than letting a slim or noisy majority
        // move real position sizing. Computes the weighted-vote result
        // first, then checks for FULL agreement -- every single signal's own
        // Direction must match the winning Direction, with no
        // partial/threshold agreement allowed, AND that winning Direction
        // must be non-Neutral (an all-Neutral "consensus" isn't a directional
        // signal worth acting on either). If both conditions hold, the
        // weighted-vote result is returned as-is. Otherwise -- any
        // disagreement at all -- a forced-Neutral, zero-CombinedScore result
        // is returned regardless of what the underlying signals said.
        // =====================================================================
        private static AggregatedSignal ConsensusOnlyAggregate(string symbol, List<Signal> signalList)
        {
            var weightedVote = WeightedVoteAggregate(symbol, signalList, AggregationMode.ConsensusOnly);

            var fullAgreement = weightedVote.Direction != SignalDirection.Neutral
                && signalList.All(s => s.Direction == weightedVote.Direction);

            if (fullAgreement)
            {
                return weightedVote;
            }

            // Disagreement (or an all-Neutral vote) -- force neutral/zero,
            // but ContributingSignalCount still reflects that real signals
            // were considered (this is "signals disagreed", not "no
            // signals"); DualMomentumV2's zero-signal fallback path is keyed
            // specifically off an empty input, handled earlier in
            // Aggregate().
            return NeutralZero(symbol, AggregationMode.ConsensusOnly, signalList.Count);
        }

        private static AggregatedSignal NeutralZero(string symbol, AggregationMode mode, int contributingSignalCount)
        {
            return new AggregatedSignal
            {
                Symbol = symbol,
                Direction = SignalDirection.Neutral,
                CombinedScore = 0.0,
                CombinedConfidence = 0.0,
                ModeUsed = mode,
                ContributingSignalCount = contributingSignalCount,
            };
        }
    }
}
