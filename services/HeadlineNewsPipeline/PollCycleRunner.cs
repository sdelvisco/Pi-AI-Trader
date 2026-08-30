using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PiAiTrader.Intelligence;
using QuantConnect.Logging;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Orchestrates one poll-fetch-score-write cycle: ask the news source
    /// for everything new since the last high-water mark, score each new
    /// headline once per in-universe tagged symbol via the injected
    /// IIntelligenceModule, and append each resulting Signal to the output
    /// file — advancing the persisted high-water mark after each headline
    /// so a mid-batch crash can only ever re-process the one headline that
    /// was in flight, never silently skip ahead.
    ///
    /// Deliberately depends on IAlpacaNewsSource and IIntelligenceModule
    /// (not the concrete AlpacaNewsClient/LlmSentimentModule types) so unit
    /// tests can exercise dedup/ordering/multi-symbol/failure-continuation
    /// logic with lightweight fakes, the same way LlmSentimentModule's own
    /// tests fake ILlmClient instead of routing through HTTP mocking for
    /// every case that has nothing to do with HTTP.
    /// </summary>
    public class PollCycleRunner
    {
        /// <summary>How often the service runs a full poll cycle, per this
        /// session's spec ("matching this project's documented
        /// headline-scoring cadence"). Named rather than left as a magic
        /// number so Program.cs's poll loop and this class's own
        /// first-run-seeding lookback window can both reference it.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

        /// <summary>On the very first run (no state file yet), how far back
        /// to look for headlines instead of attempting to process Alpaca's
        /// entire historical backlog. Set equal to PollInterval per this
        /// session's spec ("the last polling interval's worth of time").</summary>
        public static readonly TimeSpan InitialLookbackWindow = PollInterval;

        /// <summary>Fixed delay between consecutive Azure-calling
        /// GenerateSignalAsync calls. The Azure AI Foundry deployment this
        /// pipeline calls into was observed (via this project's own curl
        /// test against the real endpoint, per this session's prompt) to
        /// rate-limit at 20 requests / 60 seconds. At 4s of spacing, any
        /// rolling 60-second window contains at most 15 calls — clear
        /// headroom under the observed limit without needlessly slowing
        /// down a poll cycle further than necessary.</summary>
        public static readonly TimeSpan AzureCallPacingDelay = TimeSpan.FromSeconds(4);

        private readonly IAlpacaNewsSource _newsSource;
        private readonly IIntelligenceModule _intelligenceModule;
        private readonly HighWaterMarkStore _stateStore;
        private readonly SignalWriter _signalWriter;
        private readonly IPacer _pacer;
        private readonly IReadOnlyCollection<string> _universeTickers;
        private readonly HashSet<string> _universeTickerSet;

        public PollCycleRunner(
            IAlpacaNewsSource newsSource,
            IIntelligenceModule intelligenceModule,
            HighWaterMarkStore stateStore,
            SignalWriter signalWriter,
            IPacer pacer,
            IReadOnlyCollection<string> universeTickers = null)
        {
            _newsSource = newsSource ?? throw new ArgumentNullException(nameof(newsSource));
            _intelligenceModule = intelligenceModule ?? throw new ArgumentNullException(nameof(intelligenceModule));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _signalWriter = signalWriter ?? throw new ArgumentNullException(nameof(signalWriter));
            _pacer = pacer ?? throw new ArgumentNullException(nameof(pacer));

            // Defaults to the real trading universe; tests inject a small
            // fixed set instead so cases can be written against a handful
            // of symbols rather than the full ~44-ticker universe.
            _universeTickers = universeTickers ?? TickerUniverse.Tickers;
            _universeTickerSet = new HashSet<string>(_universeTickers, StringComparer.Ordinal);
        }

        /// <summary>Runs exactly one poll-fetch-score-write cycle.</summary>
        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            var state = _stateStore.Load();

            DateTime sinceUtc;
            long lastProcessedId;
            if (state == null)
            {
                sinceUtc = DateTime.UtcNow - InitialLookbackWindow;
                lastProcessedId = 0;
                Log.Trace(
                    $"PollCycleRunner: no prior state file found — this is a first run. Seeding from " +
                    $"{sinceUtc:o} ({InitialLookbackWindow.TotalMinutes} minutes back) instead of processing " +
                    "Alpaca's entire historical backlog.");
            }
            else
            {
                sinceUtc = state.LastProcessedCreatedAtUtc;
                lastProcessedId = state.LastProcessedId;
            }

            var articles = await _newsSource.GetNewsSinceAsync(_universeTickers, sinceUtc, cancellationToken)
                .ConfigureAwait(false);

            // Alpaca's own sort=ASC ordering is requested but not
            // contractually guaranteed to be strict-ID order (e.g. two
            // articles sharing a timestamp) — re-sort by Id explicitly so
            // dedup filtering and high-water-mark advancement are always
            // correct regardless of the API's actual tie-breaking, and
            // filter out anything at or below the high-water mark (covers
            // both true duplicates and the timestamp-tie case described in
            // HighWaterMarkStore's class comment).
            var newArticles = articles
                .Where(a => a.Id > lastProcessedId)
                .OrderBy(a => a.Id)
                .ToList();

            var isFirstAzureCallThisCycle = true;

            foreach (var article in newArticles)
            {
                var matchedSymbols = (article.Symbols ?? Array.Empty<string>())
                    .Where(s => _universeTickerSet.Contains(s))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                foreach (var symbol in matchedSymbols)
                {
                    if (!isFirstAzureCallThisCycle)
                    {
                        await _pacer.DelayAsync(cancellationToken).ConfigureAwait(false);
                    }
                    isFirstAzureCallThisCycle = false;

                    try
                    {
                        var request = new SignalRequest
                        {
                            Symbol = symbol,
                            InputText = article.Headline,
                            AsOfUtc = article.CreatedAtUtc,
                        };
                        var signal = await _intelligenceModule.GenerateSignalAsync(request, cancellationToken)
                            .ConfigureAwait(false);
                        _signalWriter.AppendSignal(signal);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // The caller asked us to stop — propagate rather
                        // than treating this like a scoring failure.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // One bad LLM response or a transient Azure error
                        // must not abort the whole poll cycle, per this
                        // session's spec — log clearly and move on to the
                        // next symbol/headline.
                        Log.Error(
                            $"PollCycleRunner: failed to score headline {article.Id} " +
                            $"('{article.Headline}') for symbol '{symbol}': {ex.Message}");
                    }
                }

                // Advance the high-water mark once this headline's
                // per-symbol attempts are all done (whether or not every
                // one succeeded) — a permanently-failing headline/ticker
                // pair must not block dedup progress and get retried
                // forever, and this write happens per-headline (not just
                // once at the end of the batch) so a mid-batch crash can
                // only ever re-process the headline that was in flight.
                _stateStore.Save(new HighWaterMarkState
                {
                    LastProcessedId = article.Id,
                    LastProcessedCreatedAtUtc = article.CreatedAtUtc,
                });
            }
        }
    }
}
