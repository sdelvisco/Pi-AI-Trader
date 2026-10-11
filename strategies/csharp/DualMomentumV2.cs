// =============================================================================
// DualMomentumV2.cs — Dual Momentum Strategy (Absolute + Relative Momentum)
// =============================================================================
// Implements Gary Antonacci's Dual Momentum approach with extensions:
//   1. Absolute momentum filter  — compare SPY 12-month return vs AGG.
//      If SPY > AGG → risk-on (proceed with relative ranking).
//      If SPY <= AGG → risk-off (100% defensive position in AGG).
//   2. Relative momentum ranking — sort universe by 6-month return,
//      hold top-N positions at equal weight.
//   3. Max-drawdown halt         — if portfolio falls 20% from equity peak,
//      halt trading and move entirely to AGG for a 3-month cooloff period.
//   4. Per-position stop-loss    — liquidate any position that falls 15%
//      below its entry price (checked every trading day).
//   5. Fractional sizing         — orders are sized in decimal shares
//      (rounded down to 0.0001) so each rebalance invests about 100% of the
//      account minus a 1% cash reserve; see the FRACTIONAL SIZING constants.
//
// Deployment notes:
//   - SetStartDate / SetEndDate are intentionally omitted (paper trading).
//   - SetCash(1000) is used only as an initial paper-trading seed.
//   - ALL orders (rebalance, stop-loss, drawdown-halt, force-rebalance) are
//     immediate MarketOrder()s with Day time-in-force (Alpaca time_in_force=day).
//     Rebalance orders previously used MarketOnCloseOrder() (Alpaca cls), but on
//     2026-10-01 Alpaca paper accepted every cls order and then expired all of
//     them unfilled at ~16:01 ET -- see DEVIATIONS.md (2026-10-01 incident).
//   - Rebalance schedule: FIRST TRADING DAY of each month at 10:00 ET
//     (DateRules.MonthStart on SPY's exchange calendar, so weekends and NYSE
//     holidays roll forward to the next open day). Hard-coded in Initialize().
//     History: PR #51 (2026-10-04) temporarily added a "rebalance-mode" config
//     switch with a weekly Monday 10:00 ET paper fill test; the 2026-10-05 run
//     filled on Alpaca paper, so the switch and the weekly mode were removed
//     and the 10:00 ET time was kept for the monthly schedule -- see
//     DEVIATIONS.md (2026-10-06 entry).
//
// To compile:
//   dotnet build strategies/csharp/DualMomentumV2.csproj -c Release
//
// LEAN algorithm documentation:
//   https://www.lean.io/docs/v2/writing-algorithms
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Brokerages;
using QuantConnect.Orders;
using PiAiTrader.Intelligence;

#nullable enable

namespace PiAiTrader.Strategies
{
    /// <summary>
    /// DualMomentumV2 combines absolute momentum (SPY vs AGG filter) with
    /// relative momentum ranking across a ~50-asset universe, then applies
    /// a max-drawdown circuit-breaker and per-position stop-losses.
    /// </summary>
    public class DualMomentumV2 : QCAlgorithm
    {
        // =====================================================================
        // ██  CONSTANTS  ──────────────────────────────────────────────────────
        // All "magic numbers" are declared here so they are easy to tune.
        // =====================================================================

        /// <summary>Number of top-ranked positions to hold simultaneously.</summary>
        private const int TopPositions = 5;

        /// <summary>Equal weight per position (1 / TopPositions).</summary>
        private const decimal PositionWeight = 1.0m / TopPositions; // 0.20 = 20 %

        /// <summary>
        /// Lookback window (in calendar months) for the RELATIVE momentum ranking.
        /// Gary Antonacci's original paper uses 12 months; 6 is a common variation.
        /// </summary>
        private const int RelativeMomentumMonths = 6;

        /// <summary>
        /// Lookback window (in calendar months) for the ABSOLUTE momentum filter.
        /// Compares SPY total-return proxy vs AGG over this period.
        /// </summary>
        private const int AbsoluteMomentumMonths = 12;

        /// <summary>
        /// Drawdown threshold below which all positions are liquidated and the
        /// strategy enters a defensive (AGG-only) halt mode.
        /// e.g. 0.20 means "halt if portfolio drops 20 % from its all-time high."
        /// </summary>
        private const decimal MaxDrawdownHaltThreshold = 0.20m;

        /// <summary>
        /// Number of calendar months the strategy waits in halt mode before
        /// automatically resuming normal operation.
        /// </summary>
        private const int HaltCooloffMonths = 3;

        /// <summary>
        /// Per-position stop-loss as a fraction below the entry price.
        /// e.g. 0.15 means "exit if price falls 15 % below entry."
        /// </summary>
        private const decimal StopLossThreshold = 0.15m;

        /// <summary>Ticker used as the defensive / safe-haven asset.</summary>
        private const string DefensiveTicker = "AGG";

        /// <summary>Ticker used for SPY in the absolute momentum filter.</summary>
        private const string AbsMomReferenceTicker = "SPY";

        /// <summary>Paper-trading seed capital (USD). Ignored by live brokerage.</summary>
        private const int SeedCash = 1_000;

        // =====================================================================
        // ██  FRACTIONAL SIZING  ─────────────────────────────────────────────
        // Lets each rebalance invest about 100% of the account (minus a cash
        // reserve) instead of truncating every target to whole shares. On
        // 2026-10-05 whole-share truncation left 88.7% of the ~$1,000 paper
        // account in cash (for example 0.89 shares became 0, 1.82 became 1).
        //
        // How it works:
        //   1. Initialize() lowers LEAN's lot size for every US equity from 1
        //      to FractionalLotSize, BEFORE the AddEquity loop. LEAN otherwise
        //      rejects orders under 1 share (QCAlgorithm.Trading.cs,
        //      OrderQuantityLessThanLotSize) and truncates larger ones to
        //      whole shares (BrokerageTransactionHandler.RoundOffOrder). The
        //      Alpaca plugin already sends the decimal quantity as an Alpaca
        //      fractional order (AlpacaBrokerageExtensions.CreateAlpacaOrder,
        //      OrderQuantity.Fractional), so no LEAN or plugin patch is needed.
        //      Verified by reading the pinned LEAN c88955b9 and
        //      Lean.Brokerages.Alpaca 86f896e4 source.
        //   2. The rebalance sizes every order in decimal shares, rounded
        //      DOWN to the lot size, against TotalPortfolioValue minus a cash
        //      reserve, and caps every buy at the cash actually available.
        // =====================================================================

        /// <summary>
        /// Master switch for fractional sizing. When false, the lot size is
        /// left at LEAN's default of 1 and all sizing rounds down to whole
        /// shares (the reserve, sells-first order and cash caps still apply).
        /// Declared static readonly rather than const only so the compiler
        /// does not warn about the unreachable branch for the other value.
        /// </summary>
        private static readonly bool UseFractionalSizing = true;

        /// <summary>
        /// Lot size registered for every US equity when fractional sizing is
        /// on: quantities are rounded down to a multiple of 0.0001 share.
        /// Alpaca accepts up to 9 decimal places for qty (per its fractional
        /// trading documentation), so 4 decimals is well inside that limit.
        /// It also keeps the rounding loss tiny (at most 0.0001 x price, about
        /// 5 cents on a $500 share) and keeps log lines readable.
        /// </summary>
        private const decimal FractionalLotSize = 0.0001m;

        /// <summary>
        /// Fraction of TotalPortfolioValue kept in cash at every rebalance
        /// (0.01 = 1%). Owner decision. It is the cushion for sizing against
        /// a possibly stale previous-close price: the paper account has a
        /// margin multiplier of 1, so Alpaca rejects any buy that exceeds
        /// cash, and LEAN's AlpacaBrokerageModel (which assumes 2x margin)
        /// will not catch an overspend before the order reaches Alpaca.
        /// </summary>
        private const decimal CashReserveFraction = 0.01m;

        /// <summary>
        /// Smallest order value (USD) the strategy will submit for a new
        /// position or an adjustment. Reflects Alpaca's advertised $1 minimum
        /// for fractional orders (not confirmed from Alpaca's API reference).
        /// </summary>
        private const decimal MinOrderNotional = 1.00m;

        /// <summary>
        /// Adjustments to an already-held position are skipped when their
        /// value is below max(MinOrderNotional, this fraction x
        /// TotalPortfolioValue): 0.005 = 0.5%, about $5 on a $1,000 account.
        /// Avoids buying or selling a few cents every month.
        /// </summary>
        private const decimal MinAdjustmentFraction = 0.005m;

        // =====================================================================
        // ██  ORDER FILL WAITS  ──────────────────────────────────────────────
        // Real Alpaca paper fills took about 5-7 s from pending_new to fill on
        // 2026-10-05 (XLK sell ~4.9 s, CSCO buy ~6.8 s), while LEAN's live
        // MarketOrder() wait is only 5 s (SecurityTransactionManager.SetLiveMode
        // sets MarketOrderFillTimeout = 5 s in the pinned LEAN source), so
        // waits could time out just before the fill arrived. Rebalance,
        // liquidation, risk-off and drawdown-halt orders are therefore
        // submitted asynchronously and waited for here instead, with a longer
        // per-order limit and a total cap per rebalance.
        // =====================================================================

        /// <summary>
        /// LEAN's default live wait for a synchronous MarketOrder(), in
        /// seconds. Kept for reference and logging only; the stop-loss path
        /// still uses synchronous MarketOrder() and therefore this wait.
        /// </summary>
        private const int LeanDefaultMarketOrderWaitSeconds = 5;

        /// <summary>
        /// Longest wait for one order, in seconds, measured from its submission.
        /// </summary>
        private const int OrderFillWaitSeconds = 30;

        /// <summary>
        /// Longest total time, in seconds, that one rebalance (or one drawdown
        /// halt) spends waiting for fills across all of its orders.
        /// </summary>
        private const int RebalanceFillWaitBudgetSeconds = 120;

        // =====================================================================
        // ██  REBALANCE SCHEDULE  ────────────────────────────────────────────
        // Monthly: first trading day of each month at 10:00 ET (exchange time).
        // The date rule lives in Initialize() (DateRules.MonthStart on SPY);
        // the time-of-day is declared here so it is easy to find and tune.
        // =====================================================================

        /// <summary>
        /// Hour (exchange time, ET, 24-hour clock) at which the monthly
        /// rebalance fires. 10:00 ET is 30 minutes after the 09:30 open, which
        /// avoids the opening auction and gives Day market orders the rest of
        /// the session to fill. Kept from the PR #51 weekly paper fill test,
        /// whose 2026-10-05 10:00 ET run filled on Alpaca paper; the previous
        /// 15:40 ET MarketOnClose combination expired unfilled on 2026-10-01
        /// and must not be restored -- see DEVIATIONS.md.
        /// </summary>
        private const int RebalanceHourEt = 10;

        /// <summary>Minute past <see cref="RebalanceHourEt"/> at which the monthly rebalance fires.</summary>
        private const int RebalanceMinuteEt = 0;

        // =====================================================================
        // ██  SIGNAL AGGREGATOR (Phase 2, Step 3)  ───────────────────────────
        // Sentiment-based position sizing WITHIN the existing top-N
        // relative-momentum branch only -- never in the defensive branch,
        // and never affecting which tickers the (untouched) momentum
        // ranking selects. See PositionSizer for the sizing math and
        // SignalsFileReader/AggregatorConfigReader for the fail-safe reads
        // this all depends on. Every one of these three components is
        // designed to degrade to "no adjustment" rather than throw, so a
        // problem here can never block, delay, or crash a rebalance.
        // =====================================================================

        /// <summary>Path to the HeadlineNewsPipeline service's append-only
        /// signal output. Matches services/HeadlineNewsPipeline/Program.cs's
        /// DefaultStateDir + SignalsFileName exactly -- confirmed against
        /// that file's actual current source, not assumed.</summary>
        private const string SignalsFilePath = "/var/lib/tradingpi/headline-news-pipeline/signals.jsonl";

        /// <summary>Path to the shared active-aggregation-mode config file,
        /// written by the web portal's mode-selection control
        /// (web/routes/api.py) and read fresh here on every rebalance. Lives
        /// alongside signals.jsonl in the same existing
        /// /var/lib/tradingpi/headline-news-pipeline/ directory rather than a
        /// new dedicated path, since that's already this project's one
        /// established shared-runtime-state location (no other such
        /// location exists in the codebase to prefer instead).</summary>
        private const string AggregatorConfigFilePath = "/var/lib/tradingpi/headline-news-pipeline/aggregator-config.json";

