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
    /// Tests PollCycleRunner's orchestration logic — dedup, ascending-id
    /// ordering, first-run seeding, multi-symbol fan-out, and
    /// failure-continuation — against FakeAlpacaNewsSource/
    /// FakeIntelligenceModule/FakePacer rather than real HTTP, since none
    /// of this logic depends on how the news/LLM calls are actually
    /// transported. AlpacaNewsClientTests and EndToEndPipelineTests cover
    /// the HTTP-mocked paths.
    /// </summary>
    public class PollCycleRunnerTests : IDisposable
    {
        private static readonly string[] TestUniverse = { "AAPL", "MSFT", "GOOGL", "NVDA" };

        private readonly string _tempDir;

        public PollCycleRunnerTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hnp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        private static AlpacaNewsArticle MakeArticle(long id, string headline, DateTime createdAtUtc, params string[] symbols)
        {
            return new AlpacaNewsArticle { Id = id, Headline = headline, CreatedAtUtc = createdAtUtc, Symbols = symbols };
        }

        private (HighWaterMarkStore StateStore, SignalWriter SignalWriter, string SignalsPath) MakeStores()
        {
            var stateStore = new HighWaterMarkStore(Path.Combine(_tempDir, "state.json"));
            var signalsPath = Path.Combine(_tempDir, "signals.jsonl");
            var signalWriter = new SignalWriter(signalsPath);
            return (stateStore, signalWriter, signalsPath);
        }

        [Fact]
        public async Task RunOnceAsync_FirstRun_SeedsFromInitialLookbackWindow()
        {
            var (stateStore, signalWriter, _) = MakeStores();
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>());
            var runner = new PollCycleRunner(
                newsSource, new FakeIntelligenceModule(), stateStore, signalWriter, new FakePacer(), TestUniverse);

            await runner.RunOnceAsync(CancellationToken.None);

            Assert.NotNull(newsSource.LastRequestedSinceUtc);
            var expectedSince = DateTime.UtcNow - PollCycleRunner.InitialLookbackWindow;
            Assert.True(Math.Abs((newsSource.LastRequestedSinceUtc.Value - expectedSince).TotalSeconds) < 5,
                $"expected ~{expectedSince:o}, got {newsSource.LastRequestedSinceUtc:o}");
        }

        [Fact]
        public async Task RunOnceAsync_HeadlinesAtOrBelowHighWaterMark_AreSkipped()
        {
            var (stateStore, signalWriter, _) = MakeStores();
            stateStore.Save(new HighWaterMarkState { LastProcessedId = 5, LastProcessedCreatedAtUtc = DateTime.UtcNow.AddMinutes(-10) });

            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>
            {
                MakeArticle(3, "old headline 1", DateTime.UtcNow, "AAPL"),
                MakeArticle(5, "old headline 2 (== high-water mark)", DateTime.UtcNow, "AAPL"),
                MakeArticle(6, "new headline", DateTime.UtcNow, "AAPL"),
            });
            var module = new FakeIntelligenceModule();
            var runner = new PollCycleRunner(newsSource, module, stateStore, signalWriter, new FakePacer(), TestUniverse);

            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Single(module.Requests);
            Assert.Equal("new headline", module.Requests[0].InputText);
        }

        [Fact]
        public async Task RunOnceAsync_ProcessesArticlesInAscendingIdOrder_RegardlessOfSourceOrder()
        {
            var (stateStore, signalWriter, _) = MakeStores();
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>
            {
                MakeArticle(20, "second", DateTime.UtcNow, "AAPL"),
                MakeArticle(10, "first", DateTime.UtcNow, "MSFT"),
            });
            var module = new FakeIntelligenceModule();
            var runner = new PollCycleRunner(newsSource, module, stateStore, signalWriter, new FakePacer(), TestUniverse);

            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Equal(2, module.Requests.Count);
            Assert.Equal("first", module.Requests[0].InputText);
            Assert.Equal("second", module.Requests[1].InputText);
            Assert.Equal(20, stateStore.Load().LastProcessedId);
        }

        [Fact]
        public async Task RunOnceAsync_UpdatesHighWaterMarkAfterEachHeadline_NotJustAtEndOfBatch()
        {
            var (stateStore, signalWriter, _) = MakeStores();
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>
            {
                MakeArticle(10, "first", DateTime.UtcNow, "AAPL"),
                MakeArticle(20, "second", DateTime.UtcNow, "MSFT"),
            });
            var module = new FakeIntelligenceModule();
            long? highWaterMarkSeenBeforeSecondHeadline = null;
            module.OnBeforeGenerate = request =>
            {
                if (request.Symbol == "MSFT")
                {
                    // The first headline (id=10) must already be persisted
                    // by the time we start scoring the second headline
                    // (id=20) — proving the state file is updated per
                    // headline, not deferred to the end of the whole poll.
                    highWaterMarkSeenBeforeSecondHeadline = stateStore.Load()?.LastProcessedId;
                }
            };
            var runner = new PollCycleRunner(newsSource, module, stateStore, signalWriter, new FakePacer(), TestUniverse);

            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Equal(10, highWaterMarkSeenBeforeSecondHeadline);
            Assert.Equal(20, stateStore.Load().LastProcessedId);
        }

        [Fact]
        public async Task RunOnceAsync_MultiSymbolHeadline_ProducesOneSignalPerInUniverseSymbolOnly()
        {
            var (stateStore, signalWriter, signalsPath) = MakeStores();
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>
            {
                // TSLA is deliberately NOT in TestUniverse.
                MakeArticle(1, "multi-ticker headline", DateTime.UtcNow, "AAPL", "GOOGL", "NVDA", "TSLA"),
            });
            var module = new FakeIntelligenceModule();
            var runner = new PollCycleRunner(newsSource, module, stateStore, signalWriter, new FakePacer(), TestUniverse);

            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Equal(3, module.Requests.Count);
            Assert.Equal(new[] { "AAPL", "GOOGL", "NVDA" }, module.Requests.Select(r => r.Symbol).OrderBy(s => s));
            Assert.DoesNotContain(module.Requests, r => r.Symbol == "TSLA");

            var lines = File.ReadAllLines(signalsPath);
            Assert.Equal(3, lines.Length);
        }

        [Fact]
        public async Task RunOnceAsync_GenerateSignalAsyncFailureForOnePair_LogsAndContinuesToNextPair()
        {
            var (stateStore, signalWriter, signalsPath) = MakeStores();
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>
            {
                MakeArticle(1, "headline", DateTime.UtcNow, "AAPL", "MSFT"),
            });
            var module = new FakeIntelligenceModule(symbolsToFail: new[] { "AAPL" });
            var runner = new PollCycleRunner(newsSource, module, stateStore, signalWriter, new FakePacer(), TestUniverse);

            // Must not throw — one failing pair should not abort the cycle.
            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Equal(2, module.Requests.Count);
            var lines = File.ReadAllLines(signalsPath);
            Assert.Single(lines);
            Assert.Contains("\"Symbol\":\"MSFT\"", lines[0]);

            // The headline is still considered processed (high-water mark
            // advances) even though one of its two symbols failed — a
            // permanently-failing pair must not block dedup progress.
            Assert.Equal(1, stateStore.Load().LastProcessedId);
        }

        [Fact]
        public async Task RunOnceAsync_PacesDelayBetweenConsecutiveCallsButNotBeforeTheFirst()
        {
            var (stateStore, signalWriter, _) = MakeStores();
            var newsSource = new FakeAlpacaNewsSource(new List<AlpacaNewsArticle>
            {
                MakeArticle(1, "headline", DateTime.UtcNow, "AAPL", "MSFT", "GOOGL"),
            });
            var module = new FakeIntelligenceModule();
            var pacer = new FakePacer();
            var runner = new PollCycleRunner(newsSource, module, stateStore, signalWriter, pacer, TestUniverse);

            await runner.RunOnceAsync(CancellationToken.None);

            Assert.Equal(3, module.Requests.Count);
            Assert.Equal(2, pacer.CallCount);
        }
    }
}
