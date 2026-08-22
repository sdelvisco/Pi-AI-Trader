using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.Intelligence.Tests
{
    /// <summary>
    /// Minimal HttpMessageHandler test double so AzureLlmClient's unit tests
    /// never make a real network call — the handler is handed a delegate
    /// that inspects the outgoing request and returns whatever
    /// HttpResponseMessage (or throws whatever exception) the test wants to
    /// simulate. Deliberately hand-rolled rather than pulling in a mocking
    /// library (Moq, etc.): this repo has no existing test project or
    /// established mocking convention to match (see DEVIATIONS.md), and
    /// HttpMessageHandler is specifically designed by .NET to be
    /// subclassed for exactly this kind of test seam.
    /// </summary>
    public class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        /// <summary>The most recent request this handler received, so tests
        /// can assert on headers/URI/method after calling into
        /// AzureLlmClient.</summary>
        public HttpRequestMessage LastRequest { get; private set; }

        /// <summary>The most recent request body, captured as a string
        /// since HttpContent is only readable once and tests need to
        /// inspect it after the fact.</summary>
        public string LastRequestBody { get; private set; }

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;

            return _responder(request);
        }
    }
}
