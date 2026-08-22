using System;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Thrown when an ILlmClient implementation is missing required
    /// configuration (e.g. an environment variable) at construction time.
    /// Deliberately a distinct type from LlmRequestException: this is a
    /// deploy/config-time failure (fail loudly at startup) rather than a
    /// runtime call failure, so callers — and anyone reading a stack trace
    /// or log line — can tell the two apart immediately instead of having
    /// to inspect the message text.
    /// </summary>
    public class LlmConfigurationException : Exception
    {
        public LlmConfigurationException(string message) : base(message)
        {
        }

        public LlmConfigurationException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
