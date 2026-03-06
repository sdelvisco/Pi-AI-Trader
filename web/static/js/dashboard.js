/**
 * dashboard.js — Shared dashboard JavaScript
 *
 * Handles:
 *   - Periodic engine status polling (every 10 seconds)
 *   - System health card updates (every 30 seconds)
 *   - Navbar status badge update
 *   - Footer clock
 */

"use strict";

// ---------------------------------------------------------------------------
// Polling intervals (milliseconds)
// ---------------------------------------------------------------------------
const STATUS_INTERVAL  = 10_000;   // engine status
const HEALTH_INTERVAL  = 30_000;   // CPU temp, memory, disk

// ---------------------------------------------------------------------------
// Status polling
// ---------------------------------------------------------------------------

function refreshStatus() {
    fetch("/api/status")
        .then(r => r.json())
        .then(data => {
            const badge = document.getElementById("engine-status-badge");
            const paperBadge = document.getElementById("paper-trading-badge");

            if (!badge) return;

            if (data.lean_active) {
                badge.innerHTML = '<i class="bi bi-circle-fill text-success"></i> Running';
                badge.className = "badge bg-success bg-opacity-25 text-success border border-success";
            } else if (data.lean_status === "inactive") {
                badge.innerHTML = '<i class="bi bi-circle-fill text-danger"></i> Stopped';
                badge.className = "badge bg-danger bg-opacity-25 text-danger border border-danger";
            } else {
                badge.innerHTML = `<i class="bi bi-circle-fill text-warning"></i> ${data.lean_status}`;
                badge.className = "badge bg-warning bg-opacity-25 text-warning border border-warning";
            }

            // Paper trading indicator
            if (paperBadge) {
                if (data.paper_trading === "true") {
                    paperBadge.textContent = "PAPER";
                    paperBadge.className = "badge bg-info text-dark";
                } else if (data.paper_trading === "false") {
                    paperBadge.textContent = "LIVE $";
                    paperBadge.className = "badge bg-danger";
                }
            }

            // Update the engine status card on the dashboard page if present.
            const engineCard = document.getElementById("card-engine-status");
            if (engineCard) {
                engineCard.textContent = data.lean_active ? "Running" : data.lean_status;
                engineCard.className = "fs-5 fw-bold " + (data.lean_active ? "text-success" : "text-danger");
            }
        })
        .catch(() => {
            const badge = document.getElementById("engine-status-badge");
            if (badge) {
                badge.innerHTML = '<i class="bi bi-wifi-off"></i> Offline';
                badge.className = "badge bg-secondary";
            }
        });
}

// ---------------------------------------------------------------------------
// System health
// ---------------------------------------------------------------------------

function refreshHealth() {
    fetch("/api/health")
        .then(r => r.json())
        .then(data => {
            // CPU temperature — top nav and dashboard card
            const tempText = data.cpu_temp_c !== null ? `${data.cpu_temp_c}°C` : "—";
            const tempColor = data.cpu_temp_c > 75 ? "text-danger"
                            : data.cpu_temp_c > 60 ? "text-warning"
                            : "text-success";

            ["card-cpu-temp", "health-cpu-temp"].forEach(id => {
                const el = document.getElementById(id);
                if (el) {
                    el.textContent = tempText;
                    el.className = tempColor;
                }
            });

            // Memory bar
            const memText = document.getElementById("health-memory-text");
            const memBar  = document.getElementById("health-memory-bar");
            if (memText && data.memory_used_mb !== null) {
                memText.textContent = `${data.memory_used_mb} / ${data.memory_total_mb} MB`;
                memBar.style.width = `${data.memory_percent}%`;
                memBar.className = "progress-bar " + (data.memory_percent > 85 ? "bg-danger" : "bg-info");
            }

            // Disk bar
            const diskText = document.getElementById("health-disk-text");
            const diskBar  = document.getElementById("health-disk-bar");
            if (diskText && data.disk_used_gb !== null) {
                diskText.textContent = `${data.disk_used_gb} / ${data.disk_total_gb} GB`;
                diskBar.style.width = `${data.disk_percent}%`;
                diskBar.className = "progress-bar " + (data.disk_percent > 85 ? "bg-danger" : "bg-warning");
            }
        })
        .catch(() => {/* silently ignore health fetch errors */});
}

// ---------------------------------------------------------------------------
// Footer clock
// ---------------------------------------------------------------------------

function updateClock() {
    const el = document.getElementById("footer-time");
    if (el) {
        el.textContent = new Date().toLocaleTimeString();
    }
}

// ---------------------------------------------------------------------------
// Initialise
// ---------------------------------------------------------------------------

document.addEventListener("DOMContentLoaded", function () {
    // Run immediately, then on interval.
    refreshStatus();
    refreshHealth();
    updateClock();

    setInterval(refreshStatus, STATUS_INTERVAL);
    setInterval(refreshHealth, HEALTH_INTERVAL);
    setInterval(updateClock, 1000);
});
