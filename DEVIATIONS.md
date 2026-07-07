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

### Alpaca brokerage plugin — GetCashBalance() bypasses GetAccountAsync()
- **Date discovered:** 2026-07-07
- **Reason:** Alpaca deprecated the `pattern_day_trader` field on the
  `GET /v2/account` response ahead of FINRA's new Intraday Margin Standards
  (effective before 2026-06-04), which replaced the old PDT flag. Alpaca no
  longer sends that field at all, but the vendored `Alpaca.Markets.dll` (built
  from `alpacahq/alpaca-trade-api-csharp`, tag `sdk-8.0.0-beta4` — the newest
  release available; there is no newer SDK version to update to) deserializes
  the account response with a strict model that requires `pattern_day_trader`
  to be present. Every call to `_tradingClient.GetAccountAsync()` therefore
  threw `Required property 'pattern_day_trader' not found in JSON.` — this
  happens inside `AlpacaBrokerage.GetCashBalance()`, which runs during LEAN's
  `BrokerageSetupHandler.Setup()` at algorithm startup, so the engine crashed
  on every single restart.
- **Change:** `GetCashBalance()` in `AlpacaBrokerage.cs` no longer calls
  `_tradingClient.GetAccountAsync()`. Instead it makes a manual authenticated
  HTTP GET directly to Alpaca's REST account endpoint
  (`https://paper-api.alpaca.markets/v2/account` or
  `https://api.alpaca.markets/v2/account`, matching `isPaperTrading`) and
  parses only the `cash` and `currency` fields with `Newtonsoft.Json`,
  bypassing the SDK's strict deserialization entirely. `Alpaca.Markets.dll`
  itself is not modified or rebuilt.
- **Impact:** No known upstream fix as of this writing. This is a permanent
  workaround for a deprecated/removed Alpaca field, not a temporary fix
  pending an SDK update — there is no newer SDK release to move to.
- **Patch mechanism:** Applied by `setup/06_lean_build.sh` (Step 4) via the
  same approach as the `ValidateSubscription()` patch above: a Python
  string-replacement patch run against the freshly cloned
  `AlpacaBrokerage.cs`, idempotent (skipped on re-run if already applied), and
  never committed upstream — it lives only in `/opt/lean-alpaca` and must be
  re-applied (automatically, by re-running `06_lean_build.sh`) after any LEAN
  Alpaca plugin update.

### Alpaca plugin DLL copy — existence-check filter instead of unconditional copy
- **Date discovered:** 2026-07-07
- **Reason:** `setup/06_lean_build.sh` Step 8 copies every DLL produced by the
  Alpaca plugin's own build output into LEAN's
  `Launcher/bin/Release/` directory so LEAN's plugin loader can find the
  Alpaca brokerage assembly. That step previously did this unconditionally
  with `cp -f`, with no filtering — copying not just the Alpaca-specific
  assemblies (`Alpaca.Markets.dll`, `QuantConnect.Brokerages.Alpaca.dll`) but
  also every third-party dependency the Alpaca plugin happens to vendor in
  its own output (`Python.Runtime.dll`, `Newtonsoft.Json.dll`,
  `NodaTime.dll`, `CsvHelper.dll`, `MessagePack.dll`, and others). On
  2026-07-07, a same-day `git reset --hard origin/master` pull of LEAN (Step
  6, unpinned to any tag/commit) compiled against `Python.Runtime`
  2.0.57.0 and placed the correct copy in `Launcher/bin/Release/`. Step 8
  then unconditionally overwrote it with the Alpaca plugin's own vendored
  `Python.Runtime.dll` — version 2.0.53.0, dated 2026-02-23, and not a NuGet
  package reference in the plugin's `.csproj` (confirmed via `grep` — no
  match), i.e. a stale binary bundled in the plugin's own build output. This
  version collision caused `QuantConnect.Algorithm.dll` (built fresh in Step
  6, which requires `Python.Runtime` 2.0.57.0) to conflict with the
  downgraded 2.0.53.0 copy, so DualMomentumV2.dll's subsequent `make all`
  build failed with `CS1705` (assembly version mismatch).
- **Change:** Step 8 now skips copying any DLL that already exists in LEAN's
  Release output directory (trusting that Step 6's own build already placed
  an authoritative copy of that shared dependency), except
  `Alpaca.Markets.dll` and `QuantConnect.Brokerages.Alpaca.dll`, which are
  always force-overwritten since they are the actual deliverable of this
  step. The log output now reports three counts (force-overwritten, copied,
  skipped) instead of one combined total, so it's clear at a glance whether
  the two critical files were actually refreshed on a given run.
- **Impact:** This fix addresses the copy-clobbering *symptom* only. The
  underlying root cause — that both the LEAN repo and the Alpaca plugin repo
  are cloned via unpinned `git reset --hard origin/master` with no tag or
  commit pinning — is **not** fixed here and remains a known risk. A future
  same-day upstream change on either side could still introduce a
  version-incompatible dependency that this existence-check does not (and
  cannot) resolve, since it only decides whether to overwrite, not which
  version is actually compatible. Pinning both repos to known-good
  tags/commits is a separate, unaddressed follow-up.

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

### Algorithm-type-name and DLL naming
- **Date discovered:** Initial setup / revised 2026-05-05
- **Reason:** LEAN scans all DLLs in its working directory and resolves the algorithm
  class by `algorithm-type-name` in `config.json`. The DLL filename does **not** need
  to match any particular pattern — LEAN does not load by filename.
- **algorithm-type-name:** `DualMomentumV2` (short class name, no namespace prefix needed
  when only one class with that name exists in the loaded assemblies)
- **algorithm-location:** `DualMomentumV2.dll`
- **Note:** A previous entry (2026-03-10) incorrectly stated that LEAN "loads the DLL by
  name". The actual crash at that time was caused by `algorithm-location` in `config.json`
  referencing a file that no longer existed after the assembly was renamed. The fix was
  to keep the assembly name consistent with whatever `algorithm-location` references.

### Automated deployment pipeline (added 2026-05-05)
- **Replaces:** Manual `cp` + `systemctl restart` workflow
- **Tools:** `Makefile` in project root + GitHub Actions (`.github/workflows/deploy.yml`)
- **Deploy command:**
```bash
  make all        # build + deploy + verify in sequence
  make build      # compile only → strategies/csharp/bin/Release/net10.0/DualMomentumV2.dll
  make deploy     # copy DLL, verify config.json, restart lean-trader
  make verify     # poll journal for "DualMomentumV2 Initialized" (up to 60s)
```
- **GitHub Actions:** Pushes to `main` SSH into the Pi, pull latest code, run `make all`.
  Requires `PI_SSH_KEY` GitHub Secret and passwordless sudo configured on Pi
  (see Makefile header comment for exact sudoers rules).
- **Health check:** `scripts/health_check.sh` runs daily via cron (see script header
  for cron setup). Alerts written to `/var/log/pi-ai-trader/alerts.log`.

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

*Last updated: 2026-07-07*
