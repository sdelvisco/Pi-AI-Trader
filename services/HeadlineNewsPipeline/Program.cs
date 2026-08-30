using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PiAiTrader.Intelligence;
using QuantConnect.Logging;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Entry point for the standalone headline-news pipeline service. Runs
    /// PollCycleRunner.RunOnceAsync on a fixed PollCycleRunner.PollInterval
    /// loop for as long as the process is alive.
    ///
    /// Deliberately its own process — see this session's isolation
    /// rationale (README/DEVIATIONS.md): this pipeline must never be able
    /// to affect DualMomentumV2 or the live/paper trading path, since the
    /// LLM signal it produces hasn't been validated yet. It shares nothing
    /// at runtime with lean-trader beyond reading the same
    /// /etc/tradingpi/alpaca.env credentials file (also needs azure.env)
    /// and, transitively via the Intelligence library, QuantConnect.Logging
    /// for log output.
    /// </summary>
    public static class Program
    {
        // Overridable for local/dev/test use; defaults to a
        // /var/lib/tradingpi/-style location for persistent runtime state,
        // kept distinct from /etc/tradingpi's credential-only role (see
        // DEVIATIONS.md — this repo had no prior convention for
        // non-credential persistent state to match).
        private const string StateDirEnvVar = "HEADLINE_PIPELINE_STATE_DIR";
        private const string DefaultStateDir = "/var/lib/tradingpi/headline-news-pipeline";

        private const string StateFileName = "state.json";
        private const string SignalsFileName = "signals.jsonl";

        public static async Task Main(string[] args)
        {
            var stateDir = Environment.GetEnvironmentVariable(StateDirEnvVar);
            if (string.IsNullOrWhiteSpace(stateDir))
            {
                stateDir = DefaultStateDir;
            }
            Directory.CreateDirectory(stateDir);

            var stateStore = new HighWaterMarkStore(Path.Combine(stateDir, StateFileName));
            var signalWriter = new SignalWriter(Path.Combine(stateDir, SignalsFileName));
            var pacer = new FixedDelayPacer();

            using (var newsSource = new AlpacaNewsClient())
            using (var llmClient = new AzureLlmClient())
            {
                IIntelligenceModule intelligenceModule = new LlmSentimentModule(llmClient);
                var runner = new PollCycleRunner(newsSource, intelligenceModule, stateStore, signalWriter, pacer);

                using (var cancellationSource = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        cancellationSource.Cancel();
                    };

                    Log.Trace("HeadlineNewsPipeline: starting poll loop.");

                    while (!cancellationSource.IsCancellationRequested)
                    {
                        try
                        {
                            await runner.RunOnceAsync(cancellationSource.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            // A whole-cycle failure (e.g. Alpaca network
                            // outage, corrupt state file) should not crash
                            // this long-running service — the next
                            // scheduled poll gets another chance. Individual
                            // headline/ticker failures are already handled
                            // inside PollCycleRunner; this is the outer
                            // safety net for failures in the surrounding
                            // fetch/state-file plumbing itself.
                            Log.Error($"HeadlineNewsPipeline: poll cycle failed: {ex}");
                        }

                        try
                        {
                            await Task.Delay(PollCycleRunner.PollInterval, cancellationSource.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }

            Log.Trace("HeadlineNewsPipeline: shutting down.");
        }
    }
}
