#!/usr/bin/env bash
# =============================================================================
# fix_lean_config.sh — Repair a malformed LEAN config.json on the Pi
# =============================================================================
# Problem: manual sed edits to /opt/lean-engine/Launcher/config.json can
# introduce duplicate keys, trailing commas, or comment lines that break
# Python's json.load() (used by the Makefile deploy step).
#
# What this script does:
#   1. Reads /opt/lean-engine/Launcher/config.json
#   2. Backs up the broken file to config.json.broken-may5
#   3. Strips C-style comments (// … and /* … */)
#   4. Removes trailing commas before } or ]
#   5. Deduplicates keys (last occurrence wins — same as Python dict behaviour)
#   6. Enforces required algorithm values:
#        "algorithm-type-name": "DualMomentumV2"
#        "algorithm-location":  "DualMomentumV2.dll"
#   7. Writes indented, valid JSON back to config.json
#   8. Validates the final file with python3 -m json.tool
#
# Usage (run on the Pi as pi-admin or root):
#   bash ~/Pi-AI-Trader/scripts/fix_lean_config.sh
#
# The script is idempotent: running it multiple times produces the same result.
# =============================================================================

set -euo pipefail

CONFIG="/opt/lean-engine/Launcher/config.json"
BACKUP="${CONFIG}.broken-may5"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
info()  { echo "[INFO]  $*"; }
warn()  { echo "[WARN]  $*"; }
error() { echo "[ERROR] $*" >&2; }
die()   { error "$*"; exit 1; }

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
command -v python3 >/dev/null 2>&1 || die "python3 is required but not found in PATH."

[ -f "${CONFIG}" ] || die "Config file not found: ${CONFIG}"

# ---------------------------------------------------------------------------
# Step 1 — Back up the (possibly broken) file
# ---------------------------------------------------------------------------
if [ -f "${BACKUP}" ]; then
    warn "Backup already exists at ${BACKUP} — will not overwrite it."
    warn "If you need a fresh backup, remove it first: rm '${BACKUP}'"
else
    cp "${CONFIG}" "${BACKUP}"
    info "Backed up broken config to: ${BACKUP}"
fi

# ---------------------------------------------------------------------------
# Step 2 — Repair and rewrite via Python
# ---------------------------------------------------------------------------
info "Attempting to repair: ${CONFIG}"

python3 - "${CONFIG}" <<'PYEOF'
import sys, re, json, collections

path = sys.argv[1]

with open(path, "r", encoding="utf-8") as fh:
    raw = fh.read()

# ---- 2a. Strip single-line comments (// …) --------------------------------
# Must run before the multi-line pass so // inside /* */ doesn't confuse us.
raw = re.sub(r'//[^\n]*', '', raw)

# ---- 2b. Strip block comments (/* … */) ------------------------------------
raw = re.sub(r'/\*.*?\*/', '', raw, flags=re.DOTALL)

# ---- 2c. Remove trailing commas before } or ] --------------------------------
# This regex handles cases like:  ,\n  }  or  ,  ]
raw = re.sub(r',\s*([}\]])', r'\1', raw)

# ---- 2d. Try to parse as-is -------------------------------------------------
try:
    cfg = json.loads(raw, object_pairs_hook=collections.OrderedDict)
    print("[INFO]  JSON parsed successfully after comment/comma cleanup.")
except json.JSONDecodeError as exc:
    print(f"[ERROR] Still cannot parse JSON after cleanup: {exc}", file=sys.stderr)
    print("[ERROR] Manual inspection required. Broken file is preserved at the backup path.", file=sys.stderr)
    sys.exit(1)

# ---- 2e. Deduplicate keys (last value wins) ----------------------------------
# json.loads with OrderedDict keeps all pairs; convert to a plain dict to
# drop duplicates while preserving the last-seen value for each key.
def dedup(obj):
    if isinstance(obj, collections.OrderedDict):
        seen = {}
        for k, v in obj.items():
            seen[k] = dedup(v)
        return seen
    if isinstance(obj, list):
        return [dedup(i) for i in obj]
    return obj

cfg = dedup(cfg)

# ---- 2f. Enforce required algorithm values -----------------------------------
required = {
    "algorithm-type-name": "DualMomentumV2",
    "algorithm-location":  "DualMomentumV2.dll",
}
for key, value in required.items():
    old = cfg.get(key)
    if old != value:
        print(f"[INFO]  Setting {key!r}: {old!r} -> {value!r}")
        cfg[key] = value
    else:
        print(f"[INFO]  {key!r} already correct: {value!r}")

# ---- 2g. Write corrected JSON ------------------------------------------------
output = json.dumps(cfg, indent=2)
with open(path, "w", encoding="utf-8") as fh:
    fh.write(output)
    fh.write("\n")

print(f"[INFO]  Wrote repaired config to: {path}")
PYEOF

# ---------------------------------------------------------------------------
# Step 3 — Final validation
# ---------------------------------------------------------------------------
info "Validating repaired file..."
if python3 -m json.tool "${CONFIG}" > /dev/null 2>&1; then
    info "Validation PASSED — ${CONFIG} is valid JSON."
else
    error "Validation FAILED — the repaired file is still not valid JSON."
    error "The backup is at: ${BACKUP}"
    error "Run:  python3 -m json.tool '${CONFIG}'  for details."
    exit 1
fi

info "Done. You can now run 'make deploy' from ~/Pi-AI-Trader."
