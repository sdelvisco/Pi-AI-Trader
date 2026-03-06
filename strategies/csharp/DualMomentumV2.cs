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
//   - All orders are market orders.
//
// To compile:
//   dotnet build strategies/csharp/DualMomentumV2.csproj -c Release
//
// LEAN algorithm documentation:
//   https://www.lean.io/docs/v2/writing-algorithms
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Brokerages;
using QuantConnect.Orders;

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
        ///   4. Triggering monthly rebalance on the first trading day of the month.
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

            // ── 4. Monthly rebalance — first trading day of each calendar month ─
            // IsMarketOpen() gates ensure we only act on liquid trading sessions.
            // We compare the current month to the last-rebalanced month to avoid
            // triggering more than once per month.
            if (Time.Month != _lastRebalanceMonth)
            {
                _lastRebalanceMonth = Time.Month;
                Log($"[Rebalance] Triggered on {Time:yyyy-MM-dd} (first trading day of {Time:MMMM yyyy})");
                Rebalance();
            }
        }

        // =====================================================================
        // ██  REBALANCE  ──────────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Core monthly rebalance logic.
        /// Steps:
        ///   A. Run the absolute momentum filter (SPY vs AGG over 12 months).
        ///      → If risk-off: move 100% to AGG.
        ///   B. If risk-on: rank universe by 6-month return, pick top-N symbols.
        ///   C. Liquidate positions not in the new top-N.
        ///   D. Allocate PositionWeight to each of the top-N symbols.
        /// </summary>
        private void Rebalance()
        {
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
                LiquidateAllExcept(DefensiveTicker);
                SetHoldings(_symbols[DefensiveTicker], 1.0m);
                Log($"[Defensive] Target: 100% {DefensiveTicker}");
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
            LiquidateAllExcept(topTickers);

            // ------------------------------------------------------------------
            // D. ALLOCATE equal weight to each top-N symbol
            // ------------------------------------------------------------------
            foreach (var ticker in topTickers)
            {
                var sym = _symbols[ticker];
                SetHoldings(sym, PositionWeight);
                // Record the current price as the "entry price" for stop-loss tracking.
                // (Will be refined by OnOrderEvent fill price — this is a best-effort
                //  initialisation in case OnOrderEvent is delayed.)
                if (Securities.ContainsKey(sym) && Securities[sym].Price > 0)
                {
                    _entryPrices[sym] = Securities[sym].Price;
                }
                Log($"[Allocate] {ticker} → {PositionWeight:P0} (entry ~${_entryPrices.GetValueOrDefault(sym, 0):F2})");
            }

            Log($"[Rebalance] Complete. Portfolio target: {string.Join(", ", topTickers.Select(t => $"{t}@{PositionWeight:P0}"))}");
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
                if (currentPrice <= 0m) continue;

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

            // Liquidate stopped positions.
            foreach (var sym in toStop)
            {
                Liquidate(sym);
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
                LiquidateAllExcept(DefensiveTicker);
                SetHoldings(_symbols[DefensiveTicker], 1.0m);
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
        /// </summary>
        /// <param name="keepTickers">Tickers to retain. Pass empty to liquidate everything.</param>
        private void LiquidateAllExcept(IEnumerable<string> keepTickers)
        {
            var keepSet = new HashSet<string>(keepTickers, StringComparer.OrdinalIgnoreCase);

            foreach (var holding in Portfolio.Values)
            {
                if (!holding.Invested) continue;

                var ticker = holding.Symbol.Value;
                if (!keepSet.Contains(ticker))
                {
                    Log($"[Liquidate] Exiting {ticker} (not in new target set).");
                    Liquidate(holding.Symbol);
                    _entryPrices.Remove(holding.Symbol);
                }
            }
        }

        /// <summary>
        /// Overload that accepts a single ticker string (convenience wrapper for
        /// the defensive-mode path where we only want to keep one asset).
        /// </summary>
        private void LiquidateAllExcept(string keepTicker)
            => LiquidateAllExcept(new[] { keepTicker });

        // =====================================================================
        // ██  ORDER EVENTS  ───────────────────────────────────────────────────
        // =====================================================================

        /// <summary>
        /// Called by LEAN whenever an order status changes.
        /// On fill: records the actual fill price as the entry price for
        ///          stop-loss tracking (more accurate than the pre-order estimate).
        /// </summary>
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
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
