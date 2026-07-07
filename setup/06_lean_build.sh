#!/usr/bin/env bash
# =============================================================================
# 06_lean_build.sh — Build LEAN Engine and Alpaca Brokerage Plugin from Source
# =============================================================================
# Clones and builds the QuantConnect LEAN engine and the Alpaca brokerage
# plugin directly from their GitHub repositories, without Docker or the
# LEAN CLI. This is the correct approach for this project because:
#
#   - No Docker daemon is installed or required
#   - No paid QuantConnect account is required
#   - Builds run natively using the .NET 10 SDK already installed by 04_dotnet.sh
#
# What this script does:
#   1. Clones QuantConnect/Lean             → /opt/lean-engine
#   2. Clones QuantConnect/Lean.Brokerages.Alpaca → /opt/lean-alpaca
#   3. Patches AlpacaBrokerage.cs to comment out ValidateSubscription()
#      (this call fails for free QuantConnect accounts at runtime)
#   4. Patches AlpacaBrokerage.cs to bypass GetAccountAsync() in GetCashBalance()
#      (the vendored SDK requires the now-removed pattern_day_trader field)
#   5. Creates symlink /opt/Lean → /opt/lean-engine to satisfy the Alpaca
#      plugin's hardcoded relative path reference to ../../Lean/
#   6. Builds LEAN in Release configuration
#   7. Builds the Alpaca plugin in Release configuration
#   8. Copies Alpaca plugin DLLs into LEAN's Release output directory
#      so LEAN can discover the brokerage plugin at runtime
#   9. Sets /opt/lean-engine and /opt/lean-alpaca ownership to pi-admin
#
# WARNING: Both builds are computationally intensive. On Raspberry Pi 4
# hardware, each build may take 30–60 minutes. Do not interrupt the script
# once it begins building.
#
# Usage:
#   sudo bash 06_lean_build.sh
#
# Requirements:
#   - 04_dotnet.sh must have been run (.NET 10 SDK at /usr/local/share/dotnet)
#   - Internet connectivity (for git clone)
#   - Run as root or via sudo
# =============================================================================

set -euo pipefail
# -e  : Exit immediately if any command returns a non-zero status.
# -u  : Treat unset variables as errors.
# -o pipefail : A pipeline fails if any command in it fails (not just the last).

# -----------------------------------------------------------------------------
# Helper functions
# -----------------------------------------------------------------------------

# Print a section header for readability in the terminal log.
section() {
    echo ""
    echo "============================================================"
    echo "  $1"
    echo "============================================================"
}

# Print an informational message with a timestamp.
info() {
    echo "[INFO  $(date '+%H:%M:%S')] $1"
}

# Print an error message to stderr and exit with code 1.
die() {
    echo "[ERROR $(date '+%H:%M:%S')] $1" >&2
    exit 1
}

# Confirm the script is running as root, which is required for writes to /opt.
[[ $EUID -eq 0 ]] || die "This script must be run as root (use: sudo bash $0)"

# -----------------------------------------------------------------------------
# Configuration
# -----------------------------------------------------------------------------

# The non-root user that will own the build output directories.
# SUDO_USER is set by sudo and reflects who invoked the script.
PROJECT_USER="${SUDO_USER:-pi-admin}"

# Clone destinations under /opt — system-level placement keeps them separate
# from the project repository and matches standard practice for compiled engines.
LEAN_ENGINE_DIR="/opt/lean-engine"
LEAN_ALPACA_DIR="/opt/lean-alpaca"

# Source repositories (official QuantConnect repositories on GitHub).
LEAN_REPO_URL="https://github.com/QuantConnect/Lean.git"
ALPACA_REPO_URL="https://github.com/QuantConnect/Lean.Brokerages.Alpaca.git"

# Solution files for each component.
LEAN_SLN="${LEAN_ENGINE_DIR}/QuantConnect.Lean.sln"
ALPACA_SLN="${LEAN_ALPACA_DIR}/QuantConnect.AlpacaBrokerage.sln"