        // =====================================================================
        // ██  UNIVERSE DEFINITION  ────────────────────────────────────────────
        // ~50 assets spanning equity ETFs, sectors, international, fixed-income,
        // individual large-caps, AI/tech names, commodities, crypto proxies, and
        // safe-haven instruments.
        // =====================================================================

        private static readonly string[] UniverseTickers = new[]
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

        // =====================================================================
        // ██  INSTANCE STATE  ─────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Maps each ticker string to its LEAN Symbol object after AddEquity().
        /// </summary>
        private readonly Dictionary<string, Symbol> _symbols = new Dictionary<string, Symbol>();

        /// <summary>
        /// Tracks the entry (average fill) price for each currently-held symbol.
        /// Used to evaluate the per-position stop-loss condition each day.
        /// </summary>
        private readonly Dictionary<Symbol, decimal> _entryPrices = new Dictionary<Symbol, decimal>();

        /// <summary>
        /// The highest portfolio value recorded since inception.
        /// Used to calculate current drawdown from peak.
        /// </summary>
        private decimal _peakPortfolioValue = SeedCash;

        /// <summary>
        /// Indicates whether the max-drawdown circuit-breaker has been triggered.
        /// When true the strategy holds 100% AGG until the cooloff period expires.
        /// </summary>
        private bool _haltActive = false;

        /// <summary>
        /// The UTC datetime at which halt mode was entered.
        /// The strategy auto-resumes once (UtcTime - _haltStartDate) >= HaltCooloffMonths months.
        /// </summary>
        private DateTime _haltStartDate = DateTime.MinValue;

        /// <summary>
        /// Remembers the last calendar month in which a rebalance was executed,
        /// so that we only rebalance once per month (on the first trading day).
        /// </summary>
        private int _lastRebalanceMonth = -1;

        /// <summary>
        /// Reads recent HeadlineNewsPipeline signals for a symbol. Stateless
        /// and safe to reuse across rebalances -- each call re-reads the
        /// file fresh (see SignalsFileReader's own class comment on why no
        /// caching/locking is needed here).
        /// </summary>
        private readonly SignalsFileReader _signalsFileReader = new SignalsFileReader(SignalsFilePath);

        /// <summary>
        /// Reads the active AggregationMode from the shared config file.
        /// Deliberately re-read fresh at the start of every rebalance (never
        /// cached) so a web-portal-driven mode change takes effect on the
        /// very next rebalance without a lean-trader restart.
        /// </summary>
        private readonly AggregatorConfigReader _aggregatorConfigReader = new AggregatorConfigReader(AggregatorConfigFilePath);

        /// <summary>Combines a ticker's recent signals into one AggregatedSignal.</summary>
        private readonly ISignalAggregator _signalAggregator = new SignalAggregator();

        // =====================================================================
        // ██  SKIP / ZERO-PRICE VISIBILITY  ──────────────────────────────────
        // Logging-only state. Added after the 2026-10-05 rebalance, where
        // MSFT and AAPL were silently skipped because Securities[sym].Price
        // was 0 and nothing in the log said so. None of this changes which
        // orders are placed or how they are sized -- it only reports why a
        // selected symbol did or did not get an order.
        // =====================================================================

        // Skip reason: Securities[sym].Price was 0 (or negative), so no
        // target quantity could be computed and no order was submitted.
        private const string SkipNoPrice = "NO_PRICE";

        // Skip reason: the symbol was not present in Securities at all.
        private const string SkipNotInSecurities = "NOT_IN_SECURITIES";

        // NOTE: the former TARGET_TRUNCATED_TO_ZERO reason was removed with
        // fractional sizing. A target that rounds down to (almost) nothing
        // for a symbol that is not held is now a new buy below
        // MinOrderNotional, reported as BELOW_MIN_ORDER.

        // Skip reason: the target quantity, rounded down to the lot size,
        // equals the currently held quantity, so there is nothing to trade.
        private const string SkipAlreadyAtTarget = "ALREADY_AT_TARGET";

        // Skip reason: the symbol is already held and the adjustment needed
        // is worth less than max(MinOrderNotional, MinAdjustmentFraction x
        // TotalPortfolioValue), so it is skipped to avoid tiny churn trades.
        private const string SkipBelowMinAdjustment = "BELOW_MIN_ADJUSTMENT";

        // Skip reason: the symbol is not held and the new buy is worth less
        // than MinOrderNotional (Alpaca's advertised $1 fractional minimum).
        private const string SkipBelowMinOrder = "BELOW_MIN_ORDER";

        // Skip reason: the buy was capped by the cash actually available
        // (Portfolio.Cash minus the reserve and minus unfilled earlier buys)
        // and the capped order would be worth less than MinOrderNotional.
        private const string SkipInsufficientCash = "INSUFFICIENT_CASH";

        // Fixed display order for the per-reason counts in the
        // "[Rebalance] Complete" summary, so the line reads the same way
        // every month regardless of which reason happened first.
        private static readonly string[] SkipReasonDisplayOrder = new[]
        {
            SkipNoPrice, SkipNotInSecurities, SkipAlreadyAtTarget, SkipBelowMinAdjustment,
            SkipBelowMinOrder, SkipInsufficientCash
        };

        // Rate limiter for the stop-loss zero-price warning. CheckStopLosses()
        // runs on every OnData() slice (not strictly once per day), so without
        // this a held position with no price could log the same warning many
        // times. Maps symbol -> the algorithm date (Time.Date) on which its
        // warning was last logged; the warning is logged at most once per
        // symbol per trading day.
        private readonly Dictionary<Symbol, DateTime> _stopLossNoPriceWarnedOn = new Dictionary<Symbol, DateTime>();

        // Lot size the sizing code actually rounds to. Set in Initialize()
        // AFTER the AddEquity loop from the lot size LEAN really applied to
        // SPY, never smaller than the configured lot. If the lot-size
        // override in Initialize() ever fails to take effect, this falls
        // back to LEAN's real lot size (1), so the strategy sizes whole
        // shares instead of sending quantities LEAN would reject or truncate.
        private decimal _sizingLotSize = 1m;

        // Price used to size the most recent order for each symbol, so that
        // OnOrderEvent() can log fill price versus sizing price (slippage,
        // including the gap when the sizing price was a stale previous
        // close). Guarded by _sizingPriceLock because in live trading order
        // events can arrive on a different thread than the one placing orders.
        private readonly Dictionary<Symbol, decimal> _sizingPriceBySymbol = new Dictionary<Symbol, decimal>();
        private readonly object _sizingPriceLock = new object();

        // Submission time (Stopwatch timestamp) of each order placed through
        // PlaceSizedMarketOrder(), by LEAN order id, so the wait loop can
        // enforce the per-order limit and log the elapsed seconds. Guarded by
        // _sizingPriceLock: in live trading the stop-loss (OnData, main
        // thread) and the rebalance (scheduled event, RealTime thread) can
        // both place orders.
        private readonly Dictionary<int, long> _orderSubmittedAt = new Dictionary<int, long>();

        // Total time spent waiting for fills in the current rebalance or
        // drawdown halt. Restarted at the start of each Rebalance() and each
        // triggered drawdown halt; checked against RebalanceFillWaitBudgetSeconds.
        private System.Diagnostics.Stopwatch _fillWaitSpent = new System.Diagnostics.Stopwatch();

        // =====================================================================
        // ██  INITIALIZE  ─────────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Called once by LEAN before the first data event.
        /// Configures the algorithm: cash, brokerage, data subscriptions.
        /// Start/End dates are intentionally omitted for paper-trading deployment.
        /// </summary>
        public override void Initialize()
        {
            // ------------------------------------------------------------------
            // Paper-trading date range (commented-out placeholders only).
            // Uncomment and adjust for historical backtests:
            // ------------------------------------------------------------------
            // SetStartDate(2015, 1, 1);
            // SetEndDate(DateTime.Now);

            // ------------------------------------------------------------------
            // Seed capital — used by the LEAN paper-trading engine.
            // Live brokerage accounts use their actual balance instead.
            // ------------------------------------------------------------------
            SetCash(SeedCash);

            // ------------------------------------------------------------------
            // Brokerage model.
            // AlpacaBrokerageModel provides realistic fill/fee simulation.
            // ------------------------------------------------------------------
            SetBrokerageModel(BrokerageName.Alpaca, AccountType.Margin);

            // ------------------------------------------------------------------
            // Default order properties — force Day-validity market orders so
            // that orders submitted at any time during the trading session are
            // accepted by the Alpaca brokerage model.  Without this setting,
            // AlpacaBrokerageModel wraps SetHoldings() / Liquidate() calls in
            // MarketOnOpen orders, which are only valid for submission between
            // 07:00–09:28 local time and are rejected when OnData fires outside
            // that window with: "MarketOnOpen submission time is invalid."
            // ------------------------------------------------------------------
            DefaultOrderProperties = new AlpacaOrderProperties
            {
                TimeInForce = TimeInForce.Day
            };

            // ------------------------------------------------------------------
            // Fractional lot size for US equities. MUST run before the
            // AddEquity loop below: a security copies its symbol properties
            // when it is created, and LEAN offers no public way to change them
            // afterwards (Security.UpdateSymbolProperties is internal).
            // ------------------------------------------------------------------
            RegisterFractionalLotSize();

            // ------------------------------------------------------------------
            // Subscribe to daily equity bars for every universe ticker.
            // Daily resolution is sufficient for a monthly-rebalance strategy.
            // ------------------------------------------------------------------
            foreach (var ticker in UniverseTickers)
            {
                // AddEquity returns an EquitySubscriptionDataConfig; we only need
                // the resulting Symbol for later order/history calls.
                var equity = AddEquity(ticker, Resolution.Daily);
                _symbols[ticker] = equity.Symbol;
            }

            // Confirm the lot-size override really took effect, and choose
            // the lot size the sizing code rounds to from what LEAN applied.
            VerifyEffectiveLotSize();

            Log("=== DualMomentumV2 Initialized ===");
            Log($"Universe size     : {UniverseTickers.Length} symbols");
            Log($"Top positions     : {TopPositions} @ {PositionWeight:P0} each");
            Log($"Relative lookback : {RelativeMomentumMonths} months");
            Log($"Absolute lookback : {AbsoluteMomentumMonths} months");
            Log($"Max drawdown halt : {MaxDrawdownHaltThreshold:P0}");
            Log($"Halt cooloff      : {HaltCooloffMonths} months");
            Log($"Stop-loss         : {StopLossThreshold:P0} per position");
            Log($"Defensive asset   : {DefensiveTicker}");
            Log($"Fractional sizing : {(UseFractionalSizing ? "ON" : "OFF")}, sizing lot size {_sizingLotSize} share");
            Log($"Cash reserve      : {CashReserveFraction:P1} of portfolio value at each rebalance");
            Log($"Min order / adj.  : new buys >= ${MinOrderNotional:F2}; adjustments >= max(${MinOrderNotional:F2}, {MinAdjustmentFraction:P1} of portfolio)");
            Log($"Fill waits        : up to {OrderFillWaitSeconds}s per order, {RebalanceFillWaitBudgetSeconds}s total per rebalance " +
                $"(LEAN default {LeanDefaultMarketOrderWaitSeconds}s; stop-loss orders keep the LEAN default)");

            // Log the active schedule at startup so it is visible in the journal
            // right next to the "DualMomentumV2 Initialized" line make verify checks.
            Log($"Rebalance schedule: first trading day of month {RebalanceHourEt:D2}:{RebalanceMinuteEt:D2} ET, Day market orders");

            // ------------------------------------------------------------------
            // Monthly rebalance: FIRST TRADING DAY of each month at 10:00 ET.
            //
            // Date rule -- DateRules.MonthStart(symbol): LEAN resolves this
            // against the symbol's SecurityExchangeHours (market-hours
            // database). It fires on the 1st of the month if the exchange is
            // open that day, otherwise on the next trading day, so weekends
            // and NYSE holidays (e.g. Jan 1) roll forward automatically.
            // Verified by reading DateRules.MonthStart/GetScheduledDay at the
            // LEAN commit pinned in setup/06_lean_build.sh (c88955b9).
            //
            // Reference symbol -- SPY (AbsMomReferenceTicker), looked up from
            // _symbols (populated by the AddEquity loop above). The original
            // schedule used Securities.Keys.First(), which picks whichever key
            // the SecurityManager enumerates first; that only worked because
            // every universe symbol is a US equity on the same NYSE calendar.
            // Naming SPY makes the calendar choice explicit and deterministic
            // while producing the same dates.
            //
            // Time rule -- TimeRules.At(10, 0) in the algorithm's time zone
            // (America/New_York, LEAN's default). Kept from the PR #51 weekly
            // paper fill test; see RebalanceHourEt for why.
            //
            // Orders -- Rebalance() submits immediate Day MarketOrder()s
            // (Alpaca time_in_force=day). Do NOT switch back to
            // MarketOnCloseOrder() + 15:40 ET: that combination expired
            // unfilled on 2026-10-01 (see DEVIATIONS.md).
            // ------------------------------------------------------------------
            Schedule.On(
                DateRules.MonthStart(_symbols[AbsMomReferenceTicker]),
                TimeRules.At(RebalanceHourEt, RebalanceMinuteEt),
                () =>
                {
                    // Safety guard against duplicate execution within the
                    // same calendar month (e.g. if the event were ever
                    // re-registered). Reset to -1 by the force-rebalance
                    // trigger below.
                    if (Time.Month != _lastRebalanceMonth)
                    {
                        // Remember that this month's rebalance has run.
                        _lastRebalanceMonth = Time.Month;
                        // Log the trigger with date AND time so the journal
                        // shows exactly when the scheduled event fired.
                        Log($"[Rebalance] Triggered on {Time:yyyy-MM-dd HH:mm} (first trading day of {Time:MMMM yyyy})");
                        // Run the rebalance (Day market orders).
                        Rebalance();
                    }
                }
            );

            // Poll every minute during market hours for a manual rebalance trigger file.
            // To trigger: touch /tmp/force_rebalance on the Pi.
            // Rebalance() always uses immediate Day MarketOrder()s, so this
            // behaves identically to the scheduled monthly rebalance.
            Schedule.On(
                DateRules.EveryDay(),
                TimeRules.Every(TimeSpan.FromMinutes(1)),
                () =>
                {
                    if (!IsMarketOpen("SPY")) return;

                    if (File.Exists("/tmp/force_rebalance"))
                    {
                        Log("[Rebalance] Manual trigger detected via /tmp/force_rebalance");
                        File.Delete("/tmp/force_rebalance");
                        _lastRebalanceMonth = -1;
                        Rebalance();
                    }
                }
            );
        }

