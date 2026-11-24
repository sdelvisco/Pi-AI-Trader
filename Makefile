# =============================================================================
# Pi-AI-Trader — Strategy Build & Deployment
# =============================================================================
# Usage:
#   make build    — compile DualMomentumV2.dll on the Pi
#   make deploy   — copy DLL to LEAN, verify config, restart lean-trader
#   make verify   — confirm LEAN initialized DualMomentumV2 successfully
#   make all      — build + deploy + verify in sequence
#
# Prerequisites:
#   - .NET 10 SDK: /usr/local/bin/dotnet
#   - LEAN installation: /opt/lean-engine/Launcher/bin/Release/
#   - Passwordless sudo for pi-admin (add to /etc/sudoers.d/pi-admin-lean):
#       pi-admin ALL=(ALL) NOPASSWD: /bin/cp /home/pi-admin/Pi-AI-Trader/strategies/csharp/bin/Release/net10.0/DualMomentumV2.dll /opt/lean-engine/Launcher/bin/Release/DualMomentumV2.dll
#       pi-admin ALL=(ALL) NOPASSWD: /bin/cp /home/pi-admin/Pi-AI-Trader/strategies/csharp/bin/Release/net10.0/PiAiTrader.Intelligence.dll /opt/lean-engine/Launcher/bin/Release/PiAiTrader.Intelligence.dll
#       pi-admin ALL=(ALL) NOPASSWD: /usr/bin/python3 /home/pi-admin/Pi-AI-Trader/scripts/render_lean_config.py *
#       pi-admin ALL=(ALL) NOPASSWD: /bin/systemctl restart lean-trader
#       pi-admin ALL=(ALL) NOPASSWD: /bin/systemctl start lean-trader
#       pi-admin ALL=(ALL) NOPASSWD: /bin/systemctl stop universe-apply.path
#       pi-admin ALL=(ALL) NOPASSWD: /bin/systemctl start universe-apply.path
#   - The two universe-apply.path rules are used only by universe-seed (below)
#     in the rare case universe.json is missing while the .path watcher is
#     running; without them sudo simply asks for a password at that step.
#   - render_lean_config.py needs sudo (not just the config-copy step it
#     replaces) because it must read /etc/tradingpi/alpaca.env, which is
#     chmod 600 root:root, and write config.json into a directory owned
#     by the lean-trader service's WorkingDirectory.
# =============================================================================

STRATEGY_DIR := strategies/csharp
CSPROJ       := $(STRATEGY_DIR)/DualMomentumV2.csproj
DLL_NAME     := DualMomentumV2.dll
BUILD_DIR    := $(STRATEGY_DIR)/bin/Release/net10.0
BUILD_OUTPUT := $(BUILD_DIR)/$(DLL_NAME)
DEPLOY_DIR   := /opt/lean-engine/Launcher/bin/Release
# Phase 2 Step 3 (Signal Aggregator): DualMomentumV2.csproj now carries a
# ProjectReference to strategies/csharp/Intelligence/Intelligence.csproj, so
# `dotnet build` copies PiAiTrader.Intelligence.dll into BUILD_DIR alongside
# DualMomentumV2.dll. That second DLL must be deployed too -- LEAN loads
# DualMomentumV2.dll on its own, and without PiAiTrader.Intelligence.dll
# sitting next to it in DEPLOY_DIR, the algorithm fails to load the moment it
# touches any PiAiTrader.Intelligence type (SignalAggregator, PositionSizer,
# etc).
INTELLIGENCE_DLL_NAME := PiAiTrader.Intelligence.dll
INTELLIGENCE_BUILD_OUTPUT := $(BUILD_DIR)/$(INTELLIGENCE_DLL_NAME)
# LEAN reads config.json from the same directory as its own executable, not
# from Launcher/ one level up: WorkingDirectory and ExecStart in
# /etc/systemd/system/lean-trader.service both point at
# Launcher/bin/Release/QuantConnect.Lean.Launcher.dll. A config.json written
# to Launcher/config.json is never read by the running service — it silently
# falls back to whatever config.json already happens to be sitting in
# bin/Release/ (e.g. LEAN's own bundled sample config after a rebuild).
LEAN_CONFIG  := /opt/lean-engine/Launcher/bin/Release/config.json
SERVICE      := lean-trader
# EnvironmentFile consumed by lean-trader.service; also read directly by
# scripts/render_lean_config.py to substitute ${VAR} placeholders in the
# config template, since neither LEAN nor `sudo` (which doesn't inherit the
# invoking shell's environment) resolve them on their own.
CREDENTIALS_ENV := /etc/tradingpi/alpaca.env
# Shared trading-universe file (read by DualMomentumV2 and
# HeadlineNewsPipeline, written by the web portal) and the one canonical
# seed copy in the repo. universe-seed installs the seed ONLY when the file
# does not exist yet; it never overwrites portal edits.
UNIVERSE_FILE      := /var/lib/tradingpi/headline-news-pipeline/universe.json
UNIVERSE_SEED      := config/universe.default.json
# systemd .path unit that restarts lean-trader whenever UNIVERSE_FILE changes
# (services/universe-apply.path).
UNIVERSE_PATH_UNIT := universe-apply.path

