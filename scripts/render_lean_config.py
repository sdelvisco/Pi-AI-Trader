#!/usr/bin/env python3
# =============================================================================
# render_lean_config.py — Substitute ${VAR} credential placeholders into
# config/lean_config.template.json before it is written to LEAN's config.json.
#
# QuantConnect.Configuration.Config performs NO environment-variable
# substitution of its own (confirmed by reading its GetValue<T>/GetToken()
# source directly), and make deploy's previous config-copy step was a plain
# json.load()/json.dump() round-trip. Neither ever resolved ${ALPACA_KEY_ID}
# -style placeholders into real values -- this script is what actually does
# that now, reading credentials from the systemd EnvironmentFile directly
# (not the process environment, since `sudo` does not inherit it).
#
# Usage: render_lean_config.py <template.json> <env-file> <output.json>
# =============================================================================

import json
import os
import sys


def load_env_file(path):
    env = {}
    if not os.path.exists(path):
        return env
    with open(path, "r", encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            key = key.strip()
            value = value.strip()
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
                value = value[1:-1]
            env[key] = value
    return env


def main():
    if len(sys.argv) != 4:
        print(f"Usage: {sys.argv[0]} <template.json> <env-file> <output.json>", file=sys.stderr)
        sys.exit(2)

    template_path, env_path, output_path = sys.argv[1:4]

    with open(template_path, "r", encoding="utf-8") as fh:
        raw = fh.read()

    file_env = load_env_file(env_path)

    # Substitute against the file's own values first, falling back to any
    # matching process environment variable, and leaving unresolved ${VAR}
    # tokens (e.g. a typo, or a var genuinely not meant to be substituted)
    # untouched rather than blanking them out -- os.path.expandvars already
    # has this "leave as-is if undefined" behavior built in.
    merged_env = dict(os.environ)
    merged_env.update(file_env)
    saved_environ = dict(os.environ)
    os.environ.clear()
    os.environ.update(merged_env)
    try:
        substituted = os.path.expandvars(raw)
    finally:
        os.environ.clear()
        os.environ.update(saved_environ)

    # Parses (and thus validates) the substituted JSON before writing it out.
    cfg = json.loads(substituted)

    with open(output_path, "w", encoding="utf-8") as fh:
        json.dump(cfg, fh, indent=2)
        fh.write("\n")

    # config.json now contains live credentials in plaintext -- restrict it
    # the same way /etc/tradingpi/alpaca.env itself is required to be.
    os.chmod(output_path, 0o600)

    # Never print credential values -- only whether substitution resolved.
    required_vars = ("ALPACA_KEY_ID", "ALPACA_SECRET_KEY", "ALPACA_PAPER_TRADING")
    missing = [v for v in required_vars if not merged_env.get(v)]
    if missing:
        print(
            f"WARNING: these credential env vars were empty or not found in {env_path}: "
            f"{', '.join(missing)} -- their placeholders were left unresolved.",
            file=sys.stderr,
        )


if __name__ == "__main__":
    main()
