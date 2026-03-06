#!/usr/bin/env bash
# =============================================================================
# 04_dotnet.sh — .NET 10 SDK Installation
# =============================================================================
# Installs the .NET 10 SDK on Raspberry Pi OS Lite 64-bit (ARM64).
#
# Why .NET 10?
#   - LEAN Engine's master branch now targets net10.0, so .NET 10 is required
#     to build and run LEAN from source.
#   - C# strategies are compiled by LEAN using the installed .NET SDK.
#
# Installation method:
#   The aka.ms shortlink method is unreliable (redirects to Bing). Instead,
#   this script fetches the latest .NET 10 SDK version string directly from
#   Microsoft's build servers, downloads the tarball, and extracts it in place.
#
#   Steps:
#     1. Resolve version: https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0/latest.version
#     2. Download:        https://builds.dotnet.microsoft.com/dotnet/Sdk/{VERSION}/dotnet-sdk-{VERSION}-linux-{ARCH}.tar.gz
#     3. Extract to:      /usr/local/share/dotnet  (overwrites any existing installation)
#     4. Symlink:         /usr/local/share/dotnet/dotnet → /usr/local/bin/dotnet
#     5. Configure PATH via /etc/profile.d/dotnet.sh
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

# Installation directory for .NET binaries.
DOTNET_INSTALL_DIR="/usr/local/share/dotnet"

# Base URL for Microsoft's build server.
DOTNET_BUILD_BASE="https://builds.dotnet.microsoft.com/dotnet/Sdk"

# .NET channel to install.
DOTNET_CHANNEL="10.0"

# -----------------------------------------------------------------------------
# Step 1: Check architecture
# -----------------------------------------------------------------------------
section "Checking system architecture"

ARCH="$(uname -m)"
info "Detected architecture: $ARCH"

case "$ARCH" in
    aarch64)
        # ARM64 — correct for Raspberry Pi 4/5 with 64-bit OS
        DOTNET_ARCH="arm64"
        DOTNET_RID="linux-arm64"
        ;;
    armv7l)
        # ARM32 — only if running 32-bit OS; not recommended for this project
        DOTNET_ARCH="arm"
        DOTNET_RID="linux-arm"
        info "WARNING: 32-bit OS detected. The 64-bit OS is strongly recommended."
        ;;
    *)
        die "Unsupported architecture: $ARCH. Expected aarch64 (ARM64)."
        ;;
esac

info "Will install .NET $DOTNET_CHANNEL for architecture: $DOTNET_RID"

# -----------------------------------------------------------------------------
# Step 2: Install prerequisites
# -----------------------------------------------------------------------------
section "Installing .NET prerequisites"

# libicu — International Components for Unicode, required by .NET for
#           globalization support (date/number formatting, etc.)
# libssl  — OpenSSL, required for TLS connections to broker APIs
apt-get update -y
apt-get install -y libicu-dev libssl-dev curl \
    || die "Failed to install .NET prerequisites"

info "Prerequisites installed."

# -----------------------------------------------------------------------------
# Step 3: Resolve the latest .NET 10 SDK version string
# -----------------------------------------------------------------------------
section "Resolving latest .NET $DOTNET_CHANNEL SDK version"

VERSION_URL="${DOTNET_BUILD_BASE}/${DOTNET_CHANNEL}/latest.version"
info "Fetching version from: $VERSION_URL"

SDK_VERSION="$(curl -fsSL "$VERSION_URL" \
    || die "Failed to fetch .NET version from $VERSION_URL — check internet connectivity")"

# Trim any trailing whitespace/newline from the response.
SDK_VERSION="$(echo "$SDK_VERSION" | tr -d '[:space:]')"

[[ -n "$SDK_VERSION" ]] || die "Version string was empty — the version endpoint may be unavailable"

info "Latest .NET $DOTNET_CHANNEL SDK version: $SDK_VERSION"

# -----------------------------------------------------------------------------
# Step 4: Download the SDK tarball
# -----------------------------------------------------------------------------
section "Downloading .NET SDK $SDK_VERSION"

TARBALL_NAME="dotnet-sdk-${SDK_VERSION}-${DOTNET_RID}.tar.gz"
TARBALL_URL="${DOTNET_BUILD_BASE}/${SDK_VERSION}/${TARBALL_NAME}"
TARBALL_TMP="$(mktemp /tmp/dotnet-sdk.XXXXXX.tar.gz)"

info "Downloading: $TARBALL_URL"
info "This may take several minutes (~200MB download)..."

curl -fsSL "$TARBALL_URL" -o "$TARBALL_TMP" \
    || die "Failed to download $TARBALL_URL — check internet connectivity"

info "Download complete: $TARBALL_TMP"

# -----------------------------------------------------------------------------
# Step 5: Extract the SDK into the install directory
# -----------------------------------------------------------------------------
section "Extracting .NET SDK to $DOTNET_INSTALL_DIR"

# Create the install directory if it does not yet exist.
mkdir -p "$DOTNET_INSTALL_DIR"

# Extract over the top of any existing installation. We do NOT delete first —
# extracting in place overwrites changed files while preserving any custom
# additions the operator may have made.
info "Extracting (this will overwrite any existing installation in place)..."
tar -xzf "$TARBALL_TMP" -C "$DOTNET_INSTALL_DIR" \
    || die "Extraction failed — the downloaded archive may be corrupt"

rm -f "$TARBALL_TMP"
info "Tarball extracted. Temp file cleaned up."

# -----------------------------------------------------------------------------
# Step 6: Create /usr/local/bin/dotnet symlink
# -----------------------------------------------------------------------------
section "Creating /usr/local/bin/dotnet symlink"

DOTNET_BIN_TARGET="${DOTNET_INSTALL_DIR}/dotnet"

[[ -x "$DOTNET_BIN_TARGET" ]] \
    || die "dotnet binary not found at $DOTNET_BIN_TARGET after extraction"

ln -sf "$DOTNET_BIN_TARGET" /usr/local/bin/dotnet \
    || die "Failed to create symlink /usr/local/bin/dotnet → $DOTNET_BIN_TARGET"

info "Symlink created: /usr/local/bin/dotnet → $DOTNET_BIN_TARGET"

# -----------------------------------------------------------------------------
# Step 7: Configure PATH for all users
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
# Step 8: Verify installation
# -----------------------------------------------------------------------------
section "Verifying .NET installation"

# Use the symlink so we exercise the same path that 06_lean_build.sh will use.
DOTNET_BIN="/usr/local/bin/dotnet"

info "dotnet version:"
"$DOTNET_BIN" --version || die "dotnet --version failed"

info "Installed SDKs:"
"$DOTNET_BIN" --list-sdks

info "Installed runtimes:"
"$DOTNET_BIN" --list-runtimes

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
section ".NET $SDK_VERSION installation complete"

echo ""
echo "  .NET version : $("$DOTNET_BIN" --version)"
echo "  Install dir  : $DOTNET_INSTALL_DIR"
echo "  Symlink      : /usr/local/bin/dotnet → $DOTNET_BIN_TARGET"
echo "  PATH entry   : $PROFILE_D"
echo ""
echo "  NOTE: Log out and back in (or run 'source $PROFILE_D') to"
echo "        activate dotnet in your current shell session."
echo ""
echo "  Next step: sudo bash 05_python.sh"
echo ""
