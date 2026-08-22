using System;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Thrown when an LLM's response text was retrieved successfully (see
    /// ILlmClient.CompleteJsonAsync) but does not parse as valid JSON, is
    /// missing a required field, or has a field outside its documented
    /// range (e.g. sentiment_score of 3.5). This project's own
    /// retrospectives repeatedly trace outages back to exactly this kind of
    /// quietly-swallowed data-pipeline issue, so the rule here is: never
    /// guess or coerce malformed model output into a valid Signal — throw
    /// this, with a message that names both what was expected and what was
    /// actually received, after the raw offending text has already been
    /// logged at Warning level by the caller for diagnosis.
    /// </summary>
    public class LlmResponseFormatException : Exception
    {
        public LlmResponseFormatException(string message) : base(message)
        {
        }

        public LlmResponseFormatException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
