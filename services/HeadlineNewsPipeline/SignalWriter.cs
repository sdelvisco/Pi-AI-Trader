using System;
using System.IO;
using Newtonsoft.Json;
using PiAiTrader.Intelligence;

namespace PiAiTrader.HeadlineNewsPipeline
{
    /// <summary>
    /// Appends each successfully-produced Signal as one line of JSON to a
    /// local output file, for the future Signal Aggregator (out of scope
    /// this session — nothing consumes this file yet) to read later.
    ///
    /// Concurrency note: this doesn't need to be a full database, per this
    /// session's spec — a single complete line, terminated by '\n' and
    /// flushed immediately, written via one FileStream.Write call opened in
    /// append mode, is enough for a future single-reader consumer never to
    /// observe a torn/partial line. This deliberately does not hold the
    /// file handle open across poll cycles (open-write-flush-close per
    /// signal) since signals are produced at most a few dozen times per
    /// 15-minute poll — nowhere near hot enough to need a persistent
    /// handle.
    /// </summary>
    public class SignalWriter
    {
        private readonly string _outputFilePath;

        public SignalWriter(string outputFilePath)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                throw new ArgumentException("outputFilePath must not be null or empty.", nameof(outputFilePath));
            }
            _outputFilePath = outputFilePath;
        }

        public void AppendSignal(Signal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            var directory = Path.GetDirectoryName(_outputFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonConvert.SerializeObject(signal) + Environment.NewLine;

            // FileMode.Append + a single Write call: on Linux, a write() to
            // a file opened with O_APPEND is atomic with respect to other
            // appenders as long as it fits in one syscall (true here — a
            // single-line JSON Signal is far under any relevant pipe/write
            // buffer limit), so a concurrent reader tailing this file never
            // sees a partial line even without external locking.
            using (var stream = new FileStream(_outputFilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(line);
                writer.Flush();
            }
        }
    }
}
