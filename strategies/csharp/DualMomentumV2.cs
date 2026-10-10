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

        // Skip reason: weight x TotalPortfolioValue / Price was above 0 but
        // below 1 share, so the (long) cast truncated the target to 0 and,
        // with nothing currently held, the delta was 0.
        private const string SkipTargetTruncatedToZero = "TARGET_TRUNCATED_TO_ZERO";

        // Skip reason: the truncated target quantity equalled the currently
        // held quantity, so the delta was 0 and no order was needed.
        private const string SkipAlreadyAtTarget = "ALREADY_AT_TARGET";

        // Fixed display order for the per-reason counts in the
        // "[Rebalance] Complete" summary, so the line reads the same way
        // every month regardless of which reason happened first.
        private static readonly string[] SkipReasonDisplayOrder = new[]
        {
            SkipNoPrice, SkipNotInSecurities, SkipTargetTruncatedToZero, SkipAlreadyAtTarget
        };

        // Rate limiter for the stop-loss zero-price warning. CheckStopLosses()
        // runs on every OnData() slice (not strictly once per day), so without
        // this a held position with no price could log the same warning many
        // times. Maps symbol -> the algorithm date (Time.Date) on which its
        // warning was last logged; the warning is logged at most once per
        // symbol per trading day.
        private readonly Dictionary<Symbol, DateTime> _stopLossNoPriceWarnedOn = new Dictionary<Symbol, DateTime>();

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

            Log("=== DualMomentumV2 Initialized ===");
            Log($"Universe size     : {UniverseTickers.Length} symbols");
            Log($"Top positions     : {TopPositions} @ {PositionWeight:P0} each");
            Log($"Relative lookback : {RelativeMomentumMonths} months");
            Log($"Absolute lookback : {AbsoluteMomentumMonths} months");
            Log($"Max drawdown halt : {MaxDrawdownHaltThreshold:P0}");
            Log($"Halt cooloff      : {HaltCooloffMonths} months");
            Log($"Stop-loss         : {StopLossThreshold:P0} per position");
            Log($"Defensive asset   : {DefensiveTicker}");

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
        ///   C. Liquidate positions not in the new top-N.
        ///   D. Allocate PositionWeight to each of the top-N symbols.
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
                tickets.AddRange(LiquidateAllExcept(DefensiveTicker));
                // Use an explicit MarketOrder instead of SetHoldings() so that
                // the Day TimeInForce on DefaultOrderProperties is respected
                // and the order is not downgraded to a MarketOnOpen by the
                // Alpaca brokerage model.
                var defSym   = _symbols[DefensiveTicker];
                var defPrice = Securities[defSym].Price;
                // Logging-only bookkeeping: AGG is the single "selected"
                // symbol on this path, so it gets the same skip reporting as
                // the top-N symbols on the risk-on path.
                var defSkipCounts      = new Dictionary<string, int>();
                var defOrdersSubmitted = 0;
                // Timestamp of AGG's last data point (and STALE flag), for the log lines below.
                var defLastData = DescribeLastData(defSym);
                if (defPrice > 0)
                {
                    // Read once so the logged value is exactly the one used for sizing.
                    var defTpv    = Portfolio.TotalPortfolioValue;
                    // Same math as before: 1.0 x TotalPortfolioValue / price.
                    // Kept as a separate variable only so the fractional value
                    // (before the (long) truncation) can be logged.
                    var defFractionalTarget = 1.0m * defTpv / defPrice;
                    var targetQty = (long)defFractionalTarget;
                    var defHeldQty = (long)Portfolio[defSym].Quantity;
                    var delta     = targetQty - defHeldQty;
                    // Immediate Day market order (Alpaca time_in_force=day).
                    if (delta != 0)
                    {
                        tickets.Add(MarketOrder(defSym, (decimal)delta));
                        defOrdersSubmitted++;
                    }
                    // Sizing line: price actually used, portfolio value,
                    // fractional and truncated target, held quantity, delta.
                    Log($"[Defensive] Sizing {DefensiveTicker}: price=${defPrice:F2} {defLastData} weight=100.0% " +
                        $"tpv=${defTpv:F2} target={defFractionalTarget:F4} targetQty={targetQty} " +
                        $"held={defHeldQty} delta={delta}");
                    // No order: explain why (benign reasons, Log level with a WARNING token).
                    if (delta == 0)
                    {
                        var reason = targetQty == 0 ? SkipTargetTruncatedToZero : SkipAlreadyAtTarget;
                        CountSkip(defSkipCounts, reason);
                        Log($"[Rebalance][SKIP] {DefensiveTicker} {reason} WARNING: no order " +
                            $"(target={defFractionalTarget:F4} targetQty={targetQty} held={defHeldQty})");
                    }
                }
                else
                {
                    // Previously silent: AGG had no usable price, so no AGG
                    // order could be sized. Liquidations above still went out,
                    // so the proceeds will sit in cash instead of AGG.
                    CountSkip(defSkipCounts, SkipNoPrice);
                    Error($"[Rebalance][SKIP] {DefensiveTicker} {SkipNoPrice} — price=${defPrice:F2} {defLastData}; " +
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
            // C. LIQUIDATE positions not in the new target set
            // ------------------------------------------------------------------
            tickets.AddRange(LiquidateAllExcept(topTickers));

            // ------------------------------------------------------------------
            // D. ALLOCATE to each top-N symbol, using sentiment-adjusted
            //    weights within the existing top-N branch only (see
            //    ComputeSentimentAdjustedWeights). This NEVER changes which
            //    tickers are selected (topTickers above is untouched) --
            //    only how much capital goes to each one already selected.
            // ------------------------------------------------------------------
            var tickerWeights = ComputeSentimentAdjustedWeights(topTickers);

            // Logging-only bookkeeping for the "[Rebalance] Complete" summary:
            // per-reason count of selected symbols that got no order, and
            // the number of selected symbols that did get an order.
            var skipCounts             = new Dictionary<string, int>();
            var selectedOrdersSubmitted = 0;

            foreach (var ticker in topTickers)
            {
                var sym = _symbols[ticker];
                var weight = tickerWeights.GetValueOrDefault(ticker, PositionWeight);
                // Sizing details appended to the [Allocate] line below; filled
                // in by whichever of the three branches below applies.
                var sizingDetail = "";
                // Set when this symbol gets no order; null means an order was submitted.
                string? skipReason = null;
                // Extra context for the [Rebalance][SKIP] line.
                var skipDetail = "";
                // Use an explicit MarketOrder instead of SetHoldings() so that
                // the Day TimeInForce on DefaultOrderProperties is respected.
                // Target quantity = weight × TotalPortfolioValue / Price.
                // Subtract the current held quantity to get only the incremental
                // order needed (mirrors what SetHoldings() does internally).
                if (Securities.ContainsKey(sym) && Securities[sym].Price > 0)
                {
                    // Read price and portfolio value once, so the values
                    // logged are exactly the ones used for sizing.
                    var price = Securities[sym].Price;
                    var tpv   = Portfolio.TotalPortfolioValue;
                    // Same math as before: weight x TotalPortfolioValue / price,
                    // truncated by the (long) cast. Kept as a separate variable
                    // only so the fractional value can be logged.
                    var fractionalTarget = weight * tpv / price;
                    var targetQty = (long)fractionalTarget;
                    var heldQty   = (long)Portfolio[sym].Quantity;
                    var delta     = targetQty - heldQty;
                    // Immediate Day market order (Alpaca time_in_force=day).
                    if (delta != 0)
                    {
                        tickets.Add(MarketOrder(sym, (decimal)delta));
                        selectedOrdersSubmitted++;
                    }
                    else
                    {
                        // No order. A zero target with nothing held means the
                        // target was under 1 share; otherwise holdings already
                        // match the truncated target.
                        skipReason = targetQty == 0 ? SkipTargetTruncatedToZero : SkipAlreadyAtTarget;
                        skipDetail = $"target={fractionalTarget:F4} targetQty={targetQty} held={heldQty} " +
                                     $"(weight={weight:P1} tpv=${tpv:F2} price=${price:F2})";
                    }
                    // Record the current price as the "entry price" for stop-loss tracking.
                    // (Will be refined by OnOrderEvent fill price — this is a best-effort
                    //  initialisation in case OnOrderEvent is delayed.)
                    _entryPrices[sym] = Securities[sym].Price;
                    sizingDetail = $"price=${price:F2} {DescribeLastData(sym)} weight={weight:P1} tpv=${tpv:F2} " +
                                   $"target={fractionalTarget:F4} targetQty={targetQty} held={heldQty} delta={delta}";
                }
                else if (!Securities.ContainsKey(sym))
                {
                    // Previously silent: symbol missing from Securities entirely.
                    skipReason   = SkipNotInSecurities;
                    // held is reported as n/a: Portfolio[sym] looks the symbol
                    // up in Securities and would throw here, aborting the rebalance.
                    sizingDetail = $"price=n/a lastData=n/a weight={weight:P1} target=n/a targetQty=n/a " +
                                   "held=n/a delta=n/a";
                    skipDetail   = "symbol is not in Securities, cannot size";
                }
                else
                {
                    // Previously silent: Price was 0 (no data received yet,
                    // or the price was otherwise unusable), so no target
                    // quantity could be computed. This is the 2026-10-05
                    // MSFT/AAPL case.
                    var zeroPriceLastData = DescribeLastData(sym);
                    skipReason   = SkipNoPrice;
                    sizingDetail = $"price=${Securities[sym].Price:F2} {zeroPriceLastData} weight={weight:P1} " +
                                   $"tpv=${Portfolio.TotalPortfolioValue:F2} target=n/a targetQty=n/a " +
                                   $"held={(long)Portfolio[sym].Quantity} delta=n/a";
                    skipDetail   = $"price=${Securities[sym].Price:F2} {zeroPriceLastData}, cannot size";
                }
                // Existing prefix kept byte-for-byte (log readers may depend on it);
                // the sizing fields are appended after " | sizing: ".
                Log($"[Allocate] {ticker} → {weight:P1} (base {PositionWeight:P0}, entry ~${_entryPrices.GetValueOrDefault(sym, 0):F2})" +
                    $" | sizing: {sizingDetail}");

                if (skipReason != null)
                {
                    CountSkip(skipCounts, skipReason);
                    if (skipReason == SkipNoPrice || skipReason == SkipNotInSecurities)
                    {
                        // Problem cases: the strategy wanted a position and could not size one.
                        Error($"[Rebalance][SKIP] {ticker} {skipReason} — {skipDetail}; no order submitted");
                    }
                    else
                    {
                        // Benign cases: sizing worked, the result was simply "no change".
                        Log($"[Rebalance][SKIP] {ticker} {skipReason} WARNING: no order — {skipDetail}");
                    }
                }
            }

            // Only claims "Complete" if every order was accepted by the broker.
            // The selection summary adds how many selected symbols were skipped and why.
            LogRebalanceOutcome(tickets,
                string.Join(", ", topTickers.Select(t => $"{t}@{tickerWeights.GetValueOrDefault(t, PositionWeight):P1}")),
                FormatSelectionSummary(topTickers.Count, selectedOrdersSubmitted, skipCounts),
                skipCounts.ContainsKey(SkipNoPrice));
        }

        /// <summary>
        /// Logs the outcome of a rebalance based on the actual state of the
        /// OrderTickets it produced, rather than unconditionally claiming
        /// success.
        ///
        /// In live mode LEAN's synchronous MarketOrder() blocks for up to
        /// Transactions.MarketOrderFillTimeout (5 s by default in upstream
        /// LEAN) waiting for the order to close, so by the time this runs each
        /// ticket's Status reflects the broker's response so far:
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
        // 1 TARGET_TRUNCATED_TO_ZERO)". Liquidations of holdings that were not
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
        private Dictionary<string, decimal> ComputeSentimentAdjustedWeights(List<string> topTickers)
        {
            // Computed up front and used as both the immediate return value
            // on any failure AND the base weight fed into PositionSizer --
            // this is exactly today's pre-session equal-weight allocation.
            var fallback = topTickers.ToDictionary(t => t, t => PositionWeight);

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
                    topTickers, (double)PositionWeight, signalsByTicker, mode);

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
                    Log($"[SignalAggregator] {ticker}: base={PositionWeight:P1} adjusted={result[ticker]:P1} " +
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
                var qty = Portfolio[sym].Quantity;
                if (qty != 0) MarketOrder(sym, -qty);
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
                LiquidateAllExcept(DefensiveTicker);
                var defSym2   = _symbols[DefensiveTicker];
                var defPrice2 = Securities[defSym2].Price;
                if (defPrice2 > 0)
                {
                    var targetQty2 = (long)(1.0m * Portfolio.TotalPortfolioValue / defPrice2);
                    var delta2     = targetQty2 - (long)Portfolio[defSym2].Quantity;
                    if (delta2 != 0) MarketOrder(defSym2, (decimal)delta2);
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
                    var holdQty = Portfolio[holding.Symbol].Quantity;
                    if (holdQty != 0) tickets.Add(MarketOrder(holding.Symbol, -holdQty));
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

                // For buys/additions, update entry price to the latest fill price.
                // For sells/liquidations, remove the entry price record.
                if (orderEvent.Direction == OrderDirection.Buy)
                {
                    // Use the fill price as the stop-loss anchor.
                    _entryPrices[sym] = orderEvent.FillPrice;
                    Log($"[EntryPrice] {sym.Value} entry set to ${orderEvent.FillPrice:F2}");
                }
                else if (orderEvent.Direction == OrderDirection.Sell)
                {
                    _entryPrices.Remove(sym);
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