        // =====================================================================
        // ██  ON DATA  ────────────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Called by LEAN on every new daily bar.
        /// Handles (in order):
        ///   1. Updating the peak portfolio value tracker.
        ///   2. Checking per-position stop-losses.
        ///   3. Evaluating max-drawdown halt condition.
        /// Note: Rebalancing is handled via Schedule.On() (first trading day of
        /// each month at 10:00 ET, see Initialize()), not in OnData().
        /// </summary>
        public override void OnData(Slice data)
        {
            // ── 1. Update peak portfolio value ─────────────────────────────────
            // We use TotalPortfolioValue (cash + market value of all positions).
            var currentValue = Portfolio.TotalPortfolioValue;
            if (currentValue > _peakPortfolioValue)
            {
                _peakPortfolioValue = currentValue;
                Log($"[PeakUpdate] New equity high: ${_peakPortfolioValue:F2}");
            }

            // ── 2. Check per-position stop-losses (daily) ──────────────────────
            // Iterate over all open positions and liquidate any that have fallen
            // more than StopLossThreshold below their recorded entry price.
            CheckStopLosses();

            // ── 3. Evaluate max-drawdown circuit-breaker ───────────────────────
            // If portfolio has dropped MaxDrawdownHaltThreshold from peak,
            // enter halt mode (move to AGG and wait HaltCooloffMonths).
            // If already in halt mode, check whether cooloff period has expired.
            if (_haltActive)
            {
                TryExitHaltMode();
                // While in halt mode we do nothing else — skip rebalance logic.
                return;
            }
            else
            {
                CheckDrawdownHalt();
                // CheckDrawdownHalt() may flip _haltActive = true; return early.
                if (_haltActive) return;
            }
        }

        // =====================================================================
        // ██  REBALANCE  ──────────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Core rebalance logic (run on the monthly schedule or by the manual
        /// /tmp/force_rebalance trigger).
        /// Steps:
        ///   A. Run the absolute momentum filter (SPY vs AGG over 12 months).
        ///      → If risk-off: move 100% to AGG.
        ///   B. If risk-on: rank universe by 6-month return, pick top-N symbols.
        ///   C. Liquidate positions not in the new top-N (full quantity).
        ///   D. Exclude selected symbols without a price; redistribute their
        ///      weight (base weight 1/k over the k priced symbols).
        ///   E. Size in fractional shares against TotalPortfolioValue minus a
        ///      1% cash reserve: wait for the liquidations, sell adjustments
        ///      first, then buys in rank order capped at available cash.
        /// All orders are immediate MarketOrder()s with Day time-in-force
        /// (Alpaca time_in_force=day). MarketOnCloseOrder() (Alpaca cls) was
        /// removed after the 2026-10-01 incident in which Alpaca paper expired
        /// every cls order unfilled -- see DEVIATIONS.md.
        /// </summary>
        private void Rebalance()
        {
            // Every OrderTicket this rebalance creates (liquidations + buys/
            // adjustments) is collected here so LogRebalanceOutcome() can
            // report honestly whether the broker accepted them, instead of
            // unconditionally printing "[Rebalance] Complete".
            var tickets = new List<OrderTicket>();

            // Fresh fill-wait budget for this rebalance (see ORDER FILL WAITS).
            _fillWaitSpent = new System.Diagnostics.Stopwatch();

            // ------------------------------------------------------------------
            // A. ABSOLUTE MOMENTUM FILTER
            //    Compare SPY 12-month return vs AGG 12-month return.
            //    If SPY <= AGG → defensive (risk-off).
            // ------------------------------------------------------------------
            var spyReturn  = GetMomentumReturn(AbsMomReferenceTicker, AbsoluteMomentumMonths);
            var aggReturn  = GetMomentumReturn(DefensiveTicker,        AbsoluteMomentumMonths);

            Log($"[AbsMom] SPY {AbsoluteMomentumMonths}-month return : {spyReturn:P2}");
            Log($"[AbsMom] AGG {AbsoluteMomentumMonths}-month return : {aggReturn:P2}");

            if (spyReturn == null || aggReturn == null)
            {
                // Not enough history yet — stay in cash / current positions.
                Log("[AbsMom] Insufficient history for absolute momentum filter. Skipping rebalance.");
                return;
            }

            bool riskOn = spyReturn.Value > aggReturn.Value;
            Log($"[AbsMom] Market regime: {(riskOn ? "RISK-ON" : "RISK-OFF")}");

            if (!riskOn)
            {
                // ── Risk-off: go fully defensive ────────────────────────────────
                Log("[Defensive] Moving 100% to AGG (absolute momentum filter: RISK-OFF).");
                // Sell everything except AGG first, full quantity. The sizing
                // helper below waits for these sells before sizing the AGG buy.
                var defLiquidationTickets = LiquidateAllExcept(DefensiveTicker);
                tickets.AddRange(defLiquidationTickets);
                // Wait for the sells (up to OrderFillWaitSeconds each) before
                // anything is sized; the ones not filled are excluded from the
                // sizing base below.
                var defUnsoldTickets = WaitForTickets(defLiquidationTickets, "[Rebalance]", "liquidation", "sell");
                // Use an explicit MarketOrder instead of SetHoldings() so that
                // the Day TimeInForce on DefaultOrderProperties is respected
                // and the order is not downgraded to a MarketOnOpen by the
                // Alpaca brokerage model.
                var defSym   = _symbols[DefensiveTicker];
                var defPrice = Securities[defSym].Price;
                // AGG is the single "selected" symbol on this path, so it gets
                // the same skip reporting as the top-N symbols on the risk-on path.
                var defSkipCounts      = new Dictionary<string, int>();
                var defOrdersSubmitted = 0;
                if (defPrice > 0)
                {
                    // Same fractional sizing, reserve, sells-first order and
                    // cash cap as the risk-on path, with AGG at 100% weight.
                    var defTargets = new List<AllocationTarget>
                    {
                        new AllocationTarget(DefensiveTicker, defSym, 1.0m)
                    };
                    var defResult = ExecuteAllocation(defTargets, defUnsoldTickets,
                        AllocationContext.Defensive, 1.0m);
                    tickets.AddRange(defResult.Tickets);
                    defOrdersSubmitted = defResult.OrdersSubmitted;
                    foreach (var kv in defResult.SkipCounts)
                    {
                        defSkipCounts[kv.Key] = defSkipCounts.GetValueOrDefault(kv.Key, 0) + kv.Value;
                    }
                }
                else
                {
                    // Unchanged behavior: AGG had no usable price, so no AGG
                    // order could be sized and nothing is redistributed. The
                    // liquidations above still went out, so the proceeds will
                    // sit in cash instead of AGG.
                    CountSkip(defSkipCounts, SkipNoPrice);
                    Error($"[Rebalance][SKIP] {DefensiveTicker} {SkipNoPrice} — price=${defPrice:F2} {DescribeLastData(defSym)}; " +
                          $"no {DefensiveTicker} order submitted, liquidation proceeds will remain in cash");
                }
                Log($"[Defensive] Target: 100% {DefensiveTicker}");
                // Report whether the broker actually accepted the orders,
                // plus how the single selected symbol (AGG) was handled.
                LogRebalanceOutcome(tickets, $"100% {DefensiveTicker}",
                    FormatSelectionSummary(1, defOrdersSubmitted, defSkipCounts),
                    defSkipCounts.ContainsKey(SkipNoPrice));
                return;
            }

            // ------------------------------------------------------------------
            // B. RELATIVE MOMENTUM RANKING (risk-on path)
            //    Compute 6-month price return for each universe symbol,
            //    sort descending, select top TopPositions.
            // ------------------------------------------------------------------
            Log($"[RelMom] Ranking universe by {RelativeMomentumMonths}-month return...");

            var momentumScores = new Dictionary<string, decimal>();

            foreach (var ticker in UniverseTickers)
            {
                var ret = GetMomentumReturn(ticker, RelativeMomentumMonths);
                if (ret.HasValue)
                {
                    momentumScores[ticker] = ret.Value;
                    Log($"[RelMom] {ticker,6}: {ret.Value,8:P2}");
                }
                else
                {
                    Log($"[RelMom] {ticker,6}: insufficient history — excluded");
                }
            }

            // Sort by return descending, take the top TopPositions tickers.
            var topTickers = momentumScores
                .OrderByDescending(kv => kv.Value)
                .Take(TopPositions)
                .Select(kv => kv.Key)
                .ToList();

            Log($"[RelMom] Selected top {TopPositions}: {string.Join(", ", topTickers)}");

            // ------------------------------------------------------------------
            // C. LIQUIDATE positions not in the new target set (full quantity,
            //    dust included). ExecuteAllocation() waits for these sells
            //    before it sizes any buy.
            // ------------------------------------------------------------------
            var liquidationTickets = LiquidateAllExcept(topTickers);
            tickets.AddRange(liquidationTickets);
            // Wait for the sells (up to OrderFillWaitSeconds each, all at
            // once) before anything is sized; the ones not filled are excluded
            // from the sizing base in ExecuteAllocation().
            var unsoldTickets = WaitForTickets(liquidationTickets, "[Rebalance]", "liquidation", "sell");

            // Logging-only bookkeeping for the "[Rebalance] Complete" summary:
            // per-reason count of selected symbols that got no order, and
            // the number of selected symbols that did get an order.
            var skipCounts              = new Dictionary<string, int>();
            var selectedOrdersSubmitted = 0;

            // ------------------------------------------------------------------
            // D. EXCLUDE selected symbols that cannot be sized (no price, or
            //    missing from Securities) and REDISTRIBUTE their weight across
            //    the remaining selected symbols (owner decision). Rank order is
            //    preserved, so buys below are still placed in rank order.
            //    This NEVER changes which tickers are selected (topTickers above
            //    is untouched) -- only which of them can be sized this month.
            // ------------------------------------------------------------------
            var pricedTickers   = new List<string>();
            var excludedTickers = new List<string>();
            foreach (var ticker in topTickers)
            {
                var sym = _symbols[ticker];
                if (Securities.ContainsKey(sym) && Securities[sym].Price > 0)
                {
                    pricedTickers.Add(ticker);
                    continue;
                }

                excludedTickers.Add(ticker);
                if (!Securities.ContainsKey(sym))
                {
                    // Symbol missing from Securities entirely. held is reported
                    // as n/a: Portfolio[sym] looks the symbol up in Securities
                    // and would throw here, aborting the rebalance.
                    CountSkip(skipCounts, SkipNotInSecurities);
                    Log($"[Allocate] {ticker} → {0m:P1} (excluded, weight redistributed) | sizing: price=n/a lastData=n/a " +
                        $"weight={0m:P1} target=n/a targetQty=n/a held=n/a delta=n/a");
                    Error($"[Rebalance][SKIP] {ticker} {SkipNotInSecurities} — symbol is not in Securities, cannot size; " +
                          "no order submitted, weight redistributed to the other selected symbols");
                }
                else
                {
                    // Price was 0 (no data received yet, or the price was
                    // otherwise unusable). This is the 2026-10-05 MSFT/AAPL case.
                    // Any existing holding of this symbol is left untouched.
                    var zeroPriceLastData = DescribeLastData(sym);
                    CountSkip(skipCounts, SkipNoPrice);
                    Log($"[Allocate] {ticker} → {0m:P1} (excluded, weight redistributed) | sizing: " +
                        $"price=${Securities[sym].Price:F2} {zeroPriceLastData} weight={0m:P1} target=n/a targetQty=n/a " +
                        $"held={Portfolio[sym].Quantity:0.#########} delta=n/a");
                    Error($"[Rebalance][SKIP] {ticker} {SkipNoPrice} — price=${Securities[sym].Price:F2} {zeroPriceLastData}, " +
                          "cannot size; no order submitted, weight redistributed to the other selected symbols");
                }
            }

            // k = number of selected symbols that can be sized. Base weight is
            // 1/k, so the sentiment-adjusted weights still sum to 100% across
            // them (PositionSizer keeps the total at k x base weight).
            var pricedCount = pricedTickers.Count;
            if (pricedCount == 0)
            {
                // Nothing can be sized: place no buy orders and keep the cash.
                // Liquidations of non-selected holdings above still went out.
                Error($"[Rebalance] NO PRICED PICKS — {excludedTickers.Count} of {topTickers.Count} selected had no usable price " +
                      $"({string.Join(", ", excludedTickers)}); no buy orders placed, cash kept");
                LogRebalanceOutcome(tickets,
                    $"none sized (excluded: {string.Join(", ", excludedTickers)})",
                    FormatSelectionSummary(topTickers.Count, 0, skipCounts),
                    true);
                return;
            }

            var baseWeight = 1.0m / pricedCount;
            if (excludedTickers.Count > 0)
            {
                Log($"[Rebalance] Redistributing: {excludedTickers.Count} of {topTickers.Count} selected had no price " +
                    $"({string.Join(", ", excludedTickers)}); {pricedCount} symbols at ~{baseWeight:P1} each " +
                    "before sentiment adjustment");
            }

            // ------------------------------------------------------------------
            // E. ALLOCATE to each priced symbol, using sentiment-adjusted
            //    weights within the existing top-N branch only (see
            //    ComputeSentimentAdjustedWeights), computed over the priced
            //    symbols only with a base weight of 1/k.
            // ------------------------------------------------------------------
            var tickerWeights = ComputeSentimentAdjustedWeights(pricedTickers, baseWeight);

            var targets = new List<AllocationTarget>();
            foreach (var ticker in pricedTickers)
            {
                targets.Add(new AllocationTarget(ticker, _symbols[ticker], tickerWeights.GetValueOrDefault(ticker, baseWeight)));
            }

            // Sells first (liquidations, then down-adjustments), wait, then
            // buys capped at available cash -- see ExecuteAllocation().
            var result = ExecuteAllocation(targets, unsoldTickets, AllocationContext.RiskOn, baseWeight);
            tickets.AddRange(result.Tickets);
            selectedOrdersSubmitted += result.OrdersSubmitted;
            foreach (var kv in result.SkipCounts)
            {
                skipCounts[kv.Key] = skipCounts.GetValueOrDefault(kv.Key, 0) + kv.Value;
            }

            // Target description: priced symbols with their final weights,
            // plus the excluded ones so the line shows why fewer were bought.
            var targetDescription = string.Join(", ",
                pricedTickers.Select(t => $"{t}@{tickerWeights.GetValueOrDefault(t, baseWeight):P1}"));
            if (excludedTickers.Count > 0)
            {
                targetDescription += $" (excluded, no price: {string.Join(", ", excludedTickers)})";
            }

            // Only claims "Complete" if every order was accepted by the broker.
            // The selection summary adds how many selected symbols were skipped and why.
            LogRebalanceOutcome(tickets, targetDescription,
                FormatSelectionSummary(topTickers.Count, selectedOrdersSubmitted, skipCounts),
                skipCounts.ContainsKey(SkipNoPrice));
        }

