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

### Makefile `deploy` target wrote config.json to the wrong directory
- **Date discovered:** 2026-07-07
- **Reason:** LEAN reads `config.json` from the same directory as its own
  executable (`/opt/lean-engine/Launcher/bin/Release/`), because
  `WorkingDirectory` and `ExecStart` in `/etc/systemd/system/lean-trader.service`
  both point there — not at `/opt/lean-engine/Launcher/` one level up. The
  `deploy` target's config-copy step wrote (and its verify step checked)
  `/opt/lean-engine/Launcher/config.json` instead, which the running service
  never reads. This had likely been wrong since the target was written, but
  went unnoticed because the correct file at `bin/Release/config.json`
  already contained a correct-enough config from initial manual setup, and
  nothing had rebuilt LEAN (which regenerates `bin/Release/`, including its
  own bundled sample `config.json`) since then. A `setup/06_lean_build.sh`
  run that pulled a new LEAN commit and rebuilt it replaced
  `bin/Release/config.json` with LEAN's own stock sample config
  (`algorithm-type-name: BasicTemplateFrameworkAlgorithm`, `environment:
  backtesting`, placeholder brokerage blocks), silently overwriting whatever
  was there before. Confirmed via `diff Launcher/config.json
  Launcher/bin/Release/config.json`: `Launcher/config.json` was the correct,
  up-to-date config matching `config/lean_config.template.json`
  (DualMomentumV2 / AlpacaBrokerage / `live-mode: true`), while
  `Launcher/bin/Release/config.json` was LEAN's own unrelated default
  sample. Effect: `make deploy` / `make all` wrote the correct config to a
  path LEAN never reads, so `lean-trader` silently ran LEAN's built-in demo
  algorithm instead of DualMomentumV2 whenever `bin/Release/config.json` got
  regenerated by a LEAN rebuild. `make verify`'s poll for `DualMomentumV2
  Initialized` correctly failed, since that string never had a reason to
  appear.
- **Change:** The `LEAN_CONFIG` variable in the `Makefile` (used by both the
  write step and the verify step) now points at
  `/opt/lean-engine/Launcher/bin/Release/config.json`. The write step's
  `python3` one-liner previously hardcoded `Launcher/config.json` directly
  rather than using `$(LEAN_CONFIG)`, so it needed a separate fix to
  reference the same variable; the verify step already used `$(LEAN_CONFIG)`
  and is now correct automatically.
- **Note:** `/opt/lean-engine/Launcher/config.json` (the stale, one-level-up
  file) was left as-is — it likely serves no purpose now other than being a
  leftover from this path bug, but deleting it is a separate decision, not
  part of this fix.
- **Impact:** This class of bug — a deploy step writing to the wrong
  directory that goes unnoticed until the *correct* directory's file
  happens to get regenerated by something else (here, a LEAN rebuild) — is
  worth watching for elsewhere in the deploy pipeline, since the same
  masking pattern could recur with other config or asset paths.

### `job-user-id: ""` crashed LEAN on startup (`FormatException` in `Globals` static ctor)
- **Date discovered:** 2026-07-07
- **Reason:** `config/lean_config.template.json` has had `"job-user-id": ""`
  since the file's very first commit (2026-03-06) — confirmed via `git log
  --follow -p`; it was never edited afterward, including in the 2026-06-02
  "clean config JSON" pass that stripped the file's comments. It only
  surfaced as a crash today because today was the first time in months that
  `setup/06_lean_build.sh` rebuilt LEAN from a fresh `origin/master` pull,
  and the freshly built `QuantConnect.Globals` static constructor is what
  actually reads this key. `Globals.Reset()`
  (`/opt/lean-engine/Common/Globals.cs`) calls `Config.GetInt("job-user-id")`
  with no explicit default. `Config.GetValue<T>`
  (`/opt/lean-engine/Configuration/Config.cs`) only returns the default when
  the key is **absent** from `config.json`; here the key is *present* with
  value `""`, so it proceeds to `Convert.ChangeType("", typeof(int))`, which
  throws `System.FormatException`. Because this happens inside a static
  constructor (`QuantConnect.Globals..cctor`), .NET wraps it in a
  `TypeInitializationException` and the process aborts (`SIGABRT`) in well
  under a second, before `Main()` runs — i.e. before any of LEAN's own
  logging or error handling can engage.
- **Change:** `job-user-id` changed from `""` to `"0"` (a quoted numeric
  string, matching LEAN's own bundled sample config), with a JSON-valid
  `_comment_job_user_id` sibling key added directly above it explaining why.
  A plain `//` comment was deliberately **not** used: `make deploy`
  (Makefile) re-parses this template with Python's `json.load`, which has no
  comment support and errors immediately on `//` — confirmed by testing, and
  independently corroborated by `scripts/fix_lean_config.sh`'s own header,
  which documents comment lines as a known cause of breaking that same
  `json.load` step. This is very likely why the 2026-06-02 commit stripped
  the file's original `//` comments in the first place.
