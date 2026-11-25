using System.Collections.Generic;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Shape of the shared universe.json file: the full trading-universe
    /// ticker list, written by the web portal's Settings page
    /// (web/routes/api.py, POST /api/universe) and read by both
    /// DualMomentumV2 (once, in Initialize()) and the HeadlineNewsPipeline
    /// service (fresh at the start of every poll cycle), each through
    /// UniverseConfigReader.
    ///
    /// Same "small shared JSON file" pattern as AggregatorConfig /
    /// aggregator-config.json, in the same directory
    /// (/var/lib/tradingpi/headline-news-pipeline/). Deliberately minimal:
    /// one field, {"Tickers": ["SPY", ...]}.
    /// </summary>
    public class UniverseConfig
    {
        /// <summary>Ticker symbols in display/ranking order. Stored as plain
        /// strings so the file stays human-readable; every entry is
        /// validated (and upper-cased) by UniverseConfigReader before use.</summary>
        public List<string> Tickers { get; set; }
    }
}
