using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PiAiTrader.Intelligence;
using Xunit;

namespace PiAiTrader.HeadlineNewsPipeline.Tests
{
    public class SignalWriterTests : IDisposable
    {
        private readonly string _tempDir;

        public SignalWriterTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hnp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        private static Signal MakeSignal(string symbol) => new Signal
        {
            Symbol = symbol,
            Direction = SignalDirection.Bullish,
            RawScore = 0.5,
            Confidence = 0.9,
            SourceWeight = 1.0,
            SourceModule = "Test",
            TimestampUtc = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc),
            Rationale = "test rationale",
        };

        [Fact]
        public void AppendSignal_WritesOneJsonLinePerCall()
        {
            var path = Path.Combine(_tempDir, "signals.jsonl");
            var writer = new SignalWriter(path);

            writer.AppendSignal(MakeSignal("AAPL"));
            writer.AppendSignal(MakeSignal("MSFT"));

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);

            var first = JsonConvert.DeserializeObject<Signal>(lines[0]);
            var second = JsonConvert.DeserializeObject<Signal>(lines[1]);
            Assert.Equal("AAPL", first.Symbol);
            Assert.Equal("MSFT", second.Symbol);
        }

        [Fact]
        public void AppendSignal_CreatesParentDirectoryIfMissing()
        {
            var path = Path.Combine(_tempDir, "nested", "signals.jsonl");
            var writer = new SignalWriter(path);

            writer.AppendSignal(MakeSignal("AAPL"));

            Assert.True(File.Exists(path));
        }
    }
}
