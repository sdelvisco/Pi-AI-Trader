#!/usr/bin/env bash
# =============================================================================
# 07_verify.sh — Final System Verification
# =============================================================================
# Runs a comprehensive verification of all installed components to confirm
# the Pi-AI-Trader system is correctly configured and ready to operate.
#
# This script does NOT install or configure anything — it only checks.
# Run it after all other setup scripts have completed.
#
# Exit codes:
#   0 — All checks passed
#   1 — One or more checks failed (see output for details)
#
# Usage:
#   sudo bash 07_verify.sh
#   bash 07_verify.sh        (non-root — skips checks that require root)
# =============================================================================

set -uo pipefail
# Note: -e is intentionally NOT set here so we can collect all check results
# rather than aborting on the first failure.

# -----------------------------------------------------------------------------
# Helper functions and state tracking
# -----------------------------------------------------------------------------

section() {
    echo ""
    echo "============================================================"
    echo "  $1"
    echo "============================================================"
}

PASS_COUNT=0
FAIL_COUNT=0
WARN_COUNT=0

pass() {
    echo "  [PASS] $1"
    (( PASS_COUNT++ )) || true
}

fail() {
    echo "  [FAIL] $1" >&2
    (( FAIL_COUNT++ )) || true
}

warn() {
    echo "  [WARN] $1"
    (( WARN_COUNT++ )) || true
}

info() {
    echo "         $1"
}

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENV_DIR="${PROJECT_ROOT}/venv"
DOTNET_INSTALL_DIR="/usr/local/share/dotnet"

# -----------------------------------------------------------------------------
# Section 1: OS and network
# -----------------------------------------------------------------------------
section "OS and Network"

# Hostname
ACTUAL_HOSTNAME="$(hostname)"
if [[ "$ACTUAL_HOSTNAME" == "tradingpi" ]]; then
    pass "Hostname is 'tradingpi'"
else
    fail "Hostname is '$ACTUAL_HOSTNAME' — expected 'tradingpi'"
fi

# Timezone
ACTUAL_TZ="$(timedatectl show --value --property=Timezone 2>/dev/null || cat /etc/timezone 2>/dev/null || echo unknown)"
info "Timezone: $ACTUAL_TZ"
if [[ "$ACTUAL_TZ" == "America/New_York" ]]; then
    pass "Timezone set to America/New_York"
else
    warn "Timezone is '$ACTUAL_TZ' — expected 'America/New_York' (change in 01_os_config.sh if needed)"
fi

# NTP sync
NTP_SYNC="$(timedatectl show --value --property=NTPSynchronized 2>/dev/null || echo unknown)"
if [[ "$NTP_SYNC" == "yes" ]]; then
    pass "NTP time synchronization active"
else
    warn "NTP not synchronized (NTPSynchronized=$NTP_SYNC) — check internet connectivity"
fi

# Network connectivity (ping Google DNS)
if ping -c 1 -W 3 8.8.8.8 &>/dev/null; then
    pass "Internet connectivity (ping 8.8.8.8)"
else
    fail "No internet connectivity — ping 8.8.8.8 failed"
fi

# mDNS / avahi
if systemctl is-active --quiet avahi-daemon 2>/dev/null; then
    pass "avahi-daemon running (tradingpi.local mDNS resolution enabled)"
else
    fail "avahi-daemon not running — mDNS resolution will not work"
fi

# -----------------------------------------------------------------------------
# Section 2: SSH and firewall
# -----------------------------------------------------------------------------
section "SSH and Firewall"

# SSH service
if systemctl is-active --quiet ssh 2>/dev/null || systemctl is-active --quiet sshd 2>/dev/null; then
    pass "SSH service is running"
else
    fail "SSH service is not running"
fi

# UFW
if command -v ufw &>/dev/null; then
    UFW_STATUS="$(ufw status 2>/dev/null | head -1)"
    if [[ "$UFW_STATUS" == *"active"* ]]; then
        pass "UFW firewall is active"
    else
        warn "UFW firewall is installed but not active ($UFW_STATUS)"
    fi
else
    fail "UFW not installed"
fi

# fail2ban
if systemctl is-active --quiet fail2ban 2>/dev/null; then
    pass "fail2ban is running"
else
    warn "fail2ban is not running — SSH brute force protection inactive"
fi

# -----------------------------------------------------------------------------
# Section 3: Argon ONE driver
# -----------------------------------------------------------------------------
section "Argon ONE M.2 Fan Driver"

if systemctl is-active --quiet argonone 2>/dev/null; then
    pass "argonone.service is running"
else
    warn "argonone.service is not running — fan may not be controlled"
    info "Check: sudo systemctl status argonone"
fi

# I2C bus
if command -v i2cdetect &>/dev/null; then
    if i2cdetect -y 1 &>/dev/null; then
        pass "I2C bus 1 is accessible"
    else
        warn "i2cdetect failed on I2C bus 1 — may need reboot or I2C not enabled"
    fi
