# =============================================================================
# example_algorithm.py — Minimal LEAN Python Algorithm Template
# =============================================================================
# A minimal but complete Python algorithm for QuantConnect LEAN Engine.
# Use this as a starting point for your own Python strategies.
#
# To run a backtest:
#   cd lean && lean backtest --lean-config lean.json ../strategies/python
#
# LEAN Python algorithm documentation:
#   https://www.lean.io/docs/v2/writing-algorithms/algorithm-framework
# =============================================================================

from AlgorithmImports import *


class ExampleAlgorithm(QCAlgorithm):
    """
    ExampleAlgorithm is a simple buy-and-hold demonstration strategy
    written in Python for the LEAN Engine.

    It subscribes to daily SPY data and enters a position on the first
    available bar. Replace the logic in on_data() with your own signals.

    LEAN calls methods on this class according to the algorithm lifecycle:
      initialize()        — called once at startup
      on_data(slice)      — called on each new data bar
      on_order_event(e)   — called when order status changes
    """

    # -------------------------------------------------------------------------
    # Configuration
    # -------------------------------------------------------------------------

    TICKER        = "SPY"
    ALLOCATION    = 0.95        # Fraction of portfolio to deploy (95%)
    START_CASH    = 100_000     # Starting cash for backtests

    def initialize(self) -> None:
        """
        Set up algorithm parameters, universe, and data subscriptions.
        Called exactly once by LEAN before any data events.
        """

        # --- Backtest date range (ignored in live trading) -------------------
        self.set_start_date(2020, 1, 1)
        self.set_end_date(self.time.today())

        # --- Starting capital (backtests only) -------------------------------
        self.set_cash(self.START_CASH)

        # --- Brokerage model -------------------------------------------------
        # Configures LEAN with Alpaca's fee schedule and order types.
        self.set_brokerage_model(BrokerageName.ALPACA, AccountType.MARGIN)

        # --- Subscribe to market data ----------------------------------------
        # Resolution.DAILY for end-of-day bars.
        # Change to Resolution.MINUTE for intraday strategies.
        equity = self.add_equity(self.TICKER, Resolution.DAILY)
        self._symbol = equity.symbol

        # --- State tracking --------------------------------------------------
        self._invested = False

        self.log(f"ExampleAlgorithm initialised. Trading: {self.TICKER}")

    def on_data(self, slice: Slice) -> None:
        """
        Called by LEAN on every new data bar for subscribed symbols.
        This is where your strategy's signal and order logic belongs.

        Args:
            slice: Current slice of market data. Access individual bars
                   via slice.bars[symbol].
        """

        # Guard: skip if no data for our symbol this bar.
        if self._symbol not in slice.bars:
            return

        bar = slice.bars[self._symbol]

        # --- Entry logic (replace with your strategy) ------------------------
        if not self._invested:
            # Allocate ALLOCATION fraction of portfolio to the symbol.
            self.set_holdings(self._symbol, self.ALLOCATION)
            self._invested = True
            self.log(f"Entered {self.TICKER} at ${bar.close:.2f}")

        # Log daily state for monitoring.
        self.log(
            f"Date: {self.time:%Y-%m-%d} | "
            f"{self.TICKER}: ${bar.close:.2f} | "
            f"Portfolio: ${self.portfolio.total_portfolio_value:,.0f}"
        )

    def on_order_event(self, order_event: OrderEvent) -> None:
        """
        Called by LEAN whenever an order's status changes (submitted,
        filled, cancelled, etc.). Use this to track execution quality
        and handle order errors.

        Args:
            order_event: Contains the order ID, symbol, status, fill
                         price, and fill quantity.
        """
        if order_event.status == OrderStatus.FILLED:
            self.log(
                f"Order filled: {order_event.symbol} | "
                f"{order_event.direction} | "
                f"Qty: {order_event.fill_quantity} | "
                f"Price: ${order_event.fill_price:.2f}"
            )
        elif order_event.status in (OrderStatus.INVALID, OrderStatus.CANCELED):
            self.error(
                f"Order issue: {order_event.symbol} — {order_event.message}"
            )
