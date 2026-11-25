"""
universe.py — Trading-universe helpers for the web portal
==========================================================
Backs the Settings page's "Trading Universe" editor (routes in
web/routes/api.py: GET/POST /api/universe, POST /api/universe/validate).

The universe lives in ONE shared file,
/var/lib/tradingpi/headline-news-pipeline/universe.json, shaped
{"Tickers": ["SPY", ...]}. Readers:
  - DualMomentumV2 (strategies/csharp/DualMomentumV2.cs) reads it once in
    Initialize() through PiAiTrader.Intelligence.UniverseConfigReader.
  - HeadlineNewsPipeline reads it at the start of every poll cycle through
    the same reader.
This module is the only WRITER. A write is applied by restarting
lean-trader: services/universe-apply.path watches the file (PathChanged)
and starts services/universe-apply.service, which runs
`systemctl try-restart lean-trader` as root. lean-web itself never calls
sudo (its NoNewPrivileges=true makes sudo fail; see DEVIATIONS.md).

Deliberately NO third hardcoded copy of the ticker list lives here: when
universe.json is missing or invalid, the portal shows the repo seed file
config/universe.default.json instead (source "default").

Only the Python standard library is used (urllib, zoneinfo), so this adds
no package to the venv.
"""

import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request
from datetime import date, datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

# ---------------------------------------------------------------------------
# Validation rules. Kept IDENTICAL to UniverseConfigReader
# (strategies/csharp/Intelligence/UniverseConfigReader.cs): if the portal
# accepted a list the C# reader rejects, both consumers would silently fall
# back to their built-in lists. Change both together.
# ---------------------------------------------------------------------------

# 1-5 upper-case letters, optionally "." plus a 1-2 letter share-class
# suffix (Alpaca symbology, e.g. "BRK.B"). Same as UniverseConfigReader.TickerPattern.
TICKER_PATTERN = re.compile(r"^[A-Z]{1,5}(\.[A-Z]{1,2})?$")

# Largest accepted universe. Same as UniverseConfigReader.MaxTickers.
UNIVERSE_MAX_TICKERS = 100

# Tickers that can never be removed: DualMomentumV2's DefensiveTicker (AGG)
# and AbsMomReferenceTicker (SPY). Same as UniverseConfigReader.LockedTickers.
UNIVERSE_LOCKED_TICKERS = ("AGG", "SPY")

# ---------------------------------------------------------------------------
# Alpaca hosts. The ONLY place these two hosts appear in the web layer.
# Used solely when ALPACA_BASE_URL is empty, to derive the trading API host
# from ALPACA_PAPER_TRADING (owner decision 2026-11-24). Values are the two
# hosts documented in config/alpaca_credentials.template.
# ---------------------------------------------------------------------------
ALPACA_PAPER_BASE_URL = "https://paper-api.alpaca.markets"
ALPACA_LIVE_BASE_URL = "https://api.alpaca.markets"

# Seconds to wait for any single Alpaca HTTP call before giving up.
ALPACA_HTTP_TIMEOUT_SECONDS = 10

# Market-data feed for the price check. The free (Basic) plan only has
# real-time IEX data for equities (docs.alpaca.markets, "About Market Data
# API"); passing it explicitly makes the check mean "has a price on the
# free tier" regardless of the account's plan.
ALPACA_FREE_TIER_FEED = "iex"

# Exchange time zone used for the rebalance-window check. DualMomentumV2's
# TimeRules.At() runs in the algorithm time zone, America/New_York.
EXCHANGE_TZ = ZoneInfo("America/New_York")

# Refuse a save from this many minutes BEFORE the scheduled rebalance time...
REBALANCE_GUARD_MINUTES_BEFORE = 10
# ...until this many minutes AFTER it. The rebalance itself waits at most
# RebalanceFillWaitBudgetSeconds (120 s) for fills, plus one History() call
# per ticker for the ranking; 30 minutes is a generous margin over that.
REBALANCE_GUARD_MINUTES_AFTER = 30


class UniverseError(Exception):
    """A problem the API reports to the browser with a clear message."""


# ---------------------------------------------------------------------------
# Ticker list validation
# ---------------------------------------------------------------------------

