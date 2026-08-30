using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.HeadlineNewsPipeline.Tests
{
    /// <summary>
    /// Minimal HttpMessageHandler test double — duplicated from
    /// strategies/csharp/Intelligence.Tests/FakeHttpMessageHandler.cs rather
    /// than shared across the two test projects, so each test project stays
    /// self-contained (matching that project's own hand-rolled-over-mocking-library
    /// rationale). Supports a queue of responders so a single test can
    /// exercise multi-page pagination.
    /// </summary>
    public class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public HttpRequestMessage LastRequest { get; private set; }

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }
}
