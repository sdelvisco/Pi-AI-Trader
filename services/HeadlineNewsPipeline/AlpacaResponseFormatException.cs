using System;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Thrown when Alpaca's News API returned a successful (2xx) response
    /// whose JSON body doesn't match the shape this client expects (e.g.
    /// missing the top-level "news" array, or a news item missing a
    /// required field). This project's established rule — see
    /// PiAiTrader.Intelligence.LlmResponseFormatException — is never to
    /// guess or coerce malformed data into a usable object; throw with a
    /// message naming what was expected, after the raw offending body has
    /// already been logged for diagnosis.
    /// </summary>
    public class AlpacaResponseFormatException : Exception
    {
        public AlpacaResponseFormatException(string message) : base(message)
        {
        }

        public AlpacaResponseFormatException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
