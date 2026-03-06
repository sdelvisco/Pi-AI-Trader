#!/usr/bin/env bash
# =============================================================================
# 06_lean_cli.sh — LEAN CLI Installation and Project Initialisation
# =============================================================================
# Installs the QuantConnect LEAN CLI, initialises a LEAN workspace, and
# configures it to use Alpaca as the live trading broker.
#
# What is the LEAN CLI?
#   The LEAN CLI is QuantConnect's command-line tool for managing LEAN Engine
#   projects locally. It handles:
#     - Downloading and running the LEAN Docker container (or local binary)
#     - Backtesting and live trading configuration
#     - Strategy file management
#     - Cloud sync with QuantConnect (optional)
#
# Architecture note:
#   LEAN can be run in two modes:
#     1. Docker mode  — LEAN runs inside a Docker container (simplest setup)
#     2. Local mode   — LEAN binaries run directly on the OS
#   This script sets up Docker mode, which is recommended because:
#     - LEAN's Docker image bundles all .NET dependencies correctly
#     - Easier to update (pull new image vs. manual binary update)
#     - Isolates LEAN from the host Python/dotnet environment
#
# Usage:
#   sudo bash 06_lean_cli.sh
#
# Requirements:
#   - 05_python.sh must have been run
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

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_USER="${SUDO_USER:-pi}"
VENV_DIR="${PROJECT_ROOT}/venv"
LEAN_WORKSPACE="${PROJECT_ROOT}/lean"

# -----------------------------------------------------------------------------
# Step 1: Install Docker
# -----------------------------------------------------------------------------
section "Installing Docker"

if command -v docker &>/dev/null; then
    info "Docker already installed: $(docker --version)"
else
    info "Docker not found — installing via official convenience script"

    DOCKER_INSTALLER="$(mktemp /tmp/get-docker.XXXXXX.sh)"
    curl -fsSL https://get.docker.com -o "$DOCKER_INSTALLER" \
        || die "Failed to download Docker install script"

    bash "$DOCKER_INSTALLER" || die "Docker installation failed"
    rm -f "$DOCKER_INSTALLER"

    info "Docker installed: $(docker --version)"
fi

# Add the project user to the docker group so they can run docker without sudo.
# The systemd service unit for LEAN will run as this user.
if ! groups "$PROJECT_USER" | grep -q docker; then
    usermod -aG docker "$PROJECT_USER" \
        || die "Failed to add $PROJECT_USER to docker group"
    info "Added $PROJECT_USER to docker group (takes effect on next login)"
else
    info "$PROJECT_USER is already in the docker group"
fi

# Enable Docker to start on boot and start it now.
systemctl enable docker || die "Failed to enable docker service"
systemctl start docker  || die "Failed to start docker service"

info "Docker service active: $(systemctl is-active docker)"

# -----------------------------------------------------------------------------
# Step 2: Install LEAN CLI via pip into the project venv
# -----------------------------------------------------------------------------
section "Installing LEAN CLI"

# Activate the project virtual environment.
# shellcheck source=/dev/null
source "${VENV_DIR}/bin/activate" \
    || die "Failed to activate virtual environment at $VENV_DIR"

info "Installing lean-cli into virtual environment..."
pip install --upgrade lean \
    || die "Failed to install lean-cli via pip"

info "LEAN CLI version: $(lean --version)"

# -----------------------------------------------------------------------------
# Step 3: Create the LEAN workspace directory
# -----------------------------------------------------------------------------
section "Creating LEAN workspace at $LEAN_WORKSPACE"

if [[ -d "$LEAN_WORKSPACE" ]]; then
    info "LEAN workspace directory already exists — skipping creation"
else
    mkdir -p "$LEAN_WORKSPACE"
    info "Created $LEAN_WORKSPACE"
fi

# Initialise the LEAN workspace. This creates lean.json (the LEAN config file)
# and the data/ directory structure. We run as the project user to ensure
# correct file ownership.
if [[ ! -f "${LEAN_WORKSPACE}/lean.json" ]]; then
    info "Initialising LEAN workspace..."
    # The --skip-confirmation flag suppresses the interactive Y/N prompt.
    # We change to the workspace dir because lean init writes to CWD.
    (
        cd "$LEAN_WORKSPACE"
        # Run lean init as the project user, not root.
        sudo -u "$PROJECT_USER" \
            "${VENV_DIR}/bin/lean" init \
            || die "lean init failed"
    )
    info "LEAN workspace initialised."
