using System;
using System.IO;
using Newtonsoft.Json;
using QuantConnect.Logging;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Persists the ID-based dedup high-water mark to a small local JSON
    /// state file, so a restart of this service (or a crash mid-poll)
    /// resumes exactly where it left off instead of re-scoring — or
    /// silently skipping — headlines.
    ///
    /// Why both an ID and a timestamp are persisted: Alpaca's News API
    /// query parameters support filtering by a "start" timestamp (see
    /// AlpacaNewsClient) but have no way to filter by article ID directly.
    /// So each poll asks Alpaca for "everything since this timestamp" and
    /// then filters client-side to "Id > LastProcessedId" to avoid
    /// re-processing articles that share the same timestamp as the
    /// last-processed one — timestamp alone isn't a safe dedup key since
    /// Alpaca can (and does) publish multiple articles in the same second.
    /// </summary>
    public class HighWaterMarkStore
    {
        private readonly string _stateFilePath;

        public HighWaterMarkStore(string stateFilePath)
        {
            if (string.IsNullOrWhiteSpace(stateFilePath))
            {
                throw new ArgumentException("stateFilePath must not be null or empty.", nameof(stateFilePath));
            }
            _stateFilePath = stateFilePath;
        }

        /// <summary>Returns the persisted state, or null if no state file
        /// exists yet — the caller (PollCycleRunner) is responsible for
        /// first-run seeding behavior when this returns null, per this
        /// session's spec that a first run must not attempt to process
        /// Alpaca's entire historical backlog.</summary>
        public HighWaterMarkState Load()
        {
            if (!File.Exists(_stateFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(_stateFilePath);
            try
            {
                return JsonConvert.DeserializeObject<HighWaterMarkState>(json);
            }
            catch (JsonException ex)
            {
                // A corrupt state file is a config/data problem, not
                // something to silently paper over by treating it as
                // "first run" (which could cause the historical-backlog
                // problem this store exists to prevent) — fail loudly per
                // this project's established pattern.
                Log.Error($"HighWaterMarkStore: state file '{_stateFilePath}' was not valid JSON: {ex.Message}");
                throw new AlpacaResponseFormatException(
                    $"HighWaterMarkStore: state file '{_stateFilePath}' was not valid JSON.", ex);
            }
        }

        /// <summary>Overwrites the state file with the given state. Writes
        /// to a temp file and renames over the target so a concurrent
        /// reader (or a crash mid-write) never sees a torn/partial state
        /// file — the same "complete write, then make visible" approach
        /// used for signals.jsonl appends, just via rename instead of
        /// append-and-flush since this file's whole content changes.</summary>
        public void Save(HighWaterMarkState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonConvert.SerializeObject(state);
            var tempFilePath = _stateFilePath + ".tmp";
            File.WriteAllText(tempFilePath, json);
            // File.Move(..., overwrite: true) maps to a single rename(2)
            // syscall on Linux — atomic on the same filesystem, so a
            // concurrent reader or a crash mid-write never observes a
            // half-written state file.
            File.Move(tempFilePath, _stateFilePath, overwrite: true);
        }
    }
}
