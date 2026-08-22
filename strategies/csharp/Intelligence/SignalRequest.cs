using System;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Input to an intelligence module: one piece of text to score for one
    /// symbol, as of one point in time. Deliberately narrow — one request
    /// always produces at most one <see cref="Signal"/> (see
    /// <see cref="IIntelligenceModule"/>); batching many headlines together
    /// is a concern for a future caller, not this type.
    /// </summary>
    public class SignalRequest
    {
        /// <summary>The ticker this text is being scored against.</summary>
        public string Symbol { get; set; }

        /// <summary>The headline, article body, filing excerpt, etc. to
        /// score. What this text actually is depends entirely on which
        /// task type the target module implements (headline scoring is the
        /// only task type built this session).</summary>
        public string InputText { get; set; }

        /// <summary>The point in time this request represents — e.g. when
        /// the headline was published, not necessarily when the request is
        /// actually processed. Modules should prefer this over
        /// DateTime.UtcNow when stamping the resulting Signal, so that
        /// re-scoring historical text later (backtesting, replay) still
        /// produces signals dated to the original event rather than to
        /// whenever the scoring happened to run.</summary>
        public DateTime AsOfUtc { get; set; }
    }
}