def normalize_ticker(raw) -> str | None:
    """Trim and upper-case one candidate; None if it is not a string."""
    if not isinstance(raw, str):
        return None
    return raw.strip().upper()


def is_valid_ticker_syntax(ticker: str | None) -> bool:
    """True when ticker (already normalized) matches TICKER_PATTERN."""
    return ticker is not None and TICKER_PATTERN.fullmatch(ticker) is not None


def validate_ticker_list(raw_tickers) -> tuple[list[str], list[dict]]:
    """
    Applies every structural rule to a candidate list.

    Returns (normalized_tickers, errors). errors is a list of
    {"ticker": <str or None>, "message": <str>} so the UI can show each
    problem next to the ticker it is about; an empty list means valid.
    Mirrors UniverseConfigReader.ValidateAndNormalize().
    """
    errors: list[dict] = []
    normalized: list[str] = []

    if not isinstance(raw_tickers, list):
        return [], [{"ticker": None, "message": "\"tickers\" must be a JSON array of strings."}]
    if len(raw_tickers) == 0:
        return [], [{"ticker": None, "message": "The universe cannot be empty."}]
    if len(raw_tickers) > UNIVERSE_MAX_TICKERS:
        errors.append({
            "ticker": None,
            "message": f"{len(raw_tickers)} tickers exceeds the maximum of {UNIVERSE_MAX_TICKERS}.",
        })

    seen: set[str] = set()
    for index, raw in enumerate(raw_tickers):
        ticker = normalize_ticker(raw)
        if not is_valid_ticker_syntax(ticker):
            errors.append({"ticker": str(raw), "message": f"Entry {index} is not a valid ticker symbol."})
            continue
        if ticker in seen:
            errors.append({"ticker": ticker, "message": "Duplicate ticker."})
            continue
        seen.add(ticker)
        normalized.append(ticker)

    for locked in UNIVERSE_LOCKED_TICKERS:
        if locked not in seen:
            errors.append({"ticker": locked, "message": f"{locked} is required and cannot be removed."})

    return normalized, errors


# ---------------------------------------------------------------------------
# File access
# ---------------------------------------------------------------------------

def read_universe_file(path: Path) -> tuple[list[str] | None, str | None]:
    """
    Reads and validates a universe file. Returns (tickers, None) when valid,
    or (None, reason) for missing/empty/malformed/invalid -- the same
    conditions under which the C# reader falls back.
    """
    try:
        text = Path(path).read_text(encoding="utf-8")
    except FileNotFoundError:
        return None, f"{path} does not exist"
    except OSError as exc:
        return None, f"{path} could not be read: {exc}"

    if not text.strip():
        return None, f"{path} is empty"
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        return None, f"{path} is not valid JSON: {exc}"
    if not isinstance(data, dict) or "Tickers" not in data:
        return None, f"{path} has no \"Tickers\" array"

    tickers, errors = validate_ticker_list(data["Tickers"])
    if errors:
        return None, f"{path} failed validation: " + "; ".join(
            (f"{e['ticker']}: " if e["ticker"] else "") + e["message"] for e in errors
        )
    return tickers, None


def effective_universe(universe_path: Path, seed_path: Path) -> dict:
    """
    The list the portal shows and edits.

    source "file":    universe.json is valid (this is what both consumers use).
    source "default": universe.json is missing/invalid, so the repo seed
                      config/universe.default.json is shown. In this state the
                      C# consumers are using their own built-in lists, which
                      were seeded identically. fallback_reason says why.
    Raises UniverseError if the seed itself is unusable (a repo bug).
    """
    tickers, reason = read_universe_file(universe_path)
    if tickers is not None:
        return {"tickers": tickers, "source": "file", "fallback_reason": None}

    seed_tickers, seed_reason = read_universe_file(seed_path)
    if seed_tickers is None:
        raise UniverseError(f"Universe file unusable ({reason}) and seed file unusable ({seed_reason}).")
    return {"tickers": seed_tickers, "source": "default", "fallback_reason": reason}