# Brokerage-only project file — built instead of the full solution to avoid the
# test project that references ../Lean/Tests/ (which resolves to /opt/Lean/Tests/
# and does not exist; LEAN is at /opt/lean-engine/).
ALPACA_CSPROJ="${LEAN_ALPACA_DIR}/QuantConnect.AlpacaBrokerage/QuantConnect.AlpacaBrokerage.csproj"

# Release output directories produced by dotnet build.
LEAN_RELEASE_DIR="${LEAN_ENGINE_DIR}/Launcher/bin/Release"
ALPACA_RELEASE_DIR="${LEAN_ALPACA_DIR}/QuantConnect.AlpacaBrokerage/bin/Release"

# The AlpacaBrokerage.cs file that contains the ValidateSubscription() call
# which must be patched before building.
ALPACA_BROKERAGE_CS="${LEAN_ALPACA_DIR}/QuantConnect.AlpacaBrokerage/AlpacaBrokerage.cs"

# dotnet binary — use the one installed by 04_dotnet.sh, not a system package.
DOTNET_BIN="/usr/local/bin/dotnet"

# -----------------------------------------------------------------------------
# Pre-flight checks
# -----------------------------------------------------------------------------
section "Pre-flight checks"

# Verify the .NET SDK is available at the expected location.
if [[ ! -x "$DOTNET_BIN" ]]; then
    die ".NET SDK not found at $DOTNET_BIN — run 04_dotnet.sh first"
fi

DOTNET_VERSION="$("$DOTNET_BIN" --version 2>/dev/null || echo unknown)"
info ".NET SDK found: $DOTNET_VERSION"

# LEAN master targets net10.0 — reject anything other than .NET 10.
if [[ "$DOTNET_VERSION" != 10.* ]]; then
    die ".NET 10 is required (LEAN master targets net10.0) but found: $DOTNET_VERSION — run 04_dotnet.sh to upgrade"
fi

# Verify git is installed (installed by 01_os_config.sh).
if ! command -v git &>/dev/null; then
    die "git not found — ensure 01_os_config.sh has been run"
fi

info "git version: $(git --version)"

# Warn the operator that both builds will take a long time on Pi hardware.
echo ""
echo "  *** BUILD TIME WARNING ***"
echo "  Each dotnet build on Raspberry Pi 4 ARM64 hardware can take"
echo "  30–60 minutes. This script will run unattended until complete."
echo "  Do NOT interrupt the script once building begins."
echo ""

# -----------------------------------------------------------------------------
# Step 1: Clone the LEAN engine
# -----------------------------------------------------------------------------
section "Cloning LEAN engine from source"

if [[ -d "${LEAN_ENGINE_DIR}/.git" ]]; then
    info "LEAN engine repository already exists at $LEAN_ENGINE_DIR — pulling latest"
    git -C "$LEAN_ENGINE_DIR" fetch origin \
        || die "git fetch failed for LEAN engine — check internet connectivity"
    git -C "$LEAN_ENGINE_DIR" reset --hard origin/master \
        || die "git reset failed for LEAN engine"
    info "LEAN engine repository updated."
else
    # Remove a partial clone directory if it exists but is not a git repo.
    if [[ -d "$LEAN_ENGINE_DIR" ]]; then
        info "Removing incomplete directory at $LEAN_ENGINE_DIR before cloning"
        rm -rf "$LEAN_ENGINE_DIR"
    fi

    info "Cloning $LEAN_REPO_URL → $LEAN_ENGINE_DIR"
    info "This may take several minutes depending on connection speed..."

    git clone --depth 1 "$LEAN_REPO_URL" "$LEAN_ENGINE_DIR" \
        || die "git clone failed for LEAN engine — check internet connectivity"

    info "LEAN engine clone complete."
fi

# Verify the solution file is present after cloning.
[[ -f "$LEAN_SLN" ]] \
    || die "Expected solution file not found: $LEAN_SLN — clone may be incomplete"

info "LEAN solution file confirmed: $LEAN_SLN"

# -----------------------------------------------------------------------------
# Step 2: Clone the Alpaca brokerage plugin
# -----------------------------------------------------------------------------
section "Cloning Alpaca brokerage plugin from source"

