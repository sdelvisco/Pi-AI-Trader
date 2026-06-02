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
#       pi-admin ALL=(ALL) NOPASSWD: /bin/systemctl restart lean-trader
#       pi-admin ALL=(ALL) NOPASSWD: /bin/systemctl start lean-trader
# =============================================================================

STRATEGY_DIR := strategies/csharp
CSPROJ       := $(STRATEGY_DIR)/DualMomentumV2.csproj
DLL_NAME     := DualMomentumV2.dll
BUILD_DIR    := $(STRATEGY_DIR)/bin/Release/net10.0
BUILD_OUTPUT := $(BUILD_DIR)/$(DLL_NAME)
DEPLOY_DIR   := /opt/lean-engine/Launcher/bin/Release
LEAN_CONFIG  := /opt/lean-engine/Launcher/config.json
SERVICE      := lean-trader

.PHONY: all build deploy verify force-rebalance

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
	@echo "Build OK: $(BUILD_OUTPUT)"

# -----------------------------------------------------------------------------
# deploy: copy DLL, verify config.json, restart lean-trader service
#
# Config checks performed before restart:
#   - algorithm-type-name == "DualMomentumV2"
#   - algorithm-location contains "DualMomentumV2.dll"
# If either check fails the deploy is aborted (service not restarted).
# -----------------------------------------------------------------------------
deploy:
	@echo "==> Deploying $(DLL_NAME) to $(DEPLOY_DIR)..."
	sudo cp "$(BUILD_OUTPUT)" "$(DEPLOY_DIR)/$(DLL_NAME)"
	@echo "==> Copying config template to $(LEAN_CONFIG)..."
	sudo python3 -c "import json,sys; d=json.load(open('config/lean_config.template.json')); json.dump(d, open('/opt/lean-engine/Launcher/config.json','w'), indent=2)"
	@echo "==> Verifying $(LEAN_CONFIG)..."
	@printf '%s\n' \
		'import json, sys' \
		'cfg = json.load(open("$(LEAN_CONFIG)"))' \
		'atn = cfg.get("algorithm-type-name", "")' \
		'al  = cfg.get("algorithm-location",  "")' \
		'lm  = cfg.get("live-mode", False)' \
		'errs = []' \
		'if atn != "DualMomentumV2": errs.append("algorithm-type-name is " + repr(atn) + " -- expected \"DualMomentumV2\"")' \
		'if "DualMomentumV2.dll" not in al: errs.append("algorithm-location " + repr(al) + " does not contain DualMomentumV2.dll")' \
		'if lm is not True: errs.append("live-mode is not true -- LEAN will backtest instead of live trade")' \
		'[print("CONFIG ERROR: " + e) for e in errs]' \
		'sys.exit(len(errs))' \
		| python3
	@echo "Config OK"
	@echo "==> Restarting $(SERVICE)..."
	sudo systemctl restart $(SERVICE)
	@echo "Deploy complete. Run 'make verify' to confirm initialization."

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
