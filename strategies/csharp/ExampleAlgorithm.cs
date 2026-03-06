// =============================================================================
// ExampleAlgorithm.cs — Minimal LEAN C# Algorithm Template
// =============================================================================
// A minimal but complete C# algorithm for QuantConnect LEAN Engine.
// Use this as a starting point for your own strategies.
//
// To run a backtest:
//   cd lean && lean backtest --lean-config lean.json ../strategies/csharp
//
// To run live:
//   Ensure lean.json is configured for Alpaca and paper trading is enabled,
//   then start the lean-trader systemd service.
//
// LEAN algorithm documentation:
//   https://www.lean.io/docs/v2/writing-algorithms
// =============================================================================

using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Orders;
using System;

namespace PiAiTrader.Strategies
{
    /// <summary>
    /// ExampleAlgorithm is a simple buy-and-hold demonstration strategy.
    ///
    /// It buys SPY on the first day and holds it indefinitely, logging
    /// portfolio state on each data bar. This is intentionally minimal —
    /// replace the logic in OnData() with your actual strategy.
    /// </summary>
    public class ExampleAlgorithm : QCAlgorithm
    {
        // -----------------------------------------------------------------------
        // Configuration constants
        // -----------------------------------------------------------------------

        // Symbol to trade.
        private const string TickerSymbol = "SPY";

        // Fraction of portfolio to allocate to this position.
        // 0.95 = 95% of available capital (leaves 5% as cash buffer for fees).
        private const decimal AllocationFraction = 0.95m;

        // Internal tracking.
        private Symbol _symbol;
        private bool _invested = false;

        // -----------------------------------------------------------------------
        // Initialize — called once at algorithm start
        // -----------------------------------------------------------------------

        /// <summary>
        /// Set algorithm parameters, universe, and data subscriptions.
        /// This method is called once by LEAN before the first data event.
        /// </summary>
        public override void Initialize()
        {
            // ---- Backtest date range ----------------------------------------
            // For live trading these dates are ignored.
            SetStartDate(2020, 1, 1);
            SetEndDate(DateTime.Now);

            // ---- Starting cash (backtests only) --------------------------------
            // For live trading the brokerage account balance is used instead.
            SetCash(100_000);

            // ---- Brokerage model -------------------------------------------
            // AlpacaBrokerageModel configures LEAN with Alpaca's order types,
            // fill models, and fee schedules.
            SetBrokerageModel(BrokerageName.Alpaca, AccountType.Margin);

            // ---- Data subscription -----------------------------------------
            // Subscribe to daily SPY bars. For intraday strategies, change
            // Resolution.Daily to Resolution.Minute or Resolution.Hour.
            _symbol = AddEquity(TickerSymbol, Resolution.Daily).Symbol;

            Log($"ExampleAlgorithm initialised. Trading: {TickerSymbol}");
        }

        // -----------------------------------------------------------------------
        // OnData — called on every new data bar
        // -----------------------------------------------------------------------

        /// <summary>
        /// Called by LEAN each time a new data bar arrives for any subscribed symbol.
        /// This is where your strategy logic lives.
        /// </summary>
        /// <param name="data">The current slice of market data for all symbols.</param>
        public override void OnData(Slice data)
        {
            // Ignore bars where we don't have data for our symbol.
            if (!data.Bars.ContainsKey(_symbol)) return;

            var bar = data.Bars[_symbol];

            // --- Entry logic ---------------------------------------------------
            // Simple example: buy if not yet invested.
            // Replace this with your actual signal logic.
            if (!_invested)
            {
                SetHoldings(_symbol, AllocationFraction);
                _invested = true;
                Log($"Entered position: {TickerSymbol} at ${bar.Close:F2}");
            }

            // Log daily portfolio state.
            Log($"Date: {Time:yyyy-MM-dd} | {TickerSymbol}: ${bar.Close:F2} | " +
                $"Portfolio: ${Portfolio.TotalPortfolioValue:F0}");
        }

        // -----------------------------------------------------------------------
        // Order event handler
        // -----------------------------------------------------------------------

        /// <summary>
        /// Called by LEAN whenever an order status changes.
        /// Use this to track fills, cancellations, and errors.
        /// </summary>
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled)
            {
                Log($"Order filled: {orderEvent.Symbol} | " +
                    $"Direction: {orderEvent.Direction} | " +
                    $"Quantity: {orderEvent.FillQuantity} | " +
                    $"Price: ${orderEvent.FillPrice:F2}");
            }
            else if (orderEvent.Status == OrderStatus.Invalid ||
                     orderEvent.Status == OrderStatus.CanceledAndFilled)
            {
                Error($"Order problem: {orderEvent.Symbol} — {orderEvent.Message}");
            }
        }
    }
}
