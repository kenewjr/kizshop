using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using KizShop.Common;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class PruneLogsTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _liteDbPath;
        private readonly string _sqlitePath;
        private readonly MockClock _clock;

        public PruneLogsTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_prune_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _liteDbPath = Path.Combine(_tempDir, "test.litedb");
            _sqlitePath = Path.Combine(_tempDir, "test.db");
            _clock = new MockClock { UtcNow = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc) };
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public async Task LiteDbStorage_PruneLogs_RemovesOldLogsAndPreservesRecent()
        {
            using var storage = new LiteDbStorage(_liteDbPath, _clock);
            await storage.InitializeAsync();

            // Insert old transaction (40 days in the past)
            _clock.UtcNow = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            await storage.LogTransactionAsync(1001, "OldWithdraw", Money.FromDecimal(10m), "OldRecord");

            // Insert recent transaction (1 day in the past)
            _clock.UtcNow = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            await storage.LogTransactionAsync(1001, "RecentWithdraw", Money.FromDecimal(20m), "RecentRecord");

            // Clock now at 2026-10-11, prune records older than 30 days
            _clock.UtcNow = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
            await storage.PruneLogsAsync(30);
            storage.Dispose();

            // Verify with direct read
            using var db = new LiteDB.LiteDatabase(_liteDbPath);
            var txs = db.GetCollection<LiteDbStorage.TransactionDoc>("transactions").FindAll().ToList();

            Assert.Single(txs);
            Assert.Equal("RecentWithdraw", txs[0].Type);
            Assert.Equal(2000L, txs[0].AmountCents);
        }

        [Fact]
        public async Task SqliteStorage_PruneLogs_RemovesOldLogsAndPreservesRecent()
        {
            using var storage = new SqliteStorage(_sqlitePath, _clock);
            await storage.InitializeAsync();

            // Insert old record
            _clock.UtcNow = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            await storage.LogTransactionAsync(2001, "OldSqlite", Money.FromDecimal(5m), "OldRecord");

            // Insert recent record
            _clock.UtcNow = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            await storage.LogTransactionAsync(2001, "RecentSqlite", Money.FromDecimal(15m), "RecentRecord");

            // Prune with clock at 2026-10-11
            _clock.UtcNow = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
            await storage.PruneLogsAsync(30);

            // Query SQLite directly
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_sqlitePath}");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT type, amount FROM kiz_transactions;";
            using var reader = await cmd.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal("RecentSqlite", reader.GetString(0));
            Assert.Equal(1500L, reader.GetInt64(1));
            Assert.False(await reader.ReadAsync()); // Only 1 row remains!
        }
    }
}
