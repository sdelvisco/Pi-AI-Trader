"""
dashboard.py — HTML page routes
================================
Serves the rendered HTML pages of the trading dashboard.
All actual data is fetched by the frontend via the /api routes (api.py).
"""

from flask import Blueprint, render_template

dashboard_bp = Blueprint("dashboard", __name__)


@dashboard_bp.route("/")
def index():
    """
    Dashboard home page — displays trading status, open positions,
    recent trades, and system health.
    """
    return render_template("dashboard.html")


@dashboard_bp.route("/logs")
def logs():
    """
    Log viewer page — displays recent LEAN output and system logs.
    Log entries are streamed in real time via WebSocket from the API layer.
    """
    return render_template("logs.html")


@dashboard_bp.route("/performance")
def performance():
    """
    Performance page — displays backtest and live trading statistics:
    Sharpe ratio, drawdown, win rate, P&L curves.
    """
    return render_template("performance.html")


@dashboard_bp.route("/settings")
def settings():
    """
    Settings page — allows reviewing (not editing) active configuration.
    Credentials are never displayed; only non-sensitive settings are shown.
    """
    return render_template("settings.html")
