# Pi-AI-Trader

An algorithmic trading system running on a Raspberry Pi 4, powered by the [QuantConnect LEAN Engine](https://github.com/QuantConnect/Lean) and [Alpaca Markets](https://alpaca.markets) as the broker. Strategies are written in C# and Python. A Flask web dashboard provides local-network monitoring and control. All services are managed by systemd.

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
│   ├── 01_os_config.sh             # OS post-install: hostname, timezone, SSH, UFW
│   ├── 02_argon_driver.sh          # Argon ONE M.2 fan driver and I2C
│   ├── 03_ssd_boot.sh              # EEPROM bootloader update, USB boot, rpi-clone
│   ├── 04_dotnet.sh                # .NET 10 SDK (ARM64) - LEAN master branch requires .NET 10
│   ├── 05_python.sh                # Python 3 venv and pip dependencies
│   ├── 06_lean_build.sh            # Build LEAN and Alpaca plugin from source (30-60 min on Pi)
│   ├── 07_verify.sh                # Final verification of all components
│   └── 08_configure_lean.sh        # Configure LEAN for Alpaca paper trading
│
├── config/                         # Configuration templates (commit-safe)
│   ├── lean_config.template.json   # LEAN Engine config template
│   ├── alpaca_credentials.template # Alpaca API key template
│   └── notifications.template      # Email/SMS notification settings template
│
├── services/                       # systemd unit files
│   ├── lean-trader.service         # LEAN Engine live trading service
│   └── lean-web.service            # Flask web interface service
│
├── web/                            # Flask web dashboard
│   ├── app.py                      # Application factory and entry point
│   ├── routes/
│   │   ├── dashboard.py            # HTML page routes
│   │   └── api.py                  # REST API endpoints (/api/*)
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
│   │   ├── DualMomentumV2.csproj   # Strategy project file
│   │   └── bin/Release/net10.0/    # Compiled strategy DLLs
│   └── python/
│       └── example_algorithm.py
│
│   Note: LEAN engine itself is built at /opt/lean-engine/ (outside this repo)
│   Configuration: /opt/lean-engine/Launcher/bin/Release/config.json
│
├── venv/                           # Python virtual environment (git-ignored)
├── requirements.txt                # Python dependencies
├── .gitignore
└── README.md
```

---

## Prerequisites

Before running any setup scripts:

1. **Flash Raspberry Pi OS Lite 64-bit** to an SD card using the [Raspberry Pi Imager](https://www.raspberrypi.com/software/).
   - In the Imager's advanced options (gear icon): set hostname to `tradingpi`, enable SSH, and configure your Wi-Fi or plan to use Ethernet.

2. **Boot the Pi** from the SD card and SSH in from Windows:
   ```
   ssh pi@tradingpi.local
   ```

3. **Clone this repository** onto the Pi:
   ```bash
   git clone https://github.com/sdelvisco/Pi-AI-Trader.git
   cd Pi-AI-Trader
   ```

4. **Alpaca account**: Create a free account at [alpaca.markets](https://alpaca.markets). Generate a Paper Trading API key pair from the dashboard.

5. **Twilio account** (optional, for SMS): Create an account at [twilio.com](https://www.twilio.com) and note your Account SID, Auth Token, and phone number.

---

## Setup Instructions

Run each script in order. Each script is self-contained and explains what it does via verbose comments. **All scripts must be run as root** (`sudo bash <script>`).

### Step 1 — OS Post-Install Configuration
```bash
sudo bash setup/01_os_config.sh
```
Configures hostname (`tradingpi`), timezone, locale, NTP, SSH hardening, UFW firewall, and fail2ban. After this step the Pi is hardened and ready for application software.

### Step 2 — Argon ONE M.2 Driver
```bash
sudo bash setup/02_argon_driver.sh
```
Enables I2C, installs the official Argon ONE fan controller daemon, and writes a default temperature-based fan curve. **A reboot is required after this step.**
```bash
sudo reboot
# Wait ~30 seconds, then SSH back in
ssh pi@tradingpi.local
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
Downloads and installs the .NET 10 SDK for ARM64 (required by LEAN master branch). Configures system PATH and disables telemetry. Runs a smoke test to confirm the runtime is working.

### Step 5 — Python 3 Environment
```bash
sudo bash setup/05_python.sh
```
Creates a Python virtual environment at `venv/`, generates `requirements.txt`, and installs all dependencies: Flask, Gunicorn, alpaca-py, pandas, Twilio, and others.

### Step 6 — Build LEAN from Source
```bash
sudo bash setup/06_lean_build.sh
```
Clones LEAN and the Alpaca brokerage plugin from GitHub, patches `ValidateSubscription()` to bypass paid subscription checks, builds both projects from source, and installs them to `/opt/lean-engine/`. This step takes 30–60 minutes on Raspberry Pi 4.

### Step 7 — Verify Everything
```bash
sudo bash setup/07_verify.sh
```
Runs a comprehensive check of all installed components and prints a PASS / WARN / FAIL summary. Resolve any failures before proceeding.

### Step 8 — Configure LEAN
```bash
sudo bash setup/08_configure_lean.sh
```
Configures LEAN for Alpaca paper trading. Reads credentials from `/etc/tradingpi/alpaca.env`, sets the algorithm to `PiAiTrader.Strategies.DualMomentumV2`, and creates a timestamped backup of the config before changes.

---

## LEAN Configuration

After running the setup scripts, configure LEAN for paper trading:

### Configure LEAN for Alpaca
```bash
sudo bash setup/configure_lean.sh
```

This script:
- Reads credentials from `/etc/tradingpi/alpaca.env`
- Configures LEAN to use `live-alpaca` environment
- Sets algorithm to `PiAiTrader.Strategies.DualMomentumV2`
- Creates timestamped backup before changes

### Deploy Strategy Updates

When you modify `DualMomentumV2.cs`:
```bash
# 1. Compile the strategy
cd ~/Pi-AI-Trader/strategies/csharp
dotnet build DualMomentumV2.csproj -c Release

# 2. Copy DLL to LEAN directory
cp bin/Release/net10.0/DualMomentumV2.dll \
   /opt/lean-engine/Launcher/bin/Release/

# 3. Restart LEAN service
sudo systemctl restart lean-trader
```

### Fix a Broken config.json (JSONDecodeError from sed edits)

If `make deploy` fails with `json.decoder.JSONDecodeError`, the LEAN config file has been
corrupted by manual `sed` edits (duplicate keys, trailing commas, comment lines, etc.).
Run the repair script on the Pi:

```bash
# SSH into the Pi
ssh pi-admin@tradingpi.local
cd ~/Pi-AI-Trader

# Run the repair script (no root needed — reads/writes /opt/lean-engine/Launcher/)
# If the file is owned by root, prefix with sudo:
bash scripts/fix_lean_config.sh
```

The script will:
1. Back up the broken file to `/opt/lean-engine/Launcher/config.json.broken-may5` (only once — won't overwrite an existing backup)
2. Strip C-style comments and trailing commas
3. Deduplicate keys (last value wins)
4. Enforce `"algorithm-type-name": "DualMomentumV2"` and `"algorithm-location": "DualMomentumV2.dll"`
5. Write valid, indented JSON back to `config.json`
6. Validate the result with `python3 -m json.tool`

After the script exits `[INFO] Done`, re-run `make deploy` as normal.

**If the script reports `[ERROR] Still cannot parse JSON`**, the damage is too
structural for automated repair. Restore from the template:

```bash
# Restore from the project template (you will need to re-enter credentials)
sudo cp config/lean_config.template.json /opt/lean-engine/Launcher/config.json
# Then re-set credentials manually or re-run setup/08_configure_lean.sh
```

### Important Notes

- **Algorithm namespace**: Must use full namespace `PiAiTrader.Strategies.DualMomentumV2` in config
- **LEAN location**: Built at `/opt/lean-engine/` (not in project directory)
- **.NET version**: Requires .NET 10 (LEAN master targets net10.0)

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

# Generate a Flask secret key and add it
FLASK_SECRET=$(python3 -c "import secrets; print(secrets.token_hex(32))")
echo "FLASK_SECRET_KEY=${FLASK_SECRET}" | sudo tee /etc/tradingpi/web.env > /dev/null
sudo chmod 600 /etc/tradingpi/web.env
```

---

## Running the Services

### Install systemd units
```bash
sudo cp services/lean-trader.service /etc/systemd/system/
sudo cp services/lean-web.service    /etc/systemd/system/
sudo systemctl daemon-reload
```

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

### Start live trading
```bash
# Ensure ALPACA_PAPER_TRADING=true in /etc/tradingpi/alpaca.env until validated
sudo systemctl enable lean-trader
sudo systemctl start lean-trader

# Follow trading logs
sudo journalctl -u lean-trader -f
```

---

## Writing Strategies

### C# strategies
Place `.cs` files in `strategies/csharp/`. The main strategy is [`DualMomentumV2.cs`](strategies/csharp/DualMomentumV2.cs).

```bash
# Compile and deploy the strategy
cd ~/Pi-AI-Trader/strategies/csharp
dotnet build DualMomentumV2.csproj -c Release
cp bin/Release/net10.0/DualMomentumV2.dll /opt/lean-engine/Launcher/bin/Release/
sudo systemctl restart lean-trader
```

### Python strategies
Place `.py` files in `strategies/python/`. See [`example_algorithm.py`](strategies/python/example_algorithm.py) for a template.

---

## Useful Commands

```bash
# Check service status
sudo systemctl status lean-trader lean-web

# Follow all trading logs in real time
sudo journalctl -u lean-trader -f

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

---

## Security Notes

- **API keys** are stored in `/etc/tradingpi/*.env` with `chmod 600`. They are never in this repository.
- **SSH password authentication** is disabled after adding your SSH key (see `01_os_config.sh` comments).
- The **web dashboard** binds to all interfaces on port 5000 but UFW restricts access to the local network. It is not exposed to the internet.
- **Paper trading** is enabled by default (`ALPACA_PAPER_TRADING=true`). Only set this to `false` after thorough backtesting and paper trading validation.
- The `.gitignore` excludes credentials, LEAN data, the Python venv, and C# build artifacts.

---

## Key Implementation Differences

This implementation differs from standard LEAN deployment:

### Build from Source (Not LEAN CLI)
- **Why**: LEAN CLI requires paid QuantConnect subscription for local use
- **Method**: Clone and build LEAN from GitHub source
- **Location**: `/opt/lean-engine/` (system-level, not in project)
- **Build time**: ~30 minutes on Raspberry Pi 4

### No Docker
- **Why**: Resource efficiency on Pi hardware (only 4GB RAM)
- **Method**: Native .NET execution
- **Benefit**: Lower memory footprint, easier debugging

### Alpaca Plugin Modifications
- **Clone**: Separate repository `QuantConnect/Lean.Brokerages.Alpaca`
- **Patch required**: Comment out `ValidateSubscription()` call
  - This method checks for paid QuantConnect subscription
  - Always fails for free accounts
- **Symlink**: `/opt/Lean → /opt/lean-engine` for hardcoded paths

### .NET 10 Requirement
- **README originally said**: .NET 8
- **Actually requires**: .NET 10 (preview/RC)
- **Reason**: LEAN master branch targets net10.0
- **Install**: Custom installation, not via package manager

---

## License

[GNU General Public License v3.0](LICENSE)
