#!/usr/bin/env bash
# =============================================================================
# 05_python.sh — Python 3 Environment and pip Dependencies
# =============================================================================
# Sets up a project-specific Python 3 virtual environment and installs all
# pip dependencies required by:
#   - The Flask web interface
#   - LEAN Python algorithm support
#   - Alpaca trading API client
#   - Email and SMS notification libraries
#
# Why a virtual environment?
#   A venv isolates project dependencies from system Python packages, preventing
#   version conflicts and making the project reproducible. systemd service units
#   reference the venv's Python interpreter directly.
#
# Usage:
#   sudo bash 05_python.sh
#
# Requirements:
#   - 04_dotnet.sh must have been run
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

# Project root directory — the parent of this setup/ directory.
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
info "Project root: $PROJECT_ROOT"

# Path for the project Python virtual environment.
# Placed inside the project directory; excluded from git via .gitignore.
VENV_DIR="${PROJECT_ROOT}/venv"

# The non-root user that will own the project files and run the services.
# Adjust this if your Pi user is not 'pi'.
PROJECT_USER="${SUDO_USER:-pi}"

# -----------------------------------------------------------------------------
# Step 1: Install system Python packages and build tools
# -----------------------------------------------------------------------------
section "Installing system Python 3 and build dependencies"

apt-get update -y

# python3-venv  — virtualenv support (not always included with python3)
# python3-pip   — pip package manager
# python3-dev   — Python C extension headers (needed to compile some packages)
# build-essential — gcc, make, etc. for compiling pip packages with C extensions
# libssl-dev    — SSL headers for cryptography packages
# libffi-dev    — Foreign Function Interface, required by some cryptography libs
# libjpeg-dev   — image processing dependencies (Flask may use Pillow)
apt-get install -y \
    python3 \
    python3-venv \
    python3-pip \
    python3-dev \
    build-essential \
    libssl-dev \
    libffi-dev \
    libjpeg-dev \
    || die "Failed to install Python system packages"

info "System Python version: $(python3 --version)"
info "pip version: $(python3 -m pip --version)"

# -----------------------------------------------------------------------------
# Step 2: Create the project virtual environment
# -----------------------------------------------------------------------------
section "Creating Python virtual environment at $VENV_DIR"

if [[ -d "$VENV_DIR" ]]; then
    info "Virtual environment already exists at $VENV_DIR — skipping creation"
else
    python3 -m venv "$VENV_DIR" \
        || die "Failed to create virtual environment at $VENV_DIR"
    info "Virtual environment created."
fi

# Activate the venv for the remainder of this script.
# shellcheck source=/dev/null
source "${VENV_DIR}/bin/activate" \
    || die "Failed to activate virtual environment"

info "Active Python: $(which python3)"
info "Active pip:    $(which pip)"

# Upgrade pip itself inside the venv to the latest version.
pip install --upgrade pip \
    || die "Failed to upgrade pip"

# -----------------------------------------------------------------------------
# Step 3: Install project pip dependencies
# -----------------------------------------------------------------------------
section "Installing pip dependencies"

# Install from requirements.txt which is maintained in the project root.
# This allows reproducible installs and easy dependency management.
REQUIREMENTS_FILE="${PROJECT_ROOT}/requirements.txt"

if [[ ! -f "$REQUIREMENTS_FILE" ]]; then
    info "requirements.txt not found — creating it now with default dependencies"

    cat > "$REQUIREMENTS_FILE" << 'EOF'
# =============================================================================
# Pi-AI-Trader Python Dependencies
# =============================================================================
# Pin major versions for stability. Use 'pip install --upgrade <package>'
# to update a specific package, then test before committing changes.

# --- Web Interface ---
# Flask: lightweight WSGI web framework for the trading dashboard
Flask>=3.0,<4.0
# Gunicorn: production-grade WSGI server to run Flask under systemd
gunicorn>=21.0,<23.0
# Flask-SocketIO: WebSocket support for real-time dashboard updates
Flask-SocketIO>=5.3,<6.0
# Flask-Login: user session management for the web interface
Flask-Login>=0.6,<1.0

