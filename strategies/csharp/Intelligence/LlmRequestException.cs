using System;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Thrown by an ILlmClient implementation when a call to the underlying
    /// LLM provider fails — network failure, a non-2xx HTTP response, or a
    /// response body that doesn't even have the expected HTTP-envelope
    /// shape (e.g. missing choices[0].message.content). Deliberately
    /// distinct from LlmResponseFormatException, which is about the
    /// *content* of a successfully-returned message failing to match the
    /// task-specific schema (sentiment JSON) — that failure belongs to the
    /// calling module (LlmSentimentModule), not to the transport-level
    /// client, since a different task type could reasonably expect a
    /// different schema from the same raw HTTP call.
    /// </summary>
    public class LlmRequestException : Exception
    {
        public LlmRequestException(string message) : base(message)
        {
        }

        public LlmRequestException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
