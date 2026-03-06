"""
api.py — REST API routes
=========================
Provides JSON endpoints consumed by the dashboard frontend.
All endpoints are local-only (enforced at the network layer by UFW).

Endpoints:
  GET  /api/status          — LEAN engine and system status
  GET  /api/positions        — current open positions
  GET  /api/trades           — recent trade history
  GET  /api/performance      — portfolio performance metrics
  GET  /api/health           — system resource usage (CPU, RAM, temp)
  POST /api/control/pause    — pause the trading algorithm
  POST /api/control/resume   — resume a paused algorithm
  POST /api/control/stop     — emergency stop (graceful shutdown)
"""

import os
import json
import subprocess
from pathlib import Path
from datetime import datetime, timezone

from flask import Blueprint, jsonify, current_app, request, abort

api_bp = Blueprint("api", __name__)


# ---------------------------------------------------------------------------
# Helper utilities
# ---------------------------------------------------------------------------

def _lean_results_dir() -> Path:
    """Return the path to the LEAN results directory."""
    return current_app.config["LEAN_RESULTS_DIR"]


def _read_json_safe(path: Path) -> dict | list | None:
    """Read and parse a JSON file, returning None on any error."""
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except (FileNotFoundError, json.JSONDecodeError, PermissionError):
        return None


# ---------------------------------------------------------------------------
# Status endpoint
# ---------------------------------------------------------------------------

@api_bp.route("/status")
def status():
    """
    Returns the current status of the LEAN trading engine.

    Checks whether the lean-trader.service systemd unit is active and
    reports its state, uptime, and the active algorithm name.
    """
    lean_active = False
    lean_status = "unknown"

    try:
        result = subprocess.run(
            ["systemctl", "is-active", "lean-trader"],
            capture_output=True,
            text=True,
            timeout=5,
        )
        lean_status = result.stdout.strip()
        lean_active = lean_status == "active"
    except (subprocess.TimeoutExpired, FileNotFoundError):
        lean_status = "unavailable"

    return jsonify(
        {
            "lean_active": lean_active,
            "lean_status": lean_status,
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "paper_trading": os.environ.get("ALPACA_PAPER_TRADING", "unknown"),
        }
    )


# ---------------------------------------------------------------------------
# Positions endpoint
# ---------------------------------------------------------------------------

@api_bp.route("/positions")
def positions():
    """
    Returns current open positions from the most recent LEAN state file.

    LEAN writes a live-results.json to the results directory during live
    trading. This endpoint reads that file for position data.
    """
    results_dir = _lean_results_dir()

    # LEAN names the result file after the algorithm; try to find it.
    result_files = list(results_dir.glob("**/live-*.json")) if results_dir.exists() else []

    if not result_files:
        return jsonify({"positions": [], "message": "No live results file found"})

    # Use the most recently modified results file.
    latest = max(result_files, key=lambda p: p.stat().st_mtime)
    data = _read_json_safe(latest)

    if data is None:
        return jsonify({"positions": [], "message": "Could not parse results file"})

    # Extract holdings from LEAN's result schema.
    holdings = data.get("Holdings", {})
    positions_list = [
        {
            "symbol": symbol,
            "quantity": holding.get("Quantity", 0),
            "average_price": holding.get("AveragePrice", 0),
            "market_value": holding.get("MarketValue", 0),
            "unrealized_pnl": holding.get("UnrealizedPnL", 0),
        }
        for symbol, holding in holdings.items()
        if holding.get("Quantity", 0) != 0
    ]

    return jsonify({"positions": positions_list})


# ---------------------------------------------------------------------------
# Trade history endpoint
# ---------------------------------------------------------------------------

@api_bp.route("/trades")
def trades():
    """
    Returns recent order/trade history from the LEAN transaction log.
    """
    results_dir = _lean_results_dir()
    transaction_log = results_dir / "transaction-log.json"

    data = _read_json_safe(transaction_log)
    if data is None:
        return jsonify({"trades": [], "message": "No transaction log found"})

    # Return the most recent 50 trades, newest first.
    trade_list = data if isinstance(data, list) else []
    return jsonify({"trades": trade_list[-50:][::-1]})


