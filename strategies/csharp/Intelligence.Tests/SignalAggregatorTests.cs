using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Unit tests for SignalAggregator, covering all four AggregationModes'
    /// exact math per this session's spec: agreement, disagreement, a
    /// single signal, zero signals, and mixed confidence levels. Special
    /// emphasis on ConsensusOnly forcing Neutral/zero on any disagreement,
    /// and on CapitalSplit's Aggregate() output matching ConfidenceWeighted
    /// exactly (the cohort-relative logic that actually distinguishes
    /// CapitalSplit lives in PositionSizer, not here -- see
    /// PositionSizerTests for that).
    /// </summary>
    public class SignalAggregatorTests
    {
        private static Signal MakeSignal(
            SignalDirection direction, double rawScore, double confidence,
            double sourceWeight = 1.0, string symbol = "AAPL")
        {
            return new Signal
            {
                Symbol = symbol,
                Direction = direction,
                RawScore = rawScore,
                Confidence = confidence,
                SourceWeight = sourceWeight,
                SourceModule = "Test",
                TimestampUtc = DateTime.UtcNow,
                Rationale = "test rationale",
            };
        }

        // =====================================================================
        // Zero signals -- every mode must degrade to neutral/zero, never throw.
        // =====================================================================

        [Theory]
        [InlineData(AggregationMode.WeightedVote)]
        [InlineData(AggregationMode.ConfidenceWeighted)]
        [InlineData(AggregationMode.ConsensusOnly)]
        [InlineData(AggregationMode.CapitalSplit)]
        public void Aggregate_EmptySignalList_ReturnsNeutralZeroForEveryMode(AggregationMode mode)
        {
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", Enumerable.Empty<Signal>(), mode);

            Assert.Equal("AAPL", result.Symbol);
            Assert.Equal(SignalDirection.Neutral, result.Direction);
            Assert.Equal(0.0, result.CombinedScore);
            Assert.Equal(0.0, result.CombinedConfidence);
            Assert.Equal(0, result.ContributingSignalCount);
            Assert.Equal(mode, result.ModeUsed);
        }

        [Fact]
        public void Aggregate_NullSignalList_TreatedAsEmpty_NeverThrows()
        {
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", null, AggregationMode.CapitalSplit);

            Assert.Equal(0, result.ContributingSignalCount);
            Assert.Equal(0.0, result.CombinedScore);
        }

        // =====================================================================
        // WeightedVote
        // =====================================================================

        [Fact]
        public void WeightedVote_MajorityDirectionWins_ScoreAndConfidenceOnlyFromAgreeingSignals()
        {
            var signals = new List<Signal>
            {
                MakeSignal(SignalDirection.Bullish, rawScore: 0.6, confidence: 0.8, sourceWeight: 2.0),
                MakeSignal(SignalDirection.Bullish, rawScore: 0.4, confidence: 0.6, sourceWeight: 1.0),
                MakeSignal(SignalDirection.Bearish, rawScore: -0.5, confidence: 0.9, sourceWeight: 1.0),
            };
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", signals, AggregationMode.WeightedVote);

            // Bullish total weight (3.0) beats Bearish (1.0).
            Assert.Equal(SignalDirection.Bullish, result.Direction);
            // Weighted avg RawScore among the two agreeing (Bullish) signals only.
            Assert.Equal((0.6 * 2.0 + 0.4 * 1.0) / 3.0, result.CombinedScore, 9);
            Assert.Equal((0.8 * 2.0 + 0.6 * 1.0) / 3.0, result.CombinedConfidence, 9);
            // All 3 input signals contributed to determining the outcome.
            Assert.Equal(3, result.ContributingSignalCount);
            Assert.Equal(AggregationMode.WeightedVote, result.ModeUsed);
        }

        [Fact]
        public void WeightedVote_SingleSignal_ResultEqualsThatSignal()
        {
            var signals = new List<Signal> { MakeSignal(SignalDirection.Bearish, -0.3, 0.5, sourceWeight: 1.0) };
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("TSLA", signals, AggregationMode.WeightedVote);

            Assert.Equal(SignalDirection.Bearish, result.Direction);
            Assert.Equal(-0.3, result.CombinedScore, 9);
            Assert.Equal(0.5, result.CombinedConfidence, 9);
            Assert.Equal(1, result.ContributingSignalCount);
        }

        // =====================================================================
        // ConfidenceWeighted
        // =====================================================================

        [Fact]
        public void ConfidenceWeighted_MixedConfidenceLevels_LowConfidenceSignalBarelyMovesTheResult()
        {
            var signals = new List<Signal>
            {
                MakeSignal(SignalDirection.Bullish, rawScore: 0.8, confidence: 0.9),
                // Low confidence -- should barely move the combined result,
                // per the "weight = SourceWeight x Confidence" formula.
                MakeSignal(SignalDirection.Bearish, rawScore: -0.2, confidence: 0.1),
            };
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", signals, AggregationMode.ConfidenceWeighted);

            var w1 = 1.0 * 0.9;
            var w2 = 1.0 * 0.1;
            var expectedScore = (0.8 * w1 + (-0.2) * w2) / (w1 + w2);
            var expectedConfidence = (0.9 * w1 + 0.1 * w2) / (w1 + w2);

            Assert.Equal(expectedScore, result.CombinedScore, 9);
            Assert.Equal(expectedConfidence, result.CombinedConfidence, 9);
            // Direction derived from the sign of CombinedScore, not voted.
            Assert.Equal(SignalDirection.Bullish, result.Direction);
            Assert.Equal(2, result.ContributingSignalCount);
        }

        [Fact]
        public void ConfidenceWeighted_AllSignalsContribute_EvenTheLosingDirection()
        {
            // Unlike WeightedVote, a disagreeing signal still pulls the
            // combined score -- there is no "losing side" excluded here.
            var signals = new List<Signal>
            {
                MakeSignal(SignalDirection.Bullish, rawScore: 0.5, confidence: 0.5),
                MakeSignal(SignalDirection.Bearish, rawScore: -0.5, confidence: 0.5),
            };
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", signals, AggregationMode.ConfidenceWeighted);

            Assert.Equal(0.0, result.CombinedScore, 9);
            Assert.Equal(SignalDirection.Neutral, result.Direction);
        }

        [Fact]
        public void ConfidenceWeighted_ZeroConfidenceAcrossAllSignals_ReturnsZeroNotDivideByZeroCrash()
        {
            var signals = new List<Signal>
            {
                MakeSignal(SignalDirection.Bullish, rawScore: 0.9, confidence: 0.0),
            };
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", signals, AggregationMode.ConfidenceWeighted);

            Assert.Equal(0.0, result.CombinedScore);
            Assert.Equal(0.0, result.CombinedConfidence);
            Assert.Equal(SignalDirection.Neutral, result.Direction);
            // The signal still "contributed" (it was considered) even
            // though its own zero confidence gave it zero actual weight.
            Assert.Equal(1, result.ContributingSignalCount);
        }

        // =====================================================================
        // ConsensusOnly
        // =====================================================================

        [Fact]
        public void ConsensusOnly_FullAgreementOnNonNeutralDirection_PassesThroughWeightedVoteResult()
        {
            var signals = new List<Signal>
            {
                MakeSignal(SignalDirection.Bullish, 0.5, 0.7, sourceWeight: 1.0),
                MakeSignal(SignalDirection.Bullish, 0.3, 0.6, sourceWeight: 2.0),
            };
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", signals, AggregationMode.ConsensusOnly);

            Assert.Equal(SignalDirection.Bullish, result.Direction);
            Assert.Equal((0.5 * 1.0 + 0.3 * 2.0) / 3.0, result.CombinedScore, 9);
            Assert.Equal(AggregationMode.ConsensusOnly, result.ModeUsed);
            Assert.Equal(2, result.ContributingSignalCount);
        }

        [Fact]
        public void ConsensusOnly_AnyDisagreement_ForcesNeutralZeroRegardlessOfMajority()
        {
            // Bullish has more total weight, but ANY dissent -- even a
            // single low-weight dissenting signal -- must zero the result
            // out entirely per this session's spec (no partial/threshold
            // agreement).
            var signals = new List<Signal>
            {
                MakeSignal(SignalDirection.Bullish, 0.7, 0.9, sourceWeight: 5.0),
                MakeSignal(SignalDirection.Bearish, -0.6, 0.9, sourceWeight: 0.1),
            };
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", signals, AggregationMode.ConsensusOnly);

            Assert.Equal(SignalDirection.Neutral, result.Direction);
            Assert.Equal(0.0, result.CombinedScore);
            Assert.Equal(0.0, result.CombinedConfidence);
            // Signals existed and were considered -- this is "disagreement",
            // not "zero signals", so the count reflects the real input size.
            Assert.Equal(2, result.ContributingSignalCount);
        }

        [Fact]
        public void ConsensusOnly_AllSignalsNeutral_StillForcesNeutralZero()
        {
            // An all-Neutral "consensus" doesn't pass through either -- per
            // spec, the pass-through path requires a non-Neutral winning
            // direction.
            var signals = new List<Signal>
            {
                MakeSignal(SignalDirection.Neutral, 0.05, 0.5),
                MakeSignal(SignalDirection.Neutral, -0.02, 0.4),
            };
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", signals, AggregationMode.ConsensusOnly);

            Assert.Equal(SignalDirection.Neutral, result.Direction);
            Assert.Equal(0.0, result.CombinedScore);
            Assert.Equal(2, result.ContributingSignalCount);
        }

        [Fact]
        public void ConsensusOnly_SingleSignal_TrivialConsensusPassesThrough()
        {
            var signals = new List<Signal> { MakeSignal(SignalDirection.Bearish, -0.4, 0.6) };
            var aggregator = new SignalAggregator();

            var result = aggregator.Aggregate("AAPL", signals, AggregationMode.ConsensusOnly);

            Assert.Equal(SignalDirection.Bearish, result.Direction);
            Assert.Equal(-0.4, result.CombinedScore, 9);
            Assert.Equal(1, result.ContributingSignalCount);
        }

        // =====================================================================
        // CapitalSplit -- Aggregate() output must match ConfidenceWeighted
        // exactly; the cohort-relative logic lives in PositionSizer instead.
        // =====================================================================

        [Fact]
        public void CapitalSplit_AggregateOutput_MatchesConfidenceWeightedExactly_ExceptModeUsed()
        {
            var signals = new List<Signal>
            {
                MakeSignal(SignalDirection.Bullish, 0.5, 0.8, sourceWeight: 1.5),
                MakeSignal(SignalDirection.Bearish, -0.3, 0.4, sourceWeight: 0.7),
                MakeSignal(SignalDirection.Neutral, 0.05, 0.2, sourceWeight: 1.0),
            };
            var aggregator = new SignalAggregator();

            var confidenceWeighted = aggregator.Aggregate("AAPL", signals, AggregationMode.ConfidenceWeighted);
            var capitalSplit = aggregator.Aggregate("AAPL", signals, AggregationMode.CapitalSplit);

            Assert.Equal(confidenceWeighted.CombinedScore, capitalSplit.CombinedScore, 12);
            Assert.Equal(confidenceWeighted.CombinedConfidence, capitalSplit.CombinedConfidence, 12);
            Assert.Equal(confidenceWeighted.Direction, capitalSplit.Direction);
            Assert.Equal(confidenceWeighted.ContributingSignalCount, capitalSplit.ContributingSignalCount);

            Assert.Equal(AggregationMode.ConfidenceWeighted, confidenceWeighted.ModeUsed);
            Assert.Equal(AggregationMode.CapitalSplit, capitalSplit.ModeUsed);
        }
    }
}
