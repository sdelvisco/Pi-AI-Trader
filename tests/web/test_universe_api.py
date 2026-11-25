"""
test_universe_api.py — tests for the trading-universe routes and helpers
========================================================================
The repo had no Python test pattern before this file (no pytest config,
requirements.txt is empty), so these use the standard library's unittest
only -- no new dependency. Run from the repo root with the project venv:

    venv/bin/python -m unittest discover -s tests -t . -v

What is covered:
  - GET  /api/universe           file / default(seed) sources
  - POST /api/universe/validate  the three Alpaca checks + base-URL rules
  - POST /api/universe           structural validation, unchanged, cooldown,
                                 rebalance/open-order guard (409/503),
                                 added-ticker validation, atomic write
  - helpers: rebalance window, schedule parsed from DualMomentumV2.cs
  - consistency: the repo seed equals both C# built-in fallback lists

Alpaca is never called: web.universe.alpaca_get_json is patched with a fake
that answers by URL. Only a minimal Flask app with the api blueprint is
built (web.app's module-level SocketIO setup is not imported).
"""

import json
import logging
import os
import re
import tempfile
import time
import unittest
from datetime import date, datetime
from pathlib import Path
from unittest import mock

from flask import Flask

from web import universe as uv
from web.routes.api import api_bp

# Repo root: tests/web/ -> tests/ -> repo.
REPO_ROOT = Path(__file__).resolve().parents[2]
SEED_PATH = REPO_ROOT / "config" / "universe.default.json"
STRATEGY_PATH = REPO_ROOT / "strategies" / "csharp" / "DualMomentumV2.cs"
PIPELINE_UNIVERSE_PATH = REPO_ROOT / "services" / "HeadlineNewsPipeline" / "TickerUniverse.cs"

# Environment the routes see: all five ALPACA_* variables set, like the Pi.
# Values are dummies; nothing leaves the process.
ALPACA_ENV = {
    "ALPACA_KEY_ID": "test-key",
    "ALPACA_SECRET_KEY": "test-secret",
    "ALPACA_PAPER_TRADING": "true",
    "ALPACA_BASE_URL": "https://paper.example.test",
    "ALPACA_DATA_URL": "https://data.example.test",
}


class FakeAlpaca:
    """
    Stands in for web.universe.alpaca_get_json. Answers by URL path:
      /v2/assets/<T>           -> self.assets[T] (200) or 404
      /v2/stocks/<T>/snapshot  -> self.snapshots[T] (200) or 404
      /v2/orders               -> self.open_orders (200)
      /v2/calendar             -> self.calendar (200)
    Records every URL requested in self.urls.
    """

    def __init__(self):
        self.urls = []
        self.assets = {}
        self.snapshots = {}
        self.open_orders = []
        self.orders_status = 200
        # Default calendar: first trading day of the month is the 1st.
        self.calendar = None

    def add_good(self, ticker, exchange="NASDAQ"):
        """Registers a ticker that passes all three checks."""
        self.assets[ticker] = {
            "symbol": ticker, "status": "active", "tradable": True,
            "fractionable": True, "class": "us_equity", "exchange": exchange,
        }
        self.snapshots[ticker] = {"latestTrade": {"p": 123.45}, "dailyBar": {"c": 120.0}}

    def __call__(self, url, env=None):
        self.urls.append(url)
        path = url.split("://", 1)[1].split("/", 1)[1]
        path, _, query = path.partition("?")
        parts = path.split("/")
        if parts[:2] == ["v2", "assets"]:
            asset = self.assets.get(parts[2])
            return (200, asset) if asset else (404, {"message": "not found"})
        if parts[:2] == ["v2", "stocks"] and parts[3] == "snapshot":
            snap = self.snapshots.get(parts[2])
            return (200, snap) if snap else (404, {"message": "no data"})
        if parts[:2] == ["v2", "orders"]:
            return self.orders_status, self.open_orders
        if parts[:2] == ["v2", "calendar"]:
            if self.calendar is not None:
                return 200, self.calendar
            start = re.search(r"start=([\d-]+)", query).group(1)
            return 200, [{"date": start, "open": "09:30", "close": "16:00"}]
        raise AssertionError(f"unexpected URL {url}")


