/*
 * universe.js — Settings page "Trading Universe" editor
 * =====================================================
 * Talks to the routes in web/routes/api.py:
 *   GET  /api/universe           load the current list (+ locked tickers, source)
 *   POST /api/universe/validate  Alpaca checks for one candidate before adding
 *   POST /api/universe           save the whole list once ("Save & apply")
 *
 * All edits (adds and removes) are staged in the browser until Save &
 * apply, so one save is one file write and one lean-trader restart.
 * Ticker text is only ever inserted with textContent, never innerHTML.
 */
(function () {
    "use strict";

    // ---------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------
    let savedTickers = [];        // list as last loaded/saved from the server
    let workingTickers = [];      // list being edited (what Save would write)
    let lockedTickers = new Set(); // AGG, SPY — cannot be removed
    let maxSize = 100;            // server's UNIVERSE_MAX_TICKERS
    let rowErrors = {};           // ticker -> error message from the last save
    let busy = false;             // true while a request is in flight

    // ---------------------------------------------------------------------
    // DOM handles
    // ---------------------------------------------------------------------
    const el = (id) => document.getElementById(id);
    const tableBody = el("universe-table-body");
    const sourceBadge = el("universe-source");
    const countLabel = el("universe-count");
    const dirtyBadge = el("universe-dirty");
    const fallbackAlert = el("universe-fallback");
    const addForm = el("universe-add-form");
    const addInput = el("universe-add-input");
    const addButton = el("universe-add-button");
    const addStatus = el("universe-add-status");
    const addChecks = el("universe-add-checks");
    const saveButton = el("universe-save-button");
    const revertButton = el("universe-revert-button");
    const saveErrors = el("universe-save-errors");
    const saveStatus = el("universe-save-status");
    const confirmSummary = el("universe-confirm-summary");
    const confirmButton = el("universe-confirm-button");
    // Bootstrap's JS comes from a CDN (base.html). If it failed to load,
    // fall back to the browser's own confirm() dialog with the same
    // warnings instead of breaking the whole editor.
    const confirmModal = (typeof bootstrap !== "undefined")
        ? new bootstrap.Modal(el("universe-confirm-modal"))
        : null;

    // Same rule as web/universe.py TICKER_PATTERN (server re-checks anyway).
    const TICKER_RE = /^[A-Z]{1,5}(\.[A-Z]{1,2})?$/;

    // Human-readable names for the three validation checks.
    const CHECK_LABELS = {
        exists_tradable: "Exists and tradable on Alpaca",
        fractionable: "Fractional shares",
        has_price: "Price on free (IEX) data",
    };

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    // True when the working list differs from the saved list (order matters,
    // since the file keeps order).
    function isDirty() {
        return workingTickers.length !== savedTickers.length ||
            workingTickers.some((t, i) => t !== savedTickers[i]);
    }

    // Creates an element with optional class and text.
    function make(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    // Parses a fetch Response as JSON even for error statuses.
    async function readJson(response) {
        try {
            return await response.json();
        } catch (e) {
            return { status: "error", message: `HTTP ${response.status} (no JSON body)` };
        }
    }

    // ---------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------
    function render() {
        tableBody.replaceChildren();
        const savedSet = new Set(savedTickers);

        workingTickers.forEach((ticker, index) => {
            const row = make("tr");
            row.appendChild(make("td", "text-muted-light", String(index + 1)));
            row.appendChild(make("td", "font-monospace", ticker));

            // Status column: locked / new / last-save error.
            const statusCell = make("td");
            if (lockedTickers.has(ticker)) {
                const badge = make("span", "badge bg-secondary");
                badge.appendChild(make("i", "bi bi-lock-fill"));
                badge.appendChild(document.createTextNode(" Locked"));
                badge.title = ticker === "AGG" ? "Defensive asset — required" : "Absolute-momentum reference — required";
                statusCell.appendChild(badge);
            } else if (!savedSet.has(ticker)) {
                statusCell.appendChild(make("span", "badge bg-info text-dark", "New (unsaved)"));
            }
            if (rowErrors[ticker]) {
                statusCell.appendChild(make("div", "small text-danger", rowErrors[ticker]));
            }
            row.appendChild(statusCell);

            // Action column: remove button (disabled for locked tickers).
            const actionCell = make("td", "text-end");
            const removeButton = make("button", "btn btn-sm btn-outline-danger py-0");
            removeButton.type = "button";
            removeButton.textContent = "Remove";
            if (lockedTickers.has(ticker)) {
                // Visibly locked: grey, labelled "Locked", not clickable.
                removeButton.className = "btn btn-sm btn-outline-secondary py-0";
                removeButton.textContent = "Locked";
                removeButton.disabled = true;
                removeButton.title = `${ticker} is required and cannot be removed`;
            } else {
                removeButton.addEventListener("click", () => removeTicker(ticker));
            }
            actionCell.appendChild(removeButton);
            row.appendChild(actionCell);

            tableBody.appendChild(row);
        });

        const dirty = isDirty();
        countLabel.textContent = `${workingTickers.length} tickers (max ${maxSize})`;
        dirtyBadge.classList.toggle("d-none", !dirty);
        saveButton.disabled = busy || !dirty;
        revertButton.disabled = busy || !dirty;
        addButton.disabled = busy;
    }

    // Shows per-check results for the last validated candidate.
    function renderChecks(checks) {
        addChecks.replaceChildren();
        (checks || []).forEach((check) => {
            const item = make("li", check.ok ? "text-success" : "text-danger");
            item.appendChild(make("i", check.ok ? "bi bi-check-circle" : "bi bi-x-circle"));
            item.appendChild(document.createTextNode(` ${CHECK_LABELS[check.name] || check.name}: ${check.detail}`));
            addChecks.appendChild(item);
        });
    }

    // Shows save errors: per-ticker ones go next to the row as well.
    function showSaveErrors(message, errors) {
        rowErrors = {};
        saveErrors.replaceChildren();
        saveErrors.appendChild(make("div", "fw-semibold", message || "Save failed."));
        if (Array.isArray(errors) && errors.length) {
            const list = make("ul", "mb-0");
            errors.forEach((error) => {
                const label = error.ticker ? `${error.ticker}: ` : "";
                list.appendChild(make("li", null, label + error.message));
                if (error.ticker) rowErrors[error.ticker] = error.message;
            });
            saveErrors.appendChild(list);
        }
        saveErrors.classList.remove("d-none");
        render();
    }

    function clearSaveErrors() {
        rowErrors = {};
        saveErrors.classList.add("d-none");
        saveErrors.replaceChildren();
    }

    // ---------------------------------------------------------------------
    // Actions
    // ---------------------------------------------------------------------

    // Loads the current universe from the server and resets local edits.
    async function load() {
        try {
            const data = await readJson(await fetch("/api/universe"));
            if (!Array.isArray(data.tickers)) throw new Error(data.message || "Unexpected response");
            savedTickers = data.tickers.slice();
            workingTickers = data.tickers.slice();
            lockedTickers = new Set(data.locked || []);
            maxSize = data.max_size || maxSize;

            if (data.source === "file") {
                sourceBadge.className = "badge bg-success";
                sourceBadge.textContent = "Source: universe.json";
                fallbackAlert.classList.add("d-none");
            } else {
                // Default: file missing/invalid; consumers are on built-in lists.
                sourceBadge.className = "badge bg-warning text-dark";
                sourceBadge.textContent = "Source: built-in default";
                fallbackAlert.textContent =
                    "universe.json is not in use (" + (data.fallback_reason || "unknown reason") +
                    "). The strategy and news pipeline are using their built-in lists. " +
                    "Saving will create the file.";
                fallbackAlert.classList.remove("d-none");
            }
            render();
        } catch (e) {
            sourceBadge.className = "badge bg-danger";
            sourceBadge.textContent = "Failed to load universe";
            countLabel.textContent = String(e.message || e);
        }
    }

    // Removes a non-locked ticker from the working list.
    function removeTicker(ticker) {
        if (lockedTickers.has(ticker)) return;
        workingTickers = workingTickers.filter((t) => t !== ticker);
        delete rowErrors[ticker];
        render();
    }

    // Validates the typed ticker against Alpaca and adds it if all checks pass.
    async function validateAndAdd(event) {
        event.preventDefault();
        const ticker = addInput.value.trim().toUpperCase();
        addChecks.replaceChildren();

        // Cheap local checks first.
        if (!TICKER_RE.test(ticker)) {
            addStatus.className = "small text-danger";
            addStatus.textContent = "Not a valid ticker (1-5 letters, optional .X class suffix).";
            return;
        }
        if (workingTickers.includes(ticker)) {
            addStatus.className = "small text-warning";
            addStatus.textContent = `${ticker} is already in the list.`;
            return;
        }
        if (workingTickers.length >= maxSize) {
            addStatus.className = "small text-danger";
            addStatus.textContent = `The universe is already at the maximum of ${maxSize} tickers.`;
            return;
        }

        busy = true;
        render();
        addStatus.className = "small text-muted-light";
        addStatus.textContent = `Checking ${ticker} with Alpaca…`;
        try {
            const response = await fetch("/api/universe/validate", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ ticker }),
            });
            const data = await readJson(response);
            if (!response.ok) {
                addStatus.className = "small text-danger";
                addStatus.textContent = `Could not validate ${ticker}: ${data.message || response.status}`;
                return;
            }
            renderChecks(data.checks);
            if (data.ok) {
                workingTickers.push(ticker);
                addInput.value = "";
                addStatus.className = "small text-success";
                addStatus.textContent = `${ticker} passed all checks and was added (not saved yet).`;
            } else {
                addStatus.className = "small text-danger";
                addStatus.textContent = `${ticker} failed validation and was not added.`;
            }
        } catch (e) {
            addStatus.className = "small text-danger";
            addStatus.textContent = `Validation request failed: ${e.message || e}`;
        } finally {
            busy = false;
            render();
        }
    }

    // Opens the confirmation dialog with a summary of the pending change.
    function askToSave() {
        const savedSet = new Set(savedTickers);
        const workingSet = new Set(workingTickers);
        const added = workingTickers.filter((t) => !savedSet.has(t));
        const removed = savedTickers.filter((t) => !workingSet.has(t));
        confirmSummary.textContent =
            `Adding: ${added.length ? added.join(", ") : "none"}. ` +
            `Removing: ${removed.length ? removed.join(", ") : "none"}. ` +
            `New size: ${workingTickers.length} tickers.`;
        if (confirmModal) {
            confirmModal.show();
            return;
        }
        // Fallback dialog: same content as the modal, as plain text.
        const ok = window.confirm(
            confirmSummary.textContent + "\n\n" +
            "This RESTARTS lean-trader (if it is running).\n\n" +
            "Known gap: the restart clears in-memory state. Until new entries are bought, " +
            "stop-loss entry prices are empty (the 15% stop-loss does not protect current holdings) " +
            "and the drawdown peak/halt state is reset.\n\n" +
            "Removed tickers that are held are sold at the next scheduled rebalance."
        );
        if (ok) save();
    }

    // Sends the whole working list in one POST.
    async function save() {
        if (confirmModal) confirmModal.hide();
        clearSaveErrors();
        busy = true;
        render();
        saveStatus.className = "small text-muted-light mb-2";
        saveStatus.textContent = "Checking it is safe to restart and saving…";
        try {
            const response = await fetch("/api/universe", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ tickers: workingTickers }),
            });
            const data = await readJson(response);
            if (response.ok) {
                saveStatus.className = "small text-success mb-2";
                saveStatus.textContent = data.message || "Saved.";
                busy = false;
                await load();
                return;
            }
            saveStatus.textContent = "";
            showSaveErrors(data.message || `Save failed (HTTP ${response.status}).`, data.errors);
        } catch (e) {
            saveStatus.textContent = "";
            showSaveErrors(`Save request failed: ${e.message || e}`);
        } finally {
            busy = false;
            render();
        }
    }

    // Throws away local edits.
    function revert() {
        workingTickers = savedTickers.slice();
        clearSaveErrors();
        addChecks.replaceChildren();
        addStatus.textContent = "";
        render();
    }

    // ---------------------------------------------------------------------
    // Wiring
    // ---------------------------------------------------------------------
    addForm.addEventListener("submit", validateAndAdd);
    saveButton.addEventListener("click", askToSave);
    confirmButton.addEventListener("click", save);
    revertButton.addEventListener("click", revert);

    // Warn before leaving the page with unsaved edits.
    window.addEventListener("beforeunload", (event) => {
        if (isDirty()) {
            event.preventDefault();
            event.returnValue = "";
        }
    });

    load();
})();