- **Investigation (other numeric-accessor fields):** Cloned
  `QuantConnect/Lean` at the current `origin/master` and grepped every
  `Config.GetInt`/`GetDouble`/`GetDecimal` call site, cross-referenced
  against every key in the template:
  - `"job-project-id": 0` — **not actually read under this name.**
    `Globals.Reset()` reads `Config.GetInt("project-id")` (no `job-` prefix)
    for `Globals.ProjectId`. `"project-id"` is absent from the template, so
    it silently defaults to `0` — no crash, but `job-project-id` in the
    template currently does nothing. Left unchanged (still `0`, an int
    literal so it can't hit the empty-string crash either way); renaming it
    to match the real key was out of scope for this fix, but a
    `_comment_job_project_id` sibling key documents the mismatch. Flagging
    for a future cleanup pass.
  - `"api-access-token": ""` — read via `Config.Get` (string accessor, not
    numeric), so an empty value is safe and intentional (populated only if
    syncing to QuantConnect Cloud). Left unchanged.
  - `"backtest-timeout-minutes"`, `"starting-cash"`, `"max-drawdown"` — not
    referenced by *any* `Config.GetInt`/`GetDouble`/`GetDecimal` call in the
    current LEAN source at all (they're valid JSON number literals already,
    so they wouldn't hit this failure mode regardless). No action needed.
  - No other template key collided with a `Config.GetInt`/`GetDouble`/
    `GetDecimal` key name.
- **Impact:** This was the last blocker after today's earlier
  `pattern_day_trader` deserialization fix, `Python.Runtime` version
  conflict fix, and `config.json` wrong-directory fix — `lean-trader` was
  still crashing before any of those three could even be exercised, since
  this failure happens before the algorithm or brokerage ever loads.

### `/lean` paths crashed LEAN under `ProtectSystem=strict` (`IOException: Read-only file system`), plus an invalid `data-provider`
- **Date discovered:** 2026-07-07
- **Reason:** `lean-trader.service` runs with `ProtectSystem=strict` and only
  two `ReadWritePaths`: `/opt/lean-engine/Launcher/bin/Release` and `/tmp`.
  Every other path is read-only to the service. `config/lean_config.template.json`
  had `data-folder`, `results-destination-folder`, and `transaction-log` all
  pointing under `/lean`, which has never existed anywhere on this
  filesystem (confirmed: `stat /lean` → "No such file or directory", not a
  mount point). `Engine/Initializer.cs:45` (`Initializer.Start()`)
  unconditionally calls `Directory.CreateDirectory(Globals.ResultsDestinationFolder)`
  on every startup, which threw `System.IO.IOException: Read-only file
  system : '/lean'` and crashed the service before the algorithm or
  brokerage ever loaded. This had been silently masked for months by the
  empty-`job-user-id` crash (previous entry above) — that crash happened
  earlier in the same startup sequence (in `Globals`'s static constructor,
  before `Initializer.Start()` even runs), so LEAN never got far enough to
  attempt the `/lean` write until that was fixed. This is very likely the
  explanation for a previously-unexplained project symptom: results were
  observed writing to `/opt/lean-engine/Launcher/bin/Release/` instead of
  the intended `lean/Results` path — `Globals.ResultsDestinationFolder`
  falls back to `Directory.GetCurrentDirectory()` (== `bin/Release`, the
  systemd `WorkingDirectory`) whenever the configured value can't be used
  as intended.
- **Change:** `results-destination-folder` and `transaction-log` changed to
  relative paths (`"Results"` and `"Results/transaction-log.json"`).
  Confirmed in LEAN source that relative paths here resolve against the
  process's current working directory (`Directory.CreateDirectory` in
  `Initializer.Start()`; the file write in `Engine.SaveListOfTrades()`),
  which for this systemd unit equals `WorkingDirectory=/opt/lean-engine/Launcher/bin/Release`
  — the one writable path — so no `ReadWritePaths` change was needed.
- **Second finding (not part of the original bug report, found during the
  required investigation of `data-folder`):** `"data-provider": "QuantConnectDataProvider"`
  is not a real LEAN type. Cloned `QuantConnect/Lean` at current
  `origin/master` and grepped `Engine/DataFeeds/`: the only `IDataProvider`
  implementations are `DefaultDataProvider`, `ApiDataProvider`,
  `DownloaderDataProvider`, and `CompositeDataProvider`.
  `Composer.GetExportedValueByTypeName<IDataProvider>()` matches only exact
  `AssemblyQualifiedName`/`FullName`/`Name` (`Extensions.MatchesTypeName`),
  so this would have thrown `Unable to locate any exports matching the
  requested typeName: QuantConnectDataProvider` in
  `LeanEngineAlgorithmHandlers.InitializeAuxiliaryDataProviders()` — i.e.
  fixing only the `/lean` paths would have traded one startup crash for
  another on the very next run. `ApiDataProvider` (LEAN's actual
  QuantConnect-Cloud-backed provider — its constructor throws unless
  `organization.DataAgreement.Signed`) requires a paid, terms-accepted QC
  account, which this project deliberately avoids (see "LEAN built from
  source (not CLI)" above). Changed `data-provider` to `DefaultDataProvider`
  — the same value LEAN's own bundled `Launcher/config.json` sample uses —
  which reads only from local disk under `data-folder`, touches no network,
  and needs no account.
- **`data-folder` change:** `DefaultDataProvider` requires files to already
  exist locally; it does not fetch anything. `setup/06_lean_build.sh` does a
  full (non-sparse) `git clone` of `QuantConnect/Lean` into
  `/opt/lean-engine`, which includes the repo's own bundled sample `Data/`
  directory (confirmed present in a fresh clone: 1,141 files across
  equity/forex/crypto/etc., and no step in `06_lean_build.sh` deletes or
  excludes it afterward). `data-folder` now points at
  `/opt/lean-engine/Data` — read-only under `ProtectSystem=strict`, which is
  fine since `DefaultDataProvider` only ever reads, never writes. **Caveat:**
  this is LEAN's own generic demo dataset, not verified to contain complete
  history for every symbol `DualMomentumV2` actually trades.
  `DefaultDataProvider.Fetch()` returns `null` on a missing file rather than
  crashing, so this won't reproduce the read-only-filesystem crash, but
  historical-data completeness for the live symbols is a separate,
  unaddressed question outside the scope of this fix.
- **Comment format:** Documented all of the above via `_comment_paths` and
  `_comment_data_provider` sibling JSON keys, per the same constraint
  established in the `job-user-id` fix — plain `//` comments break `make
  deploy`'s Python `json.load` step.
- **Impact:** This is the fifth distinct root cause found blocking
  `lean-trader` startup today (after `pattern_day_trader` deserialization,
  the `Python.Runtime` version conflict, the `config.json` wrong-directory
  bug, and the empty `job-user-id`). Per the project's own decision, the
  systemd sandboxing (`ProtectSystem=strict` / `ReadWritePaths`) was treated
  as fixed and correct throughout — only the application config was
  changed.

### Flat config never set `result-handler`/`setup-handler`/etc., so live-mode built a `LiveNodePacket` but ran it through backtesting handlers (`InvalidCastException`)
- **Date discovered:** 2026-07-07
- **Symptom:** With the `/lean`-path and `job-user-id` crashes fixed,
  `lean-trader` got as far as loading `DualMomentumV2.dll` as the job, then
  crashed with `System.InvalidCastException: Unable to cast object of type
  'QuantConnect.Packets.LiveNodePacket' to type
  'QuantConnect.Packets.BacktestNodePacket'`, thrown from
  `BacktestingResultHandler.Initialize()` inside `Engine.Run()`.
- **Investigation:** Read the current `QuantConnect/Lean` source directly
  rather than guessing:
  - `Queues/JobQueue.cs`'s `NextJob()` builds the job packet based on
    `Globals.LiveMode`, which is set from this file's top-level
    `"live-mode"` key — so with `live-mode: true` it correctly built a
    `LiveNodePacket`.
  - `Engine/LeanEngineAlgorithmHandlers.cs`'s `FromConfiguration()` resolves
    each handler **independently**, each with its own hardcoded default,
    none of which branch on `live-mode` at all:
    `Config.Get("result-handler", "BacktestingResultHandler")`,
    `Config.Get("setup-handler", "ConsoleSetupHandler")`,
    `Config.Get("data-feed-handler", "FileSystemDataFeed")`,
    `Config.Get("transaction-handler", "BacktestingTransactionHandler")`,
    `Config.Get("real-time-handler", "BacktestingRealTimeHandler")`. With
    none of these five keys present in the template, every handler
    silently fell back to its backtesting variant regardless of
    `live-mode`'s value — hence a live job packet run through a
    backtesting result handler.
  - Checked whether this is a LEAN regression: it is not. `git log
    --follow -p` on `config/lean_config.template.json` shows these five
    handler keys have **never** been present, back to the file's very
    first commit (`c38b3b6`). A flat config here could never have
    resolved live handlers correctly via LEAN's own defaults — whatever
    produced this project's previously-reported live Alpaca fills must
    have supplied these keys some other way (e.g. edited directly into
    the deployed `config.json` on the Pi and never committed back to this
    template, or a since-overwritten `config.json` that used LEAN's
    `environments` structure). No relevant change to LEAN's own
    config-resolution or handler-default code was found.
  - Checked whether an `"environment"`/`"environments"` block is
    structurally required for live-mode to resolve the right handlers: it
    is not. `Configuration/Config.cs`'s `GetToken()` only looks inside
    `settings.SelectToken("environments." + environment)` when an
    `"environment"` key is set; with no such key, it falls straight
    through to `settings.SelectToken(key)` on the flat top level. A
    top-level key resolves identically to one merged in from a named
    environment — so restructuring the template around `environments`
    would have been a larger diff for no behavioral difference.
- **Change:** Added five explicit top-level keys to
  `config/lean_config.template.json`, copied verbatim from LEAN's own
  bundled `Launcher/config.json` sample's `"live-alpaca"` environment
  block: `"setup-handler":
  "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler"`, `"result-handler":
  "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler"`,
  `"data-feed-handler":
  "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed"`,
  `"real-time-handler":
  "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler"`, and
  `"transaction-handler":
  "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler"`.
  The existing top-level Alpaca keys (`alpaca-access-token`,
  `alpaca-secret-key`, `paper`, `live-mode-brokerage`,
  `data-queue-handler`) did not need to move — they were already read
  correctly at the flat top level and are unaffected by this fix.
- **Comment format:** Documented via a `_comment_handler_resolution`
  sibling JSON key, per the same constraint established in the
  `job-user-id` fix — plain `//` comments break `make deploy`'s Python
  `json.load` step.
- **Impact:** This is the sixth distinct root cause found blocking
  `lean-trader` startup today (after `pattern_day_trader` deserialization,
  the `Python.Runtime` version conflict, the `config.json` wrong-directory
  bug, the empty `job-user-id`, and the `/lean`-path/`data-provider`
  crash) — all six traced back to the same unpinned `git reset --hard
  origin/master` pull of LEAN done for the first time in months. Pinning
  LEAN (and the Alpaca brokerage plugin) to specific commits/tags instead
  of floating `origin/master` is now overdue as a follow-up, since an
  unpinned pull is the common trigger across all six incidents found in a
  single day.

### `${VAR}` credential placeholders were never actually substituted, and the Alpaca config key names didn't match what `AlpacaBrokerageFactory` reads (`FormatException: String '' was not recognized as a valid Boolean`)
- **Date discovered:** 2026-07-07
- **Symptom:** With the handler-resolution fix above in place, `lean-trader`
  got past job-packet/handler resolution and started loading
  `DualMomentumV2.dll`, then crashed with `System.FormatException: String
  '' was not recognized as a valid Boolean`, thrown from
  `Convert.ToBoolean()` inside
  `AlpacaBrokerageFactory.CreateBrokerage()`, called from
  `BrokerageSetupHandler`.
- **Investigation:** Read `QuantConnect/Lean.Brokerages.Alpaca`'s current
  `AlpacaBrokerageFactory.cs` directly rather than guessing:
  - Its `BrokerageData` property reads `Config.Get("alpaca-api-key")`,
    `Config.Get("alpaca-api-secret")`, and
    `Config.Get("alpaca-paper-trading")` — **not** `alpaca-secret-key` or
    `paper`, the key names this template had used. `alpaca-api-key` was
    missing from the template entirely. Since `alpaca-paper-trading` was
    never a key in this file, `Config.Get` returned its default `""`
    (empty string), and `Convert.ToBoolean("")` throws exactly the
    observed exception — a key-name mismatch, not a LEAN bug.
  - Separately checked `QuantConnect.Configuration.Config`'s full source
    (`GetValue<T>`/`GetToken()`) for any `${VAR}` environment-variable
    substitution: there is none. It is pure JSON-token parsing and type
    conversion — no `Environment.GetEnvironmentVariable`,
    `Environment.ExpandEnvironmentVariables`, or any `$`-token handling
    anywhere in the file. `lean-trader.service`'s own header comment
    asserts "lean.json ... reference[s] [EnvironmentFile vars] via
    `${VAR_NAME}` syntax" — this assumption was simply wrong for this
    version of LEAN, not a regression; `git log --follow -p` on this
    template and on `setup/*.sh` shows no `envsubst`, `sed`, or other
    substitution step has ever existed in this pipeline either, and
    `make deploy`'s config-copy step was a plain
    `json.load()`/`json.dump()` round-trip that never touched string
    values. So these placeholders were never actually resolving to real
    credentials, independent of the key-name bug above.
  - Checked `AlpacaBrokerage`'s constructor to avoid introducing a new bug
    while fixing this: it prefers OAuth (`accessToken`) over
    `apiKey`/`apiKeySecret` whenever `accessToken` is non-empty
    (`tradingSecretKey ?? secretKey`, and `tradingSecretKey` is only null
    when `string.IsNullOrEmpty(accessToken)`). This template's old
    `"alpaca-access-token": "${ALPACA_KEY_ID}"` would have been a
    non-empty (if unsubstituted) string, silently forcing OAuth-token
    auth using garbage placeholder text instead of the intended
    API-key/secret auth, once the boolean crash was fixed — an eighth
    root cause waiting to happen if only the key names had been patched.
- **Change:**
  - Renamed/added the three real credential keys in
    `config/lean_config.template.json`: `alpaca-api-key` (new),
    `alpaca-api-secret` (was `alpaca-secret-key`), `alpaca-paper-trading`
    (was `paper`). `alpaca-access-token` is now explicitly `""` so
    `IsNullOrEmpty()` is true and auth correctly falls through to
    `alpaca-api-key`/`alpaca-api-secret` — this project uses API key/secret
    auth, not Alpaca OAuth.
  - Added `scripts/render_lean_config.py`, a new deploy-time step that
    actually performs the `${VAR}` substitution nothing else in the
    pipeline was doing: it reads `/etc/tradingpi/alpaca.env` directly
    (not the process environment, since `sudo` doesn't inherit the
    invoking shell's env), substitutes matching `${VAR}` tokens via
    `os.path.expandvars()`, leaves unmatched tokens untouched (so
    unrelated keys like `job-user-id`/`job-project-id` pass through
    unaffected), validates the result as JSON, writes it to LEAN's
    `config.json`, and `chmod 600`s it since it now contains live
    credentials in plaintext (previously it only ever contained inert
    placeholder text).
  - Updated `Makefile`'s `deploy` target to call this script instead of
    the old plain `json.load()`/`json.dump()` round-trip, and extended
    the pre-restart config verification to confirm
    `alpaca-api-key`/`alpaca-api-secret`/`alpaca-paper-trading` actually
    resolved to real values (non-empty, no leftover `"${...}"` text) —
    checking presence only, never printing the values themselves.
    Verified the new Make recipe's `$`-escaping and the full
    render-then-verify pipeline locally with fake, clearly-labeled
    placeholder credentials before committing; no real Alpaca credentials
    were used, printed, or logged during this investigation or fix.
- **Comment format:** Documented via a
  `_comment_alpaca_credential_keys` sibling JSON key, per the same
  constraint established in the `job-user-id` fix — plain `//` comments
  break `make deploy`'s Python `json.load` step.
- **Impact:** This is the seventh distinct root cause found blocking
  `lean-trader` startup today, and the second (after the handler-resolution
  fix above) traced not to LEAN itself but to this project's own
  config template having never matched what the current LEAN/Alpaca
  plugin source actually reads — both were latent bugs masked for months
  by the earlier crashes (`job-user-id`, then `/lean` paths, then handler
  resolution) that each aborted startup before reaching this code path.
  Same underlying trigger as all seven: the unpinned `git reset --hard
  origin/master` pull done for the first time in months surfaced every
  layer of pre-existing breakage in one session. Pinning LEAN and the
  Alpaca plugin to specific commits/tags remains overdue.

### Monthly rebalance silently failing since at least 2026-08-03: `history-provider` was never set, so `History<TradeBar>()` only ever read LEAN's bundled 21-symbol demo dataset (missing AGG entirely)
- **Date discovered:** 2026-08-14
- **Symptom:** The scheduled monthly rebalance (`Schedule.On(DateRules.MonthStart(...))`,
  `strategies/csharp/DualMomentumV2.cs`) fires correctly every month, but aborts before
  placing any orders. Live log evidence from the 2026-08-03 (August) rebalance attempt:
  ```
  [Rebalance] Triggered on 2026-08-03 (first trading day of August 2026)
  [AbsMom] SPY 12-month return :
  [AbsMom] AGG 12-month return :
  [AbsMom] Insufficient history for absolute momentum filter. Skipping rebalance.
  ```
  Both return values logged blank, confirming `GetMomentumReturn()` returned `null` for
  both `SPY` and `AGG`, which correctly triggers the `if (spyReturn == null || aggReturn
  == null)` safety guard in `Rebalance()` — this abort behavior is correct and was not
  changed; the fix addresses why history was unavailable in the first place, not the guard
  itself.
- **Investigation:**
  - `GetMomentumReturn()` (`strategies/csharp/DualMomentumV2.cs` ~line 650) calls
    `History<TradeBar>(sym, tradingDayEstimate, Resolution.Daily)` for both the absolute
    momentum filter (`SPY` vs `AGG`, 12-month lookback) and the relative momentum ranking
    (all ~50 `UniverseTickers`, 6-month lookback).
  - `config/lean_config.template.json` has never set a `history-provider` key. LEAN's
    `HistoryProviderManager.Initialize()` (`Engine/HistoricalData/HistoryProviderManager.cs`
    in `QuantConnect/Lean`, cloned fresh from GitHub to confirm — not the version on the
    Pi, which is unpinned `origin/master` per the existing "unpinned git reset --hard"
    caveat noted elsewhere in this file) falls back to
    `Config.Get("history-provider", "SubscriptionDataReaderHistoryProvider")` when the key
    is absent. `SubscriptionDataReaderHistoryProvider` reads only from local disk under
    `data-folder` — the same `/opt/lean-engine/Data` path documented in
    `_comment_data_provider` above as LEAN's own bundled tutorial/demo dataset.
  - Directly listed `/opt/lean-engine/Data`'s equity coverage on the Pi: 21 symbols total
    (`spy, iwm, aapl, qqq, gooav, uw, aig, wmi, fb, foxa, eem, googl, nwsa, goog, ibm, uso,
    bno, goocv, wm, aaa, bac`). `AGG` — the defensive/absolute-momentum-reference ticker
    hardcoded as `DefensiveTicker` in `DualMomentumV2.cs` — is completely absent. Since
    `GetMomentumReturn("AGG", 12)` is called on every rebalance regardless of market
    regime, this made the absolute momentum filter fail unconditionally, aborting every
    monthly rebalance since this deployment went live. The relative-momentum ranking would
    have been independently degraded too: of the ~50 `UniverseTickers`, only a handful
    (`SPY`, `IWM`, `AAPL`, `QQQ`, `EEM`, `GOOGL`) overlap with the 21-symbol demo set at
    all.
  - Confirmed `live-mode` data streaming was unaffected and is a separate LEAN subsystem:
    `data-queue-handler` is already correctly set to `AlpacaBrokerage`, which is why the
    dashboard's system health panel showed live data throughout even though rebalancing
    was broken — `History()` (backed by `history-provider`) and live streaming (backed by
    `data-queue-handler`) are resolved independently by LEAN.
  - Checked whether `QuantConnect.Brokerages.Alpaca`'s `AlpacaBrokerage` supports serving
    history at all, by cloning `QuantConnect/Lean.Brokerages.Alpaca` fresh from GitHub
    (current default branch) and reading `AlpacaBrokerage.HistoryProvider.cs` directly: it
    overrides `GetHistory(HistoryRequest)` and, for `SecurityType.Equity`, calls
    `GetEquityHistory()` → `_equityHistoricalDataClient.GetHistoricalBarsAsync()` — a real
    call to Alpaca's Market Data API via the vendored `Alpaca.Markets` SDK, not a stub.
    `_equityHistoricalDataClient` (`AlpacaBrokerage.cs` ~line 204) is constructed from
    `EnvironmentExtensions.GetAlpacaDataClient(environment, tradingSecretKey ?? secretKey)`,
    where `environment` is `Environments.Paper` or `Environments.Live` selected by the same
    `alpaca-paper-trading` flag already used for trading — i.e. the exact same
    `alpaca-api-key`/`alpaca-api-secret`/`alpaca-paper-trading` credentials already
    configured below serve both trading and historical data. There is no separate
    `ALPACA_DATA_URL`/`ALPACA_BASE_URL` key in this SDK version; that turned out not to be
    a real concern once the source was actually read, only an assumption worth checking.
  - Checked how to activate this path: `HistoryProviderManager.Initialize()` recognizes the
    literal config value `"BrokerageHistoryProvider"` (resolved via
    `Composer.GetExportedValueByTypeName<IHistoryProvider>`, matching
    `QuantConnect.Lean.Engine.HistoricalData.BrokerageHistoryProvider`'s short type name —
    same resolution mechanism as `_comment_data_provider`'s note on `Composer`, confirmed
    by re-reading `Extensions.MatchesTypeName`). It then wires the *already-running*
    `AlpacaBrokerage` data-queue-handler instance into it via
    `Composer.Instance.GetPart<IDataQueueHandler>(x => x.GetType().Name ==
    "AlpacaBrokerage")`; separately, `Engine.cs` (~line 200) unconditionally calls
    `historyProvider.SetBrokerage(brokerage)` with the live trading brokerage before every
    run. No `SetBrokerage()` call or extra wiring code needs to be added anywhere in this
    project — LEAN's launcher does it automatically once the config key is set.
  - This is not a guess: LEAN's own bundled `Launcher/config.json` sample (in the
    `QuantConnect/Lean` clone used for this investigation) ships a `"live-alpaca"`
    environment block — the *same* block `_comment_handler_resolution` above already cites
    as the source of this template's five handler keys — and its `history-provider` value,
    copied verbatim below, is `[ "BrokerageHistoryProvider", "SubscriptionDataReaderHistoryProvider" ]`.
    `HistoryProviderManager.GetHistory()` tries each entry in order and simply skips one
    that returns `null` for a given request (see its `try`/`continue` loop), so listing
    `SubscriptionDataReaderHistoryProvider` second is a harmless fallback to the local
    demo dataset for any request Alpaca can't serve, not a silent failure mode.
- **Change:** Added `"history-provider": [ "BrokerageHistoryProvider",
  "SubscriptionDataReaderHistoryProvider" ]` to `config/lean_config.template.json`,
  documented via a `_comment_history_provider` sibling JSON key (same comment-format
  constraint as the other `_comment_*` fields in this file — `make deploy` parses this
  file with Python's `json.load`, which has no `//` comment support).
  `strategies/csharp/DualMomentumV2.cs` was **not** modified — the
  `if (spyReturn == null || aggReturn == null)` abort guard remains exactly as-is, since it
  correctly protects against genuinely unavailable history; this fix makes real history
  available rather than bypassing the check that was correctly catching its absence.
- **Verified from source vs. inferred:** VERIFIED (read directly from fresh clones of
  `QuantConnect/Lean` and `QuantConnect/Lean.Brokerages.Alpaca`'s current default-branch
  source, not guessed): `AlpacaBrokerage.GetHistory()`'s real implementation and its use of
  the same paper/live credentials as trading; `HistoryProviderManager`'s config-key
  resolution and automatic brokerage wiring via `Engine.cs`; LEAN's own bundled
  `live-alpaca` sample config's exact `history-provider` value. NOT verified — remaining
  unknowns: (1) Whether the Alpaca account's actual data entitlements/subscription tier
  serve complete history for every one of the ~50 `UniverseTickers` (particularly the
  Grayscale trust proxies `GBTC`/`ETHE`) — the plugin code has no per-ticker special-casing
  for `SecurityType.Equity`, so there's no code-level reason they'd differ from `SPY`/`AGG`,
  but this depends on Alpaca's live API responses, which cannot be exercised from this
  investigation. (2) The exact behavior of the currently-running `/opt/lean-alpaca` build
  on the Pi, since it was built from an unpinned `git reset --hard origin/master` pull
  (see the "Alpaca plugin DLL copy" entry above) and may not be byte-identical to the
  fresh clone read here, though `AlpacaBrokerage.HistoryProvider.cs` is a recently-added,
  stable part of the plugin's public interface and not one of the files touched by this
  project's own `ValidateSubscription()`/`GetCashBalance()` patches.
- **Required manual verification on the Pi (this session has no SSH access to the Pi and
  cannot run this):** after `git pull`, run `make force-rebalance` (existing target — see
  `Makefile`) and confirm via `journalctl -u lean-trader -f` (or the equivalent log tail)
  that `[AbsMom] SPY 12-month return` and `[AbsMom] AGG 12-month return` both log real
  non-blank percentages, and that a representative sample of `[RelMom] <ticker>: <return>`
  lines resolve rather than logging "insufficient history — excluded" for tickers outside
  the old 21-symbol demo set.
- **Impact:** This is the root cause of every monthly rebalance failing since deployment;
  no rebalance has actually placed an order via the scheduled path. Restarting
  `lean-trader` is required to pick up the new `config.json` — see the exact commands
  below (this session does not restart services itself, per project convention).

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

**Deviation: API endpoints regressed after LEAN output naming changed to short class name**
- **Date discovered:** 2026-08-19
- **Reason:** The dashboard displayed stale July 1 data (stuck orders, blank
  Portfolio Value/P&L, "No open positions") for weeks despite the trading
  engine running correctly and completing real rebalances with confirmed
  order fills on 2026-08-14 and 2026-08-17. Live filesystem inspection on
  the Pi on 2026-08-19 found the root cause: `web/routes/api.py` hardcoded
  `algo_name = "PiAiTrader.Strategies.DualMomentumV2"` in `positions()`,
  `trades()`, and `performance()`, matching the fully-qualified type name
  documented in the entry above. `find /opt/lean-engine/Launcher/bin/Release
  -maxdepth 3 -iname "*DualMomentumV2*"` showed every current result file
  (2026-07-30 through 2026-08-20) living under `Results/` with the *short*
  name only, e.g. `Results/DualMomentumV2.json`,
  `Results/DualMomentumV2-2026-08-17-order-events.json`,
  `Results/DualMomentumV2-2026-08-19_10minute.json`. No file anywhere on
  disk used the `PiAiTrader.Strategies.` prefix. The hardcoded `algo_name`
  therefore matched zero files, all three endpoints fell through to their
  "no file found" branch, and the frontend kept rendering whatever data it
  had last successfully loaded.
- **Change:** Updated `algo_name` in `positions()` to `"DualMomentumV2"`, and
  updated the glob patterns in `trades()` (`**/DualMomentumV2-*-order-events.json`)
  and `performance()` (`**/DualMomentumV2-*_10minute.json`) to drop the
  namespace prefix. `direct_path` and the recursive fallback-glob logic were
  left unchanged — the existing `**` glob already reaches one level deeper
  into `Results/` from `LEAN_RESULTS_DIR` once `algo_name` is corrected, so
  no `LEAN_RESULTS_DIR` change or new directory-traversal logic was needed.
  Docstrings/comments in all three endpoints were updated to describe the
  short-name convention as current fact, with a detailed comment added in
  `positions()` explaining the naming-convention history for future readers.
- **Note:** The exact date the LEAN naming convention changed was not
  directly observed — only that current files (2026-07-30 onward) all use
  the short name, and no namespaced-prefix files remain on disk to date the
  transition more precisely.
- **Verification:** Pending — Lord Sal will confirm on the live Pi after deploy.

**Deviation: positions() glob matched chart snapshots instead of live-state file**
- **Date discovered:** 2026-08-19
- **Reason:** `/api/positions` returned empty positions and $0 portfolio value
  despite a confirmed live open position (XLK, qty 1) sitting in the actual
  LEAN live-state file. Confirmed via curl + direct file inspection on the
  Pi: `positions()`'s fallback glob, `results_dir.glob(f"**/*{algo_name}*.json")`,
  wildcards on *both* sides of `algo_name`, so it matched every `.json` file
  merely containing "DualMomentumV2" in its name — not just the live-state
  file, but every chart snapshot LEAN writes (e.g.
  `DualMomentumV2-<date>_minute.json`, `DualMomentumV2-<date>_10minute.json`,
  `DualMomentumV2-<date>-<hour>_second_Strategy Equity.json`, etc.). LEAN
  rewrites these chart files far more frequently than the live-state file,
  so `max(candidates, key=mtime)` often selected a chart snapshot instead of
  the live-state file. Chart JSON has no top-level `holdings` key, so
  `data.get("holdings", {})` silently returned `{}`, producing empty
  positions and $0 values with no error. Confirmed directly on the Pi:
  `Results/DualMomentumV2.json` (the live-state file, correct XLK qty-1
  holding) had mtime `1787195918`, while
  `Results/DualMomentumV2-2026-08-20_minute.json` and sibling chart files
  had mtime `1787196398` — 480 seconds newer — so the chart file won the
  `max()` comparison.
- **Change:** Changed the fallback glob in `positions()` from
  `f"**/*{algo_name}*.json"` (wildcard on both sides) to `f"**/{algo_name}.json"`
  (exact filename, still recursive so it still reaches `Results/` regardless
  of depth). This matches only files literally named `DualMomentumV2.json`,
  excluding every chart/order-event/log variant. The double-wildcard glob
  was originally written to "handle date-stamped variants," but no such
  variant of the live-state file itself exists in practice — only
  chart/order-event/log files carry date stamps — so the wildcard was doing
  nothing but accidentally matching those other files instead. `max(...,
  key=mtime)` is kept as a no-op safety net for any future scenario with
  multiple stale live-state copies. `trades()` and `performance()` were
  unaffected (confirmed working via curl) and were not changed.
- **Verification:** Pending — Lord Sal will confirm on the live Pi after deploy.

**Deviation: positions() direct_path fast-path matched a stale March 2026 crash-run artifact**
- **Date discovered:** 2026-08-19
- **Reason:** `/api/positions` still returned empty positions and $0 portfolio
  value even after the 2026-08-19 fallback-glob fix above (wildcard-both-sides
  changed to exact-name glob). Curl confirmed the endpoint returned a clean
  success response (no `message` field, `cash_usd`/`total_portfolio_value`
  present) — meaning it found *a* file and parsed it without error, just not
  the right one. Direct file inspection on the Pi on 2026-08-19 found two
  files with the exact literal name `DualMomentumV2.json`:
  `Launcher/bin/Release/DualMomentumV2.json` (mtime `1772841002`) and
  `Launcher/bin/Release/Results/DualMomentumV2.json` (mtime `1787283038`).
  The first is a stale artifact from the algorithm's very first run on
  2026-03-06, which crashed immediately with "Algorithm type name not found"
  (an unrelated, long-since-fixed config issue from initial setup) and wrote
  an empty `holdings:{}` / `cash.amount:0.0` state file directly into
  `Release/` before ever reaching `Results/`. It had sat there unused and
  unnoticed for ~166 days. `positions()`'s `direct_path` fast-path
  (`results_dir / f"{algo_name}.json"`) resolved to exactly that stale flat
  file, and since it existed, that branch was taken unconditionally — before
  the fallback glob (which was correctly fixed on 2026-08-19 to do an
  exact-name recursive match) was ever reached. The fallback fix was correct
  but was dead code as long as the stale file existed at that exact flat
  path: `direct_path` always won.
- **Change:** Removed the `direct_path` fast-path special case from
  `positions()` entirely. It now always builds the recursive exact-name glob
  (`results_dir.glob(f"**/{algo_name}.json")`) and selects the newest match by
  mtime. This naturally prefers `Results/DualMomentumV2.json` (mtime
  `1787283038`, current) over the stale flat-directory copy (mtime
  `1772841002`), and remains correct if LEAN's output location changes again
  in the future, since it no longer hardcodes an assumption about which
  directory depth is "primary." The stale file itself
  (`Launcher/bin/Release/DualMomentumV2.json`) was intentionally left in
  place — only the file-selection logic in `api.py` changed; cleaning up the
  stale file on disk is a separate manual decision for Lord Sal.
- **Verification:** Pending — Lord Sal will confirm via curl and the live
  dashboard after deploy.

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

*Last updated: 2026-08-14*