        // =====================================================================
        // ██  FRACTIONAL ALLOCATION ENGINE  ──────────────────────────────────
        // Shared by the risk-on rebalance, the risk-off (100% AGG) rebalance
        // and the drawdown halt (100% AGG). Order of operations:
        //   1. The caller has already submitted the liquidation sells and
        //      waited for them (WaitForTickets). Any that did not fill are
        //      passed in; their value is excluded from the sizing base and
        //      every buy stays capped by the cash actually available.
        //   2. investable = (TotalPortfolioValue - unsold value) x (1 - reserve).
        //   3. Per target: targetValue = weight x investable; targetQty =
        //      floor(targetValue / price) to the lot size; delta = targetQty -
        //      held (decimal). Small adjustments are skipped.
        //   4. Sell adjustments first, wait for them.
        //   5. Buys in the caller's order (rank order), each capped at
        //      Portfolio.Cash - reserve cash - value of earlier unfilled buys,
        //      and each waited for before the next buy is sized.
        // Waits use WaitForTickets(): up to OrderFillWaitSeconds per order and
        // RebalanceFillWaitBudgetSeconds in total per rebalance.
        // Quantities are always rounded DOWN to the lot size, for buys and sells.
        // =====================================================================

        // Which caller is allocating; selects the log line formats.
        private enum AllocationContext
        {
            RiskOn,
            Defensive,
            DrawdownHalt
        }

        // One symbol to allocate to, with its final weight (fraction of the
        // investable amount; the weights of one call sum to about 1.0).
        private sealed class AllocationTarget
        {
            public AllocationTarget(string ticker, Symbol symbol, decimal weight)
            {
                Ticker = ticker;
                Symbol = symbol;
                Weight = weight;
            }

            public string Ticker { get; }
            public Symbol Symbol { get; }
            public decimal Weight { get; }
        }

        // Sizing result for one target, filled in by ExecuteAllocation().
        private sealed class AllocationPlan
        {
            public AllocationPlan(AllocationTarget target)
            {
                Target = target;
            }

            public AllocationTarget Target { get; }
            // Price used for sizing (Securities[sym].Price at sizing time).
            public decimal Price { get; set; }
            // Decimal quantity currently held.
            public decimal Held { get; set; }
            // weight x investable, in USD.
            public decimal TargetValue { get; set; }
            // TargetValue / Price rounded down to the lot size.
            public decimal TargetQty { get; set; }
            // TargetQty - Held (signed, before rounding the order quantity).
            public decimal Delta { get; set; }
            // Order quantity to trade, always positive and rounded down to the
            // lot size; the direction is given by IsBuy.
            public decimal OrderQty { get; set; }
            // True for a buy, false for a sell adjustment.
            public bool IsBuy { get; set; }
            // Skip reason, or null when an order should be placed.
            public string? SkipReason { get; set; }
            // Extra context for the skip line.
            public string SkipDetail { get; set; } = "";
        }

        // What ExecuteAllocation() did: the tickets it created (sell
        // adjustments and buys, NOT the caller's liquidations), how many
        // targets got an order, and per-reason skip counts.
        private sealed class AllocationResult
        {
            public List<OrderTicket> Tickets { get; } = new List<OrderTicket>();
            public int OrdersSubmitted { get; set; }
            public Dictionary<string, int> SkipCounts { get; } = new Dictionary<string, int>();
        }

