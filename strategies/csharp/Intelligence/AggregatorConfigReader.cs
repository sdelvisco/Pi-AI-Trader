using System;
using System.IO;
using Newtonsoft.Json;
using QuantConnect.Logging;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Reads the active AggregationMode from a small shared JSON config file
    /// (see AggregatorConfig), written by the web portal's mode-selection
    /// control (web/routes/api.py) and read fresh by DualMomentumV2 at the
    /// start of every rebalance -- never cached from Initialize() -- so a
    /// portal-driven change takes effect on the very next rebalance without
    /// requiring a lean-trader restart.
    ///
    /// Every failure mode (missing file, empty file, invalid JSON,
    /// unrecognized mode string, or any other I/O problem) falls back to
    /// DefaultMode with a logged warning. Never throws -- a config-read
    /// problem must never be able to block a rebalance.
    /// </summary>
    public class AggregatorConfigReader
    {
        /// <summary>This session's chosen default when the config file is
        /// missing, empty, or unreadable. CapitalSplit was chosen (over,
        /// say, a "no adjustment" pseudo-mode) because it is itself a
        /// reasonable, intentional aggregation mode, and because DEFERRING
        /// TO IT never bypasses the position-sizing fail-safes elsewhere in
        /// this session (a ticker with zero recent signals still gets its
        /// exact original equal weight regardless of which mode is
        /// "active").</summary>
        public const AggregationMode DefaultMode = AggregationMode.CapitalSplit;

        private readonly string _filePath;

        public AggregatorConfigReader(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("filePath must not be null or empty.", nameof(filePath));
            }
            _filePath = filePath;
        }

        public AggregationMode ReadActiveMode()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    Log.Trace(
                        $"AggregatorConfigReader: WARNING: config file '{_filePath}' does not exist. " +
                        $"Defaulting to {DefaultMode}.");
                    return DefaultMode;
                }

                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Log.Trace(
                        $"AggregatorConfigReader: WARNING: config file '{_filePath}' is empty. " +
                        $"Defaulting to {DefaultMode}.");
                    return DefaultMode;
                }

                AggregatorConfig config;
                try
                {
                    config = JsonConvert.DeserializeObject<AggregatorConfig>(json);
                }
                catch (JsonException ex)
                {
                    Log.Trace(
                        $"AggregatorConfigReader: WARNING: config file '{_filePath}' was not valid JSON " +
                        $"({ex.Message}). Defaulting to {DefaultMode}.");
                    return DefaultMode;
                }

                if (config == null || string.IsNullOrWhiteSpace(config.ActiveMode))
                {
                    Log.Trace(
                        $"AggregatorConfigReader: WARNING: config file '{_filePath}' had no ActiveMode set. " +
                        $"Defaulting to {DefaultMode}.");
                    return DefaultMode;
                }

                // ignoreCase: true so "capitalsplit"/"CapitalSplit"/"CAPITALSPLIT"
                // all resolve the same way -- the web portal writes the exact
                // enum name, but a hand-edited config file shouldn't be able
                // to silently misfire on case alone. Enum.IsDefined guards
                // against Enum.TryParse's own quirk of accepting a bare
                // integer string (e.g. "2") as a valid parse even though
                // it's not a recognized name.
                if (Enum.TryParse(config.ActiveMode, ignoreCase: true, out AggregationMode mode) &&
                    Enum.IsDefined(typeof(AggregationMode), mode))
                {
                    return mode;
                }

                Log.Trace(
                    $"AggregatorConfigReader: WARNING: config file '{_filePath}' had unrecognized ActiveMode " +
                    $"'{config.ActiveMode}'. Defaulting to {DefaultMode}.");
                return DefaultMode;
            }
            catch (Exception ex)
            {
                Log.Error(
                    $"AggregatorConfigReader: failed to read config from '{_filePath}': {ex.Message}. " +
                    $"Defaulting to {DefaultMode}.");
                return DefaultMode;
            }
        }
    }
}
