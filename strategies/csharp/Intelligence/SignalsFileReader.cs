using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using QuantConnect.Logging;

namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Reads recent Signals for one symbol out of the HeadlineNewsPipeline
    /// service's append-only signals.jsonl output
    /// (/var/lib/tradingpi/headline-news-pipeline/signals.jsonl, per
    /// services/HeadlineNewsPipeline/Program.cs's DefaultStateDir +
    /// SignalsFileName).
    ///
    /// This is the single most safety-critical class in this session besides
    /// PositionSizer's clamp: DualMomentumV2 depends on ReadRecentSignals()
    /// NEVER throwing, no matter what state the file is in -- missing,
    /// empty, malformed, or torn mid-line from a concurrent
    /// HeadlineNewsPipeline append that lands exactly while this reads. Every
    /// failure mode below degrades to "zero recent signals for this symbol"
    /// (an empty result), which is by design indistinguishable, from
    /// DualMomentumV2's perspective, from the normal "no recent news for
    /// this ticker" case.
    ///
    /// No file locking or writer coordination is attempted here, matching
    /// SignalWriter's own class comment: appends are single, small,
    /// O_APPEND writes at most a few dozen times per 15-minute poll cycle,
    /// and this reader only runs at rebalance time (monthly, plus the rare
    /// manual force-rebalance trigger) -- nowhere near hot enough to need
    /// real coordination. A best-effort tolerant read (skip whatever line
    /// doesn't parse) is sufficient.
    /// </summary>
    public class SignalsFileReader
    {
        /// <summary>Fixed, named lookback window -- not a magic number
        /// scattered through call sites. 7 days per this session's spec.</summary>
        public static readonly TimeSpan LookbackWindow = TimeSpan.FromDays(7);

        private readonly string _filePath;

        public SignalsFileReader(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("filePath must not be null or empty.", nameof(filePath));
            }
            _filePath = filePath;
        }

        /// <summary>
        /// Returns every Signal for <paramref name="symbol"/> (case-insensitive
        /// match) whose TimestampUtc is within LookbackWindow of
        /// <paramref name="nowUtc"/> (inclusive at the exact boundary).
        /// Never throws.
        /// </summary>
        public IReadOnlyList<Signal> ReadRecentSignals(string symbol, DateTime nowUtc)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    // Indistinguishable-from-"no recent signals" case #1: the
                    // pipeline hasn't produced any output yet, the path is
                    // wrong, or the file was deleted. This must not throw --
                    // it's the expected state on a brand-new deployment.
                    Log.Trace(
                        $"SignalsFileReader: WARNING: signals file '{_filePath}' does not exist. " +
                        $"Treating as zero recent signals for '{symbol}'.");
                    return Array.Empty<Signal>();
                }

                var cutoffUtc = nowUtc - LookbackWindow;
                var results = new List<Signal>();

                // File.ReadLines streams line-by-line (splitting on '\n',
                // same as File.ReadAllLines) without buffering the whole
                // file into memory up front. A torn trailing line from a
                // concurrent SignalWriter.AppendSignal() write landing
                // mid-read shows up here as one incomplete/malformed final
                // line -- caught and skipped below, never allowed to abort
                // the read of every earlier, complete line.
                foreach (var line in File.ReadLines(_filePath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    Signal signal;
                    try
                    {
                        signal = JsonConvert.DeserializeObject<Signal>(line);
                    }
                    catch (JsonException ex)
                    {
                        Log.Trace(
                            $"SignalsFileReader: WARNING: skipping unparseable line in '{_filePath}' " +
                            $"(likely a torn trailing line from a concurrent writer): {ex.Message}");
                        continue;
                    }

                    if (signal == null || string.IsNullOrWhiteSpace(signal.Symbol))
                    {
                        continue;
                    }

                    if (!string.Equals(signal.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (signal.TimestampUtc >= cutoffUtc)
                    {
                        results.Add(signal);
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                // Catch-all safety net for any failure not already handled
                // above (permission error, file deleted out from under us
                // mid-read, disk error, etc.). Per this session's explicit
                // top priority: this method must never throw, block, or
                // delay a rebalance, regardless of cause.
                Log.Error(
                    $"SignalsFileReader: failed to read recent signals for '{symbol}' from '{_filePath}': {ex.Message}. " +
                    "Treating as zero recent signals.");
                return Array.Empty<Signal>();
            }
        }
    }
}
