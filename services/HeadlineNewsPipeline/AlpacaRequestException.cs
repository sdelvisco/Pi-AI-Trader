using System;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Thrown when a call to Alpaca's News API fails at the
    /// transport/HTTP level — network failure, a non-2xx response, or a
    /// response body that isn't even valid JSON. Mirrors
    /// PiAiTrader.Intelligence.LlmRequestException's split between
    /// transport-level failures and content-schema failures (see
    /// AlpacaResponseFormatException for the latter).
    /// </summary>
    public class AlpacaRequestException : Exception
    {
        public AlpacaRequestException(string message) : base(message)
        {
        }

        public AlpacaRequestException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
