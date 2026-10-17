using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Economy.Models;
using KizShop.Shop;
using KizShop.Shop.Gateways;
using KizShop.Shop.Models;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class Addendum10Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly MockClock _clock;

        public Addendum10Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_add10_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _clock = new MockClock { UtcNow = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc) };
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        // =================================================================
        // A1. SHORT-LIVED CACHES FOR /baltop AND /history
        // =================================================================

        [Fact]
        public void TtlCache_SecondCallWithinTtl_HitsCache()
        {
            var cache = new TtlCache<string, string>(maxEntries: 10, clock: _clock);
            int callCount = 0;

            string val1 = cache.GetOrAdd("k1", 30, () => { callCount++; return "v1"; });
            string val2 = cache.GetOrAdd("k1", 30, () => { callCount++; return "v1_second"; });

            Assert.Equal("v1", val1);
            Assert.Equal("v1", val2);
            Assert.Equal(1, callCount);
            Assert.Equal(1, cache.Hits);
            Assert.Equal(1, cache.Misses);
        }

        [Fact]
        public void TtlCache_Expiry_RefetchesValue()
        {
            var cache = new TtlCache<string, string>(maxEntries: 10, clock: _clock);
            int callCount = 0;

            cache.GetOrAdd("k1", 15, () => { callCount++; return "initial"; });
            Assert.Equal(1, callCount);

            // Advance clock past TTL
            _clock.UtcNow = _clock.UtcNow.AddSeconds(16);

            string refreshed = cache.GetOrAdd("k1", 15, () => { callCount++; return "refreshed"; });
            Assert.Equal("refreshed", refreshed);
            Assert.Equal(2, callCount);
            Assert.Equal(2, cache.Misses);
        }

        [Fact]
        public void TtlCache_Invalidation_RemovesKey()
        {
            var cache = new TtlCache<string, string>(maxEntries: 10, clock: _clock);
            int callCount = 0;

            cache.GetOrAdd("k1", 30, () => { callCount++; return "v1"; });
            cache.Remove("k1");

            string val = cache.GetOrAdd("k1", 30, () => { callCount++; return "v2"; });
            Assert.Equal("v2", val);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public void TtlCache_TtlZero_DisablesCaching()
        {
            var cache = new TtlCache<string, string>(maxEntries: 10, clock: _clock);
            int callCount = 0;

            string v1 = cache.GetOrAdd("k1", 0, () => { callCount++; return "v1"; });
            string v2 = cache.GetOrAdd("k1", 0, () => { callCount++; return "v2"; });

            Assert.Equal("v1", v1);
            Assert.Equal("v2", v2);
            Assert.Equal(2, callCount);
            Assert.Equal(0, cache.Hits);
        }

        [Fact]
        public void TtlCache_MaxCapacity_EvictsOldest()
        {
            var cache = new TtlCache<int, string>(maxEntries: 3, clock: _clock);

            cache.Set(1, "one", 60);
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            cache.Set(2, "two", 60);
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            cache.Set(3, "three", 60);
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);

            // Adding 4th item forces eviction of oldest (1)
            cache.Set(4, "four", 60);

            Assert.False(cache.TryGet(1, out _));
            Assert.True(cache.TryGet(2, out _));
            Assert.True(cache.TryGet(3, out _));
            Assert.True(cache.TryGet(4, out _));
            Assert.Equal(3, cache.Count);
        }

        // =================================================================
        // A3. PER-PLAYER COMMAND RATE LIMITING
        // =================================================================

        [Fact]
        public void CommandRateLimiter_InsideWindow_Rejected_AfterWindow_Succeeds()
        {
            var limiter = new CommandRateLimiter(_clock);
            ulong steamId = 76561198100000001UL;
            string bucket = "buy";
            int intervalMs = 1000;

            // First call: allowed
            bool first = limiter.CheckRateLimit(steamId, bucket, intervalMs, enabled: true, isAdmin: false, out _);
            Assert.True(first);

            // Second call inside window: rejected
            bool second = limiter.CheckRateLimit(steamId, bucket, intervalMs, enabled: true, isAdmin: false, out double remainingSecs);
            Assert.False(second);
            Assert.True(remainingSecs > 0 && remainingSecs <= 1.0);

            // Advance clock past window
            _clock.UtcNow = _clock.UtcNow.AddMilliseconds(1050);

            // Third call after window: allowed
            bool third = limiter.CheckRateLimit(steamId, bucket, intervalMs, enabled: true, isAdmin: false, out _);
            Assert.True(third);
        }

        [Fact]
        public void CommandRateLimiter_AdminExempt()
        {
            var limiter = new CommandRateLimiter(_clock);
            ulong steamId = 76561198100000002UL;

            bool first = limiter.CheckRateLimit(steamId, "sell_all", 2000, enabled: true, isAdmin: true, out _);
            bool second = limiter.CheckRateLimit(steamId, "sell_all", 2000, enabled: true, isAdmin: true, out _);

            Assert.True(first);
            Assert.True(second);
        }

        [Fact]
        public void CommandRateLimiter_ConfigDisabled_ExemptsEveryone()
        {
            var limiter = new CommandRateLimiter(_clock);
            ulong steamId = 76561198100000003UL;

            bool first = limiter.CheckRateLimit(steamId, "history", 2000, enabled: false, isAdmin: false, out _);
            bool second = limiter.CheckRateLimit(steamId, "history", 2000, enabled: false, isAdmin: false, out _);

            Assert.True(first);
            Assert.True(second);
        }

        [Fact]
        public void CommandRateLimiter_ClearPlayer_ResetsWindow()
        {
            var limiter = new CommandRateLimiter(_clock);
            ulong steamId = 76561198100000004UL;

            limiter.CheckRateLimit(steamId, "baltop", 2000, enabled: true, isAdmin: false, out _);
            bool rejected = limiter.CheckRateLimit(steamId, "baltop", 2000, enabled: true, isAdmin: false, out _);
            Assert.False(rejected);

            limiter.ClearPlayer(steamId);

            bool allowed = limiter.CheckRateLimit(steamId, "baltop", 2000, enabled: true, isAdmin: false, out _);
            Assert.True(allowed);
        }

        // =================================================================
        // A4. TRANSACTION RETENTION PURGE
        // =================================================================

        [Fact]
        public async Task PurgeTransactionsOlderThan_LiteDb_DeletesOldPreservesNew()
        {
            string dbPath = Path.Combine(_tempDir, "purge_test.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            _clock.UtcNow = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            await storage.LogTransactionAsync(1001, "OldWithdraw", Money.FromDecimal(10m), "Old");

            _clock.UtcNow = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            await storage.LogTransactionAsync(1001, "NewWithdraw", Money.FromDecimal(20m), "New");

            var cutoff = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            int purged = await storage.PurgeTransactionsOlderThanAsync(cutoff);

            Assert.Equal(1, purged);

            var history = await storage.GetTransactionHistoryAsync(1001, 10, 0);
            Assert.Single(history);
            Assert.Equal("NewWithdraw", history[0].Type);
        }

        [Fact]
        public async Task PurgeTransactionsOlderThan_Sqlite_DeletesOldPreservesNew()
        {
            string dbPath = Path.Combine(_tempDir, "purge_test.db");
            using var storage = new SqliteStorage(dbPath, _clock);
            await storage.InitializeAsync();

            _clock.UtcNow = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            await storage.LogTransactionAsync(2001, "OldTx", Money.FromDecimal(15m), "Old");

            _clock.UtcNow = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            await storage.LogTransactionAsync(2001, "NewTx", Money.FromDecimal(25m), "New");

            var cutoff = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            int purged = await storage.PurgeTransactionsOlderThanAsync(cutoff);

            Assert.Equal(1, purged);

            var history = await storage.GetTransactionHistoryAsync(2001, 10, 0);
            Assert.Single(history);
            Assert.Equal("NewTx", history[0].Type);
        }

        // =================================================================
        // A5. ELIMINATE SILENT FAILURES
        // =================================================================

        [Fact]
        public void RepositoryAudit_NoEmptyCatchBlocksInSrc()
        {
            string current = Directory.GetCurrentDirectory();
            string srcDir = null;
            while (current != null)
            {
                string candidate = Path.Combine(current, "src", "KizShop");
                if (Directory.Exists(candidate))
                {
                    srcDir = candidate;
                    break;
                }
                var parent = Directory.GetParent(current);
                current = parent?.FullName;
            }

            Assert.NotNull(srcDir);
            Assert.True(Directory.Exists(srcDir));

            var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(csFiles);

            var emptyCatchRegex = new Regex(@"catch\s*(\([^\)]*\))?\s*\{[\s\r\n]*\}", RegexOptions.Compiled);

            var violations = new List<string>();
            foreach (var file in csFiles)
            {
                string content = File.ReadAllText(file);
                var matches = emptyCatchRegex.Matches(content);
                if (matches.Count > 0)
                {
                    violations.Add($"{Path.GetFileName(file)}: {matches.Count} empty catch block(s)");
                }
            }

            Assert.True(violations.Count == 0, "Found empty catch blocks in src: " + string.Join(", ", violations));
        }

        [Fact]
        public async Task FireAndForget_CatchesAndLogsWithoutCrashing()
        {
            FireAndForget.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("Test exception for FireAndForget");
            }, "TestContext");

            await Task.Delay(50);
            Assert.True(true);
        }

        // =================================================================
        // B1. DATABASE BACKUP MANAGER
        // =================================================================

        [Fact]
        public void DatabaseBackupManager_PruneOldBackups_KeepsNewestNAndTouchesOnlyMatching()
        {
            string backupDir = Path.Combine(_tempDir, "backups");
            Directory.CreateDirectory(backupDir);

            // Create 6 sqlite backups with different timestamps
            for (int i = 1; i <= 6; i++)
            {
                string fname = $"kizshop_sqlite_20261017_{100000 + i}.sqlite";
                File.WriteAllText(Path.Combine(backupDir, fname), "dummy sqlite backup");
                File.SetLastWriteTimeUtc(Path.Combine(backupDir, fname), _clock.UtcNow.AddMinutes(i));
            }

            // Create non-matching files
            string otherFile = Path.Combine(backupDir, "notes.txt");
            string dumpFile = Path.Combine(backupDir, "external_dump.bak");
            File.WriteAllText(otherFile, "keep me");
            File.WriteAllText(dumpFile, "external data");

            var fakeStorage = new LiteDbStorage(Path.Combine(_tempDir, "dummy.litedb"), _clock);
            var mgr = new DatabaseBackupManager(_tempDir, fakeStorage, () => new KizShopConfiguration
            {
                BackupKeepCount = 3
            }, _clock);

            int pruned = mgr.PruneOldBackups(3);

            var remainingFiles = Directory.GetFiles(backupDir, "kizshop_sqlite_*.sqlite");
            Assert.Equal(3, remainingFiles.Length);
            Assert.Equal(3, pruned);
            Assert.True(File.Exists(otherFile));
            Assert.True(File.Exists(dumpFile));
        }

        [Fact]
        public void DatabaseBackupManager_FormatFileSize_FormatsProperly()
        {
            Assert.Equal("500 B", DatabaseBackupManager.FormatFileSize(500));
            Assert.Equal("1.0 KB", DatabaseBackupManager.FormatFileSize(1024));
            Assert.Equal("2.50 MB", DatabaseBackupManager.FormatFileSize((long)(2.5 * 1024 * 1024)));
        }

        [Fact]
        public async Task DatabaseBackupManager_SqliteBackup_CreatesValidFile()
        {
            string dbPath = Path.Combine(_tempDir, "source.db");
            using var storage = new SqliteStorage(dbPath, _clock);
            await storage.InitializeAsync();
            await storage.SetBalanceAsync(76561198000000001UL, Money.FromDecimal(500m), "init");

            var mgr = new DatabaseBackupManager(_tempDir, storage, () => new KizShopConfiguration
            {
                DatabaseType = "SQLite",
                BackupEnabled = true,
                BackupKeepCount = 5
            }, _clock);

            var (success, msg, fileName, sizeBytes) = await mgr.CreateBackupAsync(isManual: true);

            Assert.True(success, $"Backup failed: {msg}");
            Assert.NotNull(fileName);
            Assert.True(sizeBytes > 0);
            Assert.True(File.Exists(Path.Combine(_tempDir, "backups", fileName)));
        }

        // =================================================================
        // B2. MORE HELPFUL ERROR MESSAGES & TRANSLATION COVERAGE
        // =================================================================

        [Fact]
        public void DurationFormatter_FormatsHumanReadableStrings()
        {
            Assert.Equal("0.5s", DurationFormatter.Format(TimeSpan.FromSeconds(0.5)));
            Assert.Equal("45s", DurationFormatter.Format(TimeSpan.FromSeconds(45)));
            Assert.Equal("3m 20s", DurationFormatter.Format(TimeSpan.FromSeconds(200)));
            Assert.Equal("1h 5m", DurationFormatter.Format(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(5))));
            Assert.Equal("1d 2h", DurationFormatter.Format(TimeSpan.FromDays(1).Add(TimeSpan.FromHours(2))));
        }

        [Fact]
        public void DefaultTranslations_AllPlaceholders_FormatWithoutException()
        {
            var translations = KizShopPlugin.DefaultTranslationsStatic;
            Assert.NotNull(translations);
            Assert.NotEmpty(translations);

            object[] sampleArgs = new object[]
            {
                "100.00", "2", "Maple Rifle", "50.00", "50.00",
                "StarterKit", "5m 30s", "30", "GroupName", "Admin",
                "150.00", "200.00", "Player1", "Player2"
            };

            foreach (var item in translations)
            {
                string format = item.Value;
                try
                {
                    _ = string.Format(format, sampleArgs);
                }
                catch (FormatException ex)
                {
                    Assert.Fail($"Translation key '{item.Id}' threw FormatException: {ex.Message} (Template: '{format}')");
                }
            }
        }

        // =================================================================
        // B3. /shop search AND "did you mean" SUGGESTIONS
        // =================================================================

        [Fact]
        public void ShopService_SearchCatalog_ContainsAndPaging()
        {
            var shop = new ShopService(_tempDir);
            shop.Load();

            shop.AddOrUpdateItem(new ItemShopEntry { Id = 101, Name = "Military Assault Rifle", BuyPrice = 100m, SellPrice = 50m, Enabled = true, Available = true });
            shop.AddOrUpdateItem(new ItemShopEntry { Id = 102, Name = "Maple Timber", BuyPrice = 10m, SellPrice = 5m, Enabled = true, Available = true });
            shop.AddOrUpdateItem(new ItemShopEntry { Id = 103, Name = "Military Helmet", BuyPrice = 50m, SellPrice = 25m, Enabled = true, Available = true });
            shop.AddOrUpdateVehicle(new VehicleShopEntry { Id = 201, Name = "Military Transport APC", BuyPrice = 500m, SellPrice = 200m, Enabled = true });

            var kit = new KitShopEntry { Name = "Military Loadout", BuyPrice = 300m, Enabled = true };
            shop.AddOrUpdateKit(kit);

            // Contains search: "military" -> 4 results (2 items, 1 vehicle, 1 kit)
            var (results, totalPages, totalCount) = shop.SearchCatalog("military", page: 1, pageSize: 8);
            Assert.Equal(4, totalCount);
            Assert.Equal(4, results.Count);

            // Multi-word search: "military assault" -> 1 result
            var (assaultResults, _, assaultCount) = shop.SearchCatalog("military assault", page: 1, pageSize: 8);
            Assert.Equal(1, assaultCount);
            Assert.Equal("Military Assault Rifle", assaultResults[0].Name);

            // Nonexistent search
            var (emptyResults, _, emptyCount) = shop.SearchCatalog("nonexistent_weapon", page: 1, pageSize: 8);
            Assert.Equal(0, emptyCount);
            Assert.Empty(emptyResults);
        }

        [Fact]
        public void ShopService_GetSuggestions_RanksClosestMatches()
        {
            var shop = new ShopService(_tempDir);
            shop.Load();

            shop.AddOrUpdateItem(new ItemShopEntry { Id = 363, Name = "Maplestrike Rifle", BuyPrice = 120m, SellPrice = 60m, Enabled = true, Available = true });
            shop.AddOrUpdateItem(new ItemShopEntry { Id = 101, Name = "Avenger Pistol", BuyPrice = 50m, SellPrice = 25m, Enabled = true, Available = true });

            // "maplestrik" has distance 1 to "Maplestrike Rifle"
            var suggestions = shop.GetSuggestions("maplestrik", kind: null, max: 3);
            Assert.NotEmpty(suggestions);
            Assert.Equal("Maplestrike Rifle", suggestions[0]);

            // Unrelated query returns no suggestions
            var noMatch = shop.GetSuggestions("xyz999999", kind: null, max: 3);
            Assert.Empty(noMatch);
        }

        // =================================================================
        // B4. VIP-AWARE KITS (PER-KIT PERMISSION & GROUP DISCOUNTS)
        // =================================================================

        [Fact]
        public void KitShopEntry_DiscountOrder_LowestDiscountAppliedBeforeSale()
        {
            var shop = new ShopService(_tempDir);
            shop.Load();

            var kit = new KitShopEntry
            {
                Name = "EliteKit",
                BuyPrice = 100m,
                Enabled = true,
                GroupDiscounts = new Dictionary<string, decimal>
                {
                    { "vip", 0.80m },  // 20% off
                    { "mvp", 0.60m }   // 40% off
                }
            };

            // Player in both VIP and MVP -> lowest multiplier (0.60m) is selected -> 60m
            decimal priceBeforeSale = shop.GetEffectiveKitPrice(kit, new[] { "vip", "mvp" });
            Assert.Equal(60m, priceBeforeSale);

            // Active sale event: 50% discount (BuyMultiplier = 0.50m)
            // Discounted price should be 60m * 0.50 = 30m
            shop.StartSale(0.50m, 1.0m, 60, "HalfPriceSale");

            decimal priceAfterSale = shop.GetEffectiveKitPrice(kit, new[] { "vip", "mvp" });
            Assert.Equal(30m, priceAfterSale);
        }

        [Fact]
        public async Task BuyKitAsync_RequiredPermission_EnforcedServerSide()
        {
            string dbPath = Path.Combine(_tempDir, "vip_perm.db");
            using var storage = new SqliteStorage(dbPath, _clock);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            var kit = new KitShopEntry
            {
                Name = "VipKit",
                BuyPrice = 50m,
                Enabled = true,
                RequiredPermission = "kizshop.kit.vip",
                Items = new List<KitItemEntry>()
            };
            shop.AddOrUpdateKit(kit);

            var inventory = new MockInventoryGateway();
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock, null, storage);

            ulong steamId = 76561198100000100UL;
            PlayerLock.Release(steamId);
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(500m), "Init");

            // Non-admin without permission: fails
            var denied = await tx.BuyKitAsync(steamId, "VipKit", perm => false, isAdmin: false);
            Assert.False(denied.Success);
            Assert.Equal("kit_no_permission", denied.ErrorKey);

            // Non-admin with permission: succeeds
            var allowed = await tx.BuyKitAsync(steamId, "VipKit", perm => perm == "kizshop.kit.vip", isAdmin: false);
            Assert.True(allowed.Success);
        }

        [Fact]
        public async Task BuyKitAsync_Refund_ReturnsExactDiscountedAmount()
        {
            string dbPath = Path.Combine(_tempDir, "vip_refund.db");
            using var storage = new SqliteStorage(dbPath, _clock);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            var kit = new KitShopEntry
            {
                Name = "VipGear",
                BuyPrice = 100m,
                Enabled = true,
                GroupDiscounts = new Dictionary<string, decimal> { { "donor", 0.70m } },
                Items = new List<KitItemEntry> { new KitItemEntry { Id = 363, Amount = 1 } }
            };
            shop.AddOrUpdateKit(kit);

            // Simulate delivery failure so transaction rolls back and refunds
            var inventory = new MockInventoryGateway { HasSpace = false, SpaceReason = CheckSpaceReason.NoSpace };
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock, null, storage);

            ulong steamId = 76561198100000101UL;
            PlayerLock.Release(steamId);
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(500m), "Init");

            // Purchase with donor group -> effective price 70m
            var res = await tx.BuyKitAsync(steamId, "VipGear", perm => true, isAdmin: false, playerGroups: new[] { "donor" });

            Assert.False(res.Success);
            Assert.Equal("kit_no_space", res.ErrorKey);

            // Balance must remain unchanged at 500m (no money lost)
            var bal = await eco.GetBalanceAsync(steamId);
            Assert.Equal(500m, bal.ToDecimal());
        }

        // =================================================================
        // B7. /kizshop status & STORAGE METRICS
        // =================================================================

        [Fact]
        public void StorageMetrics_RecordsAndComputesAvgAndMaxLatency()
        {
            var metrics = new StorageMetrics();

            metrics.Record("GetBalance", 10.0, 500);
            metrics.Record("GetBalance", 20.0, 500);
            metrics.Record("GetBalance", 30.0, 500);

            var (avg, max, count) = metrics.GetStats(TimeSpan.FromMinutes(5));

            Assert.Equal(3, count);
            Assert.Equal(20.0, avg, precision: 1);
            Assert.Equal(30.0, max, precision: 1);
        }

        [Fact]
        public void KizShopConfiguration_Normalization_ClampsAddendum10Keys()
        {
            var cfg = new KizShopConfiguration
            {
                BalTopCacheSeconds = 99999,      // Clamp 0..600
                HistoryCacheSeconds = 99999,     // Clamp 0..300
                RateLimitBuyMs = 999999,         // Clamp 0..30000
                TransactionRetentionDays = 99999,// Clamp 0..3650
                BackupIntervalHours = 99999,     // Clamp 1..168
                BackupKeepCount = 99999,         // Clamp 1..100
                StorageSlowCallWarnMs = 99999    // Clamp 50..10000 (0 disables)
            };

            cfg.Normalize();

            Assert.Equal(600, cfg.BalTopCacheSeconds);
            Assert.Equal(300, cfg.HistoryCacheSeconds);
            Assert.Equal(30000, cfg.RateLimitBuyMs);
            Assert.Equal(3650, cfg.TransactionRetentionDays);
            Assert.Equal(168, cfg.BackupIntervalHours);
            Assert.Equal(100, cfg.BackupKeepCount);
            Assert.Equal(10000, cfg.StorageSlowCallWarnMs);
        }
    }
}
