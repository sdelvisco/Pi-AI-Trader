using System;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// The pipeline's dedup checkpoint: the highest Alpaca article ID
    /// successfully processed so far, plus that article's own timestamp
    /// (used as the "start" query parameter on the next poll, since
    /// Alpaca's News API has no way to filter directly by ID — see
    /// HighWaterMarkStore's class comment for why both fields are needed
    /// together).
    /// </summary>
    public class HighWaterMarkState
    {
        public long LastProcessedId { get; set; }

        public DateTime LastProcessedCreatedAtUtc { get; set; }
    }
}
