using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Contract for any pluggable intelligence source (LLM sentiment, ML
    /// prediction, RL position sizing, etc). One request in, one signal
    /// out. Batching/caching, if ever needed, wraps this — this interface
    /// stays minimal so every future module (article sentiment, SEC filing
    /// scoring, signal narrative generation, non-LLM models entirely) can
    /// implement it without inheriting assumptions from any one task type.
    /// </summary>
    public interface IIntelligenceModule
    {
        /// <summary>Identifies this module for logging/debugging, e.g.
        /// "LlmSentimentModule". Combined with the task type in
        /// Signal.SourceModule (e.g. "LlmSentimentModule:Headline") by the
        /// implementation, since one module may support several task
        /// types.</summary>
        string ModuleName { get; }

        /// <summary>Score one request and produce one signal. Throws if the
        /// underlying model's response can't be parsed/validated into a
        /// well-formed Signal — callers should not receive a partially
        /// populated or best-guess Signal for malformed model output (see
        /// LlmSentimentModule's malformed-response handling for the
        /// reasoning: this project's own retrospectives repeatedly trace
        /// outages back to exactly this kind of quietly-swallowed
        /// data-pipeline issue).</summary>
        Task<Signal> GenerateSignalAsync(SignalRequest request, CancellationToken cancellationToken);
    }
}
