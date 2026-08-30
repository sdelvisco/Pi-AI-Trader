using System;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Thrown when AlpacaNewsClient is missing required configuration (an
    /// environment variable) at construction time. Mirrors
    /// PiAiTrader.Intelligence.LlmConfigurationException's rationale: a
    /// missing credential should fail loudly at startup, not surface later
    /// as a mysterious first-call failure.
    /// </summary>
    public class AlpacaConfigurationException : Exception
    {
        public AlpacaConfigurationException(string message) : base(message)
        {
        }

        public AlpacaConfigurationException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