if [[ -d "${LEAN_ALPACA_DIR}/.git" ]]; then
    info "Alpaca plugin repository already exists at $LEAN_ALPACA_DIR — pulling latest"
    git -C "$LEAN_ALPACA_DIR" fetch origin \
        || die "git fetch failed for Alpaca plugin — check internet connectivity"
    git -C "$LEAN_ALPACA_DIR" reset --hard origin/master \
        || die "git reset failed for Alpaca plugin"
    info "Alpaca plugin repository updated."
else
    if [[ -d "$LEAN_ALPACA_DIR" ]]; then
        info "Removing incomplete directory at $LEAN_ALPACA_DIR before cloning"
        rm -rf "$LEAN_ALPACA_DIR"
    fi

    info "Cloning $ALPACA_REPO_URL → $LEAN_ALPACA_DIR"
    info "This may take a few minutes depending on connection speed..."

    git clone --depth 1 "$ALPACA_REPO_URL" "$LEAN_ALPACA_DIR" \
        || die "git clone failed for Alpaca plugin — check internet connectivity"

    info "Alpaca plugin clone complete."
fi

# Verify the solution file is present.
[[ -f "$ALPACA_SLN" ]] \
    || die "Expected solution file not found: $ALPACA_SLN — clone may be incomplete"

info "Alpaca solution file confirmed: $ALPACA_SLN"

# Verify the brokerage project file is present (used for the actual build).
[[ -f "$ALPACA_CSPROJ" ]] \
    || die "Expected project file not found: $ALPACA_CSPROJ — clone may be incomplete"

info "Alpaca project file confirmed: $ALPACA_CSPROJ"

# -----------------------------------------------------------------------------
# Step 3: Patch AlpacaBrokerage.cs — comment out ValidateSubscription()
# -----------------------------------------------------------------------------
section "Patching AlpacaBrokerage.cs"

# ValidateSubscription() performs a network call to QuantConnect's servers to
# verify that the account has an active paid subscription. This check always
# fails for free accounts and causes a runtime exception when LEAN starts.
# We comment it out unconditionally using sed, which is safe because the
# patched file is never committed — it lives only in /opt/lean-alpaca.
#
# The line we are targeting looks like (with leading whitespace):
#     ValidateSubscription();
#
# We use a case-sensitive match on the literal string to avoid accidentally
# commenting out other lines. The patch is idempotent: if the line is already
# commented out (e.g. from a previous run), the sed pattern will not match and
# the file will be left unchanged.

if [[ ! -f "$ALPACA_BROKERAGE_CS" ]]; then
    die "AlpacaBrokerage.cs not found at $ALPACA_BROKERAGE_CS — clone may be incomplete"
fi

# Check whether the line is already commented out from a previous run.
if grep -q '^\s*ValidateSubscription();' "$ALPACA_BROKERAGE_CS"; then
    info "Commenting out ValidateSubscription() in AlpacaBrokerage.cs"

    # Use sed to prefix the matching line with '//' while preserving indentation.
    # The substitution captures the leading whitespace (\s*) so the comment is
    # placed at the same indentation level as the original line.
    sed -i 's/^\(\s*\)ValidateSubscription();/\1\/\/ ValidateSubscription(); \/\/ Disabled: requires paid QuantConnect account/' \
        "$ALPACA_BROKERAGE_CS" \
        || die "sed patch failed on $ALPACA_BROKERAGE_CS"

    info "ValidateSubscription() commented out successfully."
else
    info "ValidateSubscription() is already commented out — skipping patch"
fi

# Confirm the patch took effect.
if grep -q 'ValidateSubscription' "$ALPACA_BROKERAGE_CS"; then
    PATCH_LINE="$(grep 'ValidateSubscription' "$ALPACA_BROKERAGE_CS" | head -1 | sed 's/^\s*//')"
    info "Patch verification — line in file: $PATCH_LINE"
else
    info "Note: ValidateSubscription not found in file (may have been removed upstream)"
fi

# -----------------------------------------------------------------------------
# Step 4: Patch AlpacaBrokerage.cs — bypass GetAccountAsync() in GetCashBalance()
# -----------------------------------------------------------------------------
section "Patching AlpacaBrokerage.cs — pattern_day_trader deserialization workaround"