.PHONY: all build deploy verify force-rebalance universe-seed

# -----------------------------------------------------------------------------
# all: full pipeline — build → deploy → verify
# -----------------------------------------------------------------------------
all: build deploy verify

# -----------------------------------------------------------------------------
# build: compile the strategy DLL from source
# Output: strategies/csharp/bin/Release/net10.0/DualMomentumV2.dll
# -----------------------------------------------------------------------------
build:
	@echo "==> Building $(DLL_NAME)..."
	dotnet build $(CSPROJ) -c Release --nologo
	@test -f "$(BUILD_OUTPUT)" || { \
		echo "ERROR: Expected DLL not found at $(BUILD_OUTPUT)"; \
		echo "       Check build output above for errors."; \
		exit 1; \
	}
	@test -f "$(INTELLIGENCE_BUILD_OUTPUT)" || { \
		echo "ERROR: Expected DLL not found at $(INTELLIGENCE_BUILD_OUTPUT)"; \
		echo "       Check build output above for errors."; \
		exit 1; \
	}
	@echo "Build OK: $(BUILD_OUTPUT)"
	@echo "Build OK: $(INTELLIGENCE_BUILD_OUTPUT)"

# -----------------------------------------------------------------------------
# deploy: copy DLL, verify config.json, restart lean-trader service
#
# Config checks performed before restart:
#   - algorithm-type-name == "DualMomentumV2"
#   - algorithm-location contains "DualMomentumV2.dll"
#   - alpaca-api-key / alpaca-api-secret / alpaca-paper-trading resolved to
#     real values (not empty, not a leftover "${...}" placeholder) -- values
#     themselves are never printed, only whether they resolved.
# If any check fails the deploy is aborted (service not restarted).
# -----------------------------------------------------------------------------
deploy:
	@echo "==> Deploying $(DLL_NAME) to $(DEPLOY_DIR)..."
	sudo cp "$(BUILD_OUTPUT)" "$(DEPLOY_DIR)/$(DLL_NAME)"
	@echo "==> Deploying $(INTELLIGENCE_DLL_NAME) to $(DEPLOY_DIR)..."
	sudo cp "$(INTELLIGENCE_BUILD_OUTPUT)" "$(DEPLOY_DIR)/$(INTELLIGENCE_DLL_NAME)"
	@echo "==> Rendering config template (substituting \$${VAR} credential placeholders) to $(LEAN_CONFIG)..."
	sudo python3 scripts/render_lean_config.py config/lean_config.template.json $(CREDENTIALS_ENV) $(LEAN_CONFIG)
	@echo "==> Verifying $(LEAN_CONFIG)..."
	@printf '%s\n' \
		'import json, sys' \
		'cfg = json.load(open("$(LEAN_CONFIG)"))' \
		'atn = cfg.get("algorithm-type-name", "")' \
		'al  = cfg.get("algorithm-location",  "")' \
		'lm  = cfg.get("live-mode", False)' \
		'akey = cfg.get("alpaca-api-key", "")' \
		'asec = cfg.get("alpaca-api-secret", "")' \
		'apt  = str(cfg.get("alpaca-paper-trading", ""))' \
		'errs = []' \
		'if atn != "DualMomentumV2": errs.append("algorithm-type-name is " + repr(atn) + " -- expected \"DualMomentumV2\"")' \
		'if "DualMomentumV2.dll" not in al: errs.append("algorithm-location " + repr(al) + " does not contain DualMomentumV2.dll")' \
		'if lm is not True: errs.append("live-mode is not true -- LEAN will backtest instead of live trade")' \
		'if not akey or "$${" in akey: errs.append("alpaca-api-key did not resolve to a real value")' \
		'if not asec or "$${" in asec: errs.append("alpaca-api-secret did not resolve to a real value")' \
		'if not apt or "$${" in apt: errs.append("alpaca-paper-trading did not resolve to a real value")' \
		'[print("CONFIG ERROR: " + e) for e in errs]' \
		'sys.exit(len(errs))' \
		| python3
	@echo "Config OK"
	@# Seed universe.json BEFORE the restart below, so the restarted
	@# DualMomentumV2 reads it and this deploy restarts lean-trader only once.
	@$(MAKE) --no-print-directory universe-seed
	@echo "==> Restarting $(SERVICE)..."
	sudo systemctl restart $(SERVICE)
	@echo "Deploy complete. Run 'make verify' to confirm initialization."

