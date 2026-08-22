namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Coarse directional read implied by a <see cref="Signal"/>. This is
    /// deliberately a separate field from <see cref="Signal.RawScore"/>
    /// rather than something derived purely from the sign of the score:
    /// an LLM (or any future model) reports direction and magnitude as two
    /// independent judgments, and this project wants to preserve — and
    /// sanity-check — cases where they disagree (e.g. a headline scored
    /// "bearish" with a positive sentiment_score) rather than silently
    /// collapsing one into the other. See LlmSentimentModule's
    /// direction/score sanity-check logging for where that disagreement is
    /// surfaced.
    /// </summary>
    public enum SignalDirection
    {
        Bullish,
        Bearish,
        Neutral
    }
}