# Alpaca deprecated the `pattern_day_trader` field on the GET /v2/account response
# ahead of FINRA's new Intraday Margin Standards (effective before 2026-06-04),
# which replaced the old PDT flag. Alpaca no longer sends that field at all, but
# the vendored Alpaca.Markets.dll (built from alpacahq/alpaca-trade-api-csharp,
# tag sdk-8.0.0-beta4 — the newest release available; there is no newer SDK to
# upgrade to) deserializes the account response with a strict model that requires
# `pattern_day_trader` to be present. Every call to _tradingClient.GetAccountAsync()
# now throws "Required property 'pattern_day_trader' not found in JSON.", which
# happens inside AlpacaBrokerage.GetCashBalance() during LEAN's
# BrokerageSetupHandler.Setup() — so the engine crashed on every single restart.
#
# We do not patch or rebuild Alpaca.Markets.dll. Instead we patch the plugin's
# GetCashBalance() to bypass the SDK client entirely for this one call: a manual
# authenticated HTTP GET straight to Alpaca's REST account endpoint, parsing only
# "cash" and "currency" with Newtonsoft.Json (already used elsewhere in this file
# for ValidateSubscription's license parsing). This mirrors the ValidateSubscription
# patch above: applied via Python string replacement (not a diff/patch file, since
# the source is re-cloned fresh from upstream on every provisioning run and never
# committed to this repo), and is idempotent — checked via a marker comment before
# reapplying.
#
# This is a permanent workaround for a deprecated/removed Alpaca field, not a
# temporary fix pending an SDK update.

PDT_WORKAROUND_MARKER="pattern_day_trader workaround"

if grep -q "$PDT_WORKAROUND_MARKER" "$ALPACA_BROKERAGE_CS"; then
    info "pattern_day_trader workaround already applied — skipping patch"
else
    info "Applying pattern_day_trader workaround to AlpacaBrokerage.cs"

    if ! python3 - "$ALPACA_BROKERAGE_CS" <<'PYEOF'
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as f:
    src = f.read()

# --- Patch A: add private fields for the manual account-endpoint HTTP call ---
old_fields = "        private IAlpacaTradingClient _tradingClient;\n"
new_fields = old_fields + (
    "\n"
    "        // --- Alpaca account-endpoint pattern_day_trader workaround (see GetCashBalance()) ---\n"
    "        // Alpaca stopped returning `pattern_day_trader` in GET /v2/account responses\n"
    "        // (FINRA Intraday Margin Standards, effective before 2026-06-04). The vendored\n"
    "        // Alpaca.Markets.dll (sdk-8.0.0-beta4) requires that field, so _tradingClient.\n"
    "        // GetAccountAsync() throws. We fetch cash/currency with a manual authenticated\n"
    "        // HTTP GET instead. These fields are populated once in Initialize().\n"
    "        private string _accountBaseUrl;\n"
    "        private Dictionary<string, string> _accountAuthHeaders;\n"
    "        private HttpClient _accountHttpClient;\n"
)
assert old_fields in src and src.count(old_fields) == 1, "field anchor not found/unique"
src = src.replace(old_fields, new_fields, 1)

# --- Patch B: compute base URL + auth headers once in Initialize() ---
old_env = "            var environment = isPaperTrading ? Environments.Paper : Environments.Live;\n"
new_env = old_env + (
    "\n"
    "            // pattern_day_trader workaround (2026-06-04): Alpaca removed `pattern_day_trader`\n"
    "            // from the account endpoint response; the vendored SDK's strict deserialization\n"
    "            // model requires it, so GetAccountAsync() now throws. Set up a direct HTTP path to\n"
    "            // the account endpoint here so GetCashBalance() can bypass the SDK client entirely.\n"
    "            // This is a permanent workaround -- there is no newer SDK release to pick up a fix.\n"
    "            _accountBaseUrl = isPaperTrading ? \"https://paper-api.alpaca.markets\" : \"https://api.alpaca.markets\";\n"
    "            _accountHttpClient = new HttpClient();\n"
    "            _accountAuthHeaders = new Dictionary<string, string>();\n"
    "            if (tradingSecretKey != null)\n"
    "            {\n"
    "                // OAuth access token path -- mirrors the tradingSecretKey ?? secretKey precedence\n"
    "                // used for the SDK clients below.\n"
    "                _accountAuthHeaders[\"Authorization\"] = $\"Bearer {accessToken}\";\n"
    "            }\n"
    "            else\n"
    "            {\n"
    "                _accountAuthHeaders[\"APCA-API-KEY-ID\"] = apiKey;\n"
    "                _accountAuthHeaders[\"APCA-API-SECRET-KEY\"] = apiKeySecret;\n"
    "            }\n"
)
assert old_env in src and src.count(old_env) == 1, "Initialize() anchor not found/unique"
src = src.replace(old_env, new_env, 1)