        // Sizes and places the orders that move the portfolio to the given
        // targets. See the FRACTIONAL ALLOCATION ENGINE banner above for the
        // order of operations.
        //   targets            - symbols to allocate to, in the order buys are placed
        //   unsoldTickets      - liquidation sells the caller waited for that did NOT fill
        //   context            - which caller is allocating (selects the log formats)
        //   baseWeight         - base weight before sentiment adjustment (log line only)
        // Returns the tickets placed and the per-reason skip counts.
        private AllocationResult ExecuteAllocation(List<AllocationTarget> targets, List<OrderTicket> unsoldTickets,
            AllocationContext context, decimal baseWeight)
        {
            var result = new AllocationResult();
            // Prefix for this engine's own lines ([Rebalance] or [DrawdownHalt]).
            var prefix = context == AllocationContext.DrawdownHalt ? "[DrawdownHalt]" : "[Rebalance]";

            // ── 1. Liquidation sells: already submitted and waited for by the
            //       caller; unsoldTickets holds the ones that did not fill. ────

            // ── 2. Sizing base, recomputed AFTER the sells ────────────────────
            var tpv = Portfolio.TotalPortfolioValue;
            // Value of positions whose liquidation has not filled yet. It is
            // still counted in TotalPortfolioValue but is not cash, so it is
            // excluded from the sizing base (each symbol counted once).
            var unsoldValue = 0m;
            foreach (var unsoldSymbol in unsoldTickets.Select(t => t.Symbol).Distinct())
            {
                unsoldValue += Portfolio[unsoldSymbol].AbsoluteHoldingsValue;
            }
            // Cash held back at every step, from the full portfolio value.
            var reserveCash = CashReserveFraction * tpv;
            // Amount the target weights are applied to.
            var investable = (tpv - unsoldValue) * (1m - CashReserveFraction);
            if (investable < 0m) investable = 0m;
            // Smallest adjustment worth trading for an already-held symbol.
            var minAdjustment = Math.Max(MinOrderNotional, MinAdjustmentFraction * tpv);
            var basis = unsoldTickets.Count == 0
                ? "TotalPortfolioValue"
                : $"CASH-LIMITED (unsold liquidations worth ${unsoldValue:F2} excluded; buys capped by actual cash)";
            Log($"{prefix} Sizing base: tpv=${tpv:F2} cash=${Portfolio.Cash:F2} reserve=${reserveCash:F2} " +
                $"({CashReserveFraction:P1}) investable=${investable:F2} minAdjustment=${minAdjustment:F2} basis={basis}");

            // ── 3. Size every target ──────────────────────────────────────────
            var plans = new List<AllocationPlan>();
            foreach (var target in targets)
            {
                var sym  = target.Symbol;
                var plan = new AllocationPlan(target);
                plans.Add(plan);

                // Read price and held quantity once, so the logged values are
                // exactly the ones used for sizing. Callers only pass priced
                // symbols; the guard keeps a zero price from dividing by zero.
                plan.Price       = Securities[sym].Price;
                plan.Held        = Portfolio[sym].Quantity;
                plan.TargetValue = target.Weight * investable;
                plan.TargetQty   = plan.Price > 0m ? FloorToLot(sym, plan.TargetValue / plan.Price) : 0m;
                plan.Delta       = plan.TargetQty - plan.Held;
                plan.IsBuy       = plan.Delta > 0m;
                // Round the order quantity DOWN to the lot size in both
                // directions: never buy more than the target, never sell more
                // than needed (and so never more than is held).
                plan.OrderQty    = FloorToLot(sym, Math.Abs(plan.Delta));
                var orderNotional = plan.OrderQty * plan.Price;
                var alreadyHeld   = plan.Held != 0m;

                if (plan.OrderQty == 0m)
                {
                    if (alreadyHeld)
                    {
                        plan.SkipReason = SkipAlreadyAtTarget;
                    }
                    else
                    {
                        // Not held and the target rounds down to nothing.
                        plan.SkipReason = SkipBelowMinOrder;
                    }
                }
                else if (alreadyHeld && orderNotional < minAdjustment)
                {
                    // Held already: skip tiny adjustments in either direction.
                    plan.SkipReason = SkipBelowMinAdjustment;
                }
                else if (!alreadyHeld && orderNotional < MinOrderNotional)
                {
                    // New position worth less than the $1 minimum.
                    plan.SkipReason = SkipBelowMinOrder;
                }

                if (plan.SkipReason != null)
                {
                    plan.SkipDetail = $"targetQty={plan.TargetQty:0.####} held={plan.Held:0.#########} " +
                                      $"delta={plan.Delta:0.#########} notional=${orderNotional:F2} " +
                                      (plan.SkipReason == SkipBelowMinAdjustment
                                          ? $"< min adjustment ${minAdjustment:F2}"
                                          : plan.SkipReason == SkipBelowMinOrder
                                              ? $"< min order ${MinOrderNotional:F2}"
                                              : "(nothing to trade)");
                }

                // Stop-loss entry price initialisation, risk-on path only
                // (unchanged behavior): the current price, refined by the fill
                // price in OnOrderEvent().
                if (context == AllocationContext.RiskOn)
                {
                    _entryPrices[sym] = plan.Price;
                }

                // Sizing detail: price used, data age, weight, investable,
                // target value, decimal target quantity, held, delta, notional.
                var sizingDetail = $"price=${plan.Price:F2} {DescribeLastData(sym)} weight={target.Weight:P1} " +
                                   $"investable=${investable:F2} targetValue=${plan.TargetValue:F2} " +
                                   $"targetQty={plan.TargetQty:0.####} held={plan.Held:0.#########} " +
                                   $"delta={plan.Delta:0.#########} " +
                                   $"order={(plan.SkipReason != null ? "none" : (plan.IsBuy ? "BUY" : "SELL"))} " +
                                   $"orderQty={(plan.SkipReason != null ? 0m : plan.OrderQty):0.####} " +
                                   $"notional=${(plan.SkipReason != null ? 0m : orderNotional):F2}";
                if (context == AllocationContext.RiskOn)
                {
                    // Existing prefix kept (log readers may depend on it); the
                    // sizing fields are appended after " | sizing: ".
                    Log($"[Allocate] {target.Ticker} → {target.Weight:P1} (base {baseWeight:P0}, entry ~${_entryPrices.GetValueOrDefault(sym, 0):F2})" +
                        $" | sizing: {sizingDetail}");
                }
                else if (context == AllocationContext.Defensive)
                {
                    Log($"[Defensive] Sizing {target.Ticker}: {sizingDetail}");
                }
                else
                {
                    Log($"[DrawdownHalt] Sizing {target.Ticker}: {sizingDetail}");
                }

                if (plan.SkipReason != null)
                {
                    // Benign: sizing worked, the result was simply "no trade".
                    RecordSkip(result, prefix, target.Ticker, plan.SkipReason, plan.SkipDetail);
                }
            }

            // ── 4. Sell adjustments first, then wait for them ─────────────────
            var sellAdjustmentTickets = new List<OrderTicket>();
            foreach (var plan in plans.Where(p => p.SkipReason == null && !p.IsBuy))
            {
                var ticket = PlaceSizedMarketOrder(plan.Target.Symbol, -plan.OrderQty, plan.Price, true);
                Log($"{prefix}[ORDER] {plan.Target.Ticker} SELL-ADJUST qty={plan.OrderQty:0.####} " +
                    $"sizingPrice=${plan.Price:F2} notional=${plan.OrderQty * plan.Price:F2} " +
                    $"type={ticket.OrderType} status={ticket.Status}");
                sellAdjustmentTickets.Add(ticket);
                result.Tickets.Add(ticket);
                result.OrdersSubmitted++;
            }
            // Buys below are capped by Portfolio.Cash, so a sell adjustment
            // that has not filled simply leaves less cash for the buys.
            WaitForTickets(sellAdjustmentTickets, prefix, "sell adjustment", "sell");

            // ── 5. Buys in the caller's order, capped by available cash ───────
            // Earlier buys of this call with their sizing price, so the value
            // of any that have not filled yet is not counted as spendable.
            var placedBuys = new List<KeyValuePair<OrderTicket, decimal>>();
            foreach (var plan in plans.Where(p => p.SkipReason == null && p.IsBuy))
            {
                var sym = plan.Target.Symbol;
                // Value of earlier buys not yet (fully) filled: Portfolio.Cash
                // only drops when a fill is processed.
                var pendingBuyValue = 0m;
                foreach (var placed in placedBuys)
                {
                    var t = placed.Key;
                    if (t.Status == OrderStatus.Filled || t.Status == OrderStatus.Invalid || t.Status == OrderStatus.Canceled)
                    {
                        continue;
                    }
                    var unfilledQty = Math.Abs(t.Quantity) - Math.Abs(t.QuantityFilled);
                    if (unfilledQty > 0m) pendingBuyValue += unfilledQty * placed.Value;
                }

                // Cash that may be spent on this buy right now.
                var cash = Portfolio.Cash;
                var availableCash = cash - reserveCash - pendingBuyValue;
                var maxQty = availableCash > 0m ? FloorToLot(sym, availableCash / plan.Price) : 0m;
                var buyQty = plan.OrderQty;
                if (buyQty > maxQty)
                {
                    // Cash cap reduced (or removed) this buy.
                    Log($"{prefix}[CAP] {plan.Target.Ticker} buy reduced from {buyQty:0.####} to {maxQty:0.####} by cash cap: " +
                        $"cash=${cash:F2} reserve=${reserveCash:F2} pendingBuys=${pendingBuyValue:F2} " +
                        $"available=${availableCash:F2} price=${plan.Price:F2}");
                    buyQty = maxQty;
                    if (buyQty * plan.Price < MinOrderNotional)
                    {
                        // The capped buy would be below the $1 minimum: skip it.
                        RecordSkip(result, prefix, plan.Target.Ticker, SkipInsufficientCash,
                            $"capped qty={buyQty:0.####} notional=${buyQty * plan.Price:F2} < min order ${MinOrderNotional:F2} " +
                            $"(wanted {plan.OrderQty:0.####})");
                        continue;
                    }
                }

                var ticket = PlaceSizedMarketOrder(sym, buyQty, plan.Price, true);
                Log($"{prefix}[ORDER] {plan.Target.Ticker} BUY qty={buyQty:0.####} sizingPrice=${plan.Price:F2} " +
                    $"notional=${buyQty * plan.Price:F2} cashBefore=${cash:F2} " +
                    $"type={ticket.OrderType} status={ticket.Status}");
                placedBuys.Add(new KeyValuePair<OrderTicket, decimal>(ticket, plan.Price));
                result.Tickets.Add(ticket);
                result.OrdersSubmitted++;
                // Wait for this buy before sizing the next one, so the next
                // cash cap sees the real fill. A buy still open after the wait
                // is counted as pending (pendingBuyValue) by the next cap.
                WaitForTickets(new List<OrderTicket> { ticket }, prefix, "buy", "buy");
            }

            return result;
        }

        // Counts a skip for the outcome summary and logs it as a benign
        // (Log-level, WARNING token) line; LEAN has no warning level.
        private void RecordSkip(AllocationResult result, string prefix, string ticker, string reason, string detail)
        {
            CountSkip(result.SkipCounts, reason);
            Log($"{prefix}[SKIP] {ticker} {reason} WARNING: no order — {detail}");
        }

        // Waits for order tickets to close and returns the ones that are not
        // Filled. Each ticket is waited for up to OrderFillWaitSeconds from
        // its submission, and all waits of one rebalance share a total budget
        // of RebalanceFillWaitBudgetSeconds (_fillWaitSpent). Tickets are
        // waited for together, so several sells submitted at once overlap.
        // The wait uses each ticket's OrderClosed handle (the same mechanism
        // LEAN's own Transactions.WaitForOrder() uses, SecurityTransactionManager.cs
        // in the pinned LEAN source) in short slices, so a fill is noticed at
        // once. LEAN updates the portfolio before it marks an order closed
        // (BrokerageTransactionHandler: Portfolio.ProcessFills runs before the
        // ticket's order event), so Portfolio.Cash already includes a fill
        // once its ticket reports Filled.
        //   kind - log label, e.g. "liquidation", "sell adjustment", "buy"
        //   side - "sell" or "buy"; sells that do not fill get the
        //          SELL-NOT-FILLED error line (cash-limited sizing)
        private List<OrderTicket> WaitForTickets(List<OrderTicket> orderTickets, string prefix, string kind, string side)
        {
            var notFilled = new List<OrderTicket>();
            if (orderTickets.Count == 0) return notFilled;

            var perOrderLimitTicks = OrderFillWaitSeconds * System.Diagnostics.Stopwatch.Frequency;
            var budgetLimit = TimeSpan.FromSeconds(RebalanceFillWaitBudgetSeconds);
            // Tickets still being waited for.
            var pending = new List<OrderTicket>(orderTickets);
            // Why a ticket stopped being waited for without closing.
            var gaveUpReason = new Dictionary<int, string>();

            _fillWaitSpent.Start();
            try
            {
                while (pending.Count > 0)
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    foreach (var ticket in pending.ToList())
                    {
                        var closed = ticket.Status == OrderStatus.Filled ||
                                     ticket.Status == OrderStatus.Invalid ||
                                     ticket.Status == OrderStatus.Canceled;
                        if (closed)
                        {
                            pending.Remove(ticket);
                            Log($"{prefix}[WAIT] {ticket.Symbol.Value} {side} {ticket.Status.ToString().ToLowerInvariant()} " +
                                $"after {SecondsSinceSubmission(ticket):F1}s ({kind}, order {ticket.OrderId})");
                        }
                        else if (now - SubmittedAt(ticket) >= perOrderLimitTicks)
                        {
                            pending.Remove(ticket);
                            gaveUpReason[ticket.OrderId] = $"per-order limit {OrderFillWaitSeconds}s";
                        }
                    }
                    if (pending.Count == 0) break;
                    if (_fillWaitSpent.Elapsed >= budgetLimit)
                    {
                        // Total budget for this rebalance is used up: stop
                        // waiting for everything that is still open.
                        foreach (var ticket in pending)
                        {
                            gaveUpReason[ticket.OrderId] = $"rebalance wait budget {RebalanceFillWaitBudgetSeconds}s used up";
                        }
                        pending.Clear();
                        break;
                    }
                    // Block on the first open ticket for a short slice; returns
                    // early as soon as that order closes.
                    pending[0].OrderClosed.WaitOne(TimeSpan.FromMilliseconds(100));
                }
            }
            finally
            {
                _fillWaitSpent.Stop();
            }

            foreach (var ticket in orderTickets)
            {
                if (gaveUpReason.TryGetValue(ticket.OrderId, out var reason))
                {
                    Error($"{prefix}[WAIT] {ticket.Symbol.Value} {side} NOT FILLED after {SecondsSinceSubmission(ticket):F1}s " +
                          $"({reason}; {kind}, order {ticket.OrderId}, status={ticket.Status}, " +
                          $"filled={ticket.QuantityFilled:0.#########} of {ticket.Quantity:0.#########}); " +
                          $"total wait so far {_fillWaitSpent.Elapsed.TotalSeconds:F1}s");
                }
                if (ticket.Status != OrderStatus.Filled)
                {
                    notFilled.Add(ticket);
                    if (side == "sell")
                    {
                        Error($"{prefix}[SELL-NOT-FILLED] {ticket.Symbol.Value} {kind} order {ticket.OrderId} status={ticket.Status} " +
                              $"qty={ticket.Quantity:0.#########} filled={ticket.QuantityFilled:0.#########} type={ticket.OrderType}; " +
                              "its value is not counted as cash and buys are capped by actual Portfolio.Cash");
                    }
                }
            }
            return notFilled;
        }

        // Submission timestamp recorded by PlaceSizedMarketOrder(); a ticket
        // placed some other way counts as submitted now.
        private long SubmittedAt(OrderTicket ticket)
        {
            lock (_sizingPriceLock)
            {
                if (_orderSubmittedAt.TryGetValue(ticket.OrderId, out var submittedAt)) return submittedAt;
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                _orderSubmittedAt[ticket.OrderId] = now;
                return now;
            }
        }

