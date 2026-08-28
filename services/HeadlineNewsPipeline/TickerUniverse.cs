using System.Collections.Generic;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// The trading universe this pipeline scores headlines against.
    ///
    /// *** DUPLICATION WARNING ***
    /// This list is duplicated from DualMomentumV2.cs's UniverseTickers and
    /// must be manually kept in sync if the trading universe changes. See
    /// DEVIATIONS.md for the known duplication risk this creates.
    ///
    /// Why duplicated rather than shared: this pipeline is a deliberately
    /// separate process from DualMomentumV2 (see this session's isolation
    /// rationale — an LLM signal that hasn't been validated yet must not be
    /// able to affect the live/paper trading path), and DualMomentumV2's
    /// UniverseTickers is a private static field inside the algorithm
    /// class, so this process has no way to read it at runtime. Extracting
    /// both to a single shared JSON/config file is explicitly out of scope
    /// for this session and left as future work.
    /// </summary>
    public static class TickerUniverse
    {
        /// <summary>Copied verbatim from DualMomentumV2.cs's UniverseTickers
        /// as of this session — see the duplication warning above.</summary>
        public static readonly string[] Tickers = new[]
        {
            // ── Broad-market equity ETFs ──────────────────────────────────────
            "SPY",   // S&P 500 (SPDR)
            "IVV",   // S&P 500 (iShares)
            "QQQ",   // NASDAQ-100 (Invesco)
            "DIA",   // Dow Jones Industrial Average (SPDR)
            "IWM",   // Russell 2000 small-cap (iShares)
            "VTI",   // Total US stock market (Vanguard)

            // ── US sector ETFs (SPDR Select) ──────────────────────────────────
            "XLK",   // Technology
            "XLF",   // Financials
            "XLE",   // Energy
            "XLV",   // Health Care
            "XLI",   // Industrials
            "XLB",   // Materials
            "XLY",   // Consumer Discretionary
            "XLP",   // Consumer Staples
            "XLU",   // Utilities
            "XLRE",  // Real Estate
            "XLC",   // Communication Services

            // ── International / Emerging markets ──────────────────────────────
            "EFA",   // Developed markets ex-US (iShares MSCI EAFE)
            "EEM",   // Emerging markets (iShares MSCI EM)
            "VEA",   // Developed markets ex-US (Vanguard)
            "VWO",   // Emerging markets (Vanguard)
            "IEFA",  // Core MSCI EAFE (iShares)

            // ── Fixed-income / Bond ETFs ──────────────────────────────────────
            "AGG",   // US Aggregate Bond (iShares) — also the defensive asset
            "BND",   // Total Bond Market (Vanguard)

            // ── Large-cap individual stocks ───────────────────────────────────
            "AAPL",  // Apple
            "MSFT",  // Microsoft
            "GOOGL", // Alphabet (Google)
            "AMZN",  // Amazon
            "TSLA",  // Tesla
            "META",  // Meta Platforms
            "JPM",   // JPMorgan Chase
            "V",     // Visa

            // ── Technology & AI stocks (user-specified additions) ──────────────
            "CSCO",  // Cisco Systems
            "ORCL",  // Oracle
            "CRWD",  // CrowdStrike (cybersecurity / AI-ops)
            "NVDA",  // NVIDIA (GPU / AI infrastructure)

            // ── Commodities & alternatives ────────────────────────────────────
            "GLD",   // Gold (SPDR)
            "SLV",   // Silver (iShares)
            "USO",   // US Oil Fund
            "DBC",   // Diversified Commodities (Invesco)

            // ── Crypto proxies ────────────────────────────────────────────────
            "GBTC",  // Grayscale Bitcoin Trust
            "ETHE",  // Grayscale Ethereum Trust

            // ── Safe-haven / defensive instruments ───────────────────────────
            // AGG and BND already listed above.
            "SHY",   // Short-term Treasuries (iShares 1-3 yr)
            "TLT",   // Long-term Treasuries (iShares 20+ yr)
        };

        /// <summary>Set form of <see cref="Tickers"/> for O(1) membership
        /// checks when filtering a headline's tagged symbols down to the
        /// ones this pipeline cares about. Ordinal comparison: tickers in
        /// this list and in Alpaca's news responses are both always
        /// upper-case, so there is no case-folding concern to account for.</summary>
        public static readonly HashSet<string> TickerSet = new HashSet<string>(Tickers, System.StringComparer.Ordinal);
    }
}
