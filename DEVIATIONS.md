# Deviations from Original Design

This file documents intentional deviations from the original design, architecture decisions, and known issues.

---

## Deviation: MarketOrder instead of SetHoldings/Liquidate

* **File:** `strategies/csharp/DualMomentumV2.cs`
* **Date:** 2026-03-10
* **Reason:** AlpacaBrokerageModel converts `SetHoldings()` and `Liquidate()` calls into MarketOnOpen orders, which are only valid for submission between 07:00–09:28 local time. When `OnData` fires outside that window all orders are rejected with `NotSupported: MarketOnOpen submission time is invalid`.
* **Change:** Added `DefaultOrderProperties = new AlpacaOrderProperties { TimeInForce = TimeInForce.Day }` in `Initialize()`. Replaced all `SetHoldings()` calls with explicit `MarketOrder()` calls calculating share quantity from `PositionWeight * TotalPortfolioValue / Price`. Replaced all `Liquidate()` calls with `MarketOrder(sym, -Portfolio[sym].Quantity)`. All quantity arguments cast to `(decimal)` explicitly to resolve CS0121 ambiguity between `MarketOrder(Symbol, double)` and `MarketOrder(Symbol, decimal)` overloads.

---

## Deviation: LEAN results directory is `/opt/lean-engine/Launcher/bin/Release/`

* **File:** `web/app.py`
* **Date:** 2026-03-10
* **Reason:** LEAN (built from source, no CLI) writes all output files directly to its own build output directory, not to a separate workspace path. The original `LEAN_RESULTS_DIR` config pointed to `~/Pi-AI-Trader/lean/Results/` which does not exist and is never written to.
* **Change:** `LEAN_RESULTS_DIR` now defaults to `/opt/lean-engine/Launcher/bin/Release/` and can be overridden via the `LEAN_RESULTS_DIR` environment variable in `/etc/tradingpi/web.env`.

---

## Deviation: API endpoints use LEAN's actual file naming and abbreviated JSON keys

* **File:** `web/routes/api.py`
* **Date:** 2026-03-10
* **Reason:** LEAN does not write `live-*.json`, `transaction-log.json`, or `*Statistics*.json` files. It writes files named after the algorithm with specific suffixes. Holdings JSON uses abbreviated keys (`a`, `q`, `p`, `v`, `u`, `up`) not long-form keys (`AveragePrice`, `Quantity`, etc.).
* **Change:** `positions()` reads `PiAiTrader.Strategies.DualMomentumV2.json` directly and parses abbreviated keys. Also returns `cash_usd` and `total_portfolio_value`. `trades()` globs `*-order-events.json` files. `performance()` globs `*_10minute.json` files.

---

## Deviation: DLL assembly name is `PiAiTrader.Strategies`

* **File:** `strategies/csharp/DualMomentumV2.csproj`
* **Date:** 2026-03-10
* **Reason:** Original `AssemblyName` was `DualMomentumV2`, causing `dotnet build` to produce `DualMomentumV2.dll`. LEAN's `lean.json` references `algorithm-type-name: PiAiTrader.Strategies.DualMomentumV2` and loads the DLL by the name `PiAiTrader.Strategies.dll`. Mismatch caused `Algorithm type name not found` crash on every startup.
* **Change:** `<AssemblyName>PiAiTrader.Strategies</AssemblyName>` set in `.csproj`. Build now produces `PiAiTrader.Strategies.dll` directly. Deploy command is `cp bin/Release/net10.0/PiAiTrader.Strategies.dll /opt/lean-engine/Launcher/bin/Release/` with no rename needed.

---

## Known cosmetic issues (not yet fixed) — 2026-03-10

* Dashboard symbol names display LEAN's internal ticker format (e.g. `EEM SNQLASP67O85`) instead of clean ticker symbols (e.g. `EEM`).
* Portfolio Value and Daily P&L summary cards show dashes — dashboard JS field name mismatch with API response.
* Recent Trades TIME column displays raw Unix timestamp instead of human-readable date.
* Recent Trades SIDE and QTY columns show dashes for invalid/rejected orders (no fill data available — this is correct behavior for rejected orders but could be labeled more clearly).
