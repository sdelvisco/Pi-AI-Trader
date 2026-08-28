using System;
using System.Threading;
using System.Threading.Tasks;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Real IPacer implementation: waits PollCycleRunner.AzureCallPacingDelay
    /// via Task.Delay. See PollCycleRunner for why that specific delay value
    /// was chosen.
    /// </summary>
    public class FixedDelayPacer : IPacer
    {
        public Task DelayAsync(CancellationToken cancellationToken)
        {
            return Task.Delay(PollCycleRunner.AzureCallPacingDelay, cancellationToken);
        }
    }
}
