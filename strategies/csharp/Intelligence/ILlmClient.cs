using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Provider-agnostic LLM client abstraction. Deliberately minimal:
    /// returns raw JSON text and lets the calling module own
    /// parsing/validation against its own task-specific schema. This keeps
    /// AzureLlmClient and any future OllamaLlmClient (not built this
    /// session, but this interface must not preclude it) interchangeable
    /// without either knowing about sentiment-specific types — an
    /// ILlmClient implementation only needs to know how to talk to its
    /// provider's chat-completions endpoint, nothing about what the
    /// response is going to be used for.
    /// </summary>
    public interface ILlmClient
    {
        /// <param name="systemPrompt">System-level instructions for the model.</param>
        /// <param name="userPrompt">The actual content to process (headline, etc).</param>
        /// <param name="cancellationToken">Propagated to the underlying HTTP call.</param>
        /// <returns>Raw response text from the model (the chat completion's
        /// message content) — NOT the full HTTP envelope. Caller is
        /// responsible for parsing this as JSON and validating its shape
        /// against whatever task-specific schema it expects.</returns>
        Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
    }
}