class UniverseApiTestBase(unittest.TestCase):
    """Builds a minimal app with temp paths and patched Alpaca/time."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        tmp = Path(self.tmp.name)
        self.universe_path = tmp / "universe.json"
        self.force_path = tmp / "force_rebalance"

        app = Flask(__name__)
        app.config.update(
            TESTING=True,
            UNIVERSE_CONFIG_PATH=self.universe_path,
            UNIVERSE_SEED_PATH=SEED_PATH,
            STRATEGY_SOURCE_PATH=STRATEGY_PATH,
            FORCE_REBALANCE_TRIGGER_PATH=self.force_path,
            UNIVERSE_APPLY_COOLDOWN_SECONDS=600,
        )
        app.register_blueprint(api_bp, url_prefix="/api")
        self.client = app.test_client()

        # Fake Alpaca.
        self.alpaca = FakeAlpaca()
        patcher = mock.patch.object(uv, "alpaca_get_json", self.alpaca)
        patcher.start()
        self.addCleanup(patcher.stop)

        # Environment like the Pi's.
        env_patcher = mock.patch.dict(os.environ, ALPACA_ENV, clear=False)
        env_patcher.start()
        self.addCleanup(env_patcher.stop)

        # "Now" = mid-month afternoon, well away from any rebalance window.
        self.set_now(datetime(2026, 11, 18, 14, 0, tzinfo=uv.EXCHANGE_TZ))

    def set_now(self, value):
        """Patches the exchange clock the guard uses."""
        patcher = mock.patch.object(uv, "now_exchange_time", return_value=value)
        patcher.start()
        self.addCleanup(patcher.stop)

    def write_universe(self, tickers, age_seconds=3600):
        """Writes universe.json and back-dates it past the cooldown."""
        self.universe_path.write_text(json.dumps({"Tickers": tickers}), encoding="utf-8")
        old = time.time() - age_seconds
        os.utime(self.universe_path, (old, old))

    def seed_tickers(self):
        return json.loads(SEED_PATH.read_text(encoding="utf-8"))["Tickers"]


class GetUniverseTests(UniverseApiTestBase):

    def test_valid_file_is_reported_as_file_source(self):
        self.write_universe(["SPY", "AGG", "NVDA"])
        data = self.client.get("/api/universe").get_json()
        self.assertEqual(data["tickers"], ["SPY", "AGG", "NVDA"])
        self.assertEqual(data["source"], "file")
        self.assertEqual(data["locked"], ["AGG", "SPY"])
        self.assertIsNone(data["fallback_reason"])
        self.assertEqual(data["max_size"], uv.UNIVERSE_MAX_TICKERS)

    def test_missing_file_falls_back_to_seed(self):
        data = self.client.get("/api/universe").get_json()
        self.assertEqual(data["source"], "default")
        self.assertEqual(data["tickers"], self.seed_tickers())
        self.assertIn("does not exist", data["fallback_reason"])

    def test_file_without_spy_falls_back_to_seed(self):
        self.write_universe(["AGG", "QQQ"])
        data = self.client.get("/api/universe").get_json()
        self.assertEqual(data["source"], "default")
        self.assertIn("SPY", data["fallback_reason"])

    def test_malformed_file_falls_back_to_seed(self):
        self.universe_path.write_text("{ nope", encoding="utf-8")
        data = self.client.get("/api/universe").get_json()
        self.assertEqual(data["source"], "default")


class ValidateTickerTests(UniverseApiTestBase):

    def post(self, ticker):
        return self.client.post("/api/universe/validate", json={"ticker": ticker})

    def test_good_ticker_passes_all_three_checks(self):
        self.alpaca.add_good("AMD")
        response = self.post("amd")  # lower-case is normalized
        data = response.get_json()
        self.assertEqual(response.status_code, 200)
        self.assertTrue(data["ok"])
        self.assertEqual([c["name"] for c in data["checks"]], ["exists_tradable", "fractionable", "has_price"])
        # Uses ALPACA_BASE_URL / ALPACA_DATA_URL and the free IEX feed.
        self.assertIn("https://paper.example.test/v2/assets/AMD", self.alpaca.urls)
        self.assertIn("https://data.example.test/v2/stocks/AMD/snapshot?feed=iex", self.alpaca.urls)

    def test_bad_syntax_is_400(self):
        self.assertEqual(self.post("NOT A TICKER").status_code, 400)
        self.assertEqual(self.post(None).status_code, 400)

    def test_unknown_asset_fails_exists_check(self):
        data = self.post("ZZZZ").get_json()
        self.assertFalse(data["ok"])
        self.assertFalse(data["checks"][0]["ok"])

    def test_inactive_or_untradable_fails_exists_check(self):
        self.alpaca.add_good("OLD")
        self.alpaca.assets["OLD"].update(status="inactive", tradable=False)
        data = self.post("OLD").get_json()
        self.assertFalse(data["checks"][0]["ok"])
        self.assertIn("inactive", data["checks"][0]["detail"])

    def test_non_fractionable_fails_fractionable_check(self):
        self.alpaca.add_good("WHOLE")
        self.alpaca.assets["WHOLE"]["fractionable"] = False
        data = self.post("WHOLE").get_json()
        self.assertFalse(data["ok"])
        self.assertTrue(data["checks"][0]["ok"])
        self.assertFalse(data["checks"][1]["ok"])

    def test_no_price_fails_price_check(self):
        self.alpaca.add_good("NOPX")
        del self.alpaca.snapshots["NOPX"]
        data = self.post("NOPX").get_json()
        self.assertFalse(data["checks"][2]["ok"])

    def test_price_falls_back_to_previous_daily_bar(self):
        self.alpaca.add_good("PREV")
        self.alpaca.snapshots["PREV"] = {"latestTrade": None, "prevDailyBar": {"c": 9.5}}
        data = self.post("PREV").get_json()
        self.assertTrue(data["checks"][2]["ok"])
        self.assertIn("prevDailyBar", data["checks"][2]["detail"])

    def test_empty_base_url_derives_paper_host(self):
        self.alpaca.add_good("AMD")
        with mock.patch.dict(os.environ, {"ALPACA_BASE_URL": "", "ALPACA_PAPER_TRADING": "TRUE"}):
            self.assertEqual(self.post("AMD").status_code, 200)
        self.assertIn(f"{uv.ALPACA_PAPER_BASE_URL}/v2/assets/AMD", self.alpaca.urls)

    def test_empty_base_url_derives_live_host(self):
        self.alpaca.add_good("AMD")
        with mock.patch.dict(os.environ, {"ALPACA_BASE_URL": "", "ALPACA_PAPER_TRADING": "false"}):
            self.assertEqual(self.post("AMD").status_code, 200)
        self.assertIn(f"{uv.ALPACA_LIVE_BASE_URL}/v2/assets/AMD", self.alpaca.urls)

    def test_empty_base_url_and_unrecognized_paper_flag_refuses(self):
        with mock.patch.dict(os.environ, {"ALPACA_BASE_URL": "", "ALPACA_PAPER_TRADING": "maybe"}):
            response = self.post("AMD")
        self.assertEqual(response.status_code, 503)
        self.assertIn("refusing to guess", response.get_json()["message"])
        self.assertEqual(self.alpaca.urls, [])

    def test_base_url_with_trailing_v2_is_flagged(self):
        with mock.patch.dict(os.environ, {"ALPACA_BASE_URL": "https://paper.example.test/v2"}):
            response = self.post("AMD")
        self.assertEqual(response.status_code, 503)
        self.assertIn("malformed", response.get_json()["message"])
        self.assertEqual(self.alpaca.urls, [])

    def test_base_url_trailing_slash_is_accepted(self):
        self.alpaca.add_good("AMD")
        with mock.patch.dict(os.environ, {"ALPACA_BASE_URL": "https://paper.example.test/"}):
            self.assertEqual(self.post("AMD").status_code, 200)
        self.assertIn("https://paper.example.test/v2/assets/AMD", self.alpaca.urls)

    def test_validation_logs_scheme_and_host_but_never_keys(self):
        # Owner requirement: log only the scheme+host used, never keys.
        self.alpaca.add_good("AMD")
        app = self.client.application
        with self.assertLogs(app.logger, level="INFO") as captured:
            self.assertEqual(self.post("AMD").status_code, 200)
        text = "\n".join(captured.output)
        self.assertIn("https://paper.example.test", text)
        self.assertIn("https://data.example.test", text)
        self.assertNotIn(ALPACA_ENV["ALPACA_KEY_ID"], text)
        self.assertNotIn(ALPACA_ENV["ALPACA_SECRET_KEY"], text)

    def test_missing_keys_refuses(self):
        with mock.patch.dict(os.environ, {"ALPACA_KEY_ID": ""}):
            # The real header builder runs inside alpaca_get_json, which is
            # patched here, so call it directly to check the refusal.
            with self.assertRaises(uv.UniverseError):
                uv._auth_headers(os.environ)


class SaveUniverseTests(UniverseApiTestBase):

    def setUp(self):
        super().setUp()
        self.write_universe(["SPY", "AGG", "QQQ"])

    def save(self, tickers):
        return self.client.post("/api/universe", json={"tickers": tickers})

    def saved(self):
        return json.loads(self.universe_path.read_text(encoding="utf-8"))["Tickers"]

    # -- structural rules ------------------------------------------------
    def test_missing_locked_ticker_is_400_with_per_ticker_error(self):
        response = self.save(["AGG", "QQQ"])
        self.assertEqual(response.status_code, 400)
        self.assertIn("SPY", [e["ticker"] for e in response.get_json()["errors"]])
        self.assertEqual(self.saved(), ["SPY", "AGG", "QQQ"])

    def test_duplicate_is_400(self):
        response = self.save(["SPY", "AGG", "QQQ", "qqq"])
        self.assertEqual(response.status_code, 400)
        self.assertIn("Duplicate", json.dumps(response.get_json()["errors"]))

    def test_over_max_is_400(self):
        tickers = ["SPY", "AGG"] + [f"Z{chr(65 + i // 26)}{chr(65 + i % 26)}" for i in range(uv.UNIVERSE_MAX_TICKERS)]
        self.assertEqual(self.save(tickers).status_code, 400)

    def test_bad_syntax_is_400(self):
        self.assertEqual(self.save(["SPY", "AGG", "BRK/B"]).status_code, 400)
        self.assertEqual(self.save("SPY,AGG").status_code, 400)

    # -- unchanged -------------------------------------------------------
    def test_unchanged_list_is_not_rewritten(self):
        before = self.universe_path.stat().st_mtime
        response = self.save(["SPY", "AGG", "QQQ"])
        self.assertEqual(response.get_json()["status"], "unchanged")
        self.assertEqual(self.universe_path.stat().st_mtime, before)

    # -- success ---------------------------------------------------------
    def test_successful_save_writes_atomically_and_validates_only_added(self):
        self.alpaca.add_good("NVDA")
        response = self.save(["SPY", "AGG", "nvda"])  # QQQ removed, NVDA added
        data = response.get_json()
        self.assertEqual(response.status_code, 200, data)
        self.assertEqual(data["status"], "ok")
        self.assertEqual(data["added"], ["NVDA"])
        self.assertEqual(data["removed"], ["QQQ"])
        self.assertEqual(self.saved(), ["SPY", "AGG", "NVDA"])
        # No temp file left behind.
        self.assertFalse(self.universe_path.with_suffix(".json.tmp").exists())
        # Only the added ticker was looked up (not SPY/AGG).
        asset_urls = [u for u in self.alpaca.urls if "/v2/assets/" in u]
        self.assertEqual(asset_urls, ["https://paper.example.test/v2/assets/NVDA"])

    def test_save_from_default_source_creates_file(self):
        self.universe_path.unlink()
        seed = self.seed_tickers()
        response = self.save(seed)  # identical to the seed, but no file yet
        self.assertEqual(response.status_code, 200, response.get_json())
        self.assertEqual(self.saved(), seed)

    def test_added_ticker_failing_validation_is_400_and_nothing_written(self):
        self.alpaca.add_good("WHOLE")
        self.alpaca.assets["WHOLE"]["fractionable"] = False
        response = self.save(["SPY", "AGG", "QQQ", "WHOLE"])
        self.assertEqual(response.status_code, 400)
        self.assertEqual(response.get_json()["errors"][0]["ticker"], "WHOLE")
        self.assertEqual(self.saved(), ["SPY", "AGG", "QQQ"])

    # -- cooldown --------------------------------------------------------
    def test_save_within_cooldown_is_429(self):
        self.write_universe(["SPY", "AGG", "QQQ"], age_seconds=60)
        response = self.save(["SPY", "AGG"])
        self.assertEqual(response.status_code, 429)
        self.assertEqual(self.saved(), ["SPY", "AGG", "QQQ"])

    # -- rebalance / open-order guard ---------------------------------------
    def test_pending_force_rebalance_is_409(self):
        self.force_path.write_text("", encoding="utf-8")
        self.assertEqual(self.save(["SPY", "AGG"]).status_code, 409)
        self.assertEqual(self.saved(), ["SPY", "AGG", "QQQ"])

    def test_inside_scheduled_rebalance_window_is_409(self):
        # First trading day = Nov 2 (calendar), now = 10:05 ET that day.
        self.alpaca.calendar = [{"date": "2026-11-02"}, {"date": "2026-11-03"}]
        self.set_now(datetime(2026, 11, 2, 10, 5, tzinfo=uv.EXCHANGE_TZ))
        response = self.save(["SPY", "AGG"])
        self.assertEqual(response.status_code, 409)
        self.assertIn("10:00 ET", response.get_json()["message"])

    def test_first_trading_day_outside_window_is_allowed(self):
        self.alpaca.calendar = [{"date": "2026-11-02"}]
        self.set_now(datetime(2026, 11, 2, 13, 0, tzinfo=uv.EXCHANGE_TZ))
        self.assertEqual(self.save(["SPY", "AGG"]).status_code, 200)

    def test_open_orders_is_409(self):
        self.alpaca.open_orders = [{"id": "1"}, {"id": "2"}]
        response = self.save(["SPY", "AGG"])
        self.assertEqual(response.status_code, 409)
        self.assertIn("2 open order", response.get_json()["message"])
        self.assertEqual(self.saved(), ["SPY", "AGG", "QQQ"])

    def test_open_orders_unknown_fails_closed_503(self):
        self.alpaca.orders_status = 500
        self.assertEqual(self.save(["SPY", "AGG"]).status_code, 503)
        self.assertEqual(self.saved(), ["SPY", "AGG", "QQQ"])

    def test_unreadable_schedule_fails_closed_503(self):
        broken = Path(self.tmp.name) / "Broken.cs"
        broken.write_text("// no schedule constants here", encoding="utf-8")
        self.client.application.config["STRATEGY_SOURCE_PATH"] = broken
        self.assertEqual(self.save(["SPY", "AGG"]).status_code, 503)


class AppLoggingTests(unittest.TestCase):
    """The real app factory must let INFO lines through (Pi finding 2026-11-25)."""

    def test_create_app_logger_emits_info(self):
        try:
            # web.app imports flask_socketio (installed in the Pi's venv).
            from web.app import create_app
        except ImportError as exc:  # pragma: no cover - only without the venv
            self.skipTest(f"web.app not importable here: {exc}")
        app = create_app()
        self.assertTrue(app.logger.isEnabledFor(logging.INFO))


class HelperTests(unittest.TestCase):

    def test_schedule_is_read_from_strategy_source(self):
        self.assertEqual(uv.read_rebalance_schedule(STRATEGY_PATH), (10, 0))

    def test_rebalance_window_boundaries(self):
        day = date(2026, 12, 1)
        at = lambda h, m: datetime(2026, 12, 1, h, m, tzinfo=uv.EXCHANGE_TZ)
        self.assertFalse(uv.in_rebalance_window(at(9, 49), day, 10, 0))
        self.assertTrue(uv.in_rebalance_window(at(9, 50), day, 10, 0))
        self.assertTrue(uv.in_rebalance_window(at(10, 30), day, 10, 0))
        self.assertFalse(uv.in_rebalance_window(at(10, 31), day, 10, 0))
        # Not the first trading day -> never in the window.
        self.assertFalse(uv.in_rebalance_window(at(10, 0), date(2026, 12, 2), 10, 0))
        self.assertFalse(uv.in_rebalance_window(at(10, 0), None, 10, 0))


class ConsistencyTests(unittest.TestCase):
    """The seed and both C# fallback lists must be the same list, in order."""

    @staticmethod
    def csharp_array(path, declaration):
        text = path.read_text(encoding="utf-8")
        start = text.index(declaration)
        end = text.index("};", start)
        return re.findall(r'^\s*"([A-Z.]+)",', text[start:end], re.M)

    def test_seed_matches_both_builtin_lists(self):
        seed = json.loads(SEED_PATH.read_text(encoding="utf-8"))["Tickers"]
        strategy = self.csharp_array(STRATEGY_PATH, "BuiltInUniverseTickers = new[]")
        pipeline = self.csharp_array(PIPELINE_UNIVERSE_PATH, "BuiltInTickers = new[]")
        self.assertEqual(seed, strategy)
        self.assertEqual(seed, pipeline)
        self.assertEqual(len(seed), 44)

    def test_seed_passes_validation(self):
        seed = json.loads(SEED_PATH.read_text(encoding="utf-8"))["Tickers"]
        _, errors = uv.validate_ticker_list(seed)
        self.assertEqual(errors, [])


if __name__ == "__main__":
    unittest.main()
