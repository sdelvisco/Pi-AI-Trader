#!/usr/bin/env bash
# =============================================================================
# 03_ssd_boot.sh — SSD Boot Configuration
# =============================================================================
# Configures the Raspberry Pi 4 to boot from the SATA SSD connected via the
# Argon ONE M.2 enclosure's internal USB 3.0 interface, instead of an SD card.
#
# Background:
#   The Argon ONE M.2 connects the M.2 SATA SSD to the Pi via the internal
#   USB 3.0 port. The Pi 4's bootloader can boot from USB mass-storage devices
#   once USB boot is enabled in the EEPROM bootloader configuration.
#
#   This script:
#     1. Updates the Pi 4 EEPROM bootloader to the latest stable release
#     2. Sets the boot order to try USB (SSD) first, SD card as fallback
#     3. Optimises USB boot settings for the SSD
#
# IMPORTANT: This script prepares the running SD-card system for SSD booting.
#   After running it you will need to:
#     a) Clone the SD card to the SSD (use rpi-clone or dd)
#     b) Remove the SD card and reboot — the Pi will boot from the SSD
#
# Usage:
#   sudo bash 03_ssd_boot.sh
#
# Requirements:
#   - 02_argon_driver.sh must have been run and Pi rebooted
#   - Internet connectivity (for rpi-eeprom updates)
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

# Confirm we are on a Raspberry Pi 4.
MODEL=$(tr -d '\0' < /proc/device-tree/model 2>/dev/null || true)
if [[ "$MODEL" != *"Raspberry Pi 4"* ]]; then
    info "WARNING: This script is designed for Raspberry Pi 4."
    info "         Detected model: ${MODEL:-unknown}"
    info "         Proceeding anyway — verify results carefully."
fi

# -----------------------------------------------------------------------------
# Step 1: Install rpi-eeprom utilities
# -----------------------------------------------------------------------------
section "Installing rpi-eeprom update utilities"

apt-get update -y
apt-get install -y rpi-eeprom \
    || die "Failed to install rpi-eeprom package"

info "rpi-eeprom installed."

# -----------------------------------------------------------------------------
# Step 2: Check current EEPROM version
# -----------------------------------------------------------------------------
section "Checking current bootloader EEPROM version"

info "Current bootloader information:"
rpi-eeprom-update || true
# rpi-eeprom-update exits non-zero if an update is available, so we use || true

# -----------------------------------------------------------------------------
# Step 3: Update EEPROM to latest stable release
# -----------------------------------------------------------------------------
section "Updating EEPROM bootloader to latest stable release"

# Set the release channel to 'stable' (not 'latest'/beta).
# The stable channel receives well-tested firmware that is reliable for
# a production trading system.
export FIRMWARE_RELEASE_STATUS="stable"

info "Checking for EEPROM updates (stable channel)..."
# The -a flag applies the update automatically without requiring interactive
# confirmation. The update is written to the EEPROM on the next reboot.
if rpi-eeprom-update -a; then
    info "EEPROM update staged — will be applied on next reboot"
else
    info "No EEPROM update available or update already current"
fi

# -----------------------------------------------------------------------------
# Step 4: Configure USB boot order
# -----------------------------------------------------------------------------
section "Configuring bootloader boot order for USB-first boot"

# Extract the current bootloader config to a temp file so we can modify it.
EEPROM_CONFIG_TMP="$(mktemp /tmp/bootconf.XXXXXX.txt)"
rpi-eeprom-config > "$EEPROM_CONFIG_TMP" \
    || die "Failed to extract current EEPROM configuration"

info "Current EEPROM config:"
cat "$EEPROM_CONFIG_TMP"

# Write the desired bootloader configuration.
# Key settings explained:
#
#   BOOT_ORDER=0xf14:
#     Boot order is evaluated right to left in hex.
#     0x1 = SD card, 0x4 = USB mass storage, 0xf = restart loop
#     So 0xf14 means: try USB (4) → try SD (1) → restart and try again (f)
#     This makes the SSD the primary boot device with SD as a rescue fallback.
#
#   USB_MSD_EXCLUDE_VID_PID:
#     Leave blank unless you need to exclude specific USB devices from boot
#     attempts. The Argon ONE's USB-SATA bridge should enumerate cleanly.
#
#   HDMI_DELAY: 0
#     Don't wait for HDMI — the Pi is headless.

cat > "$EEPROM_CONFIG_TMP" << 'EOF'
[all]
# Boot order: USB mass storage (4) → SD card (1) → restart (f)
BOOT_ORDER=0xf14

# Timeout in seconds to wait for USB devices to initialise before giving up.
USB_MSD_DISCOVER_TIMEOUT=20

# Disable HDMI output at boot (headless system, saves ~15mA).
HDMI_DELAY=0

# Power-off on halt rather than leaving the Pi in a low-power idle state.
POWER_OFF_ON_HALT=1
EOF

info "Applying new bootloader configuration..."
rpi-eeprom-config --apply "$EEPROM_CONFIG_TMP" \
    || die "Failed to apply EEPROM configuration"

rm -f "$EEPROM_CONFIG_TMP"
info "EEPROM configuration update staged."

# Verify the staged config.
info "Staged EEPROM config (will activate on reboot):"
rpi-eeprom-config || true

# -----------------------------------------------------------------------------
# Step 5: Install rpi-clone for SD-to-SSD cloning
# -----------------------------------------------------------------------------
section "Installing rpi-clone utility"

# rpi-clone is a third-party script that clones the running SD card to another
# block device (our SSD) while the system is running. It is the safest and
# easiest method for transferring a working SD installation to the SSD.

RPICLONE_URL="https://raw.githubusercontent.com/billw2/rpi-clone/master/rpi-clone"
RPICLONE_DEST="/usr/local/bin/rpi-clone"

if [[ -f "$RPICLONE_DEST" ]]; then
    info "rpi-clone already installed at $RPICLONE_DEST — skipping"
else
    info "Downloading rpi-clone..."
    curl -fsSL "$RPICLONE_URL" -o "$RPICLONE_DEST" \
        || die "Failed to download rpi-clone. Check internet connectivity."
    chmod +x "$RPICLONE_DEST"
    info "rpi-clone installed at $RPICLONE_DEST"
fi

# Identify the SSD block device (typically /dev/sda for the first USB drive).
info "Detected storage devices:"
lsblk -o NAME,SIZE,TYPE,TRAN,MODEL
echo ""

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
section "SSD boot configuration complete"

echo ""
echo "  NEXT STEPS — Manual actions required:"
echo ""
echo "  1. Identify your SSD device (see 'lsblk' output above)."
echo "     It is typically /dev/sda for the Argon ONE internal connection."
echo ""
echo "  2. Clone the SD card to the SSD:"
echo "       sudo rpi-clone sda"
echo "     (Replace 'sda' with your actual SSD device name.)"
echo ""
echo "  3. After cloning completes successfully:"
echo "       a) Power off the Pi:  sudo poweroff"
echo "       b) Remove the SD card from the slot"
echo "       c) Power the Pi back on"
echo "       d) The Pi should boot from the SSD"
echo ""
echo "  4. Verify SSD boot with:  findmnt / | grep sda"
echo ""
echo "  5. Continue with: sudo bash 04_dotnet.sh"
echo ""