else
    warn "i2c-tools not installed — cannot verify I2C bus"
fi

# -----------------------------------------------------------------------------
# Section 4: Storage — confirm SSD boot
# -----------------------------------------------------------------------------
section "Storage (SSD Boot)"

# Check that the root filesystem is on a USB device (SSD).
ROOT_DEVICE="$(findmnt -n -o SOURCE / 2>/dev/null || echo unknown)"
info "Root device: $ROOT_DEVICE"

ROOT_TRANSPORT="$(lsblk -no TRAN "$ROOT_DEVICE" 2>/dev/null | head -1 || echo unknown)"
info "Root transport: $ROOT_TRANSPORT"

if [[ "$ROOT_TRANSPORT" == "usb" ]]; then
    pass "Root filesystem is on USB device (SSD) — SSD boot confirmed"
elif [[ "$ROOT_DEVICE" == *"mmcblk"* ]]; then
    warn "Root filesystem is on SD card (mmcblk) — SSD boot not yet configured"
    info "Complete 03_ssd_boot.sh instructions to migrate to SSD"
else
    warn "Could not determine root device transport (device: $ROOT_DEVICE)"
fi

# Disk usage
info "Disk usage:"
df -h / | tail -1 | awk '{printf "         Used: %s of %s (%s)\n", $3, $2, $5}'

# -----------------------------------------------------------------------------
# Section 5: .NET SDK
# -----------------------------------------------------------------------------
section ".NET SDK"

DOTNET_BIN="${DOTNET_INSTALL_DIR}/dotnet"

if [[ -x "$DOTNET_BIN" ]]; then
    DOTNET_VERSION="$("$DOTNET_BIN" --version 2>/dev/null || echo unknown)"
    if [[ "$DOTNET_VERSION" == 8* ]]; then
        pass ".NET SDK 8.x installed: $DOTNET_VERSION"
    else
        warn ".NET installed but version is '$DOTNET_VERSION' — expected 8.x"
    fi
else
    fail ".NET SDK not found at $DOTNET_BIN — run 04_dotnet.sh"
fi

# Check PATH includes dotnet.
if echo "$PATH" | grep -q "$DOTNET_INSTALL_DIR"; then
    pass "dotnet directory is in PATH"
else
    warn "dotnet directory ($DOTNET_INSTALL_DIR) is not in current PATH"
    info "Source /etc/profile.d/dotnet.sh or log out and back in"
fi

# Telemetry should be disabled.
if [[ "${DOTNET_CLI_TELEMETRY_OPTOUT:-0}" == "1" ]]; then
    pass "DOTNET_CLI_TELEMETRY_OPTOUT=1 (telemetry disabled)"
else
    warn "DOTNET_CLI_TELEMETRY_OPTOUT not set — telemetry may be enabled"
fi

# -----------------------------------------------------------------------------
# Section 6: Python environment
# -----------------------------------------------------------------------------
section "Python Virtual Environment"

PYTHON_BIN="${VENV_DIR}/bin/python3"
PIP_BIN="${VENV_DIR}/bin/pip"

if [[ -x "$PYTHON_BIN" ]]; then
    PY_VERSION="$("$PYTHON_BIN" --version 2>/dev/null)"
    pass "Python venv exists: $PY_VERSION"
else
    fail "Python venv not found at $VENV_DIR — run 05_python.sh"
fi

# Check key packages.
declare -A EXPECTED_PACKAGES=(
    ["flask"]="Flask"
    ["gunicorn"]="gunicorn"
    ["alpaca"]="alpaca-py"
    ["pandas"]="pandas"
)

for MODULE in "${!EXPECTED_PACKAGES[@]}"; do
    PKG_NAME="${EXPECTED_PACKAGES[$MODULE]}"
    if "$PYTHON_BIN" -c "import $MODULE" &>/dev/null; then
        VERSION="$("$PYTHON_BIN" -c "import importlib.metadata; print(importlib.metadata.version('$PKG_NAME'))" 2>/dev/null || echo 'installed')"
        pass "pip package '$PKG_NAME' installed (v$VERSION)"
    else
        fail "pip package '$PKG_NAME' not installed — re-run 05_python.sh"
    fi
done

# -----------------------------------------------------------------------------
# Section 7: LEAN engine and Alpaca plugin (source build)
# -----------------------------------------------------------------------------
section "LEAN Engine and Alpaca Plugin (source build)"

LEAN_ENGINE_DIR="/opt/lean-engine"
LEAN_ALPACA_DIR="/opt/lean-alpaca"
LEAN_RELEASE_DIR="${LEAN_ENGINE_DIR}/Launcher/bin/Release"
LEAN_LAUNCHER_DLL="${LEAN_RELEASE_DIR}/QuantConnect.Lean.Launcher.dll"

# Check that the LEAN launcher DLL exists — this confirms the engine was built.
if [[ -f "$LEAN_LAUNCHER_DLL" ]]; then
    pass "LEAN Launcher DLL present: $LEAN_LAUNCHER_DLL"
