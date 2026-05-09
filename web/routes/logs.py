"""
logs.py — Real-time LEAN log streaming via SocketIO
=====================================================
Provides a Flask Blueprint (logs_bp) and the background thread that tails
the LEAN log file, emitting each new line to all connected clients.

Threading model
---------------
A single daemon thread (_tail_log_file) runs for the lifetime of the process,
tailing the log file in a poll loop (readline + 250 ms sleep when idle).  The
thread is started on the first client connect and is never stopped — stopping it
would silently drop log lines for every subsequent client.  This is safe because:
  - The thread is a daemon (it won't keep the process alive on shutdown).
  - All emits go to the namespace-level broadcast, so any connected client
    receives them regardless of when it connected.
  - The thread is only ever created once (guarded by _thread_lock) even if
    many clients connect simultaneously.

Circular-import avoidance
-------------------------
This module never imports from web.app at module level.  Instead, app.py calls
init_socketio(sio) after the SocketIO instance has been constructed, injecting
the dependency here.  The @sio.on(...) decorators are therefore registered
programmatically inside init_socketio rather than at module load time.
"""

import logging
import threading
import time
from pathlib import Path

from flask import Blueprint
from flask_socketio import emit as _emit_to_caller

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Blueprint
# ---------------------------------------------------------------------------

# No url_prefix needed — this blueprint carries no HTTP routes.
# The /logs HTML page is served by dashboard_bp in dashboard.py.
logs_bp = Blueprint("logs", __name__)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

LEAN_LOG_PATH = Path("/opt/lean-engine/Launcher/bin/Release/log.txt")

# ---------------------------------------------------------------------------
# Module-level state shared between the background thread and event handlers
# ---------------------------------------------------------------------------

_socketio = None          # Injected by init_socketio(); never imported directly.
_thread: threading.Thread | None = None
_thread_lock = threading.Lock()
_stop_event = threading.Event()


# ---------------------------------------------------------------------------
# Background tail thread
# ---------------------------------------------------------------------------

def _tail_log_file() -> None:
    """
    Tail LEAN_LOG_PATH indefinitely, emitting each new line via SocketIO.

    Seek-to-end behaviour: on every (re)open of the file we seek to the end
    before entering the read loop.  This prevents the initial 15+ MB of
    historical log content from being dumped on every client connect; clients
    only see lines written *after* the web service started (or after a
    FileNotFoundError recovery cycle).

    The loop runs until _stop_event is set (currently never, by design — see
    module docstring) or until the process exits.
    """
    while not _stop_event.is_set():
        try:
            with open(LEAN_LOG_PATH, "r", encoding="utf-8", errors="replace") as fh:
                # Seek to the end so we only tail *new* content.
                fh.seek(0, 2)

                # Inner read loop: drain any new lines written since last poll.
                while not _stop_event.is_set():
                    line = fh.readline()
                    if line:
                        stripped = line.rstrip()
                        if stripped:
                            _socketio.emit(
                                "log_line",
                                {"data": stripped},
                                namespace="/",
                            )
                    else:
                        # No new data — yield the thread for 250 ms before retrying.
                        time.sleep(0.25)

        except FileNotFoundError:
            # LEAN is probably not running yet; inform the client and wait.
            _socketio.emit(
                "log_line",
                {"data": "[LEAN log file not found — is lean-trader running?]"},
                namespace="/",
            )
            time.sleep(10)

        except Exception as exc:
            logger.error("log-tailer: unexpected error: %s", exc, exc_info=True)
            time.sleep(5)


# ---------------------------------------------------------------------------
# SocketIO initialisation (called from app.py after socketio is created)
# ---------------------------------------------------------------------------

def init_socketio(sio) -> None:
    """
    Inject the SocketIO instance and register event handlers.

    Called once from app.py immediately after `socketio = SocketIO(app, ...)`.
    Registering handlers here (rather than with module-level decorators) avoids
    the circular import that would arise if this file imported `socketio` from
    web.app before web.app had finished initialising.
    """
    global _socketio
    _socketio = sio

    @sio.on("connect")
    def on_connect():
        """Start the tail thread on first connect."""
        global _thread

        # Guard with a lock so simultaneous connects don't spawn multiple threads.
        with _thread_lock:
            if _thread is None or not _thread.is_alive():
                _stop_event.clear()
                _thread = threading.Thread(
                    target=_tail_log_file,
                    daemon=True,
                    name="lean-log-tailer",
                )
                _thread.start()
                logger.info("log-tailer thread started")

    @sio.on("request_history")
    def on_request_history():
        """Replay last 50 lines to the requesting client, then confirm connection."""
        # Read the final 32 KB — covers ~50 typical log lines without scanning
        # the full file backward byte-by-byte.
        try:
            with open(LEAN_LOG_PATH, "r", encoding="utf-8", errors="replace") as fh:
                fh.seek(0, 2)
                file_size = fh.tell()
                fh.seek(max(0, file_size - 32 * 1024))
                recent_lines = fh.readlines()[-50:]
            for line in recent_lines:
                _emit_to_caller("log_line", {"data": line.rstrip()})
        except FileNotFoundError:
            _emit_to_caller(
                "log_line",
                {"data": "[LEAN log file not found — is lean-trader running?]"},
            )

        _emit_to_caller("log_line", {"data": "[Connected to log stream]"})

    @sio.on("disconnect")
    def on_disconnect():
        # Intentionally do NOT stop the tail thread — it keeps running so the
        # next client to connect receives live lines immediately.
        logger.debug("SocketIO client disconnected from log stream")
