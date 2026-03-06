#!/usr/bin/env bash
# =============================================================================
# 04_dotnet.sh — .NET 8 SDK Installation
# =============================================================================
# Installs the .NET 8 SDK on Raspberry Pi OS Lite 64-bit (ARM64).
#
# Why .NET 8?
#   - LEAN Engine is a .NET application; it requires .NET 6+ to run.
#   - .NET 8 is the current Long-Term Support (LTS) release (supported until
#     November 2026), making it the appropriate choice for a production system.
#   - C# strategies are compiled by LEAN using the installed .NET SDK.
#
# Installation method:
#   Microsoft provides an official installation script that detects the
#   architecture and OS, downloads the correct binaries, and installs them
#   to /usr/local/share/dotnet. This is the recommended approach for ARM64
#   Linux systems where the Microsoft APT repository may have limited packages.
#
# Usage:
#   sudo bash 04_dotnet.sh
#
# Requirements:
#   - Pi is now booting from SSD (03_ssd_boot.sh complete)
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

# The version of .NET to install. Change to a newer LTS if desired.
DOTNET_VERSION="8"

# Installation directory for .NET binaries.
DOTNET_INSTALL_DIR="/usr/local/share/dotnet"

# Directory for the dotnet-install script download.
INSTALLER_TMP="$(mktemp /tmp/dotnet-install.XXXXXX.sh)"

# -----------------------------------------------------------------------------
# Step 1: Check architecture
# -----------------------------------------------------------------------------
section "Checking system architecture"

ARCH="$(uname -m)"
info "Detected architecture: $ARCH"

case "$ARCH" in
    aarch64)
        # ARM64 — correct for Raspberry Pi 4 with 64-bit OS
        DOTNET_ARCH="arm64"
        ;;
    armv7l)
        # ARM32 — only if running 32-bit OS; not recommended for this project
        DOTNET_ARCH="arm"
        info "WARNING: 32-bit OS detected. The 64-bit OS is strongly recommended."
        ;;
    *)
        die "Unsupported architecture: $ARCH. Expected aarch64 (ARM64)."
        ;;
esac

info "Will install .NET for architecture: $DOTNET_ARCH"

# -----------------------------------------------------------------------------
# Step 2: Install prerequisites
# -----------------------------------------------------------------------------
section "Installing .NET prerequisites"

# libicu — International Components for Unicode, required by .NET for
#           globalization support (date/number formatting, etc.)
# libssl  — OpenSSL, required for TLS connections to broker APIs
apt-get update -y
apt-get install -y libicu-dev libssl-dev \
    || die "Failed to install .NET prerequisites"

info "Prerequisites installed."

# -----------------------------------------------------------------------------
# Step 3: Download the official dotnet-install script
# -----------------------------------------------------------------------------
section "Downloading Microsoft dotnet-install script"

DOTNET_INSTALLER_URL="https://dot.net/v1/dotnet-install.sh"

info "Downloading from $DOTNET_INSTALLER_URL"
curl -fsSL "$DOTNET_INSTALLER_URL" -o "$INSTALLER_TMP" \
    || die "Failed to download dotnet-install.sh — check internet connectivity"

chmod +x "$INSTALLER_TMP"
info "dotnet-install.sh downloaded to $INSTALLER_TMP"

# -----------------------------------------------------------------------------
# Step 4: Install .NET SDK
# -----------------------------------------------------------------------------
section "Installing .NET $DOTNET_VERSION SDK"

info "This may take several minutes on the first run (downloading ~100MB)..."

# Flags explained:
#   --channel $DOTNET_VERSION  : Install from the .NET 8 channel (LTS)
#   --install-dir              : Install to system-wide location
#   --architecture             : Specify ARM64 explicitly
#   --verbose                  : Show progress during download/install
bash "$INSTALLER_TMP" \
    --channel "$DOTNET_VERSION" \
    --install-dir "$DOTNET_INSTALL_DIR" \
    --architecture "$DOTNET_ARCH" \
    --verbose \
    || die ".NET installation failed"

rm -f "$INSTALLER_TMP"
info ".NET SDK installation complete. Temp file cleaned up."

# -----------------------------------------------------------------------------
# Step 5: Configure PATH for all users
# -----------------------------------------------------------------------------
section "Configuring PATH for dotnet"

# Add dotnet to the system-wide PATH via /etc/profile.d/ so all users
# (including the service account running LEAN) have access to the dotnet binary.
PROFILE_D="/etc/profile.d/dotnet.sh"

cat > "$PROFILE_D" << EOF
# .NET SDK — added by Pi-AI-Trader setup (04_dotnet.sh)
export DOTNET_ROOT="${DOTNET_INSTALL_DIR}"
export PATH="\$PATH:${DOTNET_INSTALL_DIR}"

# Suppress the .NET telemetry opt-in prompt.
# Telemetry is disabled entirely for a server environment.
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# Prevent .NET from generating a welcome message on first run.
export DOTNET_NOLOGO=1
EOF

chmod 644 "$PROFILE_D"
info "PATH configuration written to $PROFILE_D"

# Source the profile for the current shell session so we can use dotnet now.
# shellcheck source=/dev/null
source "$PROFILE_D"

# Also add to root's .bashrc for interactive sudo sessions.
if ! grep -q "DOTNET_ROOT" /root/.bashrc 2>/dev/null; then
    echo "source $PROFILE_D" >> /root/.bashrc
fi

# -----------------------------------------------------------------------------
# Step 6: Verify installation
# -----------------------------------------------------------------------------
section "Verifying .NET installation"

# Reload PATH in the current session.
export PATH="$PATH:$DOTNET_INSTALL_DIR"

DOTNET_BIN="${DOTNET_INSTALL_DIR}/dotnet"
[[ -x "$DOTNET_BIN" ]] || die "dotnet binary not found at $DOTNET_BIN after installation"

info "dotnet version:"
"$DOTNET_BIN" --version || die "dotnet --version failed"

info "Installed SDKs:"
"$DOTNET_BIN" --list-sdks

info "Installed runtimes:"
"$DOTNET_BIN" --list-runtimes

# Quick smoke test: create and run a minimal console app.
section "Running .NET smoke test"

SMOKE_DIR="$(mktemp -d /tmp/dotnet-smoke.XXXXXX)"
info "Smoke test directory: $SMOKE_DIR"

(
    cd "$SMOKE_DIR"
    "$DOTNET_BIN" new console -n SmokeTest --no-restore --force -o . 2>/dev/null
    "$DOTNET_BIN" run --no-restore 2>/dev/null | grep -q "Hello" \
        && info "Smoke test PASSED — .NET runtime is working correctly" \
        || die "Smoke test FAILED — dotnet run did not produce expected output"
) || die ".NET smoke test encountered an error"

rm -rf "$SMOKE_DIR"
info "Smoke test directory cleaned up."

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
section ".NET $DOTNET_VERSION SDK installation complete"

echo ""
echo "  .NET version : $("$DOTNET_BIN" --version)"
echo "  Install dir  : $DOTNET_INSTALL_DIR"
echo "  PATH entry   : $PROFILE_D"
echo ""
echo "  NOTE: Log out and back in (or run 'source $PROFILE_D') to"
echo "        activate dotnet in your current shell session."
echo ""
echo "  Next step: sudo bash 05_python.sh"
echo ""