        // Seconds since the ticket's order was submitted, for the WAIT lines.
        private double SecondsSinceSubmission(OrderTicket ticket)
        {
            var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - SubmittedAt(ticket);
            return (double)ticks / System.Diagnostics.Stopwatch.Frequency;
        }

        // Records the sizing price for the slippage log in OnOrderEvent(),
        // then submits an immediate Day market order (Alpaca
        // time_in_force=day) for the signed quantity.
        //   asynchronous - true: return at once and let WaitForTickets() do
        //                  the (longer, budgeted) wait; false: LEAN's own
        //                  synchronous wait of up to 5 s in live trading
        //                  (used by the stop-loss path, unchanged).
        private OrderTicket PlaceSizedMarketOrder(Symbol sym, decimal signedQuantity, decimal sizingPrice, bool asynchronous)
        {
            lock (_sizingPriceLock)
            {
                _sizingPriceBySymbol[sym] = sizingPrice;
            }
            var submittedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var ticket = MarketOrder(sym, signedQuantity, asynchronous);
            lock (_sizingPriceLock)
            {
                _orderSubmittedAt[ticket.OrderId] = submittedAt;
            }
            return ticket;
        }

        // Lot size used to round quantities for one symbol: the sizing lot
        // chosen in Initialize(), or the symbol's own LEAN lot size if that is
        // larger, so an order is never finer than LEAN will accept.
        private decimal LotSizeFor(Symbol sym)
        {
            var lot = _sizingLotSize;
            if (Securities.ContainsKey(sym))
            {
                var securityLot = Securities[sym].SymbolProperties.LotSize;
                if (securityLot > lot) lot = securityLot;
            }
            return lot > 0m ? lot : 1m;
        }

        // Rounds a quantity DOWN (toward zero) to a multiple of the lot size.
        // Negative and zero inputs return 0; callers pass magnitudes.
        private decimal FloorToLot(Symbol sym, decimal quantity)
        {
            if (quantity <= 0m) return 0m;
            var lot = LotSizeFor(sym);
            return Math.Floor(quantity / lot) * lot;
        }

        // Signed version for an existing holding: rounds the magnitude down to
        // the lot size and keeps the sign, so the result is never larger in
        // magnitude than the quantity actually held.
        private decimal FloorHeldToLot(Symbol sym, decimal heldQuantity)
        {
            var magnitude = FloorToLot(sym, Math.Abs(heldQuantity));
            return heldQuantity < 0m ? -magnitude : magnitude;
        }

        // Registers the fractional lot size for every US equity (the
        // usa/[*]/equity wildcard row) by copying LEAN's existing entry and
        // changing only the lot size. Called from Initialize() BEFORE the
        // AddEquity loop. Uses SymbolPropertiesDatabase.GetSymbolProperties()
        // and SetEntry(), both public in the pinned LEAN source
        // (Common/Securities/SymbolPropertiesDatabase.cs); LEAN's own
        // regression algorithm ManuallySetMarketHoursAndSymbolPropertiesDatabaseEntriesAlgorithm
        // uses the same two calls with a null symbol for the wildcard row.
        // The entry is kept across LEAN's daily live reload of the database
        // (custom entries are preserved by UpdateDataFolderDatabase()).
        private void RegisterFractionalLotSize()
        {
            if (!UseFractionalSizing)
            {
                Log("[LotSize] Fractional sizing is OFF; US equity lot size left at LEAN's default.");
                return;
            }

            try
            {
                // Current wildcard entry: usa,[*],equity,,USD,1,0.01,1 in
                // LEAN's symbol-properties-database.csv at the pinned commit.
                var existing = SymbolPropertiesDatabase.GetSymbolProperties(Market.USA, null, SecurityType.Equity, Currencies.USD);
                // Copy every field and change only the lot size.
                var fractional = new QuantConnect.Securities.SymbolProperties(
                    existing.Description,
                    existing.QuoteCurrency,
                    existing.ContractMultiplier,
                    existing.MinimumPriceVariation,
                    FractionalLotSize,
                    existing.MarketTicker,
                    existing.MinimumOrderSize,
                    existing.PriceMagnifier,
                    existing.StrikeMultiplier);
                SymbolPropertiesDatabase.SetEntry(Market.USA, null, SecurityType.Equity, fractional);
                Log($"[LotSize] Registered US equity lot size {FractionalLotSize} (was {existing.LotSize}); " +
                    $"other properties unchanged: quoteCurrency={existing.QuoteCurrency} " +
                    $"multiplier={existing.ContractMultiplier} tick={existing.MinimumPriceVariation}");
            }
            catch (Exception ex)
            {
                // Never abort Initialize() over this: VerifyEffectiveLotSize()
                // will see the unchanged lot size and fall back to whole shares.
                Error($"[LotSize] Failed to register fractional lot size: {ex.Message}. Whole-share sizing will be used.");
            }
        }

        // Reads the lot size LEAN actually applied to the universe after the
        // AddEquity loop, logs it for SPY (confirm on the Pi), reports any
        // universe symbol that differs, and sets _sizingLotSize to the
        // largest lot size in effect (never smaller than the configured one).
        private void VerifyEffectiveLotSize()
        {
            var configuredLot = UseFractionalSizing ? FractionalLotSize : 1m;
            var spyLot = Securities[_symbols[AbsMomReferenceTicker]].SymbolProperties.LotSize;
            var largestLot = configuredLot;
            var mismatched = new List<string>();
            foreach (var ticker in UniverseTickers)
            {
                var lot = Securities[_symbols[ticker]].SymbolProperties.LotSize;
                if (lot != configuredLot) mismatched.Add($"{ticker}={lot}");
                if (lot > largestLot) largestLot = lot;
            }
            _sizingLotSize = largestLot;

            if (mismatched.Count == 0)
            {
                Log($"[LotSize] Effective lot size check OK: {AbsMomReferenceTicker} lot size = {spyLot} " +
                    $"(expected {configuredLot}); all {UniverseTickers.Length} universe symbols match. " +
                    $"Sizing lot size = {_sizingLotSize}");
            }
            else
            {
                Error($"[LotSize] Effective lot size MISMATCH: {AbsMomReferenceTicker} lot size = {spyLot} " +
                      $"(expected {configuredLot}); differing symbols: {string.Join(", ", mismatched)}. " +
                      $"Sizing lot size set to {_sizingLotSize} so orders stay valid.");
            }
        }

        /// <summary>
        /// Logs the outcome of a rebalance based on the actual state of the
        /// OrderTickets it produced, rather than unconditionally claiming
        /// success.
        ///
        /// Rebalance orders are submitted asynchronously and waited for by
        /// WaitForTickets() (up to 30 s per order, 120 s per rebalance), so
        /// by the time this runs each ticket's Status reflects the broker's
        /// response so far:
        ///   Submitted / PartiallyFilled / Filled → broker accepted the order
        ///   Invalid / Canceled                   → rejected or canceled
        ///   anything else (e.g. New)             → no broker acknowledgement yet
        ///
        /// "Accepted" is NOT the same as "filled": a Submitted Day market order
        /// can still go unfilled. Actual fills are logged per order by
        /// OnOrderEvent() as "[OrderEvent] ... Status: Filled".
        /// </summary>
        /// <param name="tickets">Every OrderTicket created by this rebalance.</param>
        /// <param name="targetDescription">Human-readable target portfolio for the log line.</param>
        /// <param name="selectionSummary">Count of selected symbols, orders submitted for them, and skips by reason.</param>
        /// <param name="anyNoPriceSkip">True if any selected symbol was skipped for NO_PRICE.</param>
        private void LogRebalanceOutcome(List<OrderTicket> tickets, string targetDescription,
            string selectionSummary, bool anyNoPriceSkip)
        {
            // The selection summary is appended AFTER the existing line text,
            // so every existing "[Rebalance] Complete ..." prefix is unchanged.
            // It reports selected symbols that got no order, which the
            // accepted/rejected counts below cannot show (a skipped symbol
            // never produces a ticket). The accept/reject counting itself is
            // unchanged.
            var selectionSuffix = $" | Selection: {selectionSummary}";

            // Total time this rebalance spent waiting for fills.
            Log($"[Rebalance][WAIT] total fill wait {_fillWaitSpent.Elapsed.TotalSeconds:F1}s of " +
                $"{RebalanceFillWaitBudgetSeconds}s budget ({tickets.Count} order(s))");

            // How close to 100% invested this run got: cash left after the
            // orders above, as a share of the portfolio. Fills still pending
            // at this moment have not reduced Portfolio.Cash yet. The target
            // is about CashReserveFraction (1%) plus rounding and skips.
            var cashAfter = Portfolio.Cash;
            var tpvAfter  = Portfolio.TotalPortfolioValue;
            var cashAfterFraction = tpvAfter > 0m ? cashAfter / tpvAfter : 0m;
            selectionSuffix += $" | Cash after: ${cashAfter:F2} ({cashAfterFraction:P1} of portfolio)";

            // No orders at all: holdings were already at target (or every
            // price was 0 so nothing could be sized). Nothing to accept.
            if (tickets.Count == 0)
            {
                var noOrdersLine = $"[Rebalance] Complete — no orders needed. Portfolio target: {targetDescription}{selectionSuffix}";
                // A NO_PRICE skip means "no orders" was not really "nothing
                // needed", so raise it to Error() (LEAN has no warning level).
                if (anyNoPriceSkip) Error(noOrdersLine); else Log(noOrdersLine);
                return;
            }

            // Bucket tickets by what the broker has told us so far.
            var accepted = tickets.Where(t =>
                t.Status == OrderStatus.Submitted ||
                t.Status == OrderStatus.PartiallyFilled ||
                t.Status == OrderStatus.Filled).ToList();
            var rejected = tickets.Where(t =>
                t.Status == OrderStatus.Invalid ||
                t.Status == OrderStatus.Canceled).ToList();
            var unconfirmed = tickets.Except(accepted).Except(rejected).ToList();
            var filled = accepted.Count(t => t.Status == OrderStatus.Filled);

            if (rejected.Count == 0 && unconfirmed.Count == 0)
            {
                // Every order was acknowledged by the broker. Still be explicit
                // that acceptance != fill.
                var completeLine = $"[Rebalance] Complete — {accepted.Count}/{tickets.Count} orders accepted by broker " +
                    $"({filled} filled so far; watch [OrderEvent] lines for remaining fills). " +
                    $"Portfolio target: {targetDescription}{selectionSuffix}";
                // Same line either way; Error() when a selected symbol could
                // not be sized for lack of a price (LEAN has no warning level).
                if (anyNoPriceSkip) Error(completeLine); else Log(completeLine);
                return;
            }

            // At least one order was rejected or never acknowledged: do NOT
            // claim success. Name each problem order so it can be matched to
            // its [OrderEvent] line.
            Error($"[Rebalance] NOT COMPLETE — accepted={accepted.Count}, rejected/canceled={rejected.Count}, " +
                  $"unconfirmed={unconfirmed.Count} of {tickets.Count} orders. Intended target: {targetDescription}{selectionSuffix}");
            foreach (var t in rejected.Concat(unconfirmed))
            {
                Error($"[Rebalance]   Order {t.OrderId} {t.Symbol.Value} qty={t.Quantity} status={t.Status}");
            }
        }

        // Increments the count for one skip reason (logging-only bookkeeping).
        private static void CountSkip(Dictionary<string, int> skipCounts, string reason)
        {
            skipCounts[reason] = skipCounts.GetValueOrDefault(reason, 0) + 1;
        }

        // Builds the selection summary appended to the "[Rebalance] Complete"
        // line, e.g. "5 selected: 2 orders submitted, 3 skipped (2 NO_PRICE,
        // 1 BELOW_MIN_ADJUSTMENT)". Liquidations of holdings that were not
        // selected are not counted here; they appear in the accepted/total
        // ticket counts and in their own [Liquidate] lines.
        private static string FormatSelectionSummary(int selectedCount, int ordersSubmitted, Dictionary<string, int> skipCounts)
        {
            // Total number of selected symbols that got no order.
            var skippedTotal = skipCounts.Values.Sum();
            var summary = $"{selectedCount} selected: {ordersSubmitted} orders submitted, {skippedTotal} skipped";
            if (skippedTotal > 0)
            {
                // Per-reason counts in a fixed order, omitting reasons with zero.
                var parts = SkipReasonDisplayOrder
                    .Where(r => skipCounts.GetValueOrDefault(r, 0) > 0)
                    .Select(r => $"{skipCounts[r]} {r}");
                summary += $" ({string.Join(", ", parts)})";
            }
            return summary;
        }

