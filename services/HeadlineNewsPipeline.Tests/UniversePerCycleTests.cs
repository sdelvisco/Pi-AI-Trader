using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PiAiTrader.HeadlineNewsPipeline.Tests
{
    /// <summary>
    /// Tests that PollCycleRunner re-reads universe.json at the start of
    /// EVERY poll cycle (through a file-backed TickerUniverse) and falls back
    /// to TickerUniverse.BuiltInTickers when the file is missing or invalid.
    /// Uses the same temp-dir + fakes setup as PollCycleRunnerTests.
    /// </summary>
    public class UniversePerCycleTests : IDisposable
    {
        // Per-test temp dir holding state.json, signals.jsonl and universe.json.
        private readonly string _tempDir;

        // Path of the universe file the TickerUniverse under test reads.
        private readonly string _universePath;

        public UniversePerCycleTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hnp-universe-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _universePath = Path.Combine(_tempDir, "universe.json");
        }

        public void Dispose()
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        // Writes universe.json with the given tickers, the way the portal does.
        private void WriteUniverse(params string[] tickers)
        {
            File.WriteAllText(_universePath,
                "{ \"Tickers\": [" + string.Join(", ", tickers.Select(t => $"\"{t}\"")) + "] }");
        }

        private static AlpacaNewsArticle MakeArticle(long id, string headline, params string[] symbols)
        {
            return new AlpacaNewsArticle { Id = id, Headline = headline, CreatedAtUtc = DateTime.UtcNow, Symbols = symbols };
        }

        // Builds a runner over a file-backed TickerUniverse at _universePath.
        private PollCycleRunner MakeRunner(FakeAlpacaNewsSource newsSource, FakeIntelligenceModule module)
        {
            var stateStore = new HighWaterMarkStore(Path.Combine(_tempDir, "state.json"));
            var signalWriter = new SignalWriter(Path.Combine(_tempDir, "signals.jsonl"));
            return new PollCycleRunner(
                newsSource, module, stateStore, signalWriter, new FakePacer(),
                universe: new TickerUniverse(_universePath));
        }

        [Fact]
        public async Task RunOnceAsync_ReReadsUniverseFileEveryCycle()
        {
            // Cycle 1: NVDA is in the universe, so its headline is scored.
            WriteUniverse("SPY", "AGG", "NVDA");
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>
            {
                MakeArticle(1, "nvda headline 1", "NVDA"),
            });
            var module = new FakeIntelligenceModule();
            var runner = MakeRunner(newsSource, module);

            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Equal(new[] { "SPY", "AGG", "NVDA" }, newsSource.LastRequestedSymbols.ToArray());
            Assert.Single(module.Requests);
            Assert.Equal("NVDA", module.Requests[0].Symbol);

            // The portal replaces the file: NVDA removed, MSFT added.
            WriteUniverse("SPY", "AGG", "MSFT");
            newsSource.Articles = new List<AlpacaNewsArticle>
            {
                MakeArticle(2, "nvda headline 2", "NVDA"),
                MakeArticle(3, "msft headline", "MSFT"),
            };

            // Cycle 2: the SAME runner must pick up the new list -- NVDA is
            // no longer scored, MSFT is.
            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Equal(new[] { "SPY", "AGG", "MSFT" }, newsSource.LastRequestedSymbols.ToArray());
            Assert.Equal(2, module.Requests.Count);
            Assert.Equal("MSFT", module.Requests[1].Symbol);
        }

        [Fact]
        public async Task RunOnceAsync_MissingUniverseFile_UsesBuiltInList()
        {
            // No universe.json written.
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>());
            var runner = MakeRunner(newsSource, new FakeIntelligenceModule());

            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Equal(TickerUniverse.BuiltInTickers, newsSource.LastRequestedSymbols.ToArray());
        }

        [Fact]
        public async Task RunOnceAsync_UniverseFileWithoutAgg_UsesBuiltInList()
        {
            WriteUniverse("SPY", "NVDA");
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>());
            var runner = MakeRunner(newsSource, new FakeIntelligenceModule());

            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Equal(TickerUniverse.BuiltInTickers, newsSource.LastRequestedSymbols.ToArray());
        }

        [Fact]
        public async Task RunOnceAsync_FileBecomesInvalidBetweenCycles_FallsBackThenRecovers()
        {
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>());
            var runner = MakeRunner(newsSource, new FakeIntelligenceModule());

            // Valid -> file list.
            WriteUniverse("SPY", "AGG", "QQQ");
            await runner.RunOnceAsync(CancellationToken.None);
            Assert.Equal(new[] { "SPY", "AGG", "QQQ" }, newsSource.LastRequestedSymbols.ToArray());

            // Malformed -> built-in list.
            File.WriteAllText(_universePath, "{ not json");
            await runner.RunOnceAsync(CancellationToken.None);
            Assert.Equal(TickerUniverse.BuiltInTickers, newsSource.LastRequestedSymbols.ToArray());

            // Fixed again -> file list again.
            WriteUniverse("SPY", "AGG", "TLT");
            await runner.RunOnceAsync(CancellationToken.None);
            Assert.Equal(new[] { "SPY", "AGG", "TLT" }, newsSource.LastRequestedSymbols.ToArray());
        }

        [Fact]
        public void TickerUniverse_TickerSet_ReflectsCurrentList()
        {
            WriteUniverse("SPY", "AGG", "NVDA");
            var universe = new TickerUniverse(_universePath);

            universe.Refresh();
            Assert.Contains("NVDA", universe.TickerSet);
            Assert.Equal("file", universe.SourceLabel);

            WriteUniverse("SPY", "AGG", "MSFT");
            universe.Refresh();
            Assert.DoesNotContain("NVDA", universe.TickerSet);
            Assert.Contains("MSFT", universe.TickerSet);
        }

        [Fact]
        public void BuiltInTickers_MatchRepoSeedFile()
        {
            // The pipeline's fallback list must equal config/universe.default.json
            // (the seed) so a fallback means "the original universe", not some
            // other list. Parsed with a simple regex to avoid adding a JSON
            // dependency to this test.
            var seedPath = FindRepoFile(Path.Combine("config", "universe.default.json"));
            var seedTickers = System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(seedPath), "\"([A-Z.]+)\"")
                .Select(m => m.Groups[1].Value)
                .Where(v => v != "Tickers")
                .ToArray();

            Assert.Equal(seedTickers, TickerUniverse.BuiltInTickers);
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
