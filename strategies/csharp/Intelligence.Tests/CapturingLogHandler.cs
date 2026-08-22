using System.Collections.Generic;
using QuantConnect.Logging;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Test double for QuantConnect.Logging.ILogHandler that records every
    /// message instead of writing to the console/log file. Installed via
    /// `Log.LogHandler = ...` so tests can assert that specific log lines
    /// (e.g. the rationale line, or a direction/score mismatch warning)
    /// were actually emitted — LEAN's Log class is a global static, so this
    /// is the only seam available for observing what it was told to log.
    /// </summary>
    public class CapturingLogHandler : ILogHandler
    {
        public List<string> ErrorMessages { get; } = new List<string>();
        public List<string> DebugMessages { get; } = new List<string>();
        public List<string> TraceMessages { get; } = new List<string>();

        public void Error(string text) => ErrorMessages.Add(text);
        public void Debug(string text) => DebugMessages.Add(text);
        public void Trace(string text) => TraceMessages.Add(text);
        public void Report(string text)
        {
            // Not used by anything in this session's code; present only to
            // satisfy the interface.
        }

        public void Dispose()
        {
        }
    }
}
