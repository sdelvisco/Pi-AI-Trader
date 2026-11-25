using System;
using System.Collections.Generic;
using PiAiTrader.Intelligence;
using QuantConnect.Logging;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// The trading universe this pipeline scores headlines against.
    ///
    /// SOURCE OF TRUTH: the shared universe.json file
    /// (/var/lib/tradingpi/headline-news-pipeline/universe.json), which the
    /// web portal's Settings page edits and DualMomentumV2 also reads. An
    /// instance re-reads that file every time <see cref="Refresh"/> is called
    /// (PollCycleRunner calls it at the start of every 4-hour poll cycle),
    /// so a portal edit reaches this pipeline on its next cycle without a
    /// restart of this service.
    ///
    /// The former DUPLICATION WARNING (this list had to be kept in sync with
    /// DualMomentumV2.UniverseTickers by hand) is retired: both consumers now
    /// read the same file through PiAiTrader.Intelligence.UniverseConfigReader.
    /// <see cref="BuiltInTickers"/> below is still a copy of DualMomentumV2's
    /// BuiltInUniverseTickers, but it is now ONLY the fail-safe fallback used
    /// when universe.json is missing or invalid (logged as
    /// "hardcoded-fallback"). If the two fallback copies ever drift, the only
    /// effect is which tickers each process uses while the file is broken.
    /// See DEVIATIONS.md (2026-11-24).
    /// </summary>
    public sealed class TickerUniverse
    {
        /// <summary>Built-in fallback list: copied verbatim from
        /// DualMomentumV2.cs's BuiltInUniverseTickers (and identical to the
        /// seed file config/universe.default.json) as of 2026-11-24. Used only
        /// when universe.json cannot be used -- see the class comment.</summary>
        public static readonly string[] BuiltInTickers = new[]
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

        // Reads universe.json; null for a fixed (injected) universe.
        private readonly UniverseConfigReader _reader;

        /// <summary>Creates a file-backed universe that reads
        /// <paramref name="filePath"/> on every <see cref="Refresh"/>, falling
        /// back to <see cref="BuiltInTickers"/>. Starts out holding the
        /// built-in list until the first Refresh().</summary>
        public TickerUniverse(string filePath)
        {
            // AGG and SPY are required for the same reason DualMomentumV2
            // requires them, so both processes accept or reject a file alike.
            _reader = new UniverseConfigReader(filePath, BuiltInTickers, UniverseConfigReader.LockedTickers);
            SetCurrent(BuiltInTickers, "hardcoded-fallback (not read yet)");
        }

        // Fixed-list constructor used by Fixed(); never reads a file.
        private TickerUniverse(IReadOnlyCollection<string> fixedTickers)
        {
            _reader = null;
            SetCurrent(fixedTickers, "fixed (injected)");
        }

        /// <summary>Creates a universe that always returns
        /// <paramref name="tickers"/> and never reads a file. Used by tests
        /// and by PollCycleRunner's legacy universeTickers parameter.</summary>
        public static TickerUniverse Fixed(IReadOnlyCollection<string> tickers)
        {
            if (tickers == null) throw new ArgumentNullException(nameof(tickers));
            return new TickerUniverse(tickers);
        }

        /// <summary>The current ticker list (as of the last Refresh()).</summary>
        public IReadOnlyCollection<string> Tickers { get; private set; }

        /// <summary>Set form of <see cref="Tickers"/> for O(1) membership
        /// checks when filtering a headline's tagged symbols, rebuilt on every
        /// Refresh() so it always reflects the current list. Ordinal: both
        /// this list (upper-cased by UniverseConfigReader) and Alpaca's news
        /// symbols are upper-case.</summary>
        public HashSet<string> TickerSet { get; private set; }

        /// <summary>"file", "hardcoded-fallback", or "fixed (injected)" --
        /// where the current list came from, for log lines.</summary>
        public string SourceLabel { get; private set; }

        /// <summary>
        /// Re-reads universe.json (file-backed instances only) and updates
        /// <see cref="Tickers"/>, <see cref="TickerSet"/> and
        /// <see cref="SourceLabel"/>. Never throws (UniverseConfigReader never
        /// throws). Logs the source and size every time, and the reason when
        /// it fell back.
        /// </summary>
        public void Refresh()
        {
            if (_reader == null)
            {
                // Fixed universe: nothing to re-read.
                return;
            }

            var result = _reader.Read();
            SetCurrent(result.Tickers, result.SourceLabel);
            Log.Trace($"TickerUniverse: universe source: {result.SourceLabel}, size {result.Tickers.Count} ({_reader.FilePath})");
            if (result.Source == UniverseConfigSource.HardcodedFallback)
            {
                Log.Error($"TickerUniverse: using the built-in hardcoded universe because {result.FallbackReason}");
            }
        }

        // Replaces the current list and rebuilds the set from it.
        private void SetCurrent(IReadOnlyCollection<string> tickers, string sourceLabel)
        {
            Tickers = tickers;
            TickerSet = new HashSet<string>(tickers, StringComparer.Ordinal);
            SourceLabel = sourceLabel;
        }
    }
}
