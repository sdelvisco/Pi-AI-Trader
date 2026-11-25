using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Unit tests for UniverseConfigReader, mirroring AggregatorConfigReaderTests:
    /// a temp file per test, one Fact per failure mode. Every failure mode
    /// must return the caller's built-in list with
    /// Source=HardcodedFallback and a reason, and must never throw.
    /// </summary>
    public class UniverseConfigReaderTests : IDisposable
    {
        // Small stand-in for DualMomentumV2's BuiltInUniverseTickers, so a
        // fallback is easy to tell apart from a list read from the file.
        private static readonly string[] Fallback = { "SPY", "AGG", "QQQ", "TLT" };

        // Per-test temp file path (deleted in Dispose).
        private readonly string _tempFilePath;

        public UniverseConfigReaderTests()
        {
            _tempFilePath = Path.Combine(Path.GetTempPath(), $"universe-test-{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }
        }

        // Builds a reader over the temp file with the default required
        // tickers (AGG and SPY).
        private UniverseConfigReader MakeReader() => new UniverseConfigReader(_tempFilePath, Fallback);

        // Asserts that a result is the built-in fallback list.
        private static void AssertFallback(UniverseReadResult result)
        {
            Assert.Equal(UniverseConfigSource.HardcodedFallback, result.Source);
            Assert.Equal("hardcoded-fallback", result.SourceLabel);
            Assert.Equal(Fallback, result.Tickers.ToArray());
            Assert.False(string.IsNullOrWhiteSpace(result.FallbackReason));
        }

        [Fact]
        public void Read_ValidFile_ReturnsFileTickersInOrder()
        {
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [\"SPY\", \"NVDA\", \"AGG\", \"BRK.B\"] }");

            var result = MakeReader().Read();

            Assert.Equal(UniverseConfigSource.File, result.Source);
            Assert.Equal("file", result.SourceLabel);
            Assert.Null(result.FallbackReason);
            Assert.Equal(new[] { "SPY", "NVDA", "AGG", "BRK.B" }, result.Tickers.ToArray());
        }

        [Fact]
        public void Read_FileDoesNotExist_FallsBack()
        {
            var reader = new UniverseConfigReader(
                Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"), Fallback);

            AssertFallback(reader.Read());
        }

        [Fact]
        public void Read_EmptyFile_FallsBack()
        {
            File.WriteAllText(_tempFilePath, string.Empty);

            AssertFallback(MakeReader().Read());
        }

        [Fact]
        public void Read_WhitespaceOnlyFile_FallsBack()
        {
            File.WriteAllText(_tempFilePath, "   \n  ");

            AssertFallback(MakeReader().Read());
        }

        [Fact]
        public void Read_MalformedJson_FallsBack()
        {
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [\"SPY\", ");

            AssertFallback(MakeReader().Read());
        }

        [Fact]
        public void Read_TickersNotAnArray_FallsBack()
        {
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": \"SPY,AGG\" }");

            AssertFallback(MakeReader().Read());
        }

        [Fact]
        public void Read_TickersKeyMissing_FallsBack()
        {
            File.WriteAllText(_tempFilePath, "{ \"SomeOtherField\": 123 }");

            AssertFallback(MakeReader().Read());
        }

        [Fact]
        public void Read_EmptyTickerArray_FallsBack()
        {
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [] }");

            AssertFallback(MakeReader().Read());
        }

        [Fact]
        public void Read_MissingAgg_FallsBack()
        {
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [\"SPY\", \"QQQ\"] }");

            var result = MakeReader().Read();

            AssertFallback(result);
            Assert.Contains("AGG", result.FallbackReason);
        }

        [Fact]
        public void Read_MissingSpy_FallsBack()
        {
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [\"AGG\", \"QQQ\"] }");

            var result = MakeReader().Read();

            AssertFallback(result);
            Assert.Contains("SPY", result.FallbackReason);
        }

        [Fact]
        public void Read_DuplicateTicker_FallsBack()
        {
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [\"SPY\", \"AGG\", \"QQQ\", \"QQQ\"] }");

            var result = MakeReader().Read();

            AssertFallback(result);
            Assert.Contains("duplicate", result.FallbackReason);
        }

        [Fact]
        public void Read_DuplicateDifferingOnlyByCase_FallsBack()
        {
            // "spy" upper-cases to "SPY", so this is a duplicate.
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [\"SPY\", \"AGG\", \"spy\"] }");

            AssertFallback(MakeReader().Read());
        }

        [Fact]
        public void Read_LowerCaseAndPaddedTickers_AreNormalizedToUpperCase()
        {
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [\" spy \", \"agg\", \"Nvda\"] }");

            var result = MakeReader().Read();

            Assert.Equal(UniverseConfigSource.File, result.Source);
            Assert.Equal(new[] { "SPY", "AGG", "NVDA" }, result.Tickers.ToArray());
        }

        [Theory]
        [InlineData("SP Y")]
        [InlineData("TOOLONG")]
        [InlineData("BRK/B")]
        [InlineData("123")]
        [InlineData("")]
        [InlineData("BRK.BBB")]
        public void Read_InvalidTickerSyntax_FallsBack(string badTicker)
        {
            File.WriteAllText(_tempFilePath, $"{{ \"Tickers\": [\"SPY\", \"AGG\", \"{badTicker}\"] }}");

            AssertFallback(MakeReader().Read());
        }

        [Fact]
        public void Read_NullEntry_FallsBack()
        {
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [\"SPY\", \"AGG\", null] }");

            AssertFallback(MakeReader().Read());
        }

        [Fact]
        public void Read_OverMaximumSize_FallsBack()
        {
            // SPY and AGG plus enough distinct, syntactically valid tickers to
            // go one over MaxTickers. Generated as AAAA, AAAB, ... so every
            // entry passes the ticker pattern and only the size rule fails.
            var tickers = new System.Collections.Generic.List<string> { "SPY", "AGG" };
            for (var i = 0; tickers.Count <= UniverseConfigReader.MaxTickers; i++)
            {
                tickers.Add("Z" + (char)('A' + (i / 26 % 26)) + (char)('A' + (i % 26)));
            }
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [" + string.Join(",", tickers.Select(t => $"\"{t}\"")) + "] }");

            var result = MakeReader().Read();

            AssertFallback(result);
            Assert.Contains("maximum", result.FallbackReason);
        }

        [Fact]
        public void Read_ExactlyMaximumSize_IsAccepted()
        {
            var tickers = new System.Collections.Generic.List<string> { "SPY", "AGG" };
            for (var i = 0; tickers.Count < UniverseConfigReader.MaxTickers; i++)
            {
                tickers.Add("Z" + (char)('A' + (i / 26 % 26)) + (char)('A' + (i % 26)));
            }
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [" + string.Join(",", tickers.Select(t => $"\"{t}\"")) + "] }");

            var result = MakeReader().Read();

            Assert.Equal(UniverseConfigSource.File, result.Source);
            Assert.Equal(UniverseConfigReader.MaxTickers, result.Tickers.Count);
        }

        [Fact]
        public void Read_CustomRequiredTickers_AreEnforced()
        {
            // DualMomentumV2 passes its own DefensiveTicker/AbsMomReferenceTicker
            // constants; a different required set must be honoured.
            File.WriteAllText(_tempFilePath, "{ \"Tickers\": [\"SPY\", \"AGG\"] }");
            var reader = new UniverseConfigReader(_tempFilePath, Fallback, new[] { "SPY", "TLT" });

            var result = reader.Read();

            AssertFallback(result);
            Assert.Contains("TLT", result.FallbackReason);
        }

        [Fact]
        public void Read_UnknownExtraFields_AreIgnored()
        {
            File.WriteAllText(_tempFilePath, "{ \"_comment\": \"hand note\", \"Tickers\": [\"SPY\", \"AGG\"] }");

            var result = MakeReader().Read();

            Assert.Equal(UniverseConfigSource.File, result.Source);
        }

        [Fact]
        public void Constructor_EmptyFallback_Throws()
        {
            Assert.Throws<ArgumentException>(() => new UniverseConfigReader(_tempFilePath, new string[0]));
        }

        [Fact]
        public void Read_SeedFileInRepo_IsValid()
        {
            // config/universe.default.json is what `make deploy` installs on
            // first deploy; it must pass the same rules or every consumer would
            // log a fallback. Located relative to this test assembly's output
            // folder (strategies/csharp/Intelligence.Tests/bin/<cfg>/net10.0/).
            var seedPath = FindRepoFile(Path.Combine("config", "universe.default.json"));
            var reader = new UniverseConfigReader(seedPath, Fallback);

            var result = reader.Read();

            Assert.Equal(UniverseConfigSource.File, result.Source);
            Assert.Contains("AGG", result.Tickers);
            Assert.Contains("SPY", result.Tickers);
        }

        // Walks up from the test output folder until relativePath exists.
        private static string FindRepoFile(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException($"Could not find {relativePath} above {AppContext.BaseDirectory}");
        }
    }
}