def write_universe_atomic(path: Path, tickers: list[str]) -> None:
    """
    Writes {"Tickers": [...]} with write-to-temp-then-rename, the same
    approach aggregator_mode_post() uses, so a reader never sees a torn
    file. The rename into place is what triggers universe-apply.path
    (PathChanged fires on an atomic replace; verified on the Pi 2026-11-24).
    The temp file has a different name, so writing it does not trigger.
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = path.with_suffix(path.suffix + ".tmp")
    with open(tmp_path, "w", encoding="utf-8") as fh:
        json.dump({"Tickers": tickers}, fh, indent=2)
        fh.write("\n")
        # Flush to disk before the rename so a power loss cannot leave a
        # renamed-but-empty file behind.
        fh.flush()
        os.fsync(fh.fileno())
    tmp_path.replace(path)


# ---------------------------------------------------------------------------
# Alpaca access (stdlib urllib; never logs or returns credential values)
# ---------------------------------------------------------------------------

def _check_base_url(value: str, env_name: str) -> str:
    """
    Validates an Alpaca base URL and returns it as scheme://host (no path).
    Raises UniverseError when it looks malformed -- in particular a trailing
    path such as "/v2", which would double up with the "/v2/..." this module
    appends (owner asked for that case to be flagged).
    """
    parts = urllib.parse.urlsplit(value.strip())
    if parts.scheme != "https" or not parts.netloc:
        raise UniverseError(f"{env_name} must be an https:// URL with a host (scheme+host only).")
    if parts.path not in ("", "/") or parts.query or parts.fragment:
        raise UniverseError(
            f"{env_name} looks malformed: it has a path/query ({parts.path or ''}{'?' if parts.query else ''}) "
            f"after the host. Use scheme+host only, e.g. https://example.alpaca.markets -- "
            f"a trailing /v2 would make every request /v2/v2/..."
        )
    return f"{parts.scheme}://{parts.netloc}"


def resolve_trading_base_url(env=os.environ) -> tuple[str, str]:
    """
    Returns (scheme+host, how-it-was-chosen) for the Alpaca trading API.

    Owner decision (2026-11-24): use ALPACA_BASE_URL when it is set; only
    when it is empty, derive it from ALPACA_PAPER_TRADING ("true" -> paper
    host, "false" -> live host). Anything else refuses rather than guesses.
    """
    configured = (env.get("ALPACA_BASE_URL") or "").strip()
    if configured:
        return _check_base_url(configured, "ALPACA_BASE_URL"), "ALPACA_BASE_URL"

    paper = (env.get("ALPACA_PAPER_TRADING") or "").strip().lower()
    if paper == "true":
        return ALPACA_PAPER_BASE_URL, "derived from ALPACA_PAPER_TRADING=true"
    if paper == "false":
        return ALPACA_LIVE_BASE_URL, "derived from ALPACA_PAPER_TRADING=false"
    raise UniverseError(
        "ALPACA_BASE_URL is empty and ALPACA_PAPER_TRADING is not \"true\" or \"false\"; "
        "refusing to guess which Alpaca account to query."
    )


def resolve_data_base_url(env=os.environ) -> str:
    """Returns scheme+host for the Alpaca market-data API (ALPACA_DATA_URL; required)."""
    configured = (env.get("ALPACA_DATA_URL") or "").strip()
    if not configured:
        raise UniverseError("ALPACA_DATA_URL is not set in lean-web's environment.")
    return _check_base_url(configured, "ALPACA_DATA_URL")


def _auth_headers(env=os.environ) -> dict:
    """Alpaca auth headers (APCA-API-KEY-ID / APCA-API-SECRET-KEY, per the docs)."""
    key_id = env.get("ALPACA_KEY_ID")
    secret = env.get("ALPACA_SECRET_KEY")
    if not key_id or not secret:
        raise UniverseError("ALPACA_KEY_ID / ALPACA_SECRET_KEY are not set in lean-web's environment.")
    return {"APCA-API-KEY-ID": key_id, "APCA-API-SECRET-KEY": secret, "Accept": "application/json"}


def alpaca_get_json(url: str, env=os.environ):
    """
    GETs url with Alpaca auth headers and returns (status_code, parsed_json).
    A 4xx/5xx is returned (not raised) so callers can treat 404 as "not
    found". Network errors raise UniverseError. Tests patch this function.
    """
    request = urllib.request.Request(url, headers=_auth_headers(env), method="GET")
    try:
        with urllib.request.urlopen(request, timeout=ALPACA_HTTP_TIMEOUT_SECONDS) as response:
            return response.status, json.loads(response.read().decode("utf-8") or "null")
    except urllib.error.HTTPError as exc:
        # Body may be JSON ({"message": ...}) or empty; never fatal.
        try:
            body = json.loads(exc.read().decode("utf-8") or "null")
        except (ValueError, OSError):
            body = None
        return exc.code, body
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        # Only the host is included in the message, never headers.
        host = urllib.parse.urlsplit(url).netloc
        raise UniverseError(f"Could not reach Alpaca at {host}: {exc}") from exc


def validate_candidate(ticker: str, env=os.environ, logger=None) -> dict:
    """
    Runs the three required checks for ONE candidate ticker (owner decision 4):
      1. exists_tradable: GET {base}/v2/assets/{symbol} returns 200 with
         status "active", tradable true and class "us_equity" (LEAN adds it
         with AddEquity, so it must be a US equity).
      2. fractionable:    the same asset has fractionable true (the strategy
         sizes in fractional shares).
      3. has_price:       GET {data}/v2/stocks/{symbol}/snapshot?feed=iex
         returns a positive latestTrade / dailyBar / prevDailyBar price.
    Endpoints and field names verified against docs.alpaca.markets on
    2026-11-24 (see DEVIATIONS.md).

    Returns {"ticker", "ok", "checks": [{"name", "ok", "detail"}]}.
    Raises UniverseError for configuration problems (missing env vars,
    malformed base URL) or when Alpaca cannot be reached.
    """
    base_url, base_how = resolve_trading_base_url(env)
    data_url = resolve_data_base_url(env)
    if logger is not None:
        # scheme+host only -- never keys (owner requirement).
        logger.info("Universe validation: Alpaca trading API %s (%s); data API %s", base_url, base_how, data_url)

    quoted = urllib.parse.quote(ticker, safe="")
    checks = []

    # -- Checks 1 and 2: asset lookup ------------------------------------
    status, asset = alpaca_get_json(f"{base_url}/v2/assets/{quoted}", env)
    if status == 404:
        checks.append({"name": "exists_tradable", "ok": False, "detail": "Alpaca has no asset with this symbol (404)."})
        checks.append({"name": "fractionable", "ok": False, "detail": "Not checked: asset not found."})
    elif status != 200 or not isinstance(asset, dict):
        message = asset.get("message") if isinstance(asset, dict) else None
        raise UniverseError(f"Alpaca asset lookup for {ticker} failed with HTTP {status}" + (f": {message}" if message else "."))
    else:
        problems = []
        if asset.get("status") != "active":
            problems.append(f"status is {asset.get('status')!r}, not 'active'")
        if asset.get("tradable") is not True:
            problems.append("not tradable on Alpaca")
        if asset.get("class") != "us_equity":
            problems.append(f"asset class is {asset.get('class')!r}, not 'us_equity'")
        checks.append({
            "name": "exists_tradable",
            "ok": not problems,
            "detail": "; ".join(problems) if problems else f"Active, tradable US equity ({asset.get('exchange', '?')}).",
        })
        fractionable = asset.get("fractionable") is True
        checks.append({
            "name": "fractionable",
            "ok": fractionable,
            "detail": "Fractional shares supported." if fractionable else "Alpaca does not offer fractional shares for this asset.",
        })

    # -- Check 3: price on the free (IEX) feed ----------------------------
    status, snapshot = alpaca_get_json(
        f"{data_url}/v2/stocks/{quoted}/snapshot?feed={ALPACA_FREE_TIER_FEED}", env
    )
    price, price_from = None, None
    if status == 200 and isinstance(snapshot, dict):
        # Prefer the latest trade, then today's bar, then yesterday's bar.
        for key, field in (("latestTrade", "p"), ("dailyBar", "c"), ("prevDailyBar", "c")):
            section = snapshot.get(key)
            if isinstance(section, dict) and isinstance(section.get(field), (int, float)) and section[field] > 0:
                price, price_from = section[field], key
                break
    if price is not None:
        checks.append({"name": "has_price", "ok": True, "detail": f"${price:,.2f} from {price_from} (IEX feed)."})
    else:
        checks.append({
            "name": "has_price",
            "ok": False,
            "detail": f"No positive price on the free IEX feed (HTTP {status}).",
        })

    return {"ticker": ticker, "ok": all(c["ok"] for c in checks), "checks": checks}


def count_open_orders(env=os.environ) -> int:
    """
    Number of open orders on the Alpaca account
    (GET {base}/v2/orders?status=open, per the docs). Raises UniverseError
    when it cannot be determined -- the caller then refuses to save.
    """
    base_url, _ = resolve_trading_base_url(env)
    status, orders = alpaca_get_json(f"{base_url}/v2/orders?status=open&limit=500", env)
    if status != 200 or not isinstance(orders, list):
        raise UniverseError(f"Could not list open orders from Alpaca (HTTP {status}).")
    return len(orders)


def first_trading_day_of_month(today: date, env=os.environ) -> date | None:
    """
    First NYSE trading day of today's month, from Alpaca's market calendar
    (GET {base}/v2/calendar?start=&end=, per the docs). This matches LEAN's
    DateRules.MonthStart(SPY), which rolls the 1st forward past weekends and
    holidays. Returns None if no trading day has occurred yet this month.
    Raises UniverseError if the calendar cannot be fetched.
    """
    base_url, _ = resolve_trading_base_url(env)
    start = today.replace(day=1)
    status, days = alpaca_get_json(
        f"{base_url}/v2/calendar?start={start.isoformat()}&end={today.isoformat()}", env
    )
    if status != 200 or not isinstance(days, list):
        raise UniverseError(f"Could not read the market calendar from Alpaca (HTTP {status}).")
    dates = sorted(d["date"] for d in days if isinstance(d, dict) and isinstance(d.get("date"), str))
    return date.fromisoformat(dates[0]) if dates else None


# ---------------------------------------------------------------------------
# Rebalance schedule (read from the strategy source, never hardcoded here)
# ---------------------------------------------------------------------------

def read_rebalance_schedule(strategy_source: Path) -> tuple[int, int]:
    """
    Reads RebalanceHourEt / RebalanceMinuteEt from DualMomentumV2.cs and
    confirms the schedule still uses DateRules.MonthStart (first trading day
    of the month). Raises UniverseError if the source no longer looks the
    way this parser expects, so a refactor fails closed (save refused)
    instead of silently disabling the guard.
    """
    try:
        text = Path(strategy_source).read_text(encoding="utf-8")
    except OSError as exc:
        raise UniverseError(f"Cannot read the strategy source {strategy_source}: {exc}") from exc

    hour = re.search(r"private\s+const\s+int\s+RebalanceHourEt\s*=\s*(\d+)\s*;", text)
    minute = re.search(r"private\s+const\s+int\s+RebalanceMinuteEt\s*=\s*(\d+)\s*;", text)
    month_start = re.search(r"DateRules\.MonthStart\(", text)
    if not (hour and minute and month_start):
        raise UniverseError(
            "Could not read the rebalance schedule (RebalanceHourEt / RebalanceMinuteEt / "
            f"DateRules.MonthStart) from {strategy_source}; refusing to save."
        )
    return int(hour.group(1)), int(minute.group(1))


def in_rebalance_window(now_et: datetime, first_trading_day: date | None, hour: int, minute: int) -> bool:
    """
    True when now_et falls inside the guard window around today's scheduled
    rebalance: [time - REBALANCE_GUARD_MINUTES_BEFORE, time + ..._AFTER] on
    the first trading day of the month. Pure function (unit-tested).
    """
    if first_trading_day is None or now_et.date() != first_trading_day:
        return False
    scheduled = now_et.replace(hour=hour, minute=minute, second=0, microsecond=0)
    return (scheduled - timedelta(minutes=REBALANCE_GUARD_MINUTES_BEFORE)
            <= now_et
            <= scheduled + timedelta(minutes=REBALANCE_GUARD_MINUTES_AFTER))


def now_exchange_time() -> datetime:
    """Current time in America/New_York (patched by tests)."""
    return datetime.now(EXCHANGE_TZ)
