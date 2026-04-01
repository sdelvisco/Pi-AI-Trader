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

**Deviation: Schedule.On() for Monthly Rebalancing**
- **File:** `strategies/csharp/DualMomentumV2.cs`
- **Date:** 2026-04-01 (replaces 2026-03-10 MarketOrder approach)
- **Reason:** With `Resolution.Daily`, LEAN automatically converts ALL `MarketOrder()` calls to `MarketOnOpen` orders to prevent execution on stale end-of-day prices. This is a hard LEAN safety feature that cannot be overridden. The original fix (switching from `SetHoldings()` to `MarketOrder()` with `TimeInForce.Day`) did not resolve the issue because the conversion to `MarketOnOpen` still occurred. Since `MarketOnOpen` orders are only valid for submission between 07:00–09:28 local time, rebalancing triggered in `OnData()` at 4:00 PM (market close with daily bars) resulted in invalid orders.
- **Change:** Removed monthly rebalancing logic from `OnData()`. Added `Schedule.On(DateRules.MonthStart(), TimeRules.At(9, 15), ...)` in `Initialize()` to trigger rebalancing at 9:15 AM ET on the first trading day of each month. This ensures all orders are submitted during the valid `MarketOnOpen` window (07:00–09:28). The `_lastRebalanceMonth` guard remains in place to prevent duplicate execution. Stop-loss and drawdown checks remain in `OnData()` for daily monitoring.
- **Dependencies:** Requires `NodaTime.dll` reference in `.csproj` for `DateRules` and `TimeRules` functionality.
- **Verification:** Fix will be confirmed on first rebalance (first trading day of May 2026).

**Deviation: NodaTime Assembly Reference**
- **File:** `strategies/csharp/DualMomentumV2.csproj`
- **Date:** 2026-04-01
- **Reason:** `Schedule.On()` requires the `NodaTime` library for timezone-aware scheduling (`DateRules` and `TimeRules`). Without this reference, the build fails with `CS0012: The type 'DateTimeZone' is defined in an assembly that is not referenced`.
- **Change:** Added `<Reference Include="NodaTime">` pointing to `/opt/lean-engine/Launcher/bin/Release/NodaTime.dll` in the `.csproj` file, consistent with other LEAN assembly references.

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

## Dashboard Display Issues - Fixed 2026-04-01

The following cosmetic issues were resolved by updates to `web/templates/dashboard.html` and `web/routes/api.py`:

- **Portfolio Value and Daily P&L cards** now populate correctly. Added JavaScript in `refreshPositions()` to read `total_portfolio_value` from the API and calculate Daily P&L as the sum of all position `unrealized_pnl` values.
- **Symbol names** display clean tickers (e.g. `EEM`) instead of LEAN's internal format (e.g. `EEM SNQLASP67O85`). Frontend uses `p.symbolValue || p.symbol` fallback; backend API adds `symbolValue` field by extracting the ticker before the first space.
- **Trade timestamps** format as human-readable dates (e.g. `Apr 1, 04:06 PM`) instead of Unix epoch timestamps. JavaScript converts Unix timestamps to localized date strings.
- **Trade direction and quantities** now display correctly. JavaScript handles multiple field name variants (`direction`/`Direction`, `quantity`/`fillQuantity`) and capitalizes direction text.
- **Frontend changes:** `web/templates/dashboard.html` - Added Portfolio/P&L card population, timestamp formatting, `symbolValue` usage, color-coded P&L display.
- **Backend changes:** `web/routes/api.py` - Added `symbolValue` field extraction in `/api/positions` endpoint.

---

## Known Cosmetic Issues (not yet fixed)

| Issue | Location | Date noted | Notes |
|-------|----------|------------|-------|
| Recent Trades SIDE and QTY show dashes for invalid/rejected orders | Dashboard trades table | 2026-03-10 | **Correct behavior** - invalid orders never filled, so no side/qty data exists. Orders show red "Invalid" status badge appropriately. |

---

*Last updated: 2026-04-01*