        // Describes the timestamp of a security's last data point for log
        // lines, e.g. "lastData=2026-10-02 16:00" or
        // "lastData=2026-10-01 16:00 STALE". Logging only.
        //
        // Source: Security.GetLastData() returns Cache.GetData(), the last
        // BaseData received for the security (null if none) -- a plain field
        // read, confirmed in the LEAN source at the commit pinned in
        // setup/06_lean_build.sh (Common/Securities/Security.cs and
        // Common/Securities/SecurityCache.cs).
        //
        // STALE rule: the last data point's session date (BaseData.Time.Date,
        // the bar START, so a daily bar maps to its own trading day whether
        // its EndTime is 16:00 the same day or midnight the next day) is
        // earlier than the previous trading day on the security's exchange
        // calendar (Exchange.Hours.GetPreviousTradingDay, same LEAN source).
        // At 10:00 ET on a Monday, Friday's bar is current; Thursday's is STALE.
        //
        // Never throws: any failure returns "lastData=unavailable" so this
        // logging can never abort a rebalance or stop-loss check.
        private string DescribeLastData(Symbol sym)
        {
            try
            {
                if (!Securities.ContainsKey(sym)) return "lastData=n/a";
                var lastData = Securities[sym].GetLastData();
                // Nothing received since the algorithm started.
                if (lastData == null) return "lastData=none STALE";
                var previousTradingDay = Securities[sym].Exchange.Hours.GetPreviousTradingDay(Time.Date);
                var isStale = lastData.Time.Date < previousTradingDay.Date;
                return $"lastData={lastData.EndTime:yyyy-MM-dd HH:mm}" + (isStale ? " STALE" : "");
            }
            catch (Exception)
            {
                return "lastData=unavailable";
            }
        }

        // =====================================================================
        // ██  SENTIMENT-ADJUSTED POSITION SIZING  ────────────────────────────
        // =====================================================================

        /// <summary>
        /// Computes each top-N ticker's sentiment-adjusted weight, for use
        /// ONLY within the risk-on top-N allocation branch of Rebalance() --
        /// never called from the defensive (100% AGG) path.
        ///
        /// THE MOST IMPORTANT PROPERTY OF THIS METHOD: it can never throw,
        /// block, or meaningfully delay a rebalance. Every step below --
        /// reading the active mode, reading signals.jsonl per ticker,
        /// aggregating, and sizing -- is already individually fail-safe
        /// (AggregatorConfigReader/SignalsFileReader never throw by
        /// contract, and PositionSizer is pure in-memory math), but this
        /// method wraps the entire sequence in one more try/catch anyway, so
        /// that even an unanticipated bug in any of those components still
        /// can't do worse than fall back to exact equal weight -- identical
        /// to this strategy's pre-session behavior.
        /// </summary>
        //
        // baseWeight is 1/k for the k selected symbols that have a price
        // (fractional sizing, owner decision: an unpriced pick's weight is
        // redistributed). PositionSizer keeps the total at k x baseWeight, so
        // passing only the priced symbols with baseWeight = 1/k keeps the
        // weights summing to 100% across them. With all 5 picks priced this
        // is exactly the previous PositionWeight (20%).
        private Dictionary<string, decimal> ComputeSentimentAdjustedWeights(List<string> topTickers, decimal baseWeight)
        {
            // Computed up front and used as both the immediate return value
            // on any failure AND the base weight fed into PositionSizer --
            // equal weight across the symbols passed in.
            var fallback = topTickers.ToDictionary(t => t, t => baseWeight);

            try
            {
                var mode = _aggregatorConfigReader.ReadActiveMode();
                var nowUtc = DateTime.UtcNow;

                var signalsByTicker = new Dictionary<string, AggregatedSignal>();
                foreach (var ticker in topTickers)
                {
                    var recentSignals = _signalsFileReader.ReadRecentSignals(ticker, nowUtc);
                    signalsByTicker[ticker] = _signalAggregator.Aggregate(ticker, recentSignals, mode);
                }

                var adjustedWeights = PositionSizer.ComputeAdjustedWeights(
                    topTickers, (double)baseWeight, signalsByTicker, mode);

                var result = new Dictionary<string, decimal>();
                foreach (var ticker in topTickers)
                {
                    result[ticker] = (decimal)adjustedWeights[ticker];
                }

                // Per-rebalance summary log: mode, each ticker's base vs.
                // adjusted weight, and how many signals contributed --
                // reviewable after the fact without digging through raw
                // signal data, per this session's explicit requirement.
                Log($"[SignalAggregator] Active mode: {mode}");
                foreach (var ticker in topTickers)
                {
                    var agg = signalsByTicker[ticker];
                    Log($"[SignalAggregator] {ticker}: base={baseWeight:P1} adjusted={result[ticker]:P1} " +
                        $"signals={agg.ContributingSignalCount} score={agg.CombinedScore:F2} confidence={agg.CombinedConfidence:F2}");
                }

                return result;
            }
            catch (Exception ex)
            {
                // Any failure anywhere above -- config read, signals read,
                // aggregation, or sizing -- falls back to exact equal
                // weight. Logged as an error (not just a warning) since
                // reaching this catch means one of the individually
                // fail-safe components above didn't behave as designed and
                // is worth investigating, even though the rebalance itself
                // proceeds safely regardless.
                Error($"[SignalAggregator] Failed to compute sentiment-adjusted weights — " +
                      $"falling back to equal weight for all top-N positions. {ex.Message}");
                return fallback;
            }
        }

        // =====================================================================
        // ██  STOP-LOSS CHECK  ────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Iterates all held positions and exits any whose current price has
        /// fallen more than StopLossThreshold below the recorded entry price.
        /// Called daily from OnData().
        /// </summary>
        private void CheckStopLosses()
        {
            // Collect symbols to liquidate (avoid modifying collection while iterating).
            var toStop = new List<Symbol>();

            foreach (var holding in Portfolio.Values)
            {
                // Only consider positions with a meaningful quantity.
                if (!holding.Invested) continue;

                var sym = holding.Symbol;

                // We need a recorded entry price to evaluate the stop condition.
                if (!_entryPrices.TryGetValue(sym, out var entryPrice)) continue;
                if (entryPrice <= 0m) continue;

                var currentPrice = Securities[sym].Price;
                if (currentPrice <= 0m)
                {
                    // Previously silent: a held position with no price
                    // cannot be checked against its stop. Still skipped
                    // (unchanged behavior), but now reported -- at most once
                    // per symbol per trading day, because this method runs
                    // on every OnData() slice.
                    if (!_stopLossNoPriceWarnedOn.TryGetValue(sym, out var warnedOn) || warnedOn != Time.Date)
                    {
                        _stopLossNoPriceWarnedOn[sym] = Time.Date;
                        Error($"[StopLoss] SKIP {sym.Value} {SkipNoPrice} — held qty={holding.Quantity}, " +
                              $"entry=${entryPrice:F2}, price=${currentPrice:F2} {DescribeLastData(sym)}; " +
                              $"stop-loss not evaluated (logged at most once per symbol per trading day)");
                    }
                    continue;
                }

                // Stop-loss threshold price: entry × (1 − StopLossThreshold)
                var stopPrice = entryPrice * (1m - StopLossThreshold);

                if (currentPrice <= stopPrice)
                {
                    var dropPct = (entryPrice - currentPrice) / entryPrice;
                    Log($"[StopLoss] {sym.Value} triggered: entry=${entryPrice:F2}, " +
                        $"current=${currentPrice:F2}, drop={dropPct:P2} (threshold={StopLossThreshold:P0})");
                    toStop.Add(sym);
                }
            }

            // Liquidate stopped positions via explicit market sell orders so
            // that Day TimeInForce is applied consistently (same reasoning as
            // the SetHoldings → MarketOrder change above).
            foreach (var sym in toStop)
            {
                // Full decimal holding, rounded DOWN to the lot size so the
                // order is never larger than what is held (LEAN would round it
                // the same way in RoundOffOrder). Anything below one lot is
                // left as dust and reported.
                var heldQty = Portfolio[sym].Quantity;
                var sellQty = FloorHeldToLot(sym, heldQty);
                if (sellQty != 0)
                {
                    var stopTicket = PlaceSizedMarketOrder(sym, -sellQty, Securities[sym].Price, false);
                    // Order type is logged because LEAN converts a MarketOrder
                    // placed while the exchange is closed into MarketOnOpen
                    // (Alpaca opg), which Alpaca may reject for fractional
                    // quantities (not confirmed).
                    Log($"[StopLoss] Order {sym.Value} qty={-sellQty:0.####} held={heldQty:0.#########} " +
                        $"type={stopTicket.OrderType} status={stopTicket.Status}");
                }
                if (heldQty - sellQty != 0)
                {
                    Log($"[StopLoss] DUST {sym.Value} remaining={heldQty - sellQty:0.#########} below lot size " +
                        $"{LotSizeFor(sym)}; LEAN cannot submit it");
                }
                _entryPrices.Remove(sym);
                Log($"[StopLoss] Liquidated {sym.Value}.");
            }
        }

        // =====================================================================
        // ██  DRAWDOWN HALT  ──────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Checks whether the current portfolio drawdown from peak exceeds
        /// MaxDrawdownHaltThreshold. If so, liquidates all positions, moves to
        /// 100% AGG, and records the halt start time.
        /// </summary>
        private void CheckDrawdownHalt()
        {
            if (_peakPortfolioValue <= 0) return;

            var currentValue = Portfolio.TotalPortfolioValue;
            var drawdown     = (_peakPortfolioValue - currentValue) / _peakPortfolioValue;

            if (drawdown >= MaxDrawdownHaltThreshold)
            {
                Log($"[DrawdownHalt] TRIGGERED — drawdown={drawdown:P2} " +
                    $"(peak=${_peakPortfolioValue:F2}, current=${currentValue:F2})");
                Log($"[DrawdownHalt] Liquidating all positions, moving to {DefensiveTicker}. " +
                    $"Cooloff: {HaltCooloffMonths} months.");

                // Enter halt mode.
                _haltActive    = true;
                _haltStartDate = Time;

                // Liquidate everything and go to 100% AGG.
                // Use an explicit MarketOrder instead of SetHoldings() — same
                // reason as in Rebalance(): Day market orders work at any time
                // of day, while SetHoldings() under AlpacaBrokerageModel would
                // emit a MarketOnOpen that is rejected outside 07:00–09:28.
                // Fresh fill-wait budget for this halt (see ORDER FILL WAITS).
                _fillWaitSpent = new System.Diagnostics.Stopwatch();
                var haltLiquidationTickets = LiquidateAllExcept(DefensiveTicker);
                // Wait for the sells (up to OrderFillWaitSeconds each) before
                // the AGG buy is sized; also run when AGG has no price so the
                // sell results are logged.
                var haltUnsoldTickets = WaitForTickets(haltLiquidationTickets, "[DrawdownHalt]", "liquidation", "sell");
                var defSym2   = _symbols[DefensiveTicker];
                var defPrice2 = Securities[defSym2].Price;
                if (defPrice2 > 0)
                {
                    // Same fractional sizing, 1% reserve, sells-first order and
                    // cash cap as the rebalance, with AGG at 100% weight.
                    // Logged with the [DrawdownHalt] prefix; each order line
                    // includes the LEAN order type (MarketOnOpen if the
                    // exchange was closed when this ran).
                    var haltTargets = new List<AllocationTarget>
                    {
                        new AllocationTarget(DefensiveTicker, defSym2, 1.0m)
                    };
                    var haltResult = ExecuteAllocation(haltTargets, haltUnsoldTickets,
                        AllocationContext.DrawdownHalt, 1.0m);
                    var haltCash = Portfolio.Cash;
                    var haltTpv  = Portfolio.TotalPortfolioValue;
                    Log($"[DrawdownHalt] {DefensiveTicker} allocation: {haltResult.OrdersSubmitted} order(s) placed, " +
                        $"{haltResult.SkipCounts.Values.Sum()} skipped | Cash after: ${haltCash:F2} " +
                        $"({(haltTpv > 0m ? haltCash / haltTpv : 0m):P1} of portfolio) | " +
                        $"fill waits {_fillWaitSpent.Elapsed.TotalSeconds:F1}s of {RebalanceFillWaitBudgetSeconds}s");
                }
                else
                {
                    // Previously silent: AGG had no usable price, so no AGG
                    // order was sized. The liquidations above still went out,
                    // so the halt leaves the account in cash, not AGG. This
                    // runs once per halt (it sets _haltActive), so no rate limit.
                    Error($"[DrawdownHalt] SKIP {DefensiveTicker} {SkipNoPrice} — price=${defPrice2:F2} " +
                          $"{DescribeLastData(defSym2)}; no {DefensiveTicker} order submitted, " +
                          "liquidation proceeds will remain in cash");
                }
                _entryPrices.Clear();

                Log($"[DrawdownHalt] Halt mode entered on {_haltStartDate:yyyy-MM-dd}. " +
                    $"Will auto-resume after {_haltStartDate.AddMonths(HaltCooloffMonths):yyyy-MM-dd}.");
            }
        }

