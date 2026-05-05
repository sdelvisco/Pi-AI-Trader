#!/usr/bin/env bash
# =============================================================================
# health_check.sh — Daily health check for lean-trader
# =============================================================================
# Checks:
#   1. lean-trader systemd service is active
#   2. No error-level journal entries in the past 24 hours
#   3. "DualMomentumV2 Initialized" appears in the current service run
#      (detects silent restart failures)
#
# Output:
#   Normal log : /var/log/pi-ai-trader/health.log
#   Alert log  : /var/log/pi-ai-trader/alerts.log  (only written on failure)
#
# Cron setup (run as pi-admin, once daily at 07:00):
#   0 7 * * * /home/pi-admin/Pi-AI-Trader/scripts/health_check.sh
#
# To install:
#   crontab -e
#   # add the line above, then save
#
# Alert integration:
#   Currently alerts are written to the alerts.log file only.
#   To add email/SMS alerts, replace the alert() function body.
# =============================================================================

set -euo pipefail

SERVICE="lean-trader"
LOG_DIR="/var/log/pi-ai-trader"
LOG_FILE="${LOG_DIR}/health.log"
ALERT_FILE="${LOG_DIR}/alerts.log"

mkdir -p "${LOG_DIR}"

# Rotate logs when they exceed 5 MB to prevent unbounded growth.
if [ -f "${LOG_FILE}" ] && [ "$(stat -c%s "${LOG_FILE}" 2>/dev/null || echo 0)" -gt 5242880 ]; then
    mv "${LOG_FILE}" "${LOG_FILE}.1"
fi
if [ -f "${ALERT_FILE}" ] && [ "$(stat -c%s "${ALERT_FILE}" 2>/dev/null || echo 0)" -gt 5242880 ]; then
    mv "${ALERT_FILE}" "${ALERT_FILE}.1"
fi

# -----------------------------------------------------------------------------
# Helpers
# -----------------------------------------------------------------------------
ts() { date '+%Y-%m-%d %H:%M:%S'; }

log() {
    echo "[$(ts)] $*" | tee -a "${LOG_FILE}"
}

alert() {
    local msg="$*"
    echo "[$(ts)] ALERT: ${msg}" | tee -a "${ALERT_FILE}" "${LOG_FILE}"
    # -------------------------------------------------------------------------
    # TODO: Add your notification hook here, for example:
    #   curl -s -X POST "https://ntfy.sh/pi-ai-trader" -d "ALERT: ${msg}"
    # -------------------------------------------------------------------------
}

# -----------------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------------
log "========================================="
log "Health check started"
HEALTHY=true

# --- 1. Service active? -------------------------------------------------------
if systemctl is-active --quiet "${SERVICE}"; then
    log "OK  : ${SERVICE} is active"
else
    STATE="$(systemctl is-active "${SERVICE}" 2>/dev/null || echo unknown)"
    alert "${SERVICE} is NOT running (state: ${STATE})"
    HEALTHY=false
fi

# --- 2. Error-level journal entries in past 24h? ------------------------------
ERROR_COUNT="$(journalctl -u "${SERVICE}" --since "24 hours ago" -p err --no-pager -q 2>/dev/null | wc -l)"
if [ "${ERROR_COUNT}" -gt 0 ]; then
    alert "${ERROR_COUNT} error-level journal entries in the past 24h"
    journalctl -u "${SERVICE}" --since "24 hours ago" -p err --no-pager -q \
        >> "${ALERT_FILE}" 2>/dev/null || true
    HEALTHY=false
else
    log "OK  : No error-level entries in past 24h"
fi

# --- 3. DualMomentumV2 Initialized in current run? ---------------------------
# Get the timestamp when the service last became active.
ACTIVE_SINCE="$(systemctl show "${SERVICE}" --property=ActiveEnterTimestamp --value 2>/dev/null || true)"
if [ -n "${ACTIVE_SINCE}" ] && [ "${ACTIVE_SINCE}" != "n/a" ]; then
    INIT_FOUND="$(journalctl -u "${SERVICE}" --since "${ACTIVE_SINCE}" --no-pager -q 2>/dev/null \
        | grep -c "DualMomentumV2 Initialized" || true)"
    if [ "${INIT_FOUND}" -gt 0 ]; then
        log "OK  : DualMomentumV2 Initialized found (since ${ACTIVE_SINCE})"
    else
        alert "DualMomentumV2 Initialized NOT found since service last started (${ACTIVE_SINCE})"
        HEALTHY=false
    fi
else
    log "WARN: Could not determine service start time; skipping initialization check"
fi

# --- Summary -----------------------------------------------------------------
if ${HEALTHY}; then
    log "Health check PASSED"
else
    log "Health check FAILED — details in ${ALERT_FILE}"
fi

log "========================================="