else
    fail "LEAN Launcher DLL not found at $LEAN_LAUNCHER_DLL — run 06_lean_build.sh"
fi

# Check that Alpaca plugin DLLs were copied into the LEAN Release directory.
# We look for any DLL whose name contains 'Alpaca' (case-sensitive).
ALPACA_DLL_COUNT="$(find "$LEAN_RELEASE_DIR" -maxdepth 1 -name '*Alpaca*.dll' 2>/dev/null | wc -l)"
if [[ "$ALPACA_DLL_COUNT" -gt 0 ]]; then
    pass "Alpaca plugin DLLs present in LEAN Release directory ($ALPACA_DLL_COUNT file(s))"
else
    fail "No Alpaca plugin DLLs found in $LEAN_RELEASE_DIR — run 06_lean_build.sh"
fi

# Check that dotnet is on the PATH and returns a version string.
# The symlink at /usr/local/bin/dotnet is created by 04_dotnet.sh.
if command -v dotnet &>/dev/null; then
    DOTNET_PATH_VERSION="$(dotnet --version 2>/dev/null || echo unknown)"
    pass "dotnet on PATH, version: $DOTNET_PATH_VERSION"
else
    fail "dotnet not found on PATH — source /etc/profile.d/dotnet.sh or re-run 04_dotnet.sh"
fi

# -----------------------------------------------------------------------------
# Section 8: Project directory structure
# -----------------------------------------------------------------------------
section "Project Directory Structure"

declare -A EXPECTED_DIRS=(
    ["setup/"]="Setup scripts"
    ["config/"]="Configuration templates"
    ["services/"]="systemd unit files"
    ["web/"]="Flask web application"
    ["strategies/csharp/"]="C# strategies"
    ["strategies/python/"]="Python strategies"
)

for RELPATH in "${!EXPECTED_DIRS[@]}"; do
    DESC="${EXPECTED_DIRS[$RELPATH]}"
    if [[ -d "${PROJECT_ROOT}/${RELPATH}" ]]; then
        pass "Directory exists: $RELPATH ($DESC)"
    else
        fail "Directory missing: $RELPATH ($DESC)"
    fi
done

declare -A EXPECTED_FILES=(
    [".gitignore"]="Git ignore rules"
    ["requirements.txt"]="Python dependencies"
    ["config/lean_config.template.json"]="LEAN config template"
    ["config/alpaca_credentials.template"]="Alpaca credentials template"
    ["services/lean-trader.service"]="LEAN systemd unit"
    ["services/lean-web.service"]="Web interface systemd unit"
)

for RELPATH in "${!EXPECTED_FILES[@]}"; do
    DESC="${EXPECTED_FILES[$RELPATH]}"
    if [[ -f "${PROJECT_ROOT}/${RELPATH}" ]]; then
        pass "File exists: $RELPATH ($DESC)"
    else
        fail "File missing: $RELPATH ($DESC)"
    fi
done

# Check .gitignore excludes sensitive patterns.
if grep -q "alpaca_credentials.json" "${PROJECT_ROOT}/.gitignore" 2>/dev/null; then
    pass ".gitignore excludes alpaca_credentials.json"
else
    fail ".gitignore does not exclude credentials — review .gitignore"
fi

# -----------------------------------------------------------------------------
# Section 9: Systemd services (informational)
# -----------------------------------------------------------------------------
section "Systemd Services (installed check)"

for SERVICE in lean-trader lean-web argonone; do
    if systemctl list-unit-files "${SERVICE}.service" &>/dev/null | grep -q "$SERVICE"; then
        STATUS="$(systemctl is-active "${SERVICE}.service" 2>/dev/null || echo inactive)"
        if [[ "$STATUS" == "active" ]]; then
            pass "${SERVICE}.service installed and ACTIVE"
        else
            info "${SERVICE}.service installed, status: $STATUS"
        fi
    else
        info "${SERVICE}.service not yet installed (expected until services/ units are deployed)"
    fi
done

# -----------------------------------------------------------------------------
# Final summary
# -----------------------------------------------------------------------------
section "Verification Summary"

echo ""
printf "  PASSED : %d\n" "$PASS_COUNT"
printf "  WARNED : %d\n" "$WARN_COUNT"
printf "  FAILED : %d\n" "$FAIL_COUNT"
echo ""

if [[ $FAIL_COUNT -eq 0 && $WARN_COUNT -eq 0 ]]; then
    echo "  All checks passed. The system is ready for trading operations."
    echo ""
    exit 0
elif [[ $FAIL_COUNT -eq 0 ]]; then
    echo "  No failures. Review warnings above — some may require attention"
    echo "  before starting live trading."
    echo ""
    exit 0
else
    echo "  $FAIL_COUNT check(s) FAILED. Resolve the failures listed above"
    echo "  before proceeding. Re-run the relevant setup scripts as needed."
    echo ""
    exit 1
fi