        /// <summary>
        /// While in halt mode, checks each day whether the cooloff period has
        /// elapsed. If so, clears halt mode and allows the next monthly rebalance
        /// to resume normal operations.
        /// </summary>
        private void TryExitHaltMode()
        {
            var resumeDate = _haltStartDate.AddMonths(HaltCooloffMonths);

            if (Time >= resumeDate)
            {
                Log($"[DrawdownHalt] Cooloff period expired on {Time:yyyy-MM-dd}. " +
                    $"Resuming normal operation.");
                _haltActive = false;
                // Force a rebalance on the next bar by resetting the month tracker.
                _lastRebalanceMonth = -1;
            }
            else
            {
                // Log remaining cooloff time once per month to avoid log spam.
                if (Time.Day == 1)
                {
                    var remaining = resumeDate - Time;
                    Log($"[DrawdownHalt] Still in halt mode. " +
                        $"Resume date: {resumeDate:yyyy-MM-dd} " +
                        $"(~{remaining.Days} days remaining). Holding 100% {DefensiveTicker}.");
                }
            }
        }

        // =====================================================================
        // ██  HELPERS  ────────────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Calculates the simple price return for <paramref name="ticker"/> over
        /// the most recent <paramref name="months"/> calendar months using LEAN's
        /// History() API.
        ///
        /// Returns null if there is insufficient history.
        /// </summary>
        /// <param name="ticker">Ticker symbol string (e.g., "SPY").</param>
        /// <param name="months">Number of calendar months for the lookback.</param>
        /// <returns>Decimal return (e.g., 0.12 = +12%) or null.</returns>
        private decimal? GetMomentumReturn(string ticker, int months)
        {
            if (!_symbols.TryGetValue(ticker, out var sym))
            {
                Log($"[History] Ticker {ticker} not found in symbol dictionary.");
                return null;
            }

            // Request slightly more than months*21 trading days to ensure we span
            // the full calendar period even across holidays / weekends.
            // Trading days ≈ 21 per month; we add a 10-day buffer.
            int tradingDayEstimate = (months * 21) + 10;

            // History() returns bars newest-last (ascending date order).
            var history = History<TradeBar>(sym, tradingDayEstimate, Resolution.Daily).ToList();

            if (history.Count < 2)
            {
                // Not enough bars to compute a return.
                return null;
            }

            // We want the bar that is closest to exactly `months` ago.
            // Calculate the target date and find the nearest bar in history.
            var targetDate = Time.AddMonths(-months).Date;

            // Find the bar whose EndTime is closest to (but not after) targetDate.
            TradeBar? startBar = null;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].EndTime.Date <= targetDate)
                {
                    startBar = history[i];
                    break;
                }
            }

            if (startBar == null || startBar.Close <= 0)
            {
                // No bar found far enough back in history.
                return null;
            }

            // Most recent bar is the last element (ascending order).
            var endBar = history[history.Count - 1];
            if (endBar.Close <= 0) return null;

            // Simple price return: (endPrice / startPrice) - 1
            return (endBar.Close / startBar.Close) - 1m;
        }

        /// <summary>
        /// Liquidates all currently-held positions EXCEPT those whose ticker
        /// is included in <paramref name="keepTickers"/>.
        /// Clears the corresponding entry-price records.
        /// Returns the OrderTickets for the sell orders it submitted so that
        /// Rebalance() can include them in its accepted/rejected summary
        /// (callers that don't need them, e.g. CheckDrawdownHalt(), simply
        /// ignore the return value).
        /// </summary>
        /// <param name="keepTickers">Tickers to retain. Pass empty to liquidate everything.</param>
        /// <returns>One OrderTicket per liquidation order submitted.</returns>
        private List<OrderTicket> LiquidateAllExcept(IEnumerable<string> keepTickers)
        {
            var keepSet = new HashSet<string>(keepTickers, StringComparer.OrdinalIgnoreCase);
            // Collects the ticket for every liquidation order submitted below.
            var tickets = new List<OrderTicket>();

            foreach (var holding in Portfolio.Values)
            {
                if (!holding.Invested) continue;

                var ticker = holding.Symbol.Value;
                if (!keepSet.Contains(ticker))
                {
                    Log($"[Liquidate] Exiting {ticker} (not in new target set).");
                    // Use MarketOrder instead of Liquidate() for consistent Day
                    // TimeInForce behaviour under AlpacaBrokerageModel.
                    // Full decimal holding, never skipped for being small (an
                    // order worth under $1 is still attempted; if Alpaca
                    // rejects it, OnOrderEvent logs [OrderError] and it is not
                    // retried). Rounded DOWN to the lot size, the same way
                    // LEAN's RoundOffOrder would, so it never exceeds the holding.
                    var holdQty = Portfolio[holding.Symbol].Quantity;
                    var sellQty = FloorHeldToLot(holding.Symbol, holdQty);
                    if (sellQty != 0)
                    {
                        // Submitted asynchronously; the caller waits for all
                        // liquidations together with WaitForTickets().
                        var liquidationTicket = PlaceSizedMarketOrder(holding.Symbol, -sellQty, Securities[holding.Symbol].Price, true);
                        tickets.Add(liquidationTicket);
                        Log($"[Liquidate] Order {ticker} qty={-sellQty:0.####} held={holdQty:0.#########} " +
                            $"notional=${sellQty * Securities[holding.Symbol].Price:F2} " +
                            $"type={liquidationTicket.OrderType} status={liquidationTicket.Status}");
                    }
                    if (holdQty - sellQty != 0)
                    {
                        // Below one lot: LEAN rejects any order smaller than
                        // the lot size before it reaches Alpaca, so this
                        // remainder cannot be sold through LEAN.
                        Log($"[Liquidate] DUST {ticker} remaining={holdQty - sellQty:0.#########} below lot size " +
                            $"{LotSizeFor(holding.Symbol)}; LEAN cannot submit it");
                    }
                    _entryPrices.Remove(holding.Symbol);
                }
            }

            return tickets;
        }

        /// <summary>
        /// Overload that accepts a single ticker string (convenience wrapper for
        /// the defensive-mode path where we only want to keep one asset).
        /// </summary>
        private List<OrderTicket> LiquidateAllExcept(string keepTicker)
            => LiquidateAllExcept(new[] { keepTicker });

        // =====================================================================
        // ██  ORDER EVENTS  ───────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Called by LEAN whenever an order status changes.
        /// Always: logs one "[OrderEvent]" line for EVERY event (Submitted,
        ///         Filled, PartiallyFilled, Canceled, Invalid, and any other
        ///         status) with symbol, quantities, fill price and the
        ///         brokerage message -- added after the 2026-10-01 incident,
        ///         where orders expired at Alpaca with nothing in our log.
        /// On fill: records the actual fill price as the entry price for
        ///          stop-loss tracking (more accurate than the pre-order estimate).
        /// </summary>
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            // ── Universal order-event log line ─────────────────────────────────
            // OrderQty  = the order's total requested quantity (signed).
            // FillQty   = quantity filled by THIS event (0 for non-fill events).
            // FillPrice = price of THIS event's fill (0 for non-fill events).
            // Message   = brokerage/LEAN message (rejection reason etc.);
            //             shown as "(none)" when empty so the field is never blank.
            var brokerMessage = string.IsNullOrWhiteSpace(orderEvent.Message) ? "(none)" : orderEvent.Message;
            Log($"[OrderEvent] {orderEvent.Symbol.Value} | Id: {orderEvent.OrderId} | " +
                $"Status: {orderEvent.Status} | Dir: {orderEvent.Direction} | " +
                $"OrderQty: {orderEvent.Quantity} | FillQty: {orderEvent.FillQuantity} | " +
                $"FillPrice: ${orderEvent.FillPrice:F2} | Message: {brokerMessage}");

            if (orderEvent.Status == OrderStatus.Filled ||
                orderEvent.Status == OrderStatus.PartiallyFilled)
            {
                var sym = orderEvent.Symbol;
                Log($"[OrderFill] {sym.Value} | Dir: {orderEvent.Direction} | " +
                    $"Qty: {orderEvent.FillQuantity:F4} | " +
                    $"Price: ${orderEvent.FillPrice:F2} | " +
                    $"Status: {orderEvent.Status}");

                // Fill price versus the price the order was sized with
                // (possibly a stale previous close). The 1% cash reserve is
                // the cushion for this gap; this line shows how big it was.
                decimal sizingPrice;
                bool hasSizingPrice;
                lock (_sizingPriceLock)
                {
                    hasSizingPrice = _sizingPriceBySymbol.TryGetValue(sym, out sizingPrice);
                    if (hasSizingPrice && orderEvent.Status == OrderStatus.Filled)
                    {
                        _sizingPriceBySymbol.Remove(sym);
                    }
                }
                if (hasSizingPrice && sizingPrice > 0m)
                {
                    var slippage = (orderEvent.FillPrice - sizingPrice) / sizingPrice;
                    Log($"[Slippage] {sym.Value} | Dir: {orderEvent.Direction} | fill=${orderEvent.FillPrice:F2} " +
                        $"sizing=${sizingPrice:F2} diff={slippage:P2} | FillQty: {orderEvent.FillQuantity:0.#########}");
                }

                // For buys/additions, update entry price to the latest fill price.
                // For sells, remove the entry price record only once the
                // position is closed; a partial sell (for example a rebalance
                // sell adjustment) keeps the position and its stop-loss anchor.
                if (orderEvent.Direction == OrderDirection.Buy)
                {
                    // Use the fill price as the stop-loss anchor.
                    _entryPrices[sym] = orderEvent.FillPrice;
                    Log($"[EntryPrice] {sym.Value} entry set to ${orderEvent.FillPrice:F2}");
                }
                else if (orderEvent.Direction == OrderDirection.Sell)
                {
                    // LEAN applies the fill to the portfolio before it calls
                    // OnOrderEvent (BrokerageTransactionHandler: ProcessFills
                    // runs before the algorithm's OnOrderEvent in the pinned
                    // LEAN source), so this is the quantity after the fill.
                    // Lot-size aware: anything below one lot is dust that LEAN
                    // cannot trade, so the position counts as closed.
                    var remainingQty = Securities.ContainsKey(sym) ? Portfolio[sym].Quantity : 0m;
                    if (Math.Abs(remainingQty) < LotSizeFor(sym))
                    {
                        _entryPrices.Remove(sym);
                        Log($"[EntryPrice] {sym.Value} position closed (remaining {remainingQty:0.#########}); entry price removed");
                    }
                    else
                    {
                        Log($"[EntryPrice] {sym.Value} partial sell; {remainingQty:0.#########} still held, " +
                            $"entry kept at ${_entryPrices.GetValueOrDefault(sym, 0):F2}");
                    }
                }
            }
            else if (orderEvent.Status == OrderStatus.Invalid)
            {
                Error($"[OrderError] {orderEvent.Symbol.Value} — " +
                      $"INVALID order: {orderEvent.Message}");
            }
            else if (orderEvent.Status == OrderStatus.Canceled)
            {
                Log($"[OrderCancel] {orderEvent.Symbol.Value} order canceled: {orderEvent.Message}");
            }
        }
    }
}
