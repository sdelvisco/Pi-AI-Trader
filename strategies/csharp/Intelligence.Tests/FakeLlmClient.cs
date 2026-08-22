using System;
using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// ILlmClient test double for LlmSentimentModule's unit tests. Returns
    /// whatever raw response text the test configures (or throws, to
    /// simulate an AzureLlmClient-level failure bubbling through) without
    /// touching HTTP at all — LlmSentimentModule doesn't know or care which
    /// ILlmClient implementation it's talking to, so its tests shouldn't
    /// need to go through AzureLlmClient/HTTP mocking at all.
    /// </summary>
    public class FakeLlmClient : ILlmClient
    {
        private readonly Func<string, string, string> _responder;

        public string LastSystemPrompt { get; private set; }
        public string LastUserPrompt { get; private set; }

        public FakeLlmClient(string fixedResponse) : this((systemPrompt, userPrompt) => fixedResponse)
        {
        }

        public FakeLlmClient(Func<string, string, string> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        public Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            return Task.FromResult(_responder(systemPrompt, userPrompt));
        }
    }
}
