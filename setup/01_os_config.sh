#!/usr/bin/env bash
# =============================================================================
# 01_os_config.sh — OS Post-Install Configuration
# =============================================================================
# Run this script FIRST, immediately after the initial boot of Raspberry Pi OS
# Lite 64-bit. It configures the system hostname, locale, timezone, SSH
# hardening, package hygiene, and network settings required before any
# application-layer software is installed.
#
# Usage:
#   sudo bash 01_os_config.sh
#
# Requirements:
#   - Raspberry Pi OS Lite 64-bit (Debian Bookworm base)
#   - Internet connectivity
#   - Run as root or via sudo
# =============================================================================

set -euo pipefail
# -e  : Exit immediately if any command returns a non-zero status.
# -u  : Treat unset variables as errors.
# -o pipefail : A pipeline fails if any command in it fails (not just the last).

# -----------------------------------------------------------------------------
# Helper functions
# -----------------------------------------------------------------------------

# Print a section header for readability in the terminal log.
section() {
    echo ""
    echo "============================================================"
    echo "  $1"
    echo "============================================================"
}

# Print an informational message with a timestamp.
info() {
    echo "[INFO  $(date '+%H:%M:%S')] $1"
}

# Print an error message to stderr and exit with code 1.
die() {
    echo "[ERROR $(date '+%H:%M:%S')] $1" >&2
    exit 1
}

# Confirm the script is running as root, which is required for system changes.
[[ $EUID -eq 0 ]] || die "This script must be run as root (use: sudo bash $0)"

# -----------------------------------------------------------------------------
# Configuration — edit these values before running if needed
# -----------------------------------------------------------------------------

# The static hostname that will identify the Pi on your local network.
# Accessible as tradingpi.local via mDNS from Windows 11.
HOSTNAME="tradingpi"

# Timezone in IANA format. Adjust to your local timezone.
TIMEZONE="America/New_York"

# Locale setting for the system. en_US.UTF-8 is a safe default.
LOCALE="en_US.UTF-8"

# -----------------------------------------------------------------------------
# Step 1: Set hostname
# -----------------------------------------------------------------------------
section "Setting hostname to '$HOSTNAME'"

# Write the hostname to the kernel's hostname file.
hostnamectl set-hostname "$HOSTNAME" \
    || die "Failed to set hostname with hostnamectl"

# Ensure the hostname resolves to localhost in /etc/hosts so local services
# (e.g., avahi-daemon for mDNS) don't produce resolver warnings.
if ! grep -q "$HOSTNAME" /etc/hosts; then
    echo "127.0.1.1    $HOSTNAME" >> /etc/hosts
    info "Added $HOSTNAME to /etc/hosts"
else
    info "$HOSTNAME already present in /etc/hosts — skipping"
fi

info "Hostname set to: $(hostname)"

# -----------------------------------------------------------------------------
# Step 2: Set timezone
# -----------------------------------------------------------------------------
section "Setting timezone to '$TIMEZONE'"

timedatectl set-timezone "$TIMEZONE" \
    || die "Failed to set timezone. Check that '$TIMEZONE' is a valid IANA timezone."

# Enable NTP synchronization so the system clock is always accurate.
# Accurate time is critical for trading — API tokens and log timestamps depend
# on it, and LEAN may reject data with incorrect timestamps.
timedatectl set-ntp true \
    || die "Failed to enable NTP"

info "Timezone: $(timedatectl show --value --property=Timezone)"
info "NTP: $(timedatectl show --value --property=NTP)"

# -----------------------------------------------------------------------------
# Step 3: Set locale
# -----------------------------------------------------------------------------
section "Configuring locale to '$LOCALE'"

# Generate the locale if it isn't already available.
if ! locale -a 2>/dev/null | grep -q "^${LOCALE/UTF-8/utf8}$"; then
    sed -i "s/# ${LOCALE} UTF-8/${LOCALE} UTF-8/" /etc/locale.gen
    locale-gen || die "locale-gen failed"
    info "Locale generated: $LOCALE"
