# DEVIATIONS.md # Pi-AI-Trader — Implementation Deviations from Design

This file tracks all known differences between the documented/designed architecture and the actual deployed implementation. Updated as deviations are discovered or resolved.

---

## LEAN Engine

### LEAN built from source (not CLI)
- **Date discovered:** Initial setup
- **Reason:** LEAN CLI requires paid QuantConnect credentials for local use despite
  documentation suggesting otherwise. Building from source is the correct path for
  self-hosted deployments.
- **Impact:** No `lean` CLI commands available. All operations use `dotnet` directly.
- **LEAN location:** `/opt/lean-engine/`

### .NET 10 (not .NET 8)
- **Date discovered:** Initial setup
- **Reason:** .NET 10 was available and used during build; LEAN compiled successfully
  against it.
- **Impact:** `TargetFramework` in all `.csproj` files must be `net10.0`.

### Alpaca brokerage plugin — manual patch required
- **Date discovered:** Initial setup
- **Reason:** `ValidateSubscription()` in the Alpaca plugin requires a paid
  QuantConnect account. Called unconditionally on startup, blocking all live trading.
- **Change:** `ValidateSubscription()` call commented out in the Alpaca plugin source.
  Must be re-applied after any LEAN engine update.

### No Docker
- **Date discovered:** Initial setup
- **Reason:** Running natively on Raspberry Pi OS. Docker not used.
- **Impact:** All services managed via systemd.

### LEAN results directory is `/opt/lean-engine/Launcher/bin/Release/`
- **Date discovered:** 2026-03-10
- **Reason:** LEAN (built from source, no CLI) writes all output files directly to
  its own build output directory, not to a separate workspace path. The original
  `LEAN_RESULTS_DIR` config pointed to `~/Pi-AI-Trader/lean/Results/` which does
  not exist and is never written to.
- **Change:** `LEAN_RESULTS_DIR` in `web/app.py` now defaults to
  `/opt/lean-engine/Launcher/bin/Release/` and can be overridden via the
  `LEAN_RESULTS_DIR` environment variable in `/etc/tradingpi/web.env`.

---

## Strategy

### Fully-qualified namespace required
- **Date discovered:** Initial setup
- **Reason:** LEAN resolves algorithm type by fully-qualified name.
- **algorithm-type-name:** `PiAiTrader.Strategies.DualMomentumV2`

### Strategy DLL manually copied to LEAN Release directory
- **Date discovered:** Initial setup
- **Deploy command:**
```bash
  cp bin/Release/net10.0/PiAiTrader.Strategies.dll /opt/lean-engine/Launcher/bin/Release/
  sudo systemctl restart lean-trader
```

### DLL assembly name is `PiAiTrader.Strategies`
- **Date discovered:** 2026-03-10
- **Reason:** Original `AssemblyName` in `.csproj` was `DualMomentumV2`, causing
  `dotnet build` to produce `DualMomentumV2.dll`. LEAN loads the DLL by the name
  `PiAiTrader.Strategies.dll` and resolves the algorithm class by fully-qualified
  name. The mismatch caused an `Algorithm type name not found` crash on every startup.
- **Change:** `<AssemblyName>PiAiTrader.Strategies</AssemblyName>` set in
  `DualMomentumV2.csproj`. Build now produces `PiAiTrader.Strategies.dll` directly.
  No rename needed on copy.

### MarketOrder instead of SetHoldings/Liquidate
- **Date discovered:** 2026-03-10
- **Reason:** `AlpacaBrokerageModel` converts `SetHoldings()` and `Liquidate()` calls
  into `MarketOnOpen` orders, which are only valid for submission between 07:00–09:28
  local time. When `OnData` fires outside that window all orders are rejected:
  `NotSupported: MarketOnOpen submission time is invalid`.
- **Change:** Added `DefaultOrderProperties = new AlpacaOrderProperties
  { TimeInForce = TimeInForce.Day }` in `Initialize()`. Replaced all `SetHoldings()`
  calls with explicit `MarketOrder()` calls calculating share quantity from
  `PositionWeight * TotalPortfolioValue / Price`. Replaced all `Liquidate()` calls
  with `MarketOrder(sym, -Portfolio[sym].Quantity)`. All quantity arguments explicitly
  cast to `(decimal)` to resolve `CS0121` ambiguity between `MarketOrder(Symbol,
  double)` and `MarketOrder(Symbol, decimal)` overloads.
- **Verification:** Fix will be confirmed on first rebalance (first trading day of
  April 2026).

---

## Web Interface

### API endpoints use LEAN's actual file naming and abbreviated JSON keys
- **Date discovered:** 2026-03-10
- **Reason:** LEAN does not write `live-*.json`, `transaction-log.json`, or
  `*Statistics*.json` files. Holdings JSON uses abbreviated keys (`a`, `q`, `p`,
  `v`, `u`, `up`) not long-form keys (`AveragePrice`, `Quantity`, etc.).
- **Change:** `positions()` reads `PiAiTrader.Strategies.DualMomentumV2.json`
  directly and parses abbreviated keys. Also returns `cash_usd` and
  `total_portfolio_value`. `trades()` globs `*-order-events.json` files.
  `performance()` globs `*_10minute.json` files.

---

## Known Cosmetic Issues (not yet fixed)

| Issue | Location | Date noted |
|-------|----------|------------|
| Symbol names show LEAN internal format (`EEM SNQLASP67O85`) instead of clean ticker (`EEM`) | Dashboard positions table | 2026-03-10 |
| Portfolio Value and Daily P&L cards show dashes — JS field name mismatch with API response | Dashboard summary cards | 2026-03-10 |
| Recent Trades TIME column shows raw Unix timestamp instead of human-readable date | Dashboard trades table | 2026-03-10 |
| Recent Trades SIDE and QTY show dashes for rejected orders — correct but could be labeled more clearly | Dashboard trades table | 2026-03-10 |

---

*Last updated: 2026-03-10*
