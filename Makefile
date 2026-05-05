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

.PHONY: all build deploy verify

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
	@echo "==> Verifying $(LEAN_CONFIG)..."
	@python3 -c " \
import json, sys; \
cfg = json.load(open('$(LEAN_CONFIG)')); \
atn = cfg.get('algorithm-type-name', ''); \
al  = cfg.get('algorithm-location',  ''); \
errs = []; \
atn == 'DualMomentumV2' or errs.append('algorithm-type-name is \"' + atn + '\" — expected \"DualMomentumV2\"'); \
'DualMomentumV2.dll' in al or errs.append('algorithm-location \"' + al + '\" does not reference DualMomentumV2.dll'); \
[print('CONFIG ERROR: ' + e) for e in errs]; \
sys.exit(len(errs)) \
"
	@echo "Config OK"
	@echo "==> Restarting $(SERVICE)..."
	sudo systemctl restart $(SERVICE)
	@echo "Deploy complete. Run 'make verify' to confirm initialization."

# -----------------------------------------------------------------------------
# verify: poll journal for "DualMomentumV2 Initialized" (up to 60s)
# Exits 0 on success, 1 on timeout.
# -----------------------------------------------------------------------------
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