# -----------------------------------------------------------------------------
# universe-seed: install config/universe.default.json as UNIVERSE_FILE, only if
# UNIVERSE_FILE does not exist yet (never overwrites portal edits).
#
# Avoiding a redundant restart: universe-apply.path uses PathChanged, which
# also fires when the file is first CREATED. If the watcher is running, the
# seed write would restart lean-trader on top of the restart `make deploy`
# does right after this. So when the watcher is active, it is stopped for the
# write and started again afterwards. A PathChanged watcher started while the
# file already exists does not fire (it only reacts to changes after it
# starts) -- confirm on the Pi with `journalctl -u universe-apply.service`.
#
# The write itself is atomic (temp file, then rename in the same directory),
# the same way the portal writes. The directory is created by the
# HeadlineNewsPipeline install steps (owned by pi-admin), so no sudo is
# needed to write the file.
# -----------------------------------------------------------------------------
universe-seed:
	@if [ -e "$(UNIVERSE_FILE)" ]; then \
		echo "==> $(UNIVERSE_FILE) exists; leaving it unchanged (portal-managed)."; \
	else \
		if [ ! -d "$$(dirname $(UNIVERSE_FILE))" ]; then \
			echo "ERROR: $$(dirname $(UNIVERSE_FILE)) does not exist. Create it first:"; \
			echo "       sudo mkdir -p $$(dirname $(UNIVERSE_FILE)) && sudo chown pi-admin:pi-admin $$(dirname $(UNIVERSE_FILE))"; \
			exit 1; \
		fi; \
		WATCHING=0; \
		if systemctl is-active --quiet $(UNIVERSE_PATH_UNIT); then \
			WATCHING=1; \
			echo "==> Pausing $(UNIVERSE_PATH_UNIT) so seeding does not trigger an extra lean-trader restart..."; \
			sudo systemctl stop $(UNIVERSE_PATH_UNIT); \
		fi; \
		echo "==> Seeding $(UNIVERSE_FILE) from $(UNIVERSE_SEED)..."; \
		RC=0; \
		{ install -m 0644 "$(UNIVERSE_SEED)" "$(UNIVERSE_FILE).tmp" && mv "$(UNIVERSE_FILE).tmp" "$(UNIVERSE_FILE)"; } || RC=$$?; \
		if [ $$WATCHING = 1 ]; then \
			echo "==> Resuming $(UNIVERSE_PATH_UNIT)..."; \
			sudo systemctl start $(UNIVERSE_PATH_UNIT); \
		fi; \
		if [ $$RC != 0 ]; then echo "ERROR: seeding $(UNIVERSE_FILE) failed (exit $$RC)."; exit $$RC; fi; \
		echo "Seeded $(UNIVERSE_FILE)."; \
	fi

# -----------------------------------------------------------------------------
# verify: poll journal for "DualMomentumV2 Initialized" (up to 60s)
# Exits 0 on success, 1 on timeout.
# -----------------------------------------------------------------------------
# force-rebalance: touch the trigger file on the Pi and tail the log
# Use this to test rebalance execution without waiting for month-start.
force-rebalance:
	@echo "==> Triggering manual rebalance via /tmp/force_rebalance..."
	touch /tmp/force_rebalance
	@echo "==> Tailing LEAN log (Ctrl+C to stop)..."
	tail -f /opt/lean-engine/Launcher/bin/Release/log.txt | grep --line-buffered -i "rebalanc\|order\|submit\|cancel\|fill\|error\|warn"

verify:
	@echo "==> Verifying LEAN startup (polls up to 60s)..."
	@SECS=0; \
	while [ $$SECS -lt 60 ]; do \
		if journalctl -u $(SERVICE) --since "-5 minutes" --no-pager -q 2>/dev/null \
				| grep -q "DualMomentumV2 Initialized"; then \
			echo "VERIFY OK: DualMomentumV2 Initialized (after $${SECS}s)"; \
			exit 0; \
		fi; \
		sleep 5; \
		SECS=$$((SECS + 5)); \
	done; \
	echo "VERIFY FAIL: 'DualMomentumV2 Initialized' not found in logs after 60s."; \
	echo "--- Last 40 journal lines ---"; \
	journalctl -u $(SERVICE) -n 40 --no-pager; \
	exit 1
