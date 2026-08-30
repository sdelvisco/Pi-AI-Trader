using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Unit tests for PositionSizer -- the position-sizing math DualMomentumV2
    /// wires into its existing top-N allocation branch. These are the most
    /// important tests in this session alongside SignalsFileReaderTests'
    /// fail-safe cases: they confirm the clamp bound is respected, the
    /// renormalization keeps total invested capital unchanged, a
    /// zero-contributing-signal ticker keeps its EXACT original weight, and
    /// -- critically -- that a fully signal-less cohort (the real-world
    /// "signals.jsonl is missing" scenario) produces output byte-for-byte
    /// identical to pre-session equal-weight allocation.
    /// </summary>
    public class PositionSizerTests
    {
        private const double BaseWeight = 0.2; // 1/5, matching DualMomentumV2's TopPositions=5

        private static AggregatedSignal MakeAgg(double score, double confidence, int contributingCount = 1)
        {
            return new AggregatedSignal
            {
                Symbol = "X",
                Direction = SignalDirection.Neutral,
                CombinedScore = score,
                CombinedConfidence = confidence,
                ModeUsed = AggregationMode.ConfidenceWeighted,
                ContributingSignalCount = contributingCount,
            };
        }

        // =====================================================================
        // Fail-safe: the scenario that matters most in this entire session.
        // =====================================================================

        [Fact]
        public void ComputeAdjustedWeights_AllTickersZeroSignals_IdenticalToPreSessionEqualWeight()
        {
            var tickers = new List<string> { "A", "B", "C", "D", "E" };
            // Simulates the real "signals.jsonl missing" fail-safe path:
            // every ticker's AggregatedSignal has ContributingSignalCount == 0.
            var signalsByTicker = tickers.ToDictionary(t => t, t => MakeAgg(0.0, 0.0, contributingCount: 0));

            var result = PositionSizer.ComputeAdjustedWeights(tickers, BaseWeight, signalsByTicker, AggregationMode.CapitalSplit);

            foreach (var ticker in tickers)
            {
                // Exact equality -- not "close to" -- since this must be
                // byte-for-byte identical to today's equal-weight behavior.
                Assert.Equal(BaseWeight, result[ticker]);
            }
            Assert.Equal(tickers.Count * BaseWeight, result.Values.Sum(), 12);
        }

        [Fact]
        public void ComputeAdjustedWeights_MissingSignalEntryForTicker_TreatedSameAsZeroSignals()
        {
            // Defensive case: a caller bug that fails to populate an entry
            // for some ticker must not throw or silently misprice it --
            // it degrades the same as an explicit zero-signal entry.
            var tickers = new List<string> { "A", "B" };
            var signalsByTicker = new Dictionary<string, AggregatedSignal>
            {
                ["A"] = MakeAgg(0.5, 0.5, contributingCount: 1),
                // "B" deliberately missing.
            };

            var result = PositionSizer.ComputeAdjustedWeights(tickers, BaseWeight, signalsByTicker, AggregationMode.ConfidenceWeighted);

            // "A" is the sole active ticker; renormalized against an
            // activeBudget of just its own baseWeight forces it right back
            // to baseWeight regardless of its adjustment.
            Assert.Equal(BaseWeight, result["A"], 9);
            Assert.Equal(BaseWeight, result["B"]);
        }

        [Fact]
        public void ComputeAdjustedWeights_ZeroSignalTickerAmongActiveTickers_KeepsExactOriginalWeight()
        {
            var tickers = new List<string> { "A", "B", "C" };
            var signalsByTicker = new Dictionary<string, AggregatedSignal>
            {
                ["A"] = MakeAgg(0.8, 0.8, contributingCount: 3),
                ["B"] = MakeAgg(0.0, 0.0, contributingCount: 0), // no recent signals
                ["C"] = MakeAgg(-0.6, 0.5, contributingCount: 2),
            };

            var result = PositionSizer.ComputeAdjustedWeights(tickers, BaseWeight, signalsByTicker, AggregationMode.ConfidenceWeighted);

            // B must be EXACTLY baseWeight, unaffected by A/C's adjustments.
            Assert.Equal(BaseWeight, result["B"]);
            // Total invested capital across all 3 positions is unchanged.
            Assert.Equal(3 * BaseWeight, result["A"] + result["B"] + result["C"], 9);
        }

        // =====================================================================
        // Clamp bounds
        // =====================================================================

        [Fact]
        public void ComputeAdjustedWeights_ExtremeScores_ClampToPlusMinus50PercentSwing()
        {
            var tickers = new List<string> { "A", "B" };
            var signalsByTicker = new Dictionary<string, AggregatedSignal>
            {
                ["A"] = MakeAgg(1.0, 1.0), // raw adjustment = +1.0 -> +50% swing
                ["B"] = MakeAgg(-1.0, 1.0), // raw adjustment = -1.0 -> -50% swing
            };

            // Non-CapitalSplit mode: A and B's swings exactly offset around
            // baseWeight, so renormalization is a no-op and the clamp bound
            // is directly observable in the final result.
            var result = PositionSizer.ComputeAdjustedWeights(tickers, BaseWeight, signalsByTicker, AggregationMode.ConfidenceWeighted);

            Assert.Equal(BaseWeight * 1.5, result["A"], 9); // 30%
            Assert.Equal(BaseWeight * 0.5, result["B"], 9); // 10%
        }

        [Fact]
        public void ComputeAdjustedWeights_OutOfContractScoreBeyondUnitRange_StillClampedToBound()
        {
            // AggregatedSignal.CombinedScore is documented as -1..1, but this
            // test simulates a future upstream bug producing an out-of-range
            // value, to prove PositionSizer's own explicit clamp holds
            // regardless -- per this session's spec, the ±50% bound must not
            // depend solely on the upstream range being respected.
            var tickers = new List<string> { "A", "B" };
            var signalsByTicker = new Dictionary<string, AggregatedSignal>
            {
                ["A"] = MakeAgg(5.0, 1.0),
                ["B"] = MakeAgg(-5.0, 1.0),
            };

            var result = PositionSizer.ComputeAdjustedWeights(tickers, BaseWeight, signalsByTicker, AggregationMode.ConfidenceWeighted);

            Assert.Equal(BaseWeight * 1.5, result["A"], 9);
            Assert.Equal(BaseWeight * 0.5, result["B"], 9);
        }

        // =====================================================================
        // Renormalization
        // =====================================================================

        [Fact]
        public void ComputeAdjustedWeights_MixOfActiveTickers_RenormalizedSumEqualsOriginalTotal()
        {
            var tickers = new List<string> { "A", "B", "C", "D", "E" };
            var signalsByTicker = new Dictionary<string, AggregatedSignal>
            {
                ["A"] = MakeAgg(0.9, 0.9, 4),
                ["B"] = MakeAgg(0.3, 0.4, 2),
                ["C"] = MakeAgg(0.0, 0.0, 0), // zero signals
                ["D"] = MakeAgg(-0.5, 0.6, 3),
                ["E"] = MakeAgg(-0.2, 0.2, 1),
            };

            foreach (var mode in new[]
                     {
                         AggregationMode.WeightedVote, AggregationMode.ConfidenceWeighted,
                         AggregationMode.ConsensusOnly, AggregationMode.CapitalSplit,
                     })
            {
                var result = PositionSizer.ComputeAdjustedWeights(tickers, BaseWeight, signalsByTicker, mode);
                Assert.Equal(tickers.Count * BaseWeight, result.Values.Sum(), 9);
                Assert.Equal(BaseWeight, result["C"]); // zero-signal ticker untouched regardless of mode
            }
        }

        // =====================================================================
        // CapitalSplit's cohort-relative rescaling differs from the other
        // three modes given the exact same per-ticker raw scores.
        // =====================================================================

        [Fact]
        public void ComputeAdjustedWeights_CapitalSplit_RescalesRelativeToCohort_UnlikeOtherModes()
        {
            var tickers = new List<string> { "A", "B", "C" };
            // Raw adjustments: A=+0.40 (strongest positive), B=+0.20
            // (weaker positive), C=-0.10 (only negative). None of these
            // individually hit the ±1.0 raw bound.
            var signalsByTicker = new Dictionary<string, AggregatedSignal>
            {
                ["A"] = MakeAgg(0.5, 0.8),  // raw = 0.40
                ["B"] = MakeAgg(0.4, 0.5),  // raw = 0.20
                ["C"] = MakeAgg(-0.2, 0.5), // raw = -0.10
            };

            var nonCapitalSplit = PositionSizer.ComputeAdjustedWeights(tickers, BaseWeight, signalsByTicker, AggregationMode.ConfidenceWeighted);
            var capitalSplit = PositionSizer.ComputeAdjustedWeights(tickers, BaseWeight, signalsByTicker, AggregationMode.CapitalSplit);

            // Both preserve total invested capital...
            Assert.Equal(tickers.Count * BaseWeight, nonCapitalSplit.Values.Sum(), 9);
            Assert.Equal(tickers.Count * BaseWeight, capitalSplit.Values.Sum(), 9);

            // ...but CapitalSplit rescales A's (strongest positive) weight
            // higher than the fixed-scale mode does, since A gets pushed to
            // the full +50% bound (it's the cohort's strongest positive)
            // rather than only a proportional +20% swing.
            Assert.True(capitalSplit["A"] > nonCapitalSplit["A"],
                $"Expected CapitalSplit A ({capitalSplit["A"]}) > non-CapitalSplit A ({nonCapitalSplit["A"]})");

            // C (the cohort's only negative, and its strongest-magnitude
            // negative by definition) gets pushed further down under
            // CapitalSplit too, toward the full -50% bound.
            Assert.True(capitalSplit["C"] < nonCapitalSplit["C"],
                $"Expected CapitalSplit C ({capitalSplit["C"]}) < non-CapitalSplit C ({nonCapitalSplit["C"]})");
        }

        [Fact]
        public void ComputeAdjustedWeights_CapitalSplit_StrongestScoresHitFullClampBounds()
        {
            var tickers = new List<string> { "A", "B", "C" };
            var signalsByTicker = new Dictionary<string, AggregatedSignal>
            {
                ["A"] = MakeAgg(0.5, 0.8),  // raw = 0.40 -- strongest positive in cohort
                ["B"] = MakeAgg(0.4, 0.5),  // raw = 0.20 -- weaker positive
                ["C"] = MakeAgg(-0.2, 0.5), // raw = -0.10 -- only (and thus strongest) negative
            };

            var result = PositionSizer.ComputeAdjustedWeights(tickers, BaseWeight, signalsByTicker, AggregationMode.CapitalSplit);

            // Hand-computed expected values (see PR description / DEVIATIONS.md
            // for the full derivation): pre-renormalization weights are
            // A=0.30, B=0.25, C=0.10 (sum 0.65); renormalized against an
            // activeBudget of 3*0.2=0.6 via scaleFactor=0.6/0.65=12/13.
            Assert.Equal(0.30 * 12.0 / 13.0, result["A"], 9);
            Assert.Equal(0.25 * 12.0 / 13.0, result["B"], 9);
            Assert.Equal(0.10 * 12.0 / 13.0, result["C"], 9);
        }
    }
}
