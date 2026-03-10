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
  cd web && flask run --host=0.0.0.0 --port=5000

Usage (production via systemd):
  Managed by lean-web.service using Gunicorn
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
    """
    Create and configure the Flask application.

    Using the application factory pattern allows the app to be created
    with different configurations (testing, development, production) and
    makes it easier to manage the app lifecycle in Gunicorn workers.
    """
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

    app.register_blueprint(dashboard_bp)
    app.register_blueprint(api_bp, url_prefix="/api")

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
# Application and SocketIO instance (used by Gunicorn and the module)
# ---------------------------------------------------------------------------

app = create_app()

# SocketIO enables real-time WebSocket communication for live log streaming
# and position updates without polling. Falls back to long-polling if
# WebSockets are not available.
socketio = SocketIO(app, cors_allowed_origins="same-origin", async_mode="threading")


# ---------------------------------------------------------------------------
# Development server entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    # Run with SocketIO's development server (not Gunicorn) for local testing.
    socketio.run(
        app,
        host="0.0.0.0",
        port=5000,
        debug=True,
        use_reloader=True,
    )
