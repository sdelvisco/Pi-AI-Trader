using System;
using System.IO;
using Xunit;

namespace PiAiTrader.HeadlineNewsPipeline.Tests
{
    public class HighWaterMarkStoreTests : IDisposable
    {
        private readonly string _tempDir;

        public HighWaterMarkStoreTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hnp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public void Load_NoStateFileExists_ReturnsNull()
        {
            var store = new HighWaterMarkStore(Path.Combine(_tempDir, "state.json"));

            Assert.Null(store.Load());
        }

        [Fact]
        public void SaveThenLoad_RoundTripsExactly()
        {
            var store = new HighWaterMarkStore(Path.Combine(_tempDir, "state.json"));
            var state = new HighWaterMarkState
            {
                LastProcessedId = 42,
                LastProcessedCreatedAtUtc = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc),
            };

            store.Save(state);
            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(42, loaded.LastProcessedId);
            Assert.Equal(state.LastProcessedCreatedAtUtc, loaded.LastProcessedCreatedAtUtc);
        }

        [Fact]
        public void Save_CreatesParentDirectoryIfMissing()
        {
            var nestedPath = Path.Combine(_tempDir, "nested", "subdir", "state.json");
            var store = new HighWaterMarkStore(nestedPath);

            store.Save(new HighWaterMarkState { LastProcessedId = 1, LastProcessedCreatedAtUtc = DateTime.UtcNow });

            Assert.True(File.Exists(nestedPath));
        }

        [Fact]
        public void Save_OverwritesPreviousState()
        {
            var store = new HighWaterMarkStore(Path.Combine(_tempDir, "state.json"));

            store.Save(new HighWaterMarkState { LastProcessedId = 1, LastProcessedCreatedAtUtc = DateTime.UtcNow });
            store.Save(new HighWaterMarkState { LastProcessedId = 2, LastProcessedCreatedAtUtc = DateTime.UtcNow });

            Assert.Equal(2, store.Load().LastProcessedId);
        }
    }
}
