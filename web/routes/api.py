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
    Returns current open positions from the LEAN live-state file.

    LEAN writes algorithm state to a JSON file named after the algorithm
    class, e.g. PiAiTrader.Strategies.DualMomentumV2.json, directly in the
    Launcher's Release output directory (LEAN_RESULTS_DIR).

    LEAN uses abbreviated keys in the holdings objects:
      "a"  = average price
      "q"  = quantity
      "p"  = last price
      "v"  = market value
      "u"  = unrealized P&L
      "up" = unrealized P&L percent

    Cash is reported under cash.USD.amount; total portfolio value is
    the sum of all position market values plus the cash balance.
    """
    results_dir = _lean_results_dir()

    # -------------------------------------------------------------------
    # Locate the LEAN live-state file.
    # Primary: look for the exact well-known filename in LEAN_RESULTS_DIR.
    # Fallback: glob for any JSON whose name contains the algorithm class.
    # -------------------------------------------------------------------
    algo_name   = "PiAiTrader.Strategies.DualMomentumV2"
    direct_path = results_dir / f"{algo_name}.json"

    if results_dir.exists() and direct_path.exists():
        state_file = direct_path
    else:
        # Fallback: search recursively for a file whose name contains the
        # algorithm class name (handles date-stamped variants LEAN may write).
        candidates = (
            list(results_dir.glob(f"**/*{algo_name}*.json"))
            if results_dir.exists()
            else []
        )
        if not candidates:
            return jsonify({"positions": [], "message": "No live results file found"})
        state_file = max(candidates, key=lambda p: p.stat().st_mtime)

    data = _read_json_safe(state_file)
    if data is None:
        return jsonify({"positions": [], "message": "Could not parse results file"})

    # -------------------------------------------------------------------
    # Parse holdings using LEAN's abbreviated key schema.
    # -------------------------------------------------------------------
    holdings = data.get("holdings", {})
    positions_list = [
        {
            "symbol":           symbol,
            "symbolValue":      symbol.split()[0] if ' ' in symbol else symbol,
            "quantity":         holding.get("q", 0),
            "average_price":    holding.get("a", 0),
            "last_price":       holding.get("p", 0),
            "market_value":     holding.get("v", 0),
            "unrealized_pnl":   holding.get("u", 0),
            "unrealized_pnl_pct": holding.get("up", 0),
        }
        for symbol, holding in holdings.items()
        if holding.get("q", 0) != 0
    ]

    # -------------------------------------------------------------------
    # Extract cash balance and compute total portfolio value.
    # LEAN stores cash as: cash → USD → amount
    # -------------------------------------------------------------------
    cash_usd = (
        data.get("cash", {})
            .get("USD", {})
            .get("amount", 0)
    )
    total_market_value = sum(p["market_value"] for p in positions_list)
    total_portfolio_value = total_market_value + cash_usd

    return jsonify(
        {
            "positions":            positions_list,
            "cash_usd":             cash_usd,
            "total_portfolio_value": total_portfolio_value,
        }
    )


# ---------------------------------------------------------------------------
# Trade history endpoint
# ---------------------------------------------------------------------------

@api_bp.route("/trades")
def trades():
    """
    Returns recent order/trade history from the LEAN order-events file.

    LEAN writes order events to a file named:
      PiAiTrader.Strategies.DualMomentumV2-<date>-order-events.json
    where <date> is a UTC timestamp appended at run start.  If multiple
    files exist (e.g. after engine restarts) the most recently modified
    one is used.
    """
    results_dir = _lean_results_dir()

    # Find all order-event log files for this algorithm.
    order_event_files = (
        list(results_dir.glob("**/PiAiTrader.Strategies.DualMomentumV2-*-order-events.json"))
        if results_dir.exists()
        else []
    )

    if not order_event_files:
        return jsonify({"trades": [], "message": "No order-events file found"})

    # Use the most recently modified file (latest engine run).
    latest = max(order_event_files, key=lambda p: p.stat().st_mtime)
    data = _read_json_safe(latest)

    if data is None:
        return jsonify({"trades": [], "message": "Could not parse order-events file"})

    # Return the most recent 50 order events, newest first.
    trade_list = data if isinstance(data, list) else []
    return jsonify({"trades": trade_list[-50:][::-1]})


# ---------------------------------------------------------------------------
# Performance endpoint
# ---------------------------------------------------------------------------

@api_bp.route("/performance")
def performance():
    """
    Returns portfolio performance statistics from LEAN's 10-minute report.

    LEAN writes rolling performance snapshots to files named:
      PiAiTrader.Strategies.DualMomentumV2-<date>_10minute.json
    If multiple snapshots exist (engine restarts / multiple runs) the most
    recently modified one is used.
    """
    results_dir = _lean_results_dir()

    # Find all 10-minute performance snapshot files for this algorithm.
    perf_files = (
        list(results_dir.glob("**/PiAiTrader.Strategies.DualMomentumV2-*_10minute.json"))
        if results_dir.exists()
        else []
    )

    if not perf_files:
        return jsonify({"performance": {}, "message": "No performance file found"})

    # Use the most recently modified snapshot (latest engine run).
    latest = max(perf_files, key=lambda p: p.stat().st_mtime)
    data = _read_json_safe(latest)

    if data is None:
        return jsonify({"performance": {}, "message": "Could not parse performance file"})

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
