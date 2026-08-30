using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Seam around the fixed inter-call delay PollCycleRunner applies
    /// between GenerateSignalAsync calls, so unit tests can verify pacing
    /// behavior (how many times it was invoked) without a test actually
    /// taking AzureCallPacingDelay-times-N seconds to run.
    /// </summary>
    public interface IPacer
    {
        Task DelayAsync(CancellationToken cancellationToken);
    }
}
