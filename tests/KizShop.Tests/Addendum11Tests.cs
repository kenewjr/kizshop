using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Economy.Models;
using KizShop.Shop.Models;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class Addendum11Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly MockClock _clock;

        public Addendum11Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_add11_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _clock = new MockClock { UtcNow = new DateTime(2026, 10, 17, 14, 0, 0, DateTimeKind.Utc) };
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        // =================================================================
        // 1. BACKUP PATH RESOLUTION & LITEDB/SQLITE DATABASEPATH
        // =================================================================

        [Fact]
        public async Task BackupPathResolution_WhenConnectionStringDoesNotEndInDbOrLiteDb_ResolvesCorrectly()
        {
            // LiteDB when ConnectionString is arbitrary string
            string weirdLiteDbConn = "Server=myhost;Database=ignored_conn_string";
            var liteStorage = (LiteDbStorage)StorageFactory.Create("LiteDB", weirdLiteDbConn, _tempDir);
            await liteStorage.InitializeAsync();
            await liteStorage.SetBalanceAsync(76561198000000001UL, Money.FromDecimal(100m), "init");

            Assert.EndsWith("kizshop.litedb", liteStorage.DatabasePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(liteStorage.DatabasePath));

            var liteBackupMgr = new DatabaseBackupManager(_tempDir, liteStorage, () => new KizShopConfiguration
            {
                DatabaseType = "LiteDB",
                ConnectionString = weirdLiteDbConn,
                BackupKeepCount = 5
            }, _clock);

            var (liteSuccess, liteMsg, liteFile, liteSize) = await liteBackupMgr.CreateBackupAsync(isManual: true);
            Assert.True(liteSuccess, $"LiteDB backup failed: {liteMsg}");
            Assert.NotNull(liteFile);
            Assert.True(liteSize > 0);
            Assert.True(File.Exists(Path.Combine(_tempDir, "backups", liteFile)));

            // SQLite when ConnectionString is arbitrary string
            string weirdSqliteConn = "Data Source=some;Host=other;Invalid";
            var sqliteStorage = (SqliteStorage)StorageFactory.Create("SQLite", weirdSqliteConn, _tempDir);
            await sqliteStorage.InitializeAsync();
            await sqliteStorage.SetBalanceAsync(76561198000000002UL, Money.FromDecimal(200m), "init");

            Assert.EndsWith("kizshop.db", sqliteStorage.DatabasePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sqliteStorage.DatabasePath));

            var sqliteBackupMgr = new DatabaseBackupManager(_tempDir, sqliteStorage, () => new KizShopConfiguration
            {
                DatabaseType = "SQLite",
                ConnectionString = weirdSqliteConn,
                BackupKeepCount = 5
            }, _clock);

            var (sqliteSuccess, sqliteMsg, sqliteFile, sqliteSize) = await sqliteBackupMgr.CreateBackupAsync(isManual: true);
            Assert.True(sqliteSuccess, $"SQLite backup failed: {sqliteMsg}");
            Assert.NotNull(sqliteFile);
            Assert.True(sqliteSize > 0);
            Assert.True(File.Exists(Path.Combine(_tempDir, "backups", sqliteFile)));

            liteStorage.Dispose();
            sqliteStorage.Dispose();
        }

        // =================================================================
        // 2. FAILED BACKUP LEAVES NO FILE MATCHING PATTERN AND PRESERVES PRUNING
        // =================================================================

        [Fact]
        public async Task FailedBackup_LeavesNoMatchingFile_AndDoesNotAffectPruning()
        {
            string backupDir = Path.Combine(_tempDir, "backups");
            Directory.CreateDirectory(backupDir);

            // Seed 2 existing valid backups
            string existing1 = Path.Combine(backupDir, "kizshop_litedb_20261017_100000.db");
            string existing2 = Path.Combine(backupDir, "kizshop_litedb_20261017_110000.db");
            File.WriteAllText(existing1, "valid backup 1");
            File.WriteAllText(existing2, "valid backup 2");

            // Point storage to a non-existent database file without initializing it
            string nonExistentDb = Path.Combine(_tempDir, "does_not_exist.litedb");
            var missingStorage = new LiteDbStorage(nonExistentDb, _clock);

            var mgr = new DatabaseBackupManager(_tempDir, missingStorage, () => new KizShopConfiguration
            {
                DatabaseType = "LiteDB",
                BackupKeepCount = 1 // If pruning were triggered, it would delete one of the existing files
            }, _clock);

            var (success, msg, fileName, size) = await mgr.CreateBackupAsync(isManual: true);

            Assert.False(success);
            Assert.Null(fileName);
            Assert.Equal(0L, size);

            // Verify no leftover .tmp file
            var tmpFiles = Directory.GetFiles(backupDir, "*.tmp");
            Assert.Empty(tmpFiles);

            // Verify existing 2 backups were NOT pruned
            Assert.True(File.Exists(existing1));
            Assert.True(File.Exists(existing2));
            Assert.Equal(2, Directory.GetFiles(backupDir).Length);
        }

        // =================================================================
        // 3. PRUNING IGNORES .TMP FILES & CLEANUP DELETES LEFTOVER .TMP FILES
        // =================================================================

        [Fact]
        public void Pruning_IgnoresTempFiles_AndCleanupDeletesLeftovers()
        {
            string backupDir = Path.Combine(_tempDir, "backups");
            Directory.CreateDirectory(backupDir);

            // Create 5 valid backup files
            for (int i = 1; i <= 5; i++)
            {
                string fname = $"kizshop_sqlite_20261017_{120000 + i}.sqlite";
                string full = Path.Combine(backupDir, fname);
                File.WriteAllText(full, "valid sqlite");
                File.SetLastWriteTimeUtc(full, _clock.UtcNow.AddMinutes(i));
            }

            // Create 3 .tmp files (including ones with backup prefix)
            string tmp1 = Path.Combine(backupDir, "kizshop_sqlite_20261017_120099.sqlite.tmp");
            string tmp2 = Path.Combine(backupDir, "kizshop_litedb_20261017_120088.db.tmp");
            string tmp3 = Path.Combine(backupDir, "leftover_process.tmp");
            File.WriteAllText(tmp1, "partial write 1");
            File.WriteAllText(tmp2, "partial write 2");
            File.WriteAllText(tmp3, "partial write 3");

            var storage = new LiteDbStorage(Path.Combine(_tempDir, "test.litedb"), _clock);
            var mgr = new DatabaseBackupManager(_tempDir, storage, () => new KizShopConfiguration
            {
                BackupKeepCount = 2
            }, _clock);

            // Pruning to 2 should prune 3 valid backups, leaving 2 valid backups + all 3 .tmp files intact
            int pruned = mgr.PruneOldBackups(2);
            Assert.Equal(3, pruned);

            var validRemaining = Directory.GetFiles(backupDir, "kizshop_sqlite_*.sqlite");
            Assert.Equal(2, validRemaining.Length);

            Assert.True(File.Exists(tmp1));
            Assert.True(File.Exists(tmp2));
            Assert.True(File.Exists(tmp3));

            // Startup cleanup deletes all leftover .tmp files
            int cleaned = mgr.CleanupLeftoverTempFiles();
            Assert.Equal(3, cleaned);

            Assert.False(File.Exists(tmp1));
            Assert.False(File.Exists(tmp2));
            Assert.False(File.Exists(tmp3));

            // Valid backups remain intact
            validRemaining = Directory.GetFiles(backupDir, "kizshop_sqlite_*.sqlite");
            Assert.Equal(2, validRemaining.Length);
        }

        // =================================================================
        // 4. CACHE SAFETY (DEFENSIVE COPIES)
        // =================================================================

        [Fact]
        public async Task BalTopCache_And_HistoryCache_ReturnDefensiveCopies()
        {
            var plugin = (KizShopPlugin)RuntimeHelpers.GetUninitializedObject(typeof(KizShopPlugin));
            var balTopCache = new TtlCache<int, List<TopBalanceEntry>>(10, _clock);
            var historyCache = new TtlCache<string, List<TransactionRecord>>(10, _clock);

            plugin.BalTopCache = balTopCache;
            plugin.HistoryCache = historyCache;

            string dbPath = Path.Combine(_tempDir, "cache_safety.litedb");
            var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            // Populate balances
            await storage.SetBalanceAsync(76561198000000001UL, Money.FromDecimal(100m), "seed");
            await storage.SetBalanceAsync(76561198000000002UL, Money.FromDecimal(200m), "seed");

            plugin.Storage = storage;

            // 1. BalTopCache test
            var list1 = await plugin.GetCachedBalTopAsync(10);
            Assert.Equal(2, list1.Count);

            // Mutate list1
            list1.Clear();
            list1.Add(new TopBalanceEntry(999999UL, Money.FromDecimal(999m)));
            Assert.Single(list1);

            // Fetch again (cache hit)
            var list2 = await plugin.GetCachedBalTopAsync(10);
            Assert.Equal(2, list2.Count);
            Assert.Equal(76561198000000002UL, list2[0].SteamId);
            Assert.Equal(76561198000000001UL, list2[1].SteamId);

            // 2. HistoryCache test
            var hist1 = await plugin.GetCachedHistoryAsync(76561198000000001UL, 1, 10, 0);
            Assert.Single(hist1);

            // Mutate hist1
            hist1.Clear();
            Assert.Empty(hist1);

            // Fetch again (cache hit)
            var hist2 = await plugin.GetCachedHistoryAsync(76561198000000001UL, 1, 10, 0);
            Assert.Single(hist2);
            Assert.Equal(76561198000000001UL, hist2[0].SteamId);

            storage.Dispose();
        }

        // =================================================================
        // 5. KIT RATE-LIMIT BUCKET INDEPENDENT FROM BUY BUCKET
        // =================================================================

        [Fact]
        public void KitRateLimit_IsIndependentFromBuyBucket()
        {
            var limiter = new CommandRateLimiter(_clock);
            ulong steamId = 76561198000000123UL;

            // Acquire "kit" rate limit
            bool kitAcquired = limiter.TryAcquire(steamId, "kit", 5000, out double kitRemaining);
            Assert.True(kitAcquired);

            // Second "kit" request is rate limited
            bool kitSecond = limiter.TryAcquire(steamId, "kit", 5000, out double kitRemaining2);
            Assert.False(kitSecond);
            Assert.True(kitRemaining2 > 0);

            // "buy" bucket request succeeds independently!
            bool buyAcquired = limiter.TryAcquire(steamId, "buy", 5000, out double buyRemaining);
            Assert.True(buyAcquired);

            // Second "buy" request is rate limited
            bool buySecond = limiter.TryAcquire(steamId, "buy", 5000, out _);
            Assert.False(buySecond);
        }

        [Fact]
        public void Configuration_RateLimitKitMs_NormalizesCorrectly()
        {
            var cfg = new KizShopConfiguration();
            cfg.LoadDefaults();
            Assert.Equal(1000, cfg.RateLimitKitMs);

            cfg.RateLimitKitMs = -50;
            cfg.Normalize();
            Assert.Equal(0, cfg.RateLimitKitMs);

            cfg.RateLimitKitMs = 99999;
            cfg.Normalize();
            Assert.Equal(30000, cfg.RateLimitKitMs);
        }

        // =================================================================
        // 6. BATCHED LITEDB PURGE RETURNS RIGHT COUNT
        // =================================================================

        [Fact]
        public async Task LiteDbStorage_BatchedPurge_PurgesAcrossBatchesAndReturnsExactCount()
        {
            string dbPath = Path.Combine(_tempDir, "batched_purge.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            var cutoff = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

            // Insert 1050 old transactions (spread across 2 batches of 1000)
            int oldTarget = 1050;
            for (int i = 0; i < oldTarget; i++)
            {
                _clock.UtcNow = cutoff.AddDays(-5).AddSeconds(i);
                await storage.DepositAsync(76561198000000010UL, Money.FromDecimal(1m), "old", Money.Zero);
            }

            // Insert 25 new transactions
            int newTarget = 25;
            for (int i = 0; i < newTarget; i++)
            {
                _clock.UtcNow = cutoff.AddDays(1).AddSeconds(i);
                await storage.DepositAsync(76561198000000010UL, Money.FromDecimal(1m), "new", Money.Zero);
            }

            int totalBefore = await storage.GetTransactionCountAsync();
            Assert.Equal(oldTarget + newTarget, totalBefore);

            // Execute batched purge
            int deleted = await storage.PurgeTransactionsOlderThanAsync(cutoff);
            Assert.Equal(oldTarget, deleted);

            int totalAfter = await storage.GetTransactionCountAsync();
            Assert.Equal(newTarget, totalAfter);
        }

        // =================================================================
        // 7. VERSION STRING VERIFICATION
        // =================================================================

        [Fact]
        public void VersionString_Is0_6_1()
        {
            Assert.Equal("0.6.1", KizShopPlugin.VersionString);
            string buildInfo = KizShopPlugin.GetBuildIdentification(42);
            Assert.Contains("v0.6.1", buildInfo);
            Assert.Contains("main thread id 42", buildInfo);
        }
    }
}
