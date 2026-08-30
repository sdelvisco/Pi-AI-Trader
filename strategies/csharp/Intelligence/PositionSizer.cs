using System;
using System.Collections.Generic;
using System.Linq;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Turns a set of already-selected top-N tickers' AggregatedSignals into
    /// adjusted position weights, given each ticker's base (equal) weight.
    /// This is the position-sizing math DualMomentumV2 calls from within its
    /// existing top-N allocation branch -- it never touches which tickers
    /// are selected (the momentum ranking is untouched), only how much
    /// capital goes to each already-selected ticker.
    ///
    /// This class is deliberately free of any QCAlgorithm/LEAN dependency so
    /// it can be unit tested directly (see PositionSizerTests) without
    /// spinning up the trading engine -- all inputs and outputs are plain
    /// data.
    ///
    /// CAPITAL SPLIT'S REAL LOGIC LIVES HERE, NOT IN SignalAggregator: per
    /// SignalAggregator's own class comment, Aggregate() only ever sees one
    /// symbol at a time and returns the same numbers CapitalSplit and
    /// ConfidenceWeighted would both produce for that one symbol.
    /// CapitalSplit's actual distinguishing behavior -- rescaling a ticker's
    /// adjustment relative to the OTHER N-1 selected tickers' adjustments in
    /// this same rebalance, rather than against a fixed independent range --
    /// happens below, in the CapitalSplit branch of ComputeAdjustedWeights(),
    /// which is the first place in the whole pipeline that can see all N
    /// tickers' scores at once.
    ///
    /// Fail-safe guarantee this class exists to uphold: a ticker with
    /// ContributingSignalCount == 0 (no recent signals) always gets back
    /// EXACTLY its base weight, unadjusted -- not even the ±0.0% no-op path
    /// through the clamp/renormalize math, so there is no floating-point
    /// wiggle room for it to end up even fractionally different from
    /// pre-session equal-weight behavior. When every ticker in the cohort
    /// has zero signals (e.g. signals.jsonl is missing entirely), every
    /// ticker gets back exactly baseWeight -- byte-for-byte identical to
    /// DualMomentumV2's original equal-weight allocation.
    /// </summary>
    public static class PositionSizer
    {
        /// <summary>The ±50% swing bound around a ticker's base equal
        /// weight. Applied as an explicit clamp at the point the adjustment
        /// fraction is computed -- deliberately NOT left to be "guaranteed"
        /// merely by the -1..1 range of the underlying raw adjustment, per
        /// this session's spec, so this bound keeps holding even if a future
        /// change to the upstream scoring math (SignalAggregator, or
        /// whatever produces AggregatedSignal.CombinedScore/CombinedConfidence)
        /// ever widens that range.</summary>
        public const double MaxAdjustmentFraction = 0.50;

        /// <summary>
        /// Computes each ticker's adjusted weight. <paramref name="tickers"/>
        /// is the already-selected top-N set (order doesn't matter);
        /// <paramref name="baseWeight"/> is the equal weight each would have
        /// received pre-session (e.g. 1/N); <paramref name="signalsByTicker"/>
        /// must contain one AggregatedSignal per ticker (a missing entry is
        /// treated the same as ContributingSignalCount == 0 -- defensive,
        /// since a caller bug here must degrade safely, not throw).
        /// </summary>
        public static IReadOnlyDictionary<string, double> ComputeAdjustedWeights(
            IReadOnlyList<string> tickers,
            double baseWeight,
            IReadOnlyDictionary<string, AggregatedSignal> signalsByTicker,
            AggregationMode mode)
        {
            if (tickers == null) throw new ArgumentNullException(nameof(tickers));
            if (signalsByTicker == null) throw new ArgumentNullException(nameof(signalsByTicker));

            var result = new Dictionary<string, double>();
            var activeTickers = new List<string>();
            var rawAdjustments = new Dictionary<string, double>();

            // ── Step 1: split into "no signals -> exact base weight" and
            //    "has signals -> candidate for adjustment" ────────────────
            foreach (var ticker in tickers)
            {
                AggregatedSignal agg;
                var hasSignal = signalsByTicker.TryGetValue(ticker, out agg) && agg != null && agg.ContributingSignalCount > 0;

                if (!hasSignal)
                {
                    // Exact original equal weight, unadjusted -- per this
                    // session's spec, point 7. This assignment is final for
                    // this ticker; it does not pass through the
                    // renormalization step below.
                    result[ticker] = baseWeight;
                    continue;
                }

                activeTickers.Add(ticker);

                // Raw adjustment: CombinedScore x CombinedConfidence, each
                // already bounded to their own documented ranges, but
                // clamped again here explicitly (defense in depth -- see
                // MaxAdjustmentFraction's own comment on why this project
                // doesn't rely on upstream ranges alone).
                var raw = agg.CombinedScore * agg.CombinedConfidence;
                rawAdjustments[ticker] = Clamp(raw, -1.0, 1.0);
            }

            if (activeTickers.Count == 0)
            {
                // Every ticker in this cohort had zero recent signals (e.g.
                // signals.jsonl is missing/empty this rebalance) -- every
                // weight above is already the untouched base weight, so the
                // result here is identical to pre-session equal-weight
                // allocation.
                return result;
            }

            // ── Step 2: scale each active ticker's raw adjustment into a
            //    [-1, 1] "scaled adjustment", differently depending on mode ──
            var scaledAdjustments = new Dictionary<string, double>();

            if (mode == AggregationMode.CapitalSplit)
            {
                // CapitalSplit's distinguishing behavior: rescale relative to
                // the strongest positive and strongest negative raw
                // adjustments actually present in THIS rebalance's cohort,
                // rather than each ticker being scaled independently against
                // the fixed -1..1 range. The strongest positive score in the
                // cohort maps to a full +1.0 (i.e. it will hit the +50%
                // clamp bound below); the strongest negative maps to a full
                // -1.0 (-50%); everything else scales proportionally between.
                // Positive and negative sides are scaled independently of
                // each other (by their own respective strongest magnitude),
                // since a cohort can easily be lopsided (e.g. four mildly
                // bullish tickers and one strongly bearish one) and forcing
                // both sides through one shared scale factor would understate
                // whichever side happens to be weaker this rebalance.
                var maxPositive = activeTickers
                    .Select(t => rawAdjustments[t])
                    .Where(v => v > 0)
                    .DefaultIfEmpty(0.0)
                    .Max();
                var maxNegativeMagnitude = activeTickers
                    .Select(t => rawAdjustments[t])
                    .Where(v => v < 0)
                    .Select(v => -v)
                    .DefaultIfEmpty(0.0)
                    .Max();

                foreach (var ticker in activeTickers)
                {
                    var raw = rawAdjustments[ticker];
                    double scaled;
                    if (raw > 0)
                    {
                        scaled = maxPositive > 0 ? raw / maxPositive : 0.0;
                    }
                    else if (raw < 0)
                    {
                        scaled = maxNegativeMagnitude > 0 ? raw / maxNegativeMagnitude : 0.0;
                    }
                    else
                    {
                        scaled = 0.0;
                    }
                    scaledAdjustments[ticker] = scaled;
                }
            }
            else
            {
                // The other three modes: each ticker's raw adjustment is
                // evaluated independently against the fixed ±50% bound --
                // no cohort-relative rescaling.
                foreach (var ticker in activeTickers)
                {
                    scaledAdjustments[ticker] = rawAdjustments[ticker];
                }
            }

            // ── Step 3: map scaled adjustment onto the ±50% swing around
            //    baseWeight, with an explicit clamp ──────────────────────
            var preRenorm = new Dictionary<string, double>();
            var preRenormSum = 0.0;

            foreach (var ticker in activeTickers)
            {
                var fraction = Clamp(scaledAdjustments[ticker], -1.0, 1.0) * MaxAdjustmentFraction;
                fraction = Clamp(fraction, -MaxAdjustmentFraction, MaxAdjustmentFraction);

                var weight = baseWeight * (1.0 + fraction);
                preRenorm[ticker] = weight;
                preRenormSum += weight;
            }

            // ── Step 4: renormalize the active tickers so total invested
            //    capital across the whole top-N set is unchanged ──────────
            // Zero-signal tickers already locked in their exact base weight
            // above and are excluded from this step entirely (per this
            // session's spec, point 7: they "participate correctly" only in
            // the sense that the budget reserved for them -- zeroCount x
            // baseWeight -- is subtracted out here, leaving exactly
            // activeTickers.Count x baseWeight for the adjusted tickers to
            // share, so the grand total across all N tickers still equals
            // N x baseWeight, same as pre-session).
            var activeBudget = activeTickers.Count * baseWeight;
            var scaleFactor = preRenormSum > 0 ? activeBudget / preRenormSum : 1.0;

            foreach (var ticker in activeTickers)
            {
                result[ticker] = preRenorm[ticker] * scaleFactor;
            }

            return result;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
