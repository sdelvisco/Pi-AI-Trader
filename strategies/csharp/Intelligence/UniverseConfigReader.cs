using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using QuantConnect.Logging;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Where the ticker list returned by UniverseConfigReader.Read() came from.
    /// </summary>
    public enum UniverseConfigSource
    {
        /// <summary>universe.json existed and passed every validation rule.</summary>
        File,

        /// <summary>universe.json was missing, unreadable or invalid, so the
        /// caller's own built-in (hardcoded) list was returned instead.</summary>
        HardcodedFallback,
    }

    /// <summary>
    /// Result of one UniverseConfigReader.Read() call: the ticker list to use,
    /// which source it came from, and (for a fallback) why. Returned as one
    /// object so a caller can log "Universe source: file|hardcoded-fallback,
    /// size N" and the reason without re-deriving anything.
    /// </summary>
    public class UniverseReadResult
    {
        public UniverseReadResult(IReadOnlyList<string> tickers, UniverseConfigSource source, string fallbackReason)
        {
            Tickers = tickers;
            Source = source;
            FallbackReason = fallbackReason;
        }

        /// <summary>The ticker list to use, upper-cased, in file order (or
        /// in the built-in list's order for a fallback).</summary>
        public IReadOnlyList<string> Tickers { get; }

        /// <summary>Which source <see cref="Tickers"/> came from.</summary>
        public UniverseConfigSource Source { get; }

        /// <summary>Human-readable reason for a fallback; null when
        /// <see cref="Source"/> is <see cref="UniverseConfigSource.File"/>.</summary>
        public string FallbackReason { get; }

        /// <summary>Short label used in log lines: "file" or
        /// "hardcoded-fallback" (the exact wording the owner asked for).</summary>
        public string SourceLabel => Source == UniverseConfigSource.File ? "file" : "hardcoded-fallback";
    }

    /// <summary>
    /// Reads the trading universe from the shared universe.json file (see
    /// UniverseConfig), written by the web portal's Settings page
    /// (web/routes/api.py). Same fail-safe style as AggregatorConfigReader:
    /// every failure mode falls back to the CALLER'S OWN built-in list with
    /// a logged warning, and Read() never throws.
    ///
    /// Unlike AggregatorConfigReader, any single bad entry rejects the
    /// WHOLE file (no partial universe). A half-applied universe could
    /// silently drop tickers the owner expects to trade, whereas falling
    /// back to the known built-in list is a safe, well-understood state that
    /// is logged as such.
    ///
    /// Validation rules (kept identical to web/universe.py's
    /// validate_ticker_list(); change both together):
    ///   - file exists, is non-empty, is valid JSON, and has a "Tickers" array
    ///   - the array is non-empty and has at most <see cref="MaxTickers"/> entries
    ///   - every entry, after trimming and upper-casing, matches
    ///     <see cref="TickerPattern"/> (e.g. "SPY", "BRK.B")
    ///   - no duplicates (compared after upper-casing, so "spy" + "SPY" is a
    ///     duplicate)
    ///   - every required ticker (DualMomentumV2's DefensiveTicker "AGG" and
    ///     AbsMomReferenceTicker "SPY") is present
    ///
    /// Case handling: entries are upper-cased rather than rejected, matching
    /// AggregatorConfigReader's case-insensitive leniency for hand-edited
    /// files. The web portal itself always writes upper-case.
    /// </summary>
    public class UniverseConfigReader
    {
        /// <summary>Path of the shared universe file on the Pi. Lives next to
        /// aggregator-config.json and signals.jsonl in the project's one
        /// established shared-runtime-state directory.</summary>
        public const string DefaultFilePath = "/var/lib/tradingpi/headline-news-pipeline/universe.json";

        /// <summary>Largest accepted universe. The current list has 44
        /// tickers; 100 leaves room to grow while keeping LEAN's daily-bar
        /// subscriptions and the monthly History() ranking loop (one History
        /// call per ticker) comfortably within the Pi's resources. Must match
        /// UNIVERSE_MAX_TICKERS in web/universe.py.</summary>
        public const int MaxTickers = 100;

        /// <summary>Tickers that must always be in the universe:
        /// DualMomentumV2's DefensiveTicker ("AGG") and AbsMomReferenceTicker
        /// ("SPY"). DualMomentumV2 passes its own constants instead of this
        /// array; this copy exists for consumers that have no such constants
        /// (HeadlineNewsPipeline). Must match UNIVERSE_LOCKED_TICKERS in
        /// web/universe.py.</summary>
        public static readonly IReadOnlyList<string> LockedTickers = new[] { "AGG", "SPY" };

        /// <summary>Strict US-equity ticker pattern: 1-5 upper-case letters,
        /// optionally followed by a "." and a 1-2 letter share-class suffix
        /// (Alpaca's symbology, e.g. "BRK.B"). Must match TICKER_PATTERN in
        /// web/universe.py.</summary>
        public static readonly Regex TickerPattern = new Regex(@"^[A-Z]{1,5}(\.[A-Z]{1,2})?$", RegexOptions.CultureInvariant);

        // Absolute path of the universe file this reader reads.
        private readonly string _filePath;

        // The caller's built-in list, returned unchanged on any failure.
        private readonly IReadOnlyList<string> _fallbackTickers;

        // Tickers that must be present for the file to be accepted.
        private readonly IReadOnlyList<string> _requiredTickers;

        /// <summary>
        /// Creates a reader.
        /// </summary>
        /// <param name="filePath">Path of universe.json.</param>
        /// <param name="fallbackTickers">The caller's built-in list, returned
        /// on any failure. Must be non-empty.</param>
        /// <param name="requiredTickers">Tickers the file must contain
        /// (upper-case). Null means <see cref="LockedTickers"/>.</param>
        public UniverseConfigReader(string filePath, IReadOnlyList<string> fallbackTickers, IReadOnlyList<string> requiredTickers = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("filePath must not be null or empty.", nameof(filePath));
            }
            if (fallbackTickers == null || fallbackTickers.Count == 0)
            {
                throw new ArgumentException("fallbackTickers must not be null or empty.", nameof(fallbackTickers));
            }
            _filePath = filePath;
            // Copy so a later change to the caller's array cannot alter what
            // this reader returns.
            _fallbackTickers = fallbackTickers.ToArray();
            _requiredTickers = (requiredTickers ?? LockedTickers).ToArray();
        }

        /// <summary>The path this reader reads (for callers' log lines).</summary>
        public string FilePath => _filePath;

        /// <summary>
        /// Reads and validates the universe file. Never throws: every failure
        /// returns the built-in fallback list with Source=HardcodedFallback
        /// and a reason, and logs a warning.
        /// </summary>
        public UniverseReadResult Read()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return Fallback($"file '{_filePath}' does not exist");
                }

                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return Fallback($"file '{_filePath}' is empty");
                }

                UniverseConfig config;
                try
                {
                    config = JsonConvert.DeserializeObject<UniverseConfig>(json);
                }
                catch (JsonException ex)
                {
                    // Also covers a "Tickers" value that is not an array of
                    // strings (e.g. a number or an object), which Newtonsoft
                    // reports as a JsonSerializationException.
                    return Fallback($"file '{_filePath}' was not valid JSON of the expected shape ({ex.Message})");
                }

                if (config == null || config.Tickers == null)
                {
                    return Fallback($"file '{_filePath}' has no \"Tickers\" array");
                }

                // Collect every problem instead of stopping at the first, so
                // one log line explains everything wrong with a hand-edited file.
                var problems = ValidateAndNormalize(config.Tickers, out var normalized);
                if (problems.Count > 0)
                {
                    return Fallback($"file '{_filePath}' failed validation: {string.Join("; ", problems)}");
                }

                return new UniverseReadResult(normalized, UniverseConfigSource.File, null);
            }
            catch (Exception ex)
            {
                // Any other I/O problem (permissions, file locked mid-replace,
                // etc.) is still a fallback, never an exception.
                Log.Error($"UniverseConfigReader: failed to read '{_filePath}': {ex.Message}.");
                return Fallback($"failed to read '{_filePath}': {ex.Message}");
            }
        }

        /// <summary>
        /// Applies every validation rule to <paramref name="rawTickers"/>.
        /// Returns the list of problems (empty when valid) and, through
        /// <paramref name="normalized"/>, the trimmed, upper-cased tickers in
        /// their original order.
        /// </summary>
        private List<string> ValidateAndNormalize(IList<string> rawTickers, out List<string> normalized)
        {
            var problems = new List<string>();
            normalized = new List<string>(rawTickers.Count);

            if (rawTickers.Count == 0)
            {
                problems.Add("\"Tickers\" is empty");
                return problems;
            }
            if (rawTickers.Count > MaxTickers)
            {
                problems.Add($"{rawTickers.Count} tickers exceeds the maximum of {MaxTickers}");
            }

            // Ordinal set of tickers already seen, after upper-casing.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rawTickers.Count; i++)
            {
                var raw = rawTickers[i];
                if (raw == null)
                {
                    problems.Add($"entry {i} is null");
                    continue;
                }

                var ticker = raw.Trim().ToUpperInvariant();
                if (!TickerPattern.IsMatch(ticker))
                {
                    problems.Add($"entry {i} '{raw}' is not a valid ticker");
                    continue;
                }
                if (!seen.Add(ticker))
                {
                    problems.Add($"duplicate ticker '{ticker}'");
                    continue;
                }
                normalized.Add(ticker);
            }

            // Required tickers are checked against the normalized set, so
            // "agg" satisfies the "AGG" requirement.
            foreach (var required in _requiredTickers)
            {
                if (!seen.Contains(required))
                {
                    problems.Add($"required ticker '{required}' is missing");
                }
            }

            return problems;
        }

        /// <summary>Logs the fallback reason and returns the built-in list.</summary>
        private UniverseReadResult Fallback(string reason)
        {
            Log.Trace(
                $"UniverseConfigReader: WARNING: {reason}. Using the built-in hardcoded universe " +
                $"({_fallbackTickers.Count} tickers).");
            return new UniverseReadResult(_fallbackTickers, UniverseConfigSource.HardcodedFallback, reason);
        }
    }
}