else
    info "Locale $LOCALE already available — skipping generation"
fi

# Apply the locale system-wide.
update-locale LANG="$LOCALE" LC_ALL="$LOCALE" \
    || die "update-locale failed"

info "Locale configured."

# -----------------------------------------------------------------------------
# Step 4: Update and upgrade all packages
# -----------------------------------------------------------------------------
section "Updating package lists and upgrading installed packages"

# Update the package index from all configured repositories.
apt-get update -y || die "apt-get update failed"

# Upgrade installed packages to their latest versions.
# Using 'full-upgrade' (vs 'upgrade') allows dependency changes, which is
# important on a fresh install to ensure kernel and firmware are current.
apt-get full-upgrade -y || die "apt-get full-upgrade failed"

# Remove packages that are no longer needed after upgrades.
apt-get autoremove -y
apt-get autoclean -y

info "System packages are up to date."

# -----------------------------------------------------------------------------
# Step 5: Install essential system utilities
# -----------------------------------------------------------------------------
section "Installing essential system utilities"

# These packages are required by later setup scripts or are useful for
# headless administration:
#   curl       — download scripts and files from the web
#   wget       — alternative downloader for some installers
#   git        — version control; also used by LEAN CLI
#   unzip      — extract archives (used by some installers)
#   htop       — interactive process viewer for diagnostics
#   avahi-daemon — enables tradingpi.local mDNS resolution from Windows
#   ufw        — Uncomplicated Firewall for basic network hardening
#   fail2ban   — protects SSH from brute-force login attempts
#   net-tools  — provides ifconfig, netstat for network diagnostics
#   dnsutils   — provides nslookup, dig for DNS troubleshooting
PACKAGES=(
    curl
    wget
    git
    unzip
    htop
    avahi-daemon
    ufw
    fail2ban
    net-tools
    dnsutils
    ca-certificates
    gnupg
    lsb-release
    rsync
)

apt-get install -y "${PACKAGES[@]}" \
    || die "Failed to install one or more essential packages"

info "Essential packages installed."

# -----------------------------------------------------------------------------
# Step 6: Enable and start avahi-daemon (mDNS)
# -----------------------------------------------------------------------------
section "Enabling avahi-daemon for mDNS (.local) resolution"

# Avahi allows the Pi to advertise itself as tradingpi.local on the local
# network using multicast DNS (Bonjour/Zeroconf). This means you can SSH
# with: ssh pi@tradingpi.local — no need to remember the IP address.
systemctl enable avahi-daemon || die "Failed to enable avahi-daemon"
systemctl start avahi-daemon  || die "Failed to start avahi-daemon"

info "avahi-daemon active: $(systemctl is-active avahi-daemon)"

# -----------------------------------------------------------------------------
# Step 7: SSH hardening
# -----------------------------------------------------------------------------
section "Hardening SSH configuration"

SSHD_CONFIG="/etc/ssh/sshd_config"

# Back up the original sshd_config before making changes.
if [[ ! -f "${SSHD_CONFIG}.bak" ]]; then
    cp "$SSHD_CONFIG" "${SSHD_CONFIG}.bak"
    info "Backed up sshd_config to ${SSHD_CONFIG}.bak"
fi

# Apply hardening settings using a drop-in override file, which is cleaner
# than modifying sshd_config directly and survives package upgrades.
SSHD_OVERRIDE="/etc/ssh/sshd_config.d/99-tradingpi-hardening.conf"

cat > "$SSHD_OVERRIDE" << 'EOF'
# Pi-AI-Trader SSH hardening — applied by 01_os_config.sh
# Disable root login over SSH — always use a non-root user with sudo.
PermitRootLogin no

# Disable password authentication — use SSH key pairs only.
# Before applying this, ensure your public key is in ~/.ssh/authorized_keys.
# To add your key from Windows: ssh-copy-id pi@tradingpi.local
# PasswordAuthentication no   # <-- Uncomment AFTER adding your SSH key

