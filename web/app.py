"""
app.py — Pi-AI-Trader Flask Web Interface
=========================================
Entry point for the Flask web application. Provides a local-network dashboard
for monitoring and controlling the LEAN algorithmic trading engine.

Responsibilities:
  - Serve the web UI to the local network only
  - Expose a REST API for status, positions, and controls
  - Stream real-time LEAN log output via Server-Sent Events
  - Handle manual trade controls (pause, resume, emergency stop)
  - Display portfolio performance and trade history

Security:
  - Binds to all interfaces but should only be reachable on the LAN
    (UFW blocks external access; the Pi is on a private network)
  - No user authentication is implemented by default since this is a
    local-only tool on a dedicated device. Add Flask-Login if needed.
  - All sensitive config is read from environment variables, never hardcoded.

Usage (development):
  python3 -m web.app

Usage (production via systemd):
  Managed by lean-web.service using Flask-SocketIO's built-in server
"""

import os
import json
import subprocess
from pathlib import Path
from datetime import datetime

from flask import Flask, render_template, jsonify, abort
from flask_socketio import SocketIO

# ---------------------------------------------------------------------------
# Application factory
# ---------------------------------------------------------------------------

def create_app() -> Flask:
    """Create and configure the Flask application."""
    app = Flask(__name__)

    # -----------------------------------------------------------------------
    # Configuration
    # -----------------------------------------------------------------------

    # Secret key for signing session cookies. Read from environment — NEVER
    # hardcode this. Generate with: python3 -c "import secrets; print(secrets.token_hex(32))"
    app.config["SECRET_KEY"] = os.environ.get("FLASK_SECRET_KEY") or _generate_warning_key()

    # Project root, derived from this file's location (web/ → project root).
    app.config["PROJECT_ROOT"] = Path(__file__).parent.parent

    # Path to the LEAN results directory for reading trade logs and reports.
    # LEAN (when run via the Launcher) writes all output — live-state files,
    # order-event logs, and performance JSON — directly into the Launcher's
    # Release output directory, NOT into a project-local lean/Results/ folder.
    # The default below matches the standard Pi-AI-Trader deployment path.
    # Override with the LEAN_RESULTS_DIR environment variable if needed.
    app.config["LEAN_RESULTS_DIR"] = Path(
        os.environ.get("LEAN_RESULTS_DIR", "/opt/lean-engine/Launcher/bin/Release")
    )

    # -----------------------------------------------------------------------
    # Register blueprints
    # -----------------------------------------------------------------------
    from .routes.dashboard import dashboard_bp
    from .routes.api import api_bp
    # logs_bp carries no HTTP routes itself — it provides the SocketIO event
    # handlers and background tail thread for the live log streaming feature.
    from .routes.logs import logs_bp

    app.register_blueprint(dashboard_bp)
    app.register_blueprint(api_bp, url_prefix="/api")
    app.register_blueprint(logs_bp)

    return app


def _generate_warning_key() -> str:
    """
    Generate a random secret key if none is configured, and warn the operator.

    This key changes on every restart, invalidating all existing sessions.
    Always set FLASK_SECRET_KEY in /etc/tradingpi/web.env for production.
    """
    import secrets
    import warnings
    key = secrets.token_hex(32)
    warnings.warn(
        "FLASK_SECRET_KEY is not set. A random key has been generated for this "
        "session. Set FLASK_SECRET_KEY in /etc/tradingpi/web.env to persist sessions.",
        RuntimeWarning,
        stacklevel=2,
    )
    return key


# ---------------------------------------------------------------------------
# Application and SocketIO instance
# ---------------------------------------------------------------------------

app = create_app()

# Threading mode is correct here because we run Flask-SocketIO's built-in
# server (not Gunicorn). The built-in server handles WebSocket upgrades
# natively, and the single-worker threading model is sufficient for a
# local-network single-user dashboard.
#
# cors_allowed_origins="*" is safe here: this dashboard is local-network-only
# and UFW blocks external access, so wildcard CORS is equivalent to
# "same-origin" in practice.  The "same-origin" string is unreliable when the
# server is accessed by IP address (e.g. http://192.168.1.x:5000) rather than
# hostname — Flask-SocketIO's internal check rejects the connection, causing
# every browser client to see an immediate disconnect.
socketio = SocketIO(app, cors_allowed_origins="*", async_mode="threading")

# Inject the SocketIO instance into the logs blueprint so it can register its
# event handlers and run the background log-tail thread.  This call happens
# after socketio is constructed to avoid the circular import that would arise
# if logs.py tried to import socketio from this module at load time.
from .routes.logs import init_socketio as _init_logs_socketio  # noqa: E402
_init_logs_socketio(socketio)


# ---------------------------------------------------------------------------
# Entry point — run directly with: python3 -m web.app
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    socketio.run(app, host="0.0.0.0", port=5000, debug=False, use_reloader=False)
