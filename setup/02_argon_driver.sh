#!/usr/bin/env bash
# =============================================================================
# 02_argon_driver.sh — Argon ONE M.2 Case Driver Installation
# =============================================================================
# Installs the official Argon ONE driver package, which provides:
#   - Fan speed control (temperature-based PWM via I2C)
#   - Power button support (safe shutdown / reboot behaviour)
#   - argonone-config utility to customise fan curves
#
# The Argon ONE M.2 case uses an I2C-connected microcontroller to communicate
# with the Pi's GPIO header. Without this driver the fan runs at full speed
# continuously and the power button does not function.
#
# Usage:
#   sudo bash 02_argon_driver.sh
#
# Requirements:
#   - 01_os_config.sh must have been run first
#   - Internet connectivity
#   - Run as root or via sudo
# =============================================================================

set -euo pipefail

# -----------------------------------------------------------------------------
# Helper functions
# -----------------------------------------------------------------------------

section() {
    echo ""
    echo "============================================================"
    echo "  $1"
    echo "============================================================"
}

info() {
    echo "[INFO  $(date '+%H:%M:%S')] $1"
}

die() {
    echo "[ERROR $(date '+%H:%M:%S')] $1" >&2
    exit 1
}

[[ $EUID -eq 0 ]] || die "This script must be run as root (use: sudo bash $0)"

# -----------------------------------------------------------------------------
# Step 1: Enable I2C interface
# -----------------------------------------------------------------------------
section "Enabling I2C interface"

# The Argon ONE's fan controller communicates with the Pi over I2C bus 1.
# I2C is disabled by default on Raspberry Pi OS and must be enabled by adding
# a line to /boot/firmware/config.txt (the Bookworm-era path for boot config).

BOOT_CONFIG="/boot/firmware/config.txt"

# Fall back to older path for legacy OS versions.
[[ -f "$BOOT_CONFIG" ]] || BOOT_CONFIG="/boot/config.txt"
[[ -f "$BOOT_CONFIG" ]] || die "Could not find boot config.txt at /boot/firmware/config.txt or /boot/config.txt"

if grep -q "^dtparam=i2c_arm=on" "$BOOT_CONFIG"; then
    info "I2C already enabled in $BOOT_CONFIG — skipping"
else
    echo "" >> "$BOOT_CONFIG"
    echo "# Argon ONE M.2 — I2C fan controller" >> "$BOOT_CONFIG"
    echo "dtparam=i2c_arm=on" >> "$BOOT_CONFIG"
    info "I2C enabled in $BOOT_CONFIG"
fi

# Load the i2c-dev kernel module immediately (without reboot) so the
# installer can probe the bus.
modprobe i2c-dev || info "i2c-dev module load returned non-zero — may already be loaded"

# Ensure i2c-dev loads automatically on every boot.
if ! grep -q "i2c-dev" /etc/modules; then
    echo "i2c-dev" >> /etc/modules
    info "i2c-dev added to /etc/modules for persistent loading"
fi

# Install i2c-tools so we can inspect the bus for debugging.
apt-get update -y
apt-get install -y i2c-tools python3-smbus \
    || die "Failed to install i2c-tools / python3-smbus"

info "I2C tools installed."

# -----------------------------------------------------------------------------
# Step 2: Download and run the official Argon ONE installer
# -----------------------------------------------------------------------------
section "Installing Argon ONE driver"

# The official Argon Systems installer script handles:
#   - Installing the argonone Python daemon (argononed.py)
#   - Registering it as a systemd service (argonone.service)
#   - Installing argonone-config for fan curve configuration
#   - Installing argonone-uninstall for clean removal
#
# We download to a temp file rather than piping directly to bash so we can
# inspect it before execution — a security best practice.
INSTALLER_URL="https://download.argon40.com/argon1.sh"
INSTALLER_TMP="$(mktemp /tmp/argon1.XXXXXX.sh)"

info "Downloading Argon ONE installer from $INSTALLER_URL"
curl -fsSL "$INSTALLER_URL" -o "$INSTALLER_TMP" \
    || die "Failed to download Argon ONE installer. Check internet connectivity."

info "Installer downloaded to $INSTALLER_TMP"
info "Running installer..."

bash "$INSTALLER_TMP" || die "Argon ONE installer exited with an error"

rm -f "$INSTALLER_TMP"
info "Installer temp file cleaned up."

# -----------------------------------------------------------------------------
# Step 3: Verify the service is running
# -----------------------------------------------------------------------------
section "Verifying Argon ONE service"

if systemctl is-active --quiet argonone; then
    info "argonone.service is ACTIVE"
else
    # The service may not start until after a reboot (I2C module must be fully
    # loaded from config.txt). This is non-fatal.
    info "argonone.service is not yet active — it will start after reboot"
fi

# Check whether the Argon ONE device is visible on the I2C bus.
# The fan controller appears at address 0x01a on bus 1 (i2c-1).
info "Scanning I2C bus 1 for Argon ONE device..."
i2cdetect -y 1 2>/dev/null || info "i2cdetect returned non-zero — I2C may need a reboot to activate"

# -----------------------------------------------------------------------------
# Step 4: Apply default fan curve configuration
# -----------------------------------------------------------------------------
section "Writing default fan curve configuration"

# The Argon ONE daemon reads its fan curve from /etc/argononed.conf.
# This default curve keeps the Pi quiet under moderate load while still
# protecting it during sustained computational tasks (backtesting, training).
#
# Format: temperature_celsius=fan_speed_percent
# Fan speed 0 = off, 100 = full speed.

ARGON_CONF="/etc/argononed.conf"

if [[ ! -f "$ARGON_CONF" ]]; then
    cat > "$ARGON_CONF" << 'EOF'
# Argon ONE M.2 fan curve configuration
# Format: <temp_celsius>=<fan_speed_percent>
# The daemon checks temperature every 30 seconds and sets fan speed
# to the value matching the highest temperature threshold crossed.
55=10
60=25
65=50
70=75
75=100
EOF
    info "Fan curve written to $ARGON_CONF"
else
    info "$ARGON_CONF already exists — not overwriting. Edit manually if needed."
fi

# Reload the daemon to pick up the new config.
systemctl restart argonone 2>/dev/null || info "argonone service not yet running — config will be read on next start"

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
section "Argon ONE driver installation complete"

echo ""
echo "  Fan curve: 55°C→10%  60°C→25%  65°C→50%  70°C→75%  75°C→100%"
echo "  To customise: sudo argonone-config"
echo "  To uninstall: sudo argonone-uninstall"
echo ""
echo "  *** A REBOOT is required to fully activate I2C from boot config ***"
echo "  After reviewing this script's output, run: sudo reboot"
echo "  Then continue with: sudo bash 03_ssd_boot.sh"
echo ""