# Limit SSH to specific users (add your username here if desired).
# AllowUsers pi

# Reduce the authentication timeout window.
LoginGraceTime 30

# Limit simultaneous unauthenticated connections.
MaxStartups 3:50:10

# Disconnect idle sessions after 15 minutes of inactivity.
ClientAliveInterval 300
ClientAliveCountMax 3
EOF

info "SSH hardening config written to $SSHD_OVERRIDE"

# Validate the configuration before reloading to avoid locking ourselves out.
sshd -t || die "sshd configuration test failed — not reloading. Check $SSHD_OVERRIDE"

systemctl reload sshd || die "Failed to reload sshd"

info "SSH reloaded with hardened configuration."
info "NOTE: Password authentication is still enabled."
info "      Add your SSH public key, then uncomment 'PasswordAuthentication no'"
info "      in $SSHD_OVERRIDE and run: sudo systemctl reload sshd"

# -----------------------------------------------------------------------------
# Step 8: Configure UFW firewall
# -----------------------------------------------------------------------------
section "Configuring UFW firewall"

# Allow SSH through the firewall before enabling it — otherwise we lock
# ourselves out of the headless Pi.
ufw allow OpenSSH || die "Failed to allow SSH through UFW"

# Allow the Flask web interface port (only on the local network interface).
# Port 5000 is the default Flask development port; the production service
# will use the same port but only bind to the local network interface.
ufw allow 5000/tcp comment "Pi-AI-Trader Flask web interface" \
    || die "Failed to allow port 5000 through UFW"

# Enable the firewall with default deny-incoming policy.
ufw --force enable || die "Failed to enable UFW"

info "UFW status:"
ufw status verbose

# -----------------------------------------------------------------------------
# Step 9: Configure fail2ban for SSH protection
# -----------------------------------------------------------------------------
section "Configuring fail2ban"

# fail2ban monitors log files for repeated authentication failures and
# temporarily bans offending IP addresses. Essential for a network-accessible
# headless system.
FAIL2BAN_LOCAL="/etc/fail2ban/jail.local"

if [[ ! -f "$FAIL2BAN_LOCAL" ]]; then
    cat > "$FAIL2BAN_LOCAL" << 'EOF'
[DEFAULT]
# Ban an IP for 1 hour after 5 failed login attempts within 10 minutes.
bantime  = 3600
findtime = 600
maxretry = 5

[sshd]
enabled = true
port    = ssh
logpath = %(sshd_log)s
backend = %(sshd_backend)s
EOF
    info "fail2ban jail.local created"
else
    info "fail2ban jail.local already exists — skipping"
fi

systemctl enable fail2ban || die "Failed to enable fail2ban"
systemctl restart fail2ban || die "Failed to start fail2ban"

info "fail2ban active: $(systemctl is-active fail2ban)"

# -----------------------------------------------------------------------------
# Step 10: Disable swap (optional but recommended for SSD longevity)
# -----------------------------------------------------------------------------
section "Disabling swap to preserve SSD write cycles"

# On a Pi with 4GB RAM running a focused trading application, swap should not
# be needed. Disabling it prevents unnecessary writes to the SSD.
# If you find the system needs more memory, re-enable swap with 'dphys-swapfile'.
if systemctl is-active --quiet dphys-swapfile 2>/dev/null; then
    dphys-swapfile swapoff  || true
    dphys-swapfile uninstall || true
    systemctl disable dphys-swapfile || true
    info "Swap disabled."
else
    info "Swap service not found or already disabled — skipping."
fi

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
section "OS configuration complete"

echo ""
echo "  Hostname : $(hostname)"
echo "  Timezone : $(timedatectl show --value --property=Timezone)"
echo "  IP Addr  : $(hostname -I | awk '{print $1}')"
echo ""
echo "  Next step: Run  sudo bash 02_argon_driver.sh"
echo ""