# --- Patch C: dispose the new HttpClient alongside the other clients ---
old_dispose = "            _tradingClient.DisposeSafely();\n"
new_dispose = old_dispose + "            _accountHttpClient?.Dispose();\n"
assert old_dispose in src and src.count(old_dispose) == 1, "Dispose() anchor not found/unique"
src = src.replace(old_dispose, new_dispose, 1)

# --- Patch D: rewrite GetCashBalance() to bypass GetAccountAsync() ---
old_get_cash_balance = '''        public override List<CashAmount> GetCashBalance()
        {
            var accounts = _tradingClient.GetAccountAsync().SynchronouslyAwaitTaskResult();
            var balances = new List<CashAmount>() { new(accounts.TradableCash, accounts.Currency) };
'''
new_get_cash_balance = '''        public override List<CashAmount> GetCashBalance()
        {
            // pattern_day_trader workaround (2026-06-04): Alpaca deprecated the `pattern_day_trader`
            // field on the GET /v2/account response ahead of FINRA's new Intraday Margin Standards,
            // which replaced the old PDT flag. The vendored Alpaca.Markets.dll (sdk-8.0.0-beta4, the
            // newest release published at the time of writing) deserializes the account response
            // with a strict model requiring `pattern_day_trader` to be present, so
            // _tradingClient.GetAccountAsync() now throws:
            //   "Required property 'pattern_day_trader' not found in JSON."
            // on every call -- and GetCashBalance() runs during LEAN's
            // BrokerageSetupHandler.Setup(), so this crashed the engine on every restart. There is
            // no newer SDK release to pick up an upstream fix, so this is a permanent workaround,
            // not a stopgap: bypass the SDK's account deserialization here entirely and make a
            // manual authenticated HTTP GET to Alpaca's REST account endpoint, parsing only the
            // "cash" and "currency" fields we actually need with Newtonsoft.Json (already used
            // elsewhere in this file for ValidateSubscription's license parsing).
            using var accountRequest = new HttpRequestMessage(HttpMethod.Get, $"{_accountBaseUrl}/v2/account");
            foreach (var header in _accountAuthHeaders)
            {
                accountRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var accountResponse = _accountHttpClient.SendAsync(accountRequest).SynchronouslyAwaitTaskResult();
            var accountResponseBody = accountResponse.Content.ReadAsStringAsync().SynchronouslyAwaitTaskResult();
            if (!accountResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{nameof(AlpacaBrokerage)}.{nameof(GetCashBalance)}: Alpaca account endpoint returned {(int)accountResponse.StatusCode} {accountResponse.StatusCode}: {accountResponseBody}");
            }

            var accountJson = JObject.Parse(accountResponseBody);
            var cashToken = accountJson["cash"];
            var currencyToken = accountJson["currency"];
            if (cashToken == null || currencyToken == null)
            {
                throw new InvalidOperationException($"{nameof(AlpacaBrokerage)}.{nameof(GetCashBalance)}: Alpaca account endpoint response is missing 'cash' or 'currency': {accountResponseBody}");
            }

            var balances = new List<CashAmount>() { new(cashToken.Value<decimal>(), currencyToken.Value<string>()) };
'''
assert old_get_cash_balance in src, "GetCashBalance() anchor not found"
assert src.count(old_get_cash_balance) == 1, "GetCashBalance() anchor not unique"
src = src.replace(old_get_cash_balance, new_get_cash_balance, 1)