# ---------------------------------------------------------------------------
# Performance endpoint
# ---------------------------------------------------------------------------

@api_bp.route("/performance")
def performance():
    """
    Returns portfolio performance statistics from LEAN's output.
    """
    results_dir = _lean_results_dir()
    stats_files = list(results_dir.glob("**/*Statistics*.json")) if results_dir.exists() else []

    if not stats_files:
        return jsonify({"performance": {}, "message": "No statistics file found"})

    latest = max(stats_files, key=lambda p: p.stat().st_mtime)
    data = _read_json_safe(latest)

    if data is None:
        return jsonify({"performance": {}, "message": "Could not parse statistics"})

    return jsonify({"performance": data})


# ---------------------------------------------------------------------------
# System health endpoint
# ---------------------------------------------------------------------------

@api_bp.route("/health")
def health():
    """
    Returns system resource metrics: CPU, RAM, temperature, disk usage.
    Uses /proc and /sys interfaces available on all Linux systems.
    """
    metrics = {}

    # CPU temperature (Raspberry Pi thermal zone)
    try:
        with open("/sys/class/thermal/thermal_zone0/temp", "r") as f:
            temp_millidegrees = int(f.read().strip())
            metrics["cpu_temp_c"] = round(temp_millidegrees / 1000, 1)
    except (FileNotFoundError, ValueError):
        metrics["cpu_temp_c"] = None

    # Memory usage from /proc/meminfo
    try:
        meminfo = {}
        with open("/proc/meminfo", "r") as f:
            for line in f:
                parts = line.split()
                if len(parts) >= 2:
                    meminfo[parts[0].rstrip(":")] = int(parts[1])

        total_kb = meminfo.get("MemTotal", 0)
        available_kb = meminfo.get("MemAvailable", 0)
        used_kb = total_kb - available_kb

        metrics["memory_total_mb"] = round(total_kb / 1024, 1)
        metrics["memory_used_mb"] = round(used_kb / 1024, 1)
        metrics["memory_percent"] = round((used_kb / total_kb) * 100, 1) if total_kb else 0
    except (FileNotFoundError, ZeroDivisionError):
        metrics["memory_total_mb"] = None
        metrics["memory_used_mb"] = None
        metrics["memory_percent"] = None

    # Disk usage of the root filesystem
    try:
        stat = os.statvfs("/")
        total = stat.f_blocks * stat.f_frsize
        free = stat.f_bfree * stat.f_frsize
        used = total - free
        metrics["disk_total_gb"] = round(total / (1024 ** 3), 1)
        metrics["disk_used_gb"] = round(used / (1024 ** 3), 1)
        metrics["disk_percent"] = round((used / total) * 100, 1) if total else 0
    except OSError:
        metrics["disk_total_gb"] = None

    metrics["timestamp"] = datetime.now(timezone.utc).isoformat()
    return jsonify(metrics)


# ---------------------------------------------------------------------------
# Control endpoints
# ---------------------------------------------------------------------------

@api_bp.route("/control/pause", methods=["POST"])
def control_pause():
    """
    Signals the LEAN algorithm to pause order execution.
    The engine continues running but does not submit new orders.
    """
    # TODO: Implement LEAN pause signal (write a control file that the
    # algorithm checks, or use LEAN's live control API if available).
    return jsonify({"status": "not_implemented", "message": "Pause control coming soon"})


@api_bp.route("/control/resume", methods=["POST"])
def control_resume():
    """Resumes a paused algorithm."""
    return jsonify({"status": "not_implemented", "message": "Resume control coming soon"})


@api_bp.route("/control/stop", methods=["POST"])
def control_stop():
    """
    Emergency stop: gracefully shuts down the lean-trader systemd service.
    This triggers LEAN's shutdown handler, which liquidates positions if
    the algorithm is configured to do so.
    """
    try:
        result = subprocess.run(
            ["sudo", "systemctl", "stop", "lean-trader"],
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            return jsonify({"status": "stopping", "message": "LEAN trader stop initiated"})
        else:
            return jsonify(
                {"status": "error", "message": result.stderr.strip() or "Stop command failed"},
            ), 500
    except subprocess.TimeoutExpired:
        return jsonify({"status": "error", "message": "Stop command timed out"}), 500
