using System;
using System.IO;
using Xunit;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Unit tests for AggregatorConfigReader. Per this session's spec, all
    /// three failure modes -- missing file, empty file, unrecognized mode
    /// string -- must fall back to AggregatorConfigReader.DefaultMode
    /// (CapitalSplit) without ever throwing, since a config-read problem
    /// must never be able to block a rebalance.
    /// </summary>
    public class AggregatorConfigReaderTests : IDisposable
    {
        private readonly string _tempFilePath;

        public AggregatorConfigReaderTests()
        {
            _tempFilePath = Path.Combine(Path.GetTempPath(), $"aggregator-config-test-{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }
        }

        [Fact]
        public void ReadActiveMode_FileDoesNotExist_FallsBackToDefaultMode()
        {
            var reader = new AggregatorConfigReader(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"));

            var mode = reader.ReadActiveMode();

            Assert.Equal(AggregatorConfigReader.DefaultMode, mode);
        }

        [Fact]
        public void ReadActiveMode_EmptyFile_FallsBackToDefaultMode()
        {
            File.WriteAllText(_tempFilePath, string.Empty);
            var reader = new AggregatorConfigReader(_tempFilePath);

            var mode = reader.ReadActiveMode();

            Assert.Equal(AggregatorConfigReader.DefaultMode, mode);
        }

        [Fact]
        public void ReadActiveMode_InvalidJson_FallsBackToDefaultMode()
        {
            File.WriteAllText(_tempFilePath, "{ not valid json at all");
            var reader = new AggregatorConfigReader(_tempFilePath);

            var mode = reader.ReadActiveMode();

            Assert.Equal(AggregatorConfigReader.DefaultMode, mode);
        }

        [Fact]
        public void ReadActiveMode_UnrecognizedModeString_FallsBackToDefaultMode()
        {
            File.WriteAllText(_tempFilePath, "{ \"ActiveMode\": \"QuantumEntanglementMode\" }");
            var reader = new AggregatorConfigReader(_tempFilePath);

            var mode = reader.ReadActiveMode();

            Assert.Equal(AggregatorConfigReader.DefaultMode, mode);
        }

        [Fact]
        public void ReadActiveMode_ActiveModeMissingFromJson_FallsBackToDefaultMode()
        {
            File.WriteAllText(_tempFilePath, "{ \"SomeOtherField\": 123 }");
            var reader = new AggregatorConfigReader(_tempFilePath);

            var mode = reader.ReadActiveMode();

            Assert.Equal(AggregatorConfigReader.DefaultMode, mode);
        }

        [Theory]
        [InlineData("WeightedVote", AggregationMode.WeightedVote)]
        [InlineData("ConfidenceWeighted", AggregationMode.ConfidenceWeighted)]
        [InlineData("ConsensusOnly", AggregationMode.ConsensusOnly)]
        [InlineData("CapitalSplit", AggregationMode.CapitalSplit)]
        public void ReadActiveMode_RecognizedModeString_ReturnsThatMode(string modeString, AggregationMode expected)
        {
            File.WriteAllText(_tempFilePath, $"{{ \"ActiveMode\": \"{modeString}\" }}");
            var reader = new AggregatorConfigReader(_tempFilePath);

            var mode = reader.ReadActiveMode();

            Assert.Equal(expected, mode);
        }

        [Fact]
        public void ReadActiveMode_ModeStringIsCaseInsensitive()
        {
            File.WriteAllText(_tempFilePath, "{ \"ActiveMode\": \"weightedvote\" }");
            var reader = new AggregatorConfigReader(_tempFilePath);

            var mode = reader.ReadActiveMode();

            Assert.Equal(AggregationMode.WeightedVote, mode);
        }
    }
}