with open(path, "w", encoding="utf-8") as f:
    f.write(src)

print("AlpacaBrokerage.cs patched: pattern_day_trader workaround applied.")
PYEOF
    then
        die "pattern_day_trader workaround patch failed on $ALPACA_BROKERAGE_CS"
    fi

    info "pattern_day_trader workaround applied successfully."
fi

# Confirm the patch took effect.
if grep -q "$PDT_WORKAROUND_MARKER" "$ALPACA_BROKERAGE_CS"; then
    info "Patch verification — pattern_day_trader workaround marker found in file."
else
    die "pattern_day_trader workaround marker not found after patching — patch may have failed silently"
fi

# -----------------------------------------------------------------------------
# Step 5: Create /opt/Lean symlink for Alpaca plugin's hardcoded path
# -----------------------------------------------------------------------------
section "Creating /opt/Lean symlink for Alpaca plugin"

# QuantConnect.AlpacaBrokerage.csproj contains a hardcoded relative path:
#   <Compile Include="..\..\Lean\Common\Properties\SharedAssemblyInfo.cs" />
# From its location at /opt/lean-alpaca/QuantConnect.AlpacaBrokerage/, that
# resolves to /opt/Lean/Common/Properties/SharedAssemblyInfo.cs.  LEAN is
# cloned to /opt/lean-engine, not /opt/Lean, so the build would fail with a
# "file not found" error without this symlink.
#
# Creating /opt/Lean → /opt/lean-engine satisfies the plugin's hardcoded
# reference without modifying any upstream source files.
info "[INFO] Creating symlink /opt/Lean → /opt/lean-engine to satisfy the Alpaca plugin's hardcoded relative path reference to ../../Lean/"
ln -sfn /opt/lean-engine /opt/Lean \
    || die "Failed to create symlink /opt/Lean → /opt/lean-engine"
info "Symlink created: /opt/Lean → $(readlink /opt/Lean)"

# -----------------------------------------------------------------------------
# Step 6: Build LEAN engine in Release configuration
# -----------------------------------------------------------------------------
section "Building LEAN engine (Release) — this will take a long time"

info "Build start time: $(date '+%Y-%m-%d %H:%M:%S')"
info "Building: $LEAN_SLN"
info "Configuration: Release"
info "Output will be at: $LEAN_RELEASE_DIR"
echo ""

"$DOTNET_BIN" build "$LEAN_SLN" -c Release \
    || die "LEAN engine build failed — check the output above for compiler errors"

info "LEAN engine build complete at $(date '+%H:%M:%S')."

# Verify the primary launcher DLL was produced.
LEAN_LAUNCHER_DLL="${LEAN_RELEASE_DIR}/QuantConnect.Lean.Launcher.dll"

if [[ -f "$LEAN_LAUNCHER_DLL" ]]; then
    info "Launcher DLL confirmed: $LEAN_LAUNCHER_DLL"
else
    die "Build succeeded but launcher DLL not found at $LEAN_LAUNCHER_DLL"
fi

# -----------------------------------------------------------------------------
# Step 7: Build Alpaca brokerage plugin in Release configuration
# -----------------------------------------------------------------------------
section "Building Alpaca brokerage plugin (Release) — this will take a long time"

info "Build start time: $(date '+%Y-%m-%d %H:%M:%S')"
info "Building: $ALPACA_CSPROJ"
info "Configuration: Release"
info "Output will be at: $ALPACA_RELEASE_DIR"
echo ""

# Build the brokerage project directly instead of the full solution.
# The solution includes a test project that references ../Lean/Tests/ which
# resolves to /opt/Lean/Tests/ — a path that does not exist because LEAN is
# cloned to /opt/lean-engine/.  Building the .csproj skips that test project
# entirely and produces exactly the plugin DLLs we need.
"$DOTNET_BIN" build "$ALPACA_CSPROJ" -c Release \
    || die "Alpaca plugin build failed — check the output above for compiler errors"

info "Alpaca plugin build complete at $(date '+%H:%M:%S')."

