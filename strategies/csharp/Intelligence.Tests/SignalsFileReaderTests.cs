using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Xunit;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Unit tests for SignalsFileReader. Per this session's spec, this is
    /// one of the two most important test files in the whole session (along
    /// with PositionSizerTests' fail-safe cases): every failure mode here --
    /// missing file, empty file, a torn/unparseable trailing line, a mix of
    /// valid and invalid lines -- must degrade to an empty result and must
    /// never throw, since DualMomentumV2's entire fail-safe story depends on
    /// this reader never being able to abort a rebalance.
    ///
    /// Uses real temp files on disk (IDisposable cleanup per test) rather
    /// than mocking File I/O, since SignalsFileReader's whole job is reading
    /// real files and there's no abstraction seam here worth adding just for
    /// testability -- matches this project's existing HighWaterMarkStore
    /// test conventions (see services/HeadlineNewsPipeline.Tests).
    /// </summary>
    public class SignalsFileReaderTests : IDisposable
    {
        private readonly string _tempFilePath;

        public SignalsFileReaderTests()
        {
            _tempFilePath = Path.Combine(Path.GetTempPath(), $"signals-test-{Guid.NewGuid():N}.jsonl");
        }

        public void Dispose()
        {
            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }
        }

        private static string SerializeLine(Signal signal) => JsonConvert.SerializeObject(signal);

        private static Signal MakeSignal(string symbol, DateTime timestampUtc, double rawScore = 0.5)
        {
            return new Signal
            {
                Symbol = symbol,
                Direction = SignalDirection.Bullish,
                RawScore = rawScore,
                Confidence = 0.7,
                SourceWeight = 1.0,
                SourceModule = "Test",
                TimestampUtc = timestampUtc,
                Rationale = "test rationale",
            };
        }

        // =====================================================================
        // Missing / empty file -- must never throw.
        // =====================================================================

        [Fact]
        public void ReadRecentSignals_FileDoesNotExist_ReturnsEmptyWithoutThrowing()
        {
            var reader = new SignalsFileReader(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.jsonl"));

            var result = reader.ReadRecentSignals("AAPL", DateTime.UtcNow);

            Assert.Empty(result);
        }

        [Fact]
        public void ReadRecentSignals_EmptyFile_ReturnsEmptyWithoutThrowing()
        {
            File.WriteAllText(_tempFilePath, string.Empty);
            var reader = new SignalsFileReader(_tempFilePath);

            var result = reader.ReadRecentSignals("AAPL", DateTime.UtcNow);

            Assert.Empty(result);
        }

        // =====================================================================
        // Torn / malformed lines -- tolerated, skipped, never abort the read.
        // =====================================================================

        [Fact]
        public void ReadRecentSignals_TornTrailingLine_SkipsItButKeepsEarlierValidLines()
        {
            var now = DateTime.UtcNow;
            var goodSignal = MakeSignal("AAPL", now.AddDays(-1));
            var content = SerializeLine(goodSignal) + Environment.NewLine +
                          "{\"Symbol\":\"AAPL\",\"RawScore\":0.4,\"Confid"; // torn mid-write
            File.WriteAllText(_tempFilePath, content);

            var reader = new SignalsFileReader(_tempFilePath);
            var result = reader.ReadRecentSignals("AAPL", now);

            Assert.Single(result);
            Assert.Equal(goodSignal.RawScore, result[0].RawScore);
        }

        [Fact]
        public void ReadRecentSignals_MixOfValidAndInvalidLines_ReturnsOnlyValidMatchingOnes()
        {
            var now = DateTime.UtcNow;
            var valid1 = MakeSignal("AAPL", now.AddDays(-1), rawScore: 0.1);
            var valid2 = MakeSignal("AAPL", now.AddDays(-2), rawScore: 0.2);
            var lines = new List<string>
            {
                SerializeLine(valid1),
                "not even json",
                "{\"totally\": \"wrong shape but valid json\"}", // parses to a Signal with null Symbol -- must be skipped, not matched
                SerializeLine(valid2),
                "",
            };
            File.WriteAllLines(_tempFilePath, lines);

            var reader = new SignalsFileReader(_tempFilePath);
            var result = reader.ReadRecentSignals("AAPL", now);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, s => s.RawScore == 0.1);
            Assert.Contains(result, s => s.RawScore == 0.2);
        }

        // =====================================================================
        // Symbol filtering
        // =====================================================================

        [Fact]
        public void ReadRecentSignals_DifferentSymbol_Excluded()
        {
            var now = DateTime.UtcNow;
            File.WriteAllLines(_tempFilePath, new[]
            {
                SerializeLine(MakeSignal("AAPL", now.AddDays(-1))),
                SerializeLine(MakeSignal("MSFT", now.AddDays(-1))),
            });

            var reader = new SignalsFileReader(_tempFilePath);
            var result = reader.ReadRecentSignals("AAPL", now);

            Assert.Single(result);
            Assert.Equal("AAPL", result[0].Symbol);
        }

        [Fact]
        public void ReadRecentSignals_SymbolMatchIsCaseInsensitive()
        {
            var now = DateTime.UtcNow;
            File.WriteAllLines(_tempFilePath, new[] { SerializeLine(MakeSignal("aapl", now.AddDays(-1))) });

            var reader = new SignalsFileReader(_tempFilePath);
            var result = reader.ReadRecentSignals("AAPL", now);

            Assert.Single(result);
        }

        // =====================================================================
        // 7-day lookback window
        // =====================================================================

        [Fact]
        public void ReadRecentSignals_SignalOlderThan7Days_Excluded()
        {
            var now = DateTime.UtcNow;
            var tooOld = now - SignalsFileReader.LookbackWindow - TimeSpan.FromMinutes(1);
            File.WriteAllLines(_tempFilePath, new[] { SerializeLine(MakeSignal("AAPL", tooOld)) });

            var reader = new SignalsFileReader(_tempFilePath);
            var result = reader.ReadRecentSignals("AAPL", now);

            Assert.Empty(result);
        }

        [Fact]
        public void ReadRecentSignals_SignalExactlyAtLookbackBoundary_Included()
        {
            var now = DateTime.UtcNow;
            var exactlyAtCutoff = now - SignalsFileReader.LookbackWindow;
            File.WriteAllLines(_tempFilePath, new[] { SerializeLine(MakeSignal("AAPL", exactlyAtCutoff)) });

            var reader = new SignalsFileReader(_tempFilePath);
            var result = reader.ReadRecentSignals("AAPL", now);

            Assert.Single(result);
        }

        [Fact]
        public void ReadRecentSignals_SignalWellWithinWindow_Included()
        {
            var now = DateTime.UtcNow;
            File.WriteAllLines(_tempFilePath, new[] { SerializeLine(MakeSignal("AAPL", now.AddDays(-3))) });

            var reader = new SignalsFileReader(_tempFilePath);
            var result = reader.ReadRecentSignals("AAPL", now);

            Assert.Single(result);
        }
    }
}
