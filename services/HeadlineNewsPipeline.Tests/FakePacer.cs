using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.HeadlineNewsPipeline.Tests
{
    /// <summary>
    /// IPacer test double that completes immediately instead of actually
    /// waiting PollCycleRunner.AzureCallPacingDelay, so tests exercising
    /// many GenerateSignalAsync calls stay fast. Records how many times it
    /// was invoked so pacing behavior (delay between calls, not before the
    /// first one) can still be asserted.
    /// </summary>
    public class FakePacer : IPacer
    {
        public int CallCount { get; private set; }

        public Task DelayAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }
}