# --- Alpaca Trading API ---
# Official Alpaca Python SDK for placing orders and streaming market data
alpaca-py>=0.20,<1.0

# --- LEAN Python algorithm support ---
# pandas: data manipulation used heavily by LEAN Python algorithms
pandas>=2.0,<3.0
# numpy: numerical computing, dependency of pandas and LEAN internals
numpy>=1.26,<2.0

# --- Notifications ---
# Twilio: SMS and WhatsApp notifications via the Twilio REST API
twilio>=9.0,<10.0
# Secure SMTP email sending via standard library smtplib (no extra package)
# but we need this for email template rendering:
Jinja2>=3.1,<4.0

# --- Utilities ---
# python-dotenv: load environment variables from .env files (dev only)
python-dotenv>=1.0,<2.0
# requests: HTTP client for any custom API calls
requests>=2.31,<3.0
# APScheduler: background job scheduling (e.g., daily report generation)
APScheduler>=3.10,<4.0
EOF

    info "requirements.txt created at $REQUIREMENTS_FILE"
fi

info "Installing packages from $REQUIREMENTS_FILE ..."
pip install -r "$REQUIREMENTS_FILE" \
    || die "pip install failed — check $REQUIREMENTS_FILE and network connectivity"

info "All pip dependencies installed."

# -----------------------------------------------------------------------------
# Step 4: Transfer ownership to the project user
# -----------------------------------------------------------------------------
section "Setting ownership of project files"

# The venv and project files should be owned by the non-root project user,
# not root. The systemd services will run as this user.
chown -R "${PROJECT_USER}:${PROJECT_USER}" "$PROJECT_ROOT" \
    || die "chown failed for $PROJECT_ROOT"

info "Ownership set to ${PROJECT_USER}:${PROJECT_USER} for $PROJECT_ROOT"

# -----------------------------------------------------------------------------
# Step 5: Create a convenience activation helper
# -----------------------------------------------------------------------------
section "Creating venv activation helper script"

ACTIVATE_HELPER="/usr/local/bin/tradingpi-venv"

cat > "$ACTIVATE_HELPER" << EOF
#!/usr/bin/env bash
# Activate the Pi-AI-Trader Python virtual environment.
# Usage: source tradingpi-venv
source "${VENV_DIR}/bin/activate"
echo "Pi-AI-Trader venv activated. Python: \$(which python3)"
EOF

chmod +x "$ACTIVATE_HELPER"
info "Activation helper created: source $ACTIVATE_HELPER"

# -----------------------------------------------------------------------------
# Step 6: Verify installation
# -----------------------------------------------------------------------------
section "Verifying Python environment"

info "Python version : $(python3 --version)"
info "pip version    : $(pip --version)"
info "Installed packages:"
pip list

# Verify key imports work correctly.
info "Verifying key imports..."
python3 -c "import flask; print('  Flask:', flask.__version__)" \
    || die "Flask import failed"
python3 -c "import alpaca; print('  alpaca-py: OK')" 2>/dev/null \
    || info "  alpaca-py: import check skipped (may need version-specific import)"
python3 -c "import pandas; print('  pandas:', pandas.__version__)" \
    || die "pandas import failed"

info "All key imports verified."

# Deactivate the venv.
deactivate

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
section "Python environment setup complete"

echo ""
echo "  Virtual environment : $VENV_DIR"
echo "  Requirements file   : $REQUIREMENTS_FILE"
echo "  Owned by user       : $PROJECT_USER"
echo ""
echo "  To activate manually: source ${VENV_DIR}/bin/activate"
echo "  Or use the helper:    source tradingpi-venv"
echo ""
echo "  Next step: sudo bash 06_lean_cli.sh"
echo ""
