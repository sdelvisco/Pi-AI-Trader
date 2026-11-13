# Pi-AI-Trader

An algorithmic trading system running on a Raspberry Pi 4, powered by the [QuantConnect LEAN Engine](https://github.com/QuantConnect/Lean) and [Alpaca Markets](https://alpaca.markets) as the broker. The live strategy (`DualMomentumV2`) is written in C#, with an optional headline-sentiment layer (Azure AI Foundry LLM scoring) that adjusts position sizes. A Flask web dashboard provides local-network monitoring and control. All services are managed by systemd.

---

## Hardware

| Component | Details |
|---|---|
| Board | Raspberry Pi 4 (4 GB RAM) |
| Case / enclosure | Argon ONE M.2 |
| Storage | SATA SSD via internal USB 3.0 (M.2 slot) |
| Operating system | Raspberry Pi OS Lite 64-bit (Bookworm) |
| Network | Ethernet, static hostname `tradingpi` |
| Access | Headless SSH from Windows 11 |

---

## Project Structure

```
Pi-AI-Trader/
├── setup/                          # Numbered bash setup scripts (run in order)
│   ├── 01_os_config.sh             # OS post-install: hostname, timezone, SSH, UFW, fail2ban
│   ├── 02_argon_driver.sh          # Argon ONE M.2 fan driver and I2C
│   ├── 03_ssd_boot.sh              # EEPROM bootloader update, USB boot, rpi-clone
│   ├── 04_dotnet.sh                # .NET 10 SDK (ARM64) - LEAN targets net10.0
│   ├── 05_python.sh                # Python 3 venv and pip dependencies
│   ├── 06_lean_build.sh            # Build LEAN and Alpaca plugin from source at pinned commits (30-60 min on Pi)
│   └── 07_verify.sh                # Final verification of all components
│
├── scripts/                        # Operational helper scripts
│   ├── render_lean_config.py       # Renders config/lean_config.template.json → LEAN config.json (called by `make deploy`)
│   ├── fix_lean_config.sh          # Legacy repair tool for a hand-edited Launcher/config.json (see below)
│   └── health_check.sh             # Daily lean-trader health check (cron)
│
├── config/                         # Configuration templates (commit-safe)
│   ├── lean_config.template.json   # LEAN Engine config template (${VAR} credential placeholders)
│   ├── alpaca_credentials.template # Alpaca API key template → /etc/tradingpi/alpaca.env
│   ├── azure_credentials.template  # Azure AI Foundry LLM template → /etc/tradingpi/azure.env
│   └── notifications.template      # Email/SMS notification settings template
│
├── services/                       # systemd unit files and the headline news pipeline
│   ├── lean-trader.service         # LEAN Engine live trading service
│   ├── lean-web.service            # Flask web interface service
│   ├── headline-news-pipeline.service  # Headline news pipeline service (Phase 2 AI layer)
│   ├── HeadlineNewsPipeline/       # Standalone headline-news pipeline (Phase 2 AI layer, step 2)
│   └── HeadlineNewsPipeline.Tests/ # xUnit tests for the above
│
├── web/                            # Flask web dashboard
│   ├── __init__.py
│   ├── app.py                      # Application factory and entry point (Flask-SocketIO server)
│   ├── routes/
│   │   ├── __init__.py
│   │   ├── dashboard.py            # HTML page routes (/, /logs, /performance, /settings)
│   │   ├── api.py                  # REST API endpoints (/api/*)
│   │   └── logs.py                 # SocketIO live log streaming (no HTTP routes)
│   ├── templates/                  # Jinja2 HTML templates
│   │   ├── base.html
│   │   ├── dashboard.html
│   │   ├── logs.html
│   │   ├── performance.html
│   │   └── settings.html
│   └── static/
│       ├── css/dashboard.css
│       └── js/dashboard.js
│
├── strategies/                     # Trading algorithms
│   ├── csharp/
│   │   ├── DualMomentumV2.cs       # Main trading strategy
│   │   ├── DualMomentumV2.csproj   # Strategy project file (compiles DualMomentumV2.cs only)
│   │   ├── ExampleAlgorithm.cs     # Minimal C# template (not compiled by DualMomentumV2.csproj)
│   │   ├── bin/Release/net10.0/    # Compiled strategy DLLs (git-ignored build output)
│   │   ├── Intelligence/           # Phase 2 AI layer: LLM sentiment module + Signal Aggregator/PositionSizer
│   │   └── Intelligence.Tests/     # xUnit tests for the above
│   └── python/
│       └── example_algorithm.py    # Minimal Python template (not deployed)
│
│   Note: LEAN engine itself is built at /opt/lean-engine/ (outside this repo)
│   Configuration: /opt/lean-engine/Launcher/bin/Release/config.json
│
├── .github/workflows/deploy.yml    # GitHub Actions: on push to main, SSH to the Pi and run `make all`
├── venv/                           # Python virtual environment (git-ignored)
├── Makefile                        # build / deploy / verify / all / force-rebalance
├── Pi-AI-Trader.sln                # Solution file (references DualMomentumV2.csproj only)
├── requirements.txt                # Python dependencies (currently empty - see Step 5)
├── CLAUDE.md                       # Instructions for Claude Code sessions (branching, commit style)
├── DEVIATIONS.md                   # Log of implementation deviations and incident write-ups
├── LICENSE
├── .gitignore
└── README.md
```

---

## Prerequisites

Before running any setup scripts:

1. **Flash Raspberry Pi OS Lite 64-bit** to an SD card using the [Raspberry Pi Imager](https://www.raspberrypi.com/software/).
   - In the Imager's advanced options (gear icon): set hostname to `tradingpi`, enable SSH, and configure your Wi-Fi or plan to use Ethernet.
   - Create the user **`pi-admin`**. The systemd units (`User=pi-admin`), the Makefile's sudoers rules and `lean-web.service`'s paths all assume this user and a clone at `/home/pi-admin/Pi-AI-Trader`.

2. **Boot the Pi** from the SD card and SSH in from Windows:
   ```
   ssh pi-admin@tradingpi.local
   ```

3. **Clone this repository** into the home directory on the Pi:
   ```bash
   cd ~
   git clone https://github.com/sdelvisco/Pi-AI-Trader.git
   cd Pi-AI-Trader
   ```

4. **Alpaca account**: Create a free account at [alpaca.markets](https://alpaca.markets). Generate a Paper Trading API key pair from the dashboard.

5. **Azure AI Foundry deployment** (optional, only for the headline news pipeline): you need an endpoint, API key and deployment name for `config/azure_credentials.template`.

6. **Twilio account** (optional): `config/notifications.template` has SMTP and Twilio settings. Note that no code in this repo currently reads those settings or sends notifications.

---

## Setup Instructions

Run each script in order. Each script is self-contained and explains what it does via verbose comments. **All scripts must be run as root** (`sudo bash <script>`).

### Step 1 — OS Post-Install Configuration
```bash
sudo bash setup/01_os_config.sh
```
Configures hostname (`tradingpi`), timezone, locale, NTP, SSH, UFW firewall, and fail2ban. SSH password authentication is **not** disabled automatically: the script leaves `PasswordAuthentication no` commented out so you can add your SSH key first and then uncomment it.

### Step 2 — Argon ONE M.2 Driver
```bash
sudo bash setup/02_argon_driver.sh
```
Enables I2C, installs the official Argon ONE fan controller daemon, and writes a default temperature-based fan curve. **A reboot is required after this step.**
```bash
sudo reboot
# Wait ~30 seconds, then SSH back in
ssh pi-admin@tradingpi.local
cd Pi-AI-Trader
```

### Step 3 — SSD Boot Configuration
```bash
sudo bash setup/03_ssd_boot.sh
```
Updates the EEPROM bootloader to prefer USB boot (the SSD), then installs `rpi-clone` to migrate the SD card contents to the SSD.

After the script completes, **clone the SD card to the SSD** and switch to SSD boot:
```bash
# Identify your SSD device (usually /dev/sda)
lsblk

# Clone SD → SSD
sudo rpi-clone sda

# Power off, remove SD card, power back on
sudo poweroff
```
SSH back in and verify you are booting from the SSD:
```bash
findmnt / | grep sda   # should show your SSD device
```

### Step 4 — .NET 10 SDK
```bash
sudo bash setup/04_dotnet.sh
```
Downloads the latest .NET 10.0 SDK tarball for ARM64 from Microsoft's build servers, extracts it to `/usr/local/share/dotnet`, symlinks `/usr/local/bin/dotnet`, configures PATH and disables telemetry. Runs a smoke test to confirm the runtime is working.

### Step 5 — Python 3 Environment
```bash
sudo bash setup/05_python.sh
```
Creates a Python virtual environment at `venv/` and runs `pip install -r requirements.txt`.

**Known gap:** the committed `requirements.txt` is currently **empty**. The script only writes its built-in default dependency list when `requirements.txt` does not exist, so on a fresh clone it installs nothing. The web dashboard imports `flask` and `flask_socketio`, so those packages must be present in `venv/` for `lean-web` to start.

### Step 6 — Build LEAN from Source
```bash
sudo bash setup/06_lean_build.sh
```
Fetches LEAN and the Alpaca brokerage plugin from GitHub at the commits pinned in the script (`LEAN_PINNED_COMMIT` / `ALPACA_PINNED_COMMIT`), patches `AlpacaBrokerage.cs` to comment out `ValidateSubscription()`, builds both projects from source, installs them to `/opt/lean-engine/`, and sets ownership to `pi-admin`. This step takes 30–60 minutes on Raspberry Pi 4.

### Step 7 — Verify Everything
```bash
sudo bash setup/07_verify.sh
```
Runs a comprehensive check of all installed components and prints a PASS / WARN / FAIL summary. Resolve any failures before proceeding.

After Step 7, set up credentials (see [Credentials Setup](#credentials-setup)), configure passwordless sudo for the Makefile (see [Deployment](#deployment-makefile)), and run `make all` to build the strategy, render the LEAN config and start trading.

---

## LEAN Configuration

There is no separate "configure LEAN" script. The LEAN config is generated from the committed template every time you run `make deploy` (or `make all`):

```bash
make deploy
```

The deploy target runs:
```bash
sudo python3 scripts/render_lean_config.py \
    config/lean_config.template.json \
    /etc/tradingpi/alpaca.env \
    /opt/lean-engine/Launcher/bin/Release/config.json
```

`scripts/render_lean_config.py`:
- Reads `config/lean_config.template.json`
- Substitutes `${VAR}` placeholders (`${ALPACA_KEY_ID}`, `${ALPACA_SECRET_KEY}`, `${ALPACA_PAPER_TRADING}`) with values from `/etc/tradingpi/alpaca.env` (LEAN does no environment-variable substitution of its own)
- Leaves any placeholder it cannot resolve unchanged and prints a warning naming the missing variables (never their values)
- Validates the result as JSON, writes it to `/opt/lean-engine/Launcher/bin/Release/config.json`, and sets it to `chmod 600` because it now contains credentials

`make deploy` then checks the rendered file and **aborts without restarting** `lean-trader` if any of these fail:
- `algorithm-type-name` is `DualMomentumV2`
- `algorithm-location` contains `DualMomentumV2.dll`
- `live-mode` is `true`
- `alpaca-api-key`, `alpaca-api-secret` and `alpaca-paper-trading` resolved to real values (not empty, not a leftover `${...}`)

To change LEAN settings, edit `config/lean_config.template.json` and re-run `make deploy`. Do not hand-edit the deployed `config.json`; the next deploy overwrites it.

### Legacy: `scripts/fix_lean_config.sh`

This script repairs a hand-edited config file corrupted by `sed` (comments, trailing commas, duplicate keys), forces `"algorithm-type-name": "DualMomentumV2"` and `"algorithm-location": "DualMomentumV2.dll"`, backs the original up to `config.json.broken-may5`, and validates the result with `python3 -m json.tool`.

```bash
bash scripts/fix_lean_config.sh   # prefix with sudo if the file is owned by root
```

**Note:** the script operates on `/opt/lean-engine/Launcher/config.json`. That file is **not** the one `lean-trader` reads; LEAN reads `/opt/lean-engine/Launcher/bin/Release/config.json`, which `make deploy` regenerates from the template (see DEVIATIONS.md, "Makefile `deploy` target wrote config.json to the wrong directory"). In normal operation, re-running `make deploy` is the way to recover a broken config.

### Important Notes

- **Algorithm name**: the config uses the short class name `"algorithm-type-name": "DualMomentumV2"` with `"algorithm-location": "DualMomentumV2.dll"` (the class itself lives in the `PiAiTrader.Strategies` namespace). `make deploy` enforces both values.
- **Config location**: LEAN reads `config.json` from `/opt/lean-engine/Launcher/bin/Release/`, the service's `WorkingDirectory`.
- **LEAN location**: Built at `/opt/lean-engine/` (not in project directory)
- **.NET version**: Requires .NET 10 (LEAN targets net10.0)

---

## Deployment (Makefile)

Run these targets from the repo root on the Pi (`~/Pi-AI-Trader`).

| Target | What it does |
|---|---|
| `make build` | Runs `dotnet build strategies/csharp/DualMomentumV2.csproj -c Release`, then fails if either `DualMomentumV2.dll` or `PiAiTrader.Intelligence.dll` is missing from `strategies/csharp/bin/Release/net10.0/`. |
| `make deploy` | `sudo cp`s both DLLs to `/opt/lean-engine/Launcher/bin/Release/`, renders `config.json` via `scripts/render_lean_config.py`, checks the config (see [LEAN Configuration](#lean-configuration)), then `sudo systemctl restart lean-trader`. Aborts before the restart if any config check fails. |
| `make verify` | Polls `journalctl -u lean-trader` for up to 60 s for the line `DualMomentumV2 Initialized`. Exits 0 on success; on timeout prints the last 40 journal lines and exits 1. |
| `make all` | `build` → `deploy` → `verify` in sequence. |
| `make force-rebalance` | Creates `/tmp/force_rebalance` and tails `/opt/lean-engine/Launcher/bin/Release/log.txt`, filtered to rebalance/order/fill/error lines (Ctrl+C to stop). The strategy checks for this file every minute while the market is open, deletes it, and runs a full rebalance immediately. Outside market hours the file waits until the next open minute. |

**Both DLLs must be deployed.** `DualMomentumV2.csproj` has a `ProjectReference` to `Intelligence/Intelligence.csproj`, so the build outputs `PiAiTrader.Intelligence.dll` next to `DualMomentumV2.dll`. If only the strategy DLL is copied, LEAN fails to load it.

**Passwordless sudo is required** for `make deploy` (and therefore for `make all` and GitHub Actions). The exact `/etc/sudoers.d/pi-admin-lean` rules are listed in the header comment of the [`Makefile`](Makefile): the two DLL copies, `render_lean_config.py`, and `systemctl restart`/`start lean-trader`.

### Automatic deployment (GitHub Actions)

[`.github/workflows/deploy.yml`](.github/workflows/deploy.yml) runs on every push to `main`. It SSHes into the Pi as `pi-admin`, runs `git fetch origin main && git checkout main && git reset --hard origin/main` in `~/Pi-AI-Trader`, then runs `make all`. A final step reports whether `lean-trader` is active. It requires a `PI_SSH_KEY` repository secret whose public key is in `pi-admin`'s `~/.ssh/authorized_keys`, plus the sudoers rules above.

The workflow only rebuilds and restarts `lean-trader`. It does **not** restart `lean-web` or rebuild/restart `headline-news-pipeline`; see below.

---

## Credentials Setup

Credentials are **never stored in this repository**. They are placed in root-readable environment files on the Pi.

```bash
# Create the credentials directory
sudo mkdir -p /etc/tradingpi

# Set up Alpaca credentials
sudo cp config/alpaca_credentials.template /etc/tradingpi/alpaca.env
sudo nano /etc/tradingpi/alpaca.env          # fill in your keys
sudo chmod 600 /etc/tradingpi/alpaca.env
sudo chown root:root /etc/tradingpi/alpaca.env

# Set up notification settings (email and SMS)
sudo cp config/notifications.template /etc/tradingpi/notifications.env
sudo nano /etc/tradingpi/notifications.env   # fill in SMTP/Twilio credentials
sudo chmod 600 /etc/tradingpi/notifications.env

# Set up Azure AI Foundry credentials (needed by the headline news pipeline
# service's LLM scoring — see services/headline-news-pipeline.service)
sudo cp config/azure_credentials.template /etc/tradingpi/azure.env
sudo nano /etc/tradingpi/azure.env           # fill in endpoint/key/deployment
sudo chmod 600 /etc/tradingpi/azure.env
sudo chown root:root /etc/tradingpi/azure.env

# Persistent runtime state for the headline news pipeline (dedup high-water
# mark, JSON-lines Signal output, and the Signal Aggregator mode file —
# distinct from /etc/tradingpi's credential-only role)
sudo mkdir -p /var/lib/tradingpi/headline-news-pipeline
sudo chown pi-admin:pi-admin /var/lib/tradingpi/headline-news-pipeline

# Generate a Flask secret key and add it
FLASK_SECRET=$(python3 -c "import secrets; print(secrets.token_hex(32))")
echo "FLASK_SECRET_KEY=${FLASK_SECRET}" | sudo tee /etc/tradingpi/web.env > /dev/null
sudo chmod 600 /etc/tradingpi/web.env
```

Environment variables read by the code:

| File | Variables | Used by |
|---|---|---|
| `/etc/tradingpi/alpaca.env` | `ALPACA_KEY_ID`, `ALPACA_SECRET_KEY`, `ALPACA_PAPER_TRADING` (template also has `ALPACA_BASE_URL`, `ALPACA_DATA_URL`) | `render_lean_config.py`, `lean-trader`, `lean-web`, `headline-news-pipeline` |
| `/etc/tradingpi/azure.env` | `AZURE_LLM_ENDPOINT`, `AZURE_LLM_API_KEY`, `AZURE_LLM_DEPLOYMENT_NAME` | `headline-news-pipeline` |
| `/etc/tradingpi/web.env` | `FLASK_SECRET_KEY`; optional overrides `LEAN_RESULTS_DIR` (default `/opt/lean-engine/Launcher/bin/Release`) and `AGGREGATOR_CONFIG_PATH` (default `/var/lib/tradingpi/headline-news-pipeline/aggregator-config.json`) | `lean-web` |
| — | `HEADLINE_PIPELINE_STATE_DIR` (optional, default `/var/lib/tradingpi/headline-news-pipeline`) | `headline-news-pipeline` |

---

## Running the Services

### Install systemd units
```bash
sudo cp services/lean-trader.service /etc/systemd/system/
sudo cp services/lean-web.service    /etc/systemd/system/
sudo cp services/headline-news-pipeline.service /etc/systemd/system/
sudo systemctl daemon-reload
```

The copies in `services/` are not what systemd reads. After changing a unit file in the repo, copy it to `/etc/systemd/system/` again and run `sudo systemctl daemon-reload`.

The `lean-trader.service` runs LEAN natively via .NET (no Docker required):

```ini
User=pi-admin
WorkingDirectory=/opt/lean-engine/Launcher/bin/Release
ExecStart=/usr/local/bin/dotnet /opt/lean-engine/Launcher/bin/Release/QuantConnect.Lean.Launcher.dll
```

### Start the web dashboard
```bash
sudo systemctl enable lean-web
sudo systemctl start lean-web

# View logs
sudo journalctl -u lean-web -f
```
Access the dashboard from Windows: **http://tradingpi.local:5000**

Pages: `/` (dashboard), `/logs` (live log stream over SocketIO), `/performance`, `/settings` (includes the Signal Aggregator mode selector). JSON endpoints live under `/api/` (`status`, `positions`, `trades`, `performance`, `health`, `logs/download`, `control/pause`, `control/resume`, `control/stop`, `aggregator-mode`); see the docstring at the top of [`web/routes/api.py`](web/routes/api.py).

**No autoreload.** `lean-web` runs `python3 -m web.app` with `debug=False, use_reloader=False`, so it does not pick up code changes on its own. After a `git pull` that touches `web/`, restart it:
```bash
sudo systemctl restart lean-web
```

### Start live trading
```bash
# Ensure ALPACA_PAPER_TRADING=true in /etc/tradingpi/alpaca.env until validated
make all                       # build, deploy DLLs + config.json, restart, verify
sudo systemctl enable lean-trader

# Follow trading logs
sudo journalctl -u lean-trader -f
```

### Start the headline news pipeline (Phase 2 AI layer)
This is a standalone service, isolated by design from lean-trader/DualMomentumV2
— see [`services/headline-news-pipeline.service`](services/headline-news-pipeline.service)
for the full rationale. Every 4 hours it polls Alpaca's News API for headlines on
DualMomentumV2's trading universe, scores each headline/ticker pair with the
Azure-backed `LlmSentimentModule`, and appends the resulting signals to
`/var/lib/tradingpi/headline-news-pipeline/signals.jsonl`. It never talks to
lean-trader directly; `DualMomentumV2` reads that file at each rebalance (see
[Signal Aggregator](#signal-aggregator-sentiment-adjusted-sizing)). If the
pipeline is stopped or the file is missing, the strategy falls back to equal weights.
```bash
dotnet build services/HeadlineNewsPipeline/HeadlineNewsPipeline.csproj -c Release

sudo systemctl enable headline-news-pipeline
sudo systemctl start headline-news-pipeline

# Follow pipeline logs
sudo journalctl -u headline-news-pipeline -f
```
`make` does not build this service. After pulling changes to `services/HeadlineNewsPipeline/` or `strategies/csharp/Intelligence/`, rebuild with the `dotnet build` command above and run `sudo systemctl restart headline-news-pipeline`.

---

## Strategy Operations (DualMomentumV2)

Values below are taken from [`strategies/csharp/DualMomentumV2.cs`](strategies/csharp/DualMomentumV2.cs).

- **Rebalance schedule**: first trading day of each month at **10:00 ET**. Uses `Schedule.On(DateRules.MonthStart(SPY), TimeRules.At(RebalanceHourEt, RebalanceMinuteEt), ...)`, with `RebalanceHourEt = 10` and `RebalanceMinuteEt = 0`. Weekends and NYSE holidays roll forward to the next open day. The schedule is hard-coded, so changing it means editing the code and running `make all`; there is no config parameter for it.
- **Order type**: every order (rebalance, stop-loss, drawdown halt, force-rebalance) is an immediate `MarketOrder()` with Day time-in-force (Alpaca `time_in_force=day`). `MarketOnCloseOrder()` is no longer used: on 2026-10-01, MOC orders submitted at 15:40 ET expired unfilled on Alpaca paper. See DEVIATIONS.md for the incident and the 2026-10-06 revert to monthly.
- **Sizing**: fractional shares (rounded down to 0.0001), investing roughly 100% of the account minus a 1% cash reserve.
- **Startup log**: after `=== DualMomentumV2 Initialized ===` (the line `make verify` looks for), the algorithm logs `Rebalance schedule: first trading day of month 10:00 ET, Day market orders`.
- **Manual rebalance**: `make force-rebalance` (see [Deployment](#deployment-makefile)).

### Signal Aggregator (sentiment-adjusted sizing)

The Signal Aggregator (Phase 2, Step 3) lives in `strategies/csharp/Intelligence/` (`SignalsFileReader`, `AggregatorConfigReader`, `SignalAggregator`, `PositionSizer`, `AggregationMode`, `AggregatedSignal`, `AggregatorConfig`). It is compiled into `PiAiTrader.Intelligence.dll` and called from `DualMomentumV2.ComputeSentimentAdjustedWeights()` during each rebalance:

1. Only the **risk-on top-N branch** uses it. It never affects which tickers the momentum ranking selects, and is never used for the defensive 100% AGG path.
2. `AggregatorConfigReader` reads the active mode fresh on every rebalance from `/var/lib/tradingpi/headline-news-pipeline/aggregator-config.json`. The modes are `WeightedVote`, `ConfidenceWeighted`, `ConsensusOnly` and `CapitalSplit`. If the file is missing or invalid, it defaults to `CapitalSplit`. The mode is set from the dashboard's `/settings` page (`POST /api/aggregator-mode`), so a change takes effect at the next rebalance without restarting `lean-trader`.
3. For each selected ticker, `SignalsFileReader` reads the signals from the last **7 days** in `signals.jsonl`, and `SignalAggregator` combines them into one `AggregatedSignal`.
4. `PositionSizer.ComputeAdjustedWeights()` adjusts each ticker's equal base weight by at most **±50%**, then renormalizes so the total allocation is unchanged. A ticker with no recent signals keeps exactly its base weight.
5. Any exception falls back to exact equal weights and is logged as `[SignalAggregator] Failed to compute sentiment-adjusted weights`. Each successful rebalance logs `[SignalAggregator] Active mode: ...` and one line per ticker with base weight, adjusted weight, signal count, score and confidence.

---

## Tests

There are two xUnit test projects. Neither is in `Pi-AI-Trader.sln`, so run each `.csproj` directly. Both projects link against `QuantConnect.Logging.dll` from `/opt/lean-engine/Launcher/bin/Release/`, so they need the local LEAN build and must run on the Pi.

```bash
# Intelligence layer (LLM module, Signal Aggregator, PositionSizer, file readers) — mocked HTTP only
dotnet test strategies/csharp/Intelligence.Tests/Intelligence.Tests.csproj --filter Category!=LiveSmoke

# One opt-in live smoke test against the real Azure AI Foundry endpoint (needs Azure credentials)
dotnet test strategies/csharp/Intelligence.Tests/Intelligence.Tests.csproj --filter Category=LiveSmoke

# Headline news pipeline — mocked HTTP only
dotnet test services/HeadlineNewsPipeline.Tests/HeadlineNewsPipeline.Tests.csproj
```

---

## Writing Strategies

### C# strategies
The main strategy is [`DualMomentumV2.cs`](strategies/csharp/DualMomentumV2.cs). `DualMomentumV2.csproj` compiles **only** that file (`EnableDefaultCompileItems=false`), so other `.cs` files in `strategies/csharp/`, such as [`ExampleAlgorithm.cs`](strategies/csharp/ExampleAlgorithm.cs), are not built or deployed.

```bash
# Compile, deploy and verify the strategy
cd ~/Pi-AI-Trader
make all
```

### Python strategies
[`strategies/python/example_algorithm.py`](strategies/python/example_algorithm.py) is a minimal template. Nothing in the build/deploy pipeline or the config template runs Python algorithms (`algorithm-language` is `CSharp`). The template's header comments mention `lean backtest`, but that LEAN CLI command is not installed on this setup (see below).

---

## Useful Commands

```bash
# Check service status
sudo systemctl status lean-trader lean-web headline-news-pipeline

# Follow all trading logs in real time
sudo journalctl -u lean-trader -f

# Restart the dashboard after a git pull (no autoreload)
sudo systemctl restart lean-web

# Run the health check manually (see the script header for the daily cron line)
~/Pi-AI-Trader/scripts/health_check.sh

# Check Argon ONE fan status
sudo systemctl status argonone
sudo argonone-config          # customise fan curve interactively

# Check CPU temperature
cat /sys/class/thermal/thermal_zone0/temp | awk '{printf "%.1f°C\n", $1/1000}'

# Activate the Python venv manually
source venv/bin/activate

# Check firewall rules
sudo ufw status verbose

# Check fail2ban SSH ban list
sudo fail2ban-client status sshd
```

`scripts/health_check.sh` checks that `lean-trader` is active, that it logged no error-level journal entries in the past 24 hours, and that `DualMomentumV2 Initialized` appears in the current run. It logs to `/var/log/pi-ai-trader/health.log`, and writes failures to `/var/log/pi-ai-trader/alerts.log`. Alerts are only written to that file; no notification hook is wired up.

---

## Security Notes

- **API keys** are stored in `/etc/tradingpi/*.env` with `chmod 600`. They are never in this repository. The rendered `/opt/lean-engine/Launcher/bin/Release/config.json` contains the Alpaca credentials in plaintext and is also set to `chmod 600`.
- **SSH password authentication** must be disabled manually after you add your SSH key (uncomment `PasswordAuthentication no`, see `01_os_config.sh`).
- The **web dashboard** binds to all interfaces (`0.0.0.0`) on port 5000. `01_os_config.sh` opens `5000/tcp` in UFW with no source-address restriction, so the dashboard is reachable from anything that can reach the Pi. It is intended for local-network use only and must not be exposed to the internet.
- **Paper trading** is enabled by default (`ALPACA_PAPER_TRADING=true`). Only set this to `false` after thorough backtesting and paper trading validation.
- The `.gitignore` excludes credentials, LEAN data, the Python venv, and C# build artifacts.

---

## Key Implementation Differences

This implementation differs from standard LEAN deployment. See [`DEVIATIONS.md`](DEVIATIONS.md) for the full, dated log.

### Build from Source (Not LEAN CLI)
- **Why**: LEAN CLI requires paid QuantConnect subscription for local use
- **Method**: Fetch and build LEAN from GitHub source at a pinned commit
- **Location**: `/opt/lean-engine/` (system-level, not in project)
- **Build time**: 30–60 minutes on Raspberry Pi 4

### No Docker
- **Why**: Resource efficiency on Pi hardware (only 4GB RAM)
- **Method**: Native .NET execution
- **Benefit**: Lower memory footprint, easier debugging

### Alpaca Plugin Modifications
- **Clone**: Separate repository `QuantConnect/Lean.Brokerages.Alpaca`, pinned to a fixed commit in `06_lean_build.sh`
- **Patch required**: Comment out `ValidateSubscription()` call
  - This method checks for paid QuantConnect subscription
  - Always fails for free accounts
- **Symlink**: `/opt/Lean → /opt/lean-engine` for hardcoded paths

### .NET 10 Requirement
- **README originally said**: .NET 8
- **Actually requires**: .NET 10 (a GA Long-Term Support release since November 2025)
- **Reason**: LEAN targets net10.0; all `.csproj` files in this repo target `net10.0`
- **Install**: Custom tarball installation from Microsoft's build servers (`04_dotnet.sh`), not via package manager

---

## License

[GNU General Public License v3.0](LICENSE)