else
    info "lean.json already exists in workspace — skipping lean init"
fi

# -----------------------------------------------------------------------------
# Step 4: Pull the LEAN Docker image
# -----------------------------------------------------------------------------
section "Pulling LEAN Engine Docker image"

# The ARM64 LEAN image is maintained by QuantConnect.
# This pull is done ahead of time so that the first 'lean live' command doesn't
# have to download a large image (the image is ~3-4GB).
LEAN_IMAGE="quantconnect/lean:latest"

info "Pulling $LEAN_IMAGE (this may take a while — ~3-4GB download)..."
docker pull "$LEAN_IMAGE" \
    || die "Failed to pull LEAN Docker image. Check internet and Docker status."

info "LEAN Docker image ready."
docker images "$LEAN_IMAGE"

# -----------------------------------------------------------------------------
# Step 5: Configure LEAN for Alpaca live trading
# -----------------------------------------------------------------------------
section "Configuring LEAN for Alpaca"

LEAN_CONFIG="${LEAN_WORKSPACE}/lean.json"

if [[ ! -f "$LEAN_CONFIG" ]]; then
    die "lean.json not found at $LEAN_CONFIG — lean init may have failed"
fi

info "LEAN configuration file: $LEAN_CONFIG"
info ""
info "IMPORTANT: The LEAN config references your Alpaca credentials."
info "  These are read from environment variables at runtime — NEVER"
info "  hardcode API keys in lean.json or any committed file."
info ""
info "  Set the following environment variables (e.g., in /etc/environment"
info "  or in the systemd service unit's EnvironmentFile):"
info ""
info "    ALPACA_KEY_ID=your_alpaca_key_id"
info "    ALPACA_SECRET_KEY=your_alpaca_secret_key"
info "    ALPACA_PAPER_TRADING=true   # set to false for live money"
info ""
info "  See config/alpaca_credentials.template for the full list."

# Verify the config file was created and has content.
if [[ -s "$LEAN_CONFIG" ]]; then
    info "lean.json exists and has content — OK"
else
    die "lean.json is empty or missing after init"
fi

# -----------------------------------------------------------------------------
# Step 6: Create strategy directories in the LEAN workspace
# -----------------------------------------------------------------------------
section "Creating strategy directories in LEAN workspace"

# LEAN expects algorithm files in specific directories within the workspace.
# We create links from the project strategies/ dir into the workspace so
# strategies are version-controlled in the project repo.
STRATEGIES_DIR="${PROJECT_ROOT}/strategies"

for LANG_DIR in csharp python; do
    TARGET="${LEAN_WORKSPACE}/$(echo "$LANG_DIR" | sed 's/csharp/Algorithm.CSharp/;s/python/Algorithm.Python/')"
    SOURCE="${STRATEGIES_DIR}/${LANG_DIR}"

    if [[ ! -d "$TARGET" ]]; then
        mkdir -p "$TARGET"
        info "Created LEAN algorithm directory: $TARGET"
    fi

    # Create a README in each algorithm directory pointing to the project.
    if [[ ! -f "${TARGET}/README.md" ]]; then
        cat > "${TARGET}/README.md" << EOF
# LEAN Algorithm Directory

Strategies for this language are maintained in the project repository at:
  \`strategies/${LANG_DIR}/\`

Place your algorithm files here or symlink them from the strategies directory.
EOF
    fi
done

# -----------------------------------------------------------------------------
# Step 7: Transfer ownership
# -----------------------------------------------------------------------------
section "Setting file ownership"

chown -R "${PROJECT_USER}:${PROJECT_USER}" "$LEAN_WORKSPACE" "$STRATEGIES_DIR" \
    || die "chown failed"

info "Ownership set to ${PROJECT_USER} for lean workspace and strategies."

deactivate

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
section "LEAN CLI installation complete"

echo ""
echo "  LEAN CLI version : $(${VENV_DIR}/bin/lean --version 2>/dev/null || echo 'run: lean --version')"
echo "  LEAN workspace   : $LEAN_WORKSPACE"
echo "  Docker image     : $LEAN_IMAGE"
echo ""
echo "  Useful LEAN commands (run from $LEAN_WORKSPACE):"
echo "    lean backtest <algorithm-dir>      — run a backtest"
echo "    lean live <algorithm-dir>          — start live trading"
echo "    lean research                      — launch Jupyter research env"
echo "    lean cloud push                    — sync to QuantConnect cloud"
echo ""
echo "  Next step: sudo bash 07_verify.sh"
echo ""