# Verify at least one Alpaca DLL was produced.
ALPACA_DLL_COUNT="$(find "$ALPACA_RELEASE_DIR" -maxdepth 2 -name 'QuantConnect.Brokerages.Alpaca*.dll' 2>/dev/null | wc -l)"

if [[ "$ALPACA_DLL_COUNT" -eq 0 ]]; then
    # Try a broader search in case the output path differs slightly across versions.
    ALPACA_DLL_COUNT="$(find "$LEAN_ALPACA_DIR" -path '*/bin/Release/*.dll' -name '*Alpaca*' 2>/dev/null | wc -l)"
    if [[ "$ALPACA_DLL_COUNT" -eq 0 ]]; then
        die "Alpaca build succeeded but no Alpaca DLLs found under $LEAN_ALPACA_DIR/*/bin/Release/"
    fi
fi

info "Alpaca plugin DLLs built: $ALPACA_DLL_COUNT file(s)"

# -----------------------------------------------------------------------------
# Step 8: Copy Alpaca plugin DLLs into LEAN's Release output directory
# -----------------------------------------------------------------------------
section "Copying Alpaca plugin DLLs into LEAN Release output"

# LEAN discovers brokerage plugins by scanning its own output directory for
# DLLs matching specific naming conventions. We copy all DLLs produced by the
# Alpaca plugin build into LEAN's Launcher/bin/Release/ directory so that LEAN
# finds them automatically without any additional PATH or assembly-resolver
# configuration.
#
# We copy rather than symlink because .NET's assembly loader may not follow
# symlinks reliably across all runtime versions.

info "Source: $ALPACA_RELEASE_DIR"
info "Destination: $LEAN_RELEASE_DIR"

# Find all DLLs in the Alpaca plugin Release output tree and copy them.
COPIED=0
while IFS= read -r -d '' DLL_FILE; do
    DLL_BASENAME="$(basename "$DLL_FILE")"
    cp -f "$DLL_FILE" "${LEAN_RELEASE_DIR}/${DLL_BASENAME}" \
        || die "Failed to copy $DLL_FILE → $LEAN_RELEASE_DIR"
    info "  Copied: $DLL_BASENAME"
    (( COPIED++ )) || true
done < <(find "$ALPACA_RELEASE_DIR" -maxdepth 2 -name '*.dll' -print0 2>/dev/null)

if [[ $COPIED -eq 0 ]]; then
    die "No DLLs found in $ALPACA_RELEASE_DIR — verify the Alpaca build succeeded"
fi

info "$COPIED DLL(s) copied into LEAN Release directory."

# -----------------------------------------------------------------------------
# Step 9: Set ownership of build directories
# -----------------------------------------------------------------------------
section "Setting ownership of build directories"

# All files under /opt/lean-engine and /opt/lean-alpaca should be owned by the
# project user (pi-admin) so that the trading service can run without root and
# can write log files and configuration to those directories.
chown -R "${PROJECT_USER}:${PROJECT_USER}" "$LEAN_ENGINE_DIR" "$LEAN_ALPACA_DIR" \
    || die "chown failed for $LEAN_ENGINE_DIR and $LEAN_ALPACA_DIR"

info "Ownership set to ${PROJECT_USER}:${PROJECT_USER}"
info "  ${LEAN_ENGINE_DIR}"
info "  ${LEAN_ALPACA_DIR}"

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
section "LEAN build complete"

echo ""
echo "  Build output:"
echo "    LEAN Launcher   : ${LEAN_RELEASE_DIR}/QuantConnect.Lean.Launcher.dll"
echo "    Alpaca DLLs     : $COPIED file(s) copied into $LEAN_RELEASE_DIR"
echo ""
echo "  Source trees:"
echo "    LEAN engine     : $LEAN_ENGINE_DIR"
echo "    Alpaca plugin   : $LEAN_ALPACA_DIR"
echo ""
echo "  To run LEAN manually (as $PROJECT_USER):"
echo "    cd $LEAN_RELEASE_DIR"
echo "    dotnet QuantConnect.Lean.Launcher.dll"
echo ""
echo "  Next step: sudo bash 07_verify.sh"
echo ""
