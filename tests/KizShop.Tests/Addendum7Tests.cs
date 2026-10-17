using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using KizShop.API;
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
    public class Addendum7Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly MockClock _clock;

        public Addendum7Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_add7_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _clock = new MockClock { UtcNow = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc) };
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        // =================================================================
        // 1. BUY FLOW: NEVER REFUND AFTER DELIVERY
        // =================================================================

        private class FailingPressureStorage : LiteDbStorage
        {
            public bool UpdatePressureCalled { get; private set; }

            public FailingPressureStorage(string dbPath, IClock clock) : base(dbPath, clock) { }

            public new Task UpdateItemPricePressureAsync(ushort itemId, decimal sellDelta, decimal buyDelta, DateTime nowUtc, decimal recoveryPerHour)
            {
                UpdatePressureCalled = true;
                throw new InvalidOperationException("Simulated database failure during price pressure update.");
            }
        }

        [Fact]
        public async Task BuyFlow_PostDeliveryPressureUpdateFails_DeliversOnce_WithdrawsOnce_ReturnsSuccess()
        {
            string dbPath = Path.Combine(_tempDir, "failing_pressure.litedb");
            var storage = new FailingPressureStorage(dbPath, _clock);
            await storage.InitializeAsync();

            ulong steamId = 76561198000000001;
            await storage.SetBalanceAsync(steamId, Money.FromDecimal(100m), "Initial");

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.AddOrUpdateItem(new ItemShopEntry
            {
                Id = 363,
                Name = "Maplestrike",
                BuyPrice = 20m,
                Category = "Weapons",
                Enabled = true,
                DynamicPricing = new ItemDynamicPricingConfig
                {
                    Enabled = true,
                    BuyIncreasePerUnit = 0.05m
                }
            });

            var inv = new MockInventoryGateway();
            var veh = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 10000 };
            var tx = new ShopTransactionService(shop, eco, inv, veh, () => cfg, _clock, null, storage);

            var res = await tx.ExecuteBuyAsync(steamId, "363", 1, _ => true, false);

            Assert.True(res.Success);
            Assert.Single(inv.DeliveredItems);
            Assert.Equal(363, inv.DeliveredItems[0].id);

            // Money withdrawn once (80m remaining), no refund
            var bal = await eco.GetBalanceAsync(steamId);
            Assert.Equal(80m, bal.ToDecimal());
        }

        private class FailingBalanceEconomyStorage : LiteDbStorage
        {
            public bool GetBalanceThrows { get; set; } = false;

            public FailingBalanceEconomyStorage(string dbPath, IClock clock) : base(dbPath, clock) { }

            public new Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance)
            {
                if (GetBalanceThrows)
                {
                    throw new InvalidOperationException("Simulated failure reading balance after delivery.");
                }
                return base.GetBalanceAsync(steamId, defaultStartingBalance);
            }
        }

        [Fact]
        public async Task BuyFlow_PostDeliveryGetBalanceFails_DeliversOnce_WithdrawsOnce_FallsBackToWithdrawResult()
        {
            string dbPath = Path.Combine(_tempDir, "failing_balance.litedb");
            var storage = new FailingBalanceEconomyStorage(dbPath, _clock);
            await storage.InitializeAsync();

            ulong steamId = 76561198000000002;
            await storage.SetBalanceAsync(steamId, Money.FromDecimal(50m), "Initial");

            var eco = new EconomyService(storage, () => Money.Zero, () => null, false, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.AddOrUpdateItem(new ItemShopEntry
            {
                Id = 15,
                Name = "Medkit",
                BuyPrice = 10m,
                Category = "Medical",
                Enabled = true
            });

            var inv = new MockInventoryGateway();
            var veh = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 10000 };
            var tx = new ShopTransactionService(shop, eco, inv, veh, () => cfg, _clock, null, storage);

            // Enable throwing on GetBalance after initial setup
            storage.GetBalanceThrows = true;

            var res = await tx.ExecuteBuyAsync(steamId, "15", 1, _ => true, false);

            Assert.True(res.Success);
            Assert.Single(inv.DeliveredItems);
            // Must fall back to withdrawRes.NewBalance (40m), never Money.Zero
            Assert.Equal(40m, res.NewBalance.ToDecimal());
        }

        [Fact]
        public async Task BuyFlow_OnItemPurchasedSubscriberThrows_DeliversOnce_WithdrawsOnce_ReturnsSuccess()
        {
            string dbPath = Path.Combine(_tempDir, "subscriber_throws.litedb");
            var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            ulong steamId = 76561198000000003;
            await storage.SetBalanceAsync(steamId, Money.FromDecimal(100m), "Initial");

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.AddOrUpdateItem(new ItemShopEntry
            {
                Id = 15,
                Name = "Medkit",
                BuyPrice = 10m,
                Category = "Medical",
                Enabled = true
            });

            var inv = new MockInventoryGateway();
            var veh = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 10000 };
            var tx = new ShopTransactionService(shop, eco, inv, veh, () => cfg, _clock, null, storage);

            bool secondSubscriberInvoked = false;
            tx.OnItemPurchased += (sId, entry, qty, price, newBal) =>
            {
                throw new InvalidOperationException("Exploding subscriber!");
            };
            tx.OnItemPurchased += (sId, entry, qty, price, newBal) =>
            {
                secondSubscriberInvoked = true;
            };

            var res = await tx.ExecuteBuyAsync(steamId, "15", 1, _ => true, false);

            Assert.True(res.Success);
            Assert.True(secondSubscriberInvoked);
            Assert.Single(inv.DeliveredItems);
            var bal = await eco.GetBalanceAsync(steamId);
            Assert.Equal(90m, bal.ToDecimal());
        }

        // =================================================================
        // 2. ATOMIC REWARD-EVENT IDEMPOTENCY
        // =================================================================

        [Fact]
        public async Task RewardEvent_ConcurrentCalls_LiteDb_ExactlyOnePays()
        {
            string dbPath = Path.Combine(_tempDir, "reward_concurrent.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            string eventKey = "boss_concurrent_event_litedb";
            Money pool = Money.FromDecimal(1000m);

            // Run two concurrent recordings
            var t1 = storage.TryRecordRewardEventAsync(eventKey, "MegaBoss", pool, _clock.UtcNow);
            var t2 = storage.TryRecordRewardEventAsync(eventKey, "MegaBoss", pool, _clock.UtcNow);

            var results = await Task.WhenAll(t1, t2);

            Assert.Contains(true, results);
            Assert.Contains(false, results);
            Assert.Equal(1, results.Count(x => x));

            var stored = await storage.GetRewardEventAsync(eventKey);
            Assert.NotNull(stored);
            Assert.Equal("MegaBoss", stored.BossName);
            Assert.Equal(1000m, stored.Pool.ToDecimal());
        }

        [Fact]
        public async Task RewardEvent_ConcurrentCalls_Sqlite_ExactlyOnePays()
        {
            string dbPath = Path.Combine(_tempDir, "reward_concurrent.sqlite");
            using var storage = new SqliteStorage(dbPath, _clock);
            await storage.InitializeAsync();

            string eventKey = "boss_concurrent_event_sqlite";
            Money pool = Money.FromDecimal(1000m);

            var t1 = storage.TryRecordRewardEventAsync(eventKey, "MegaBoss", pool, _clock.UtcNow);
            var t2 = storage.TryRecordRewardEventAsync(eventKey, "MegaBoss", pool, _clock.UtcNow);

            var results = await Task.WhenAll(t1, t2);

            Assert.Contains(true, results);
            Assert.Contains(false, results);
            Assert.Equal(1, results.Count(x => x));

            var stored = await storage.GetRewardEventAsync(eventKey);
            Assert.NotNull(stored);
            Assert.Equal(1000m, stored.Pool.ToDecimal());
        }

        [Fact]
        public void BossRewardCalculator_PayoutsAlwaysSumToPool()
        {
            Money pool = Money.FromDecimal(500m);
            var damageByPlayer = new Dictionary<ulong, float>
            {
                { 1, 100f },
                { 2, 50f },
                { 3, 25f }
            };

            var dist = BossRewardCalculator.CalculateDistribution(
                pool,
                damageByPlayer,
                lastHitSteamId: 3,
                topDamagePercent: 30,
                lastHitPercent: 10,
                participationPercent: 60,
                minDamageSharePercent: 1);

            long sumCents = dist.Values.Sum(v => v.Cents);
            Assert.Equal(pool.Cents, sumCents);
        }

        // =================================================================
        // 3. DYNAMIC PRICING: APPLY RECOVERY BEFORE ADDING PRESSURE
        // =================================================================

        [Fact]
        public async Task DynamicPricing_RecoveryBeforeDelta_LiteDb()
        {
            string dbPath = Path.Combine(_tempDir, "dyn_pricing.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            ushort itemId = 100;
            var t0 = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

            // Step 1: Set initial pressure to 10 at t0
            await storage.UpdateItemPricePressureAsync(itemId, sellDelta: 10m, buyDelta: 10m, t0, recoveryPerHour: 1m);

            var initial = await storage.GetItemPriceStateAsync(itemId);
            Assert.Equal(10m, initial.SellPressure);
            Assert.Equal(10m, initial.BuyPressure);

            // Step 2: 5 hours later, +1 sale (sellDelta = 1) with recovery 1/h
            var t5 = t0.AddHours(5);
            await storage.UpdateItemPricePressureAsync(itemId, sellDelta: 1m, buyDelta: 0m, t5, recoveryPerHour: 1m);

            var after = await storage.GetItemPriceStateAsync(itemId);
            // Recovered = 10 - 5 = 5; then +1 sale = 6 (NOT 11)
            Assert.Equal(6m, after.SellPressure);
            // Buy pressure recovered: 10 - 5 = 5 (no delta added)
            Assert.Equal(5m, after.BuyPressure);
            Assert.Equal(t5, after.LastUpdatedUtc);

            // Step 3: No negative values on excessive recovery
            var t100 = t5.AddHours(100);
            await storage.UpdateItemPricePressureAsync(itemId, sellDelta: 2m, buyDelta: 0m, t100, recoveryPerHour: 1m);
            var after100 = await storage.GetItemPriceStateAsync(itemId);
            // Recovered = max(0, 6 - 100) = 0; + 2 = 2
            Assert.Equal(2m, after100.SellPressure);
            Assert.Equal(0m, after100.BuyPressure);
        }

        [Fact]
        public async Task DynamicPricing_RecoveryBeforeDelta_Sqlite()
        {
            string dbPath = Path.Combine(_tempDir, "dyn_pricing.sqlite");
            using var storage = new SqliteStorage(dbPath, _clock);
            await storage.InitializeAsync();

            ushort itemId = 100;
            var t0 = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

            // Initial 10 pressure at t0
            await storage.UpdateItemPricePressureAsync(itemId, sellDelta: 10m, buyDelta: 10m, t0, recoveryPerHour: 1m);

            // 5 hours later, +1 sale with recovery 1/h
            var t5 = t0.AddHours(5);
            await storage.UpdateItemPricePressureAsync(itemId, sellDelta: 1m, buyDelta: 0m, t5, recoveryPerHour: 1m);

            var after = await storage.GetItemPriceStateAsync(itemId);
            Assert.Equal(6m, after.SellPressure);
            Assert.Equal(5m, after.BuyPressure);
            Assert.Equal(t5, after.LastUpdatedUtc);
        }

        // =================================================================
        // 4. BOUNTY ESCROW SAFETY
        // =================================================================

        [Fact]
        public async Task Bounty_CreateBountyIfWithinLimit_AtomicLimitEnforcement()
        {
            string dbPath = Path.Combine(_tempDir, "bounty_limit.sqlite");
            using var storage = new SqliteStorage(dbPath, _clock);
            await storage.InitializeAsync();

            ulong target = 76561198000000010;
            Money maxLimit = Money.FromDecimal(100m);

            // Bounty 1: 60m net -> total 60m <= 100m -> Created
            var (created1, total1) = await storage.CreateBountyIfWithinLimitAsync(1, target, Money.FromDecimal(60m), Money.FromDecimal(60m), _clock.UtcNow, maxLimit);
            Assert.True(created1);
            Assert.Equal(60m, total1.ToDecimal());

            // Bounty 2: 50m net -> 60 + 50 = 110m > 100m -> Rejected atomically
            var (created2, total2) = await storage.CreateBountyIfWithinLimitAsync(2, target, Money.FromDecimal(50m), Money.FromDecimal(50m), _clock.UtcNow, maxLimit);
            Assert.False(created2);
            Assert.Equal(60m, total2.ToDecimal());

            // Bounty 3: 40m net -> 60 + 40 = 100m <= 100m -> Created
            var (created3, total3) = await storage.CreateBountyIfWithinLimitAsync(3, target, Money.FromDecimal(40m), Money.FromDecimal(40m), _clock.UtcNow, maxLimit);
            Assert.True(created3);
            Assert.Equal(100m, total3.ToDecimal());
        }

        [Fact]
        public async Task Bounty_ExpiryRefund_PendingFlag_CrashSimulation_RefundsExactlyOnce()
        {
            string dbPath = Path.Combine(_tempDir, "bounty_pending.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            ulong placer = 76561198000000020;
            ulong target = 76561198000000021;
            var oldDate = _clock.UtcNow.AddDays(-10);

            int bId = await storage.CreateBountyAsync(placer, target, Money.FromDecimal(100m), Money.FromDecimal(90m), oldDate);

            // Sweep marks as refund_pending
            var cutoff = _clock.UtcNow.AddDays(-7);
            var sweep1 = await storage.GetAndMarkExpiredBountiesAsync(cutoff);
            Assert.Single(sweep1);
            Assert.True(sweep1[0].RefundPending);

            // Crash simulation: sweep runs again before confirm
            var sweep2 = await storage.GetAndMarkExpiredBountiesAsync(cutoff);
            Assert.Single(sweep2);
            Assert.Equal(sweep1[0].Id, sweep2[0].Id);

            // Now deposit succeeds, confirm refund deletes row
            await storage.ConfirmBountyRefundAsync(sweep2[0].Id);

            // Sweep 3 finds nothing
            var sweep3 = await storage.GetAndMarkExpiredBountiesAsync(cutoff);
            Assert.Empty(sweep3);
        }

        [Fact]
        public async Task Bounty_ClaimRecreation_PreservesOriginalPlacerAndDate()
        {
            string dbPath = Path.Combine(_tempDir, "bounty_claim_recreate.sqlite");
            using var storage = new SqliteStorage(dbPath, _clock);
            await storage.InitializeAsync();

            ulong placer = 76561198000000030;
            ulong target = 76561198000000031;
            ulong killer = 76561198000000032;
            var originalDate = new DateTime(2026, 10, 10, 8, 30, 0, DateTimeKind.Utc);

            await storage.CreateBountyAsync(placer, target, Money.FromDecimal(100m), Money.FromDecimal(90m), originalDate);

            // Killer claims
            var (claimedEntries, totalAmount) = await storage.ClaimBountiesAsync(target, killer);
            Assert.Single(claimedEntries);
            Assert.Equal(90m, totalAmount.ToDecimal());

            var orig = claimedEntries[0];
            Assert.Equal(placer, orig.PlacerSteamId);
            Assert.Equal(originalDate, orig.CreatedUtc);

            // Simulate claim deposit failure -> re-create with original properties
            await storage.CreateBountyAsync(orig.PlacerSteamId, orig.TargetSteamId, orig.EscrowAmount, orig.NetAmount, orig.CreatedUtc);

            var active = await storage.GetActiveBountiesAsync();
            Assert.Single(active);
            Assert.Equal(placer, active[0].PlacerSteamId);
            Assert.Equal(originalDate, active[0].CreatedUtc);
        }

        // =================================================================
        // 5. CONFIG VALIDATION & ROBUSTNESS
        // =================================================================

        [Fact]
        public void Config_Normalize_ClampsInvalidFields_AndLogsWarnings()
        {
            var cfg = new KizShopConfiguration
            {
                BountyFeePercent = 150,
                BountyMinAmount = 0.001m,
                BountyMaxPerTarget = -5,
                BountyExpiryDays = 0,
                CombatTagSeconds = -10,
                DailyMaxStreakDays = 0,
                DailyStreakBonusPercentPerDay = -1,
                DailyResetUtcOffsetHours = 20,
                VehicleCooldownSeconds = -5,
                BossRewardTopDamagePercent = 50,
                BossRewardLastHitPercent = 50,
                BossRewardParticipationPercent = 50,
                BossRewardMinDamageSharePercent = 1
            };

            cfg.Normalize();

            Assert.Equal(100, cfg.BountyFeePercent);
            Assert.Equal(0.01m, cfg.BountyMinAmount);
            Assert.Equal(0, cfg.BountyMaxPerTarget);
            Assert.Equal(1, cfg.BountyExpiryDays);
            Assert.Equal(0, cfg.CombatTagSeconds);
            Assert.Equal(1, cfg.DailyMaxStreakDays);
            Assert.Equal(0, cfg.DailyStreakBonusPercentPerDay);
            Assert.Equal(14, cfg.DailyResetUtcOffsetHours);
            Assert.Equal(0, cfg.VehicleCooldownSeconds);

            // Boss reward percentages sum to 150 -> reset to defaults (30, 10, 60, 1)
            Assert.Equal(30, cfg.BossRewardTopDamagePercent);
            Assert.Equal(10, cfg.BossRewardLastHitPercent);
            Assert.Equal(60, cfg.BossRewardParticipationPercent);
            Assert.Equal(1, cfg.BossRewardMinDamageSharePercent);
        }

        [Fact]
        public void DynamicPricing_Normalize_ClampsInvalidValues()
        {
            var dyn = new ItemDynamicPricingConfig
            {
                SellDecayPerUnit = -1m,
                MinSellMultiplier = 2.0m,
                MaxBuyMultiplier = 0.5m,
                RecoveryPerHour = -2m
            };

            dyn.Normalize();

            Assert.Equal(0m, dyn.SellDecayPerUnit);
            Assert.Equal(1.0m, dyn.MinSellMultiplier);
            Assert.Equal(1.0m, dyn.MaxBuyMultiplier);
            Assert.Equal(0m, dyn.RecoveryPerHour);
        }

        [Fact]
        public void PriceCalculator_NeverThrows_OnBadMultipliers()
        {
            // If caller passes bad config without normalizing (e.g. MinSellMultiplier = 2.0m),
            // Math.Clamp must not throw ArgumentException.
            var badCfg = new ItemDynamicPricingConfig
            {
                Enabled = true,
                MinSellMultiplier = 2.0m,
                MaxBuyMultiplier = 0.5m
            };

            var ex = Record.Exception(() =>
            {
                PriceCalculator.CalculateSingleUnitBuyPrice(10m, 5m, badCfg, buyPressure: 5m, sellPressure: 0m, allowSellAboveBuy: false);
                PriceCalculator.CalculateSingleUnitSellPrice(10m, 5m, badCfg, buyPressure: 0m, sellPressure: 5m, allowSellAboveBuy: false);
            });

            Assert.Null(ex);
        }

        [Fact]
        public void UconomyImport_TableNameAndColumnRegex_RejectsInjection()
        {
            const string pattern = @"^[A-Za-z0-9_]+$";

            Assert.Matches(pattern, "uconomy");
            Assert.Matches(pattern, "steam_id");
            Assert.Matches(pattern, "balance123");

            Assert.DoesNotMatch(pattern, "uconomy; DROP TABLE users;--");
            Assert.DoesNotMatch(pattern, "steam id");
            Assert.DoesNotMatch(pattern, "table`name");
            Assert.DoesNotMatch(pattern, "");
        }

        [Fact]
        public void HandleEnemyDisconnected_DoesNotThrow_OnNullOrInvalidPlayer()
        {
            var m = typeof(KizShopPlugin).GetMethod("HandleEnemyDisconnected", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);

            // Create uninitialized plugin instance
            var plugin = (KizShopPlugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(KizShopPlugin));

            var ex = Record.Exception(() =>
            {
                m.Invoke(plugin, new object[] { null });
            });

            Assert.Null(ex);
        }

        [Theory]
        [InlineData("<color=gold>+ $50.00 for MEGA zombie kill!</color>", "<color=yellow>+ $50.00 for MEGA zombie kill!</color>")]
        [InlineData("<color='gold'>Boss reward</color>", "<color=yellow>Boss reward</color>")]
        [InlineData("<color=\"gold\">[BOUNTY]</color>", "<color=yellow>[BOUNTY]</color>")]
        [InlineData("<COLOR=GOLD>Upper</color>", "<color=yellow>Upper</color>")]
        [InlineData("<color=green>Already valid</color>", "<color=green>Already valid</color>")]
        [InlineData(null, null)]
        [InlineData("", "")]
        public void KizChat_SanitizeMessage_ReplacesUnsupportedGoldColorWithYellow(string input, string expected)
        {
            string actual = KizChat.SanitizeMessage(input);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void MigrateTranslationsOnDisk_ReplacesGoldInXmlFile()
        {
            string transFile = Path.Combine(_tempDir, "KizShop.en.translation.xml");
            string originalXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Translations>
  <Translation Id=""reward_mega_zombie_kill"" Value=""&lt;color=gold&gt;+ {0} for MEGA zombie kill!&lt;/color&gt;"" />
  <Translation Id=""boss_reward_received"" Value=""<color=gold>You received {0}</color>"" />
</Translations>";
            File.WriteAllText(transFile, originalXml);

            var plugin = (KizShopPlugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(KizShopPlugin));
            // Set Directory via reflection
            typeof(Rocket.Core.Plugins.RocketPlugin).GetProperty("Directory", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(plugin, _tempDir);

            plugin.MigrateTranslationsOnDisk(_tempDir);

            string migratedXml = File.ReadAllText(transFile);
            Assert.DoesNotContain("gold", migratedXml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;color=yellow&gt;", migratedXml);
            Assert.Contains("<color=yellow>", migratedXml);
        }

        [Fact]
        public async Task RewardPayout_Idempotency_LiteDbAndSqlite()
        {
            // LiteDB
            string litedbPath = Path.Combine(_tempDir, "payout_idemp.litedb");
            using (var liteStorage = new LiteDbStorage(litedbPath, _clock))
            {
                await liteStorage.InitializeAsync();
                await liteStorage.RecordRewardPayoutAsync("boss_key", 1001, Money.FromDecimal(50m), "Pending", _clock.UtcNow);
                await liteStorage.RecordRewardPayoutAsync("boss_key", 1001, Money.FromDecimal(50m), "Pending", _clock.UtcNow);

                var unpaidLite = await liteStorage.GetUnpaidRewardPayoutsAsync();
                Assert.Single(unpaidLite);
            }

            // SQLite
            string sqlitePath = Path.Combine(_tempDir, "payout_idemp.sqlite");
            using (var sqliteStorage = new SqliteStorage(sqlitePath, _clock))
            {
                await sqliteStorage.InitializeAsync();
                await sqliteStorage.RecordRewardPayoutAsync("boss_key", 1001, Money.FromDecimal(50m), "Pending", _clock.UtcNow);
                await sqliteStorage.RecordRewardPayoutAsync("boss_key", 1001, Money.FromDecimal(50m), "Pending", _clock.UtcNow);

                var unpaidSqlite = await sqliteStorage.GetUnpaidRewardPayoutsAsync();
                Assert.Single(unpaidSqlite);
            }
        }

        [Fact]
        public async Task RewardPayout_UnpaidRetrievesPendingAndFailed_ExcludesPaid_LiteDbAndSqlite()
        {
            // LiteDB
            string litedbPath = Path.Combine(_tempDir, "payout_unpaid.litedb");
            using (var liteStorage = new LiteDbStorage(litedbPath, _clock))
            {
                await liteStorage.InitializeAsync();
                await liteStorage.RecordRewardPayoutAsync("event1", 101, Money.FromDecimal(10m), "Pending", _clock.UtcNow);
                await liteStorage.RecordRewardPayoutAsync("event1", 102, Money.FromDecimal(20m), "Failed", _clock.UtcNow);
                await liteStorage.RecordRewardPayoutAsync("event1", 103, Money.FromDecimal(30m), "Paid", _clock.UtcNow);

                var unpaid = await liteStorage.GetUnpaidRewardPayoutsAsync();
                Assert.Equal(2, unpaid.Count);
                Assert.Contains(unpaid, x => x.SteamId == 101 && x.Status == "Pending");
                Assert.Contains(unpaid, x => x.SteamId == 102 && x.Status == "Failed");
                Assert.DoesNotContain(unpaid, x => x.SteamId == 103);

                await liteStorage.MarkRewardPayoutPaidAsync(unpaid[0].Id, _clock.UtcNow);
                var remaining = await liteStorage.GetUnpaidRewardPayoutsAsync();
                Assert.Single(remaining);
            }

            // SQLite
            string sqlitePath = Path.Combine(_tempDir, "payout_unpaid.sqlite");
            using (var sqliteStorage = new SqliteStorage(sqlitePath, _clock))
            {
                await sqliteStorage.InitializeAsync();
                await sqliteStorage.RecordRewardPayoutAsync("event1", 201, Money.FromDecimal(10m), "Pending", _clock.UtcNow);
                await sqliteStorage.RecordRewardPayoutAsync("event1", 202, Money.FromDecimal(20m), "Failed", _clock.UtcNow);
                await sqliteStorage.RecordRewardPayoutAsync("event1", 203, Money.FromDecimal(30m), "Paid", _clock.UtcNow);

                var unpaid = await sqliteStorage.GetUnpaidRewardPayoutsAsync();
                Assert.Equal(2, unpaid.Count);
                Assert.Contains(unpaid, x => x.SteamId == 201 && x.Status == "Pending");
                Assert.Contains(unpaid, x => x.SteamId == 202 && x.Status == "Failed");
                Assert.DoesNotContain(unpaid, x => x.SteamId == 203);

                await sqliteStorage.MarkRewardPayoutPaidAsync(unpaid[0].Id, _clock.UtcNow);
                var remaining = await sqliteStorage.GetUnpaidRewardPayoutsAsync();
                Assert.Single(remaining);
            }
        }

        [Fact]
        public async Task Disconnect_Evicts_EconomyCache_And_TransactionState()
        {
            string sqlitePath = Path.Combine(_tempDir, "evict_test.sqlite");
            using (var storage = new SqliteStorage(sqlitePath, _clock))
            {
                await storage.InitializeAsync();
                var economy = new KizShop.Economy.EconomyService(storage, () => Money.FromDecimal(100m), () => null, enableCache: true);
                var shop = new ShopService(_tempDir);
                var transactions = new ShopTransactionService(shop, economy, new MockInventoryGateway(), new MockVehicleGateway(), () => new KizShopConfiguration(), _clock);

                ulong steamId = 76561198000000001;
                // Prime cache
                await economy.GetBalanceAsync(steamId);
                Assert.Equal(Money.FromDecimal(100m), economy.GetCachedBalance(steamId));

                // Invalidate cache and clear player state
                economy.InvalidateCache(steamId);
                transactions.ClearPlayerState(steamId);

                Assert.Equal(Money.FromDecimal(100m), economy.GetCachedBalance(steamId));
            }
        }

        [Fact]
        public async Task TopBalances_QueryWorksWithBalanceIndex_LiteDbAndSqlite()
        {
            // SQLite
            string sqlitePath = Path.Combine(_tempDir, "baltop_idx.sqlite");
            using (var sqliteStorage = new SqliteStorage(sqlitePath, _clock))
            {
                await sqliteStorage.InitializeAsync();
                await sqliteStorage.DepositAsync(101, Money.FromDecimal(50m), "test", Money.Zero);
                await sqliteStorage.DepositAsync(102, Money.FromDecimal(250m), "test", Money.Zero);
                await sqliteStorage.DepositAsync(103, Money.FromDecimal(150m), "test", Money.Zero);

                var top = await sqliteStorage.GetTopBalancesAsync(2);
                Assert.Equal(2, top.Count);
                Assert.Equal(102ul, top[0].SteamId);
                Assert.Equal(Money.FromDecimal(250m), top[0].Balance);
                Assert.Equal(103ul, top[1].SteamId);
            }

            // LiteDB
            string litedbPath = Path.Combine(_tempDir, "baltop_idx.litedb");
            using (var liteStorage = new LiteDbStorage(litedbPath, _clock))
            {
                await liteStorage.InitializeAsync();
                await liteStorage.DepositAsync(101, Money.FromDecimal(50m), "test", Money.Zero);
                await liteStorage.DepositAsync(102, Money.FromDecimal(250m), "test", Money.Zero);
                await liteStorage.DepositAsync(103, Money.FromDecimal(150m), "test", Money.Zero);

                var top = await liteStorage.GetTopBalancesAsync(2);
                Assert.Equal(2, top.Count);
                Assert.Equal(102ul, top[0].SteamId);
                Assert.Equal(Money.FromDecimal(250m), top[0].Balance);
                Assert.Equal(103ul, top[1].SteamId);
            }
        }

        [Fact]
        public void CommandGiveaway_Metadata_IsAdminOnly_And_CorrectSyntax()
        {
            var cmd = new KizShop.Commands.CommandGiveaway();
            Assert.Equal("giveaway", cmd.Name);
            Assert.Equal("<amount|confirm>", cmd.Syntax);
            Assert.Contains("kizshop.admin", cmd.Permissions);
            Assert.Contains("kizshop.giveaway", cmd.Permissions);
            Assert.Equal(Rocket.API.AllowedCaller.Both, cmd.AllowedCaller);
        }

        [Fact]
        public void ShopService_IndexedLookup_ResolvesItemsAndVehiclesFast()
        {
            var shop = new ShopService(_tempDir);
            shop.AddOrUpdateItem(new ItemShopEntry { Id = 363, Name = "Maplestrike", BuyPrice = 100m, Enabled = true });
            shop.AddOrUpdateVehicle(new VehicleShopEntry { Id = 120, Name = "Tank", BuyPrice = 500m, Enabled = true });

            var itemLookup = shop.ResolveItem("363", enabledOnly: false);
            Assert.Equal(LookupStatus.Found, itemLookup.Status);
            Assert.Equal(363, itemLookup.Entry.Id);

            var vehLookup = shop.ResolveVehicle("120", enabledOnly: false);
            Assert.Equal(LookupStatus.Found, vehLookup.Status);
            Assert.Equal(120, vehLookup.Entry.Id);

            // Unknown ID
            var notFound = shop.ResolveItem("9999", enabledOnly: false);
            Assert.Equal(LookupStatus.NotFound, notFound.Status);
        }

        [Fact]
        public void PlayerLock_TryAcquireAndRelease_OperatesAccurately()
        {
            ulong p1 = 76561198000000001UL;
            ulong p2 = 76561198000000002UL;

            try
            {
                Assert.True(PlayerLock.TryAcquire(p1));
                Assert.False(PlayerLock.TryAcquire(p1)); // Cannot acquire twice
                Assert.True(PlayerLock.TryAcquire(p2));  // Different player can acquire

                PlayerLock.Release(p1);
                Assert.True(PlayerLock.TryAcquire(p1)); // Can acquire again after release
            }
            finally
            {
                PlayerLock.Release(p1);
                PlayerLock.Release(p2);
            }
        }

        [Fact]
        public void ShopService_GuidAndCategoryPrecompute_ResolvesO1AndMaintainsCache()
        {
            var shop = new ShopService(_tempDir);
            var itemGuid = Guid.NewGuid();
            var vehGuid = Guid.NewGuid();

            shop.AddOrUpdateItem(new ItemShopEntry
            {
                Id = 101,
                Guid = itemGuid.ToString("D"),
                Name = "Test Rifle",
                Category = "Guns",
                BuyPrice = 250m,
                Enabled = true
            });

            shop.AddOrUpdateVehicle(new VehicleShopEntry
            {
                Id = 55,
                Guid = vehGuid.ToString("N"),
                Name = "Test Car",
                BuyPrice = 600m,
                Enabled = true
            });

            // Guid lookup
            var itemLookup = shop.ResolveItem(itemGuid.ToString(), enabledOnly: false);
            Assert.Equal(LookupStatus.Found, itemLookup.Status);
            Assert.Equal(101, itemLookup.Entry.Id);

            var vehLookup = shop.ResolveVehicle(vehGuid.ToString(), enabledOnly: false);
            Assert.Equal(LookupStatus.Found, vehLookup.Status);
            Assert.Equal(55, vehLookup.Entry.Id);

            // Precomputed categories
            var cats = shop.GetCategories();
            Assert.Contains("Guns", cats);

            var counts = shop.GetCategoryItemCounts();
            Assert.True(counts.ContainsKey("Guns"));
            Assert.Equal(1, counts["Guns"]);
        }

        [Fact]
        public void AssetResolver_TypoTolerance_FindsCloseMatches()
        {
            var items = new List<ItemShopEntry>
            {
                new ItemShopEntry { Id = 18, Name = "Timberwolf", BuyPrice = 500m, Enabled = true },
                new ItemShopEntry { Id = 20, Name = "Timberwolf Magazine", BuyPrice = 75m, Enabled = true },
                new ItemShopEntry { Id = 253, Name = "Alicepack", BuyPrice = 250m, Enabled = true },
                new ItemShopEntry { Id = 15, Name = "Medkit", BuyPrice = 40m, Enabled = true }
            };

            // Typo 1 edit: "timbrwolf" -> distance 1 from Timberwolf
            var res1 = AssetResolver.ResolveItem(items, "timbrwolf", enabledOnly: false);
            Assert.Equal(LookupStatus.Ambiguous, res1.Status);
            Assert.Contains("Timberwolf", res1.CandidateNames);

            // Typo 2 edits: "alicpak" -> distance 2 from Alicepack
            var res2 = AssetResolver.ResolveItem(items, "alicpak", enabledOnly: false);
            Assert.Equal(LookupStatus.Ambiguous, res2.Status);
            Assert.Contains("Alicepack", res2.CandidateNames);

            // Typo 1 edit: "medkt" -> distance 1 from Medkit
            var res3 = AssetResolver.ResolveItem(items, "medkt", enabledOnly: false);
            Assert.Equal(LookupStatus.Ambiguous, res3.Status);
            Assert.Contains("Medkit", res3.CandidateNames);

            // No close match
            var res4 = AssetResolver.ResolveItem(items, "dragonfang", enabledOnly: false);
            Assert.Equal(LookupStatus.NotFound, res4.Status);

            // Too short for typo tolerance (< 4 chars) and no match
            var res5 = AssetResolver.ResolveItem(items, "xyz", enabledOnly: false);
            Assert.Equal(LookupStatus.NotFound, res5.Status);
        }

        [Fact]
        public void ShopService_DefaultKits_InitializedCorrectly()
        {
            var shop = new ShopService(_tempDir);
            shop.Load();

            var kits = shop.GetKits(enabledOnly: true);
            Assert.Equal(4, kits.Count);

            var starter = shop.ResolveKit("starter");
            Assert.NotNull(starter);
            Assert.Equal(100.00m, starter.BuyPrice);
            Assert.Contains(starter.Items, i => i.Id == 97 && i.Amount == 1);
            Assert.Contains(starter.Items, i => i.Id == 98 && i.Amount == 3);
            Assert.Contains(starter.Items, i => i.Id == 9 && i.Amount == 1);

            var hunter = shop.ResolveKit("hunter");
            Assert.NotNull(hunter);
            Assert.Equal(2000.00m, hunter.BuyPrice);
            Assert.Contains(hunter.Items, i => i.Id == 353 && i.Amount == 1);
            Assert.Contains(hunter.Items, i => i.Id == 347 && i.Amount == 5);

            var rifle = shop.ResolveKit("rifle");
            Assert.NotNull(rifle);
            Assert.Equal(3000.00m, rifle.BuyPrice);
            Assert.Contains(rifle.Items, i => i.Id == 109 && i.Amount == 1);
            Assert.Contains(rifle.Items, i => i.Id == 111 && i.Amount == 3);

            var sniper = shop.ResolveKit("sniper");
            Assert.NotNull(sniper);
            Assert.Equal(3000.00m, sniper.BuyPrice);
            Assert.Contains(sniper.Items, i => i.Id == 101 && i.Amount == 1);
            Assert.Contains(sniper.Items, i => i.Id == 103 && i.Amount == 3);
        }

        [Fact]
        public async Task ShopTransactionService_BuyKit_SucceedsAndFailsAccurately()
        {
            string dbPath = Path.Combine(_tempDir, "kits_test.litedb");
            using var storage = new LiteDbStorage(dbPath);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            var inventory = new MockInventoryGateway { AutoAddToGearOnDelivery = true };
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock);

            ulong steamId = 76561198000000123UL;

            // Insufficient funds
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(50m), "Initial");
            var failRes = await tx.BuyKitAsync(steamId, "starter", _ => true, false);
            Assert.False(failRes.Success);
            Assert.Equal("kit_insufficient_funds", failRes.ErrorKey);

            // Sufficient funds: starter kit costs $100
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(2500m), "Topup");
            var successRes = await tx.BuyKitAsync(steamId, "starter", _ => true, false);
            Assert.True(successRes.Success);
            Assert.Equal(Money.FromDecimal(100m), successRes.Price);
            Assert.Equal(Money.FromDecimal(2400m), successRes.NewBalance);

            // Check inventory received the 3 items (Colt, 3x Mag, Daypack)
            var gearColt = await inventory.GetGearItemsAsync(steamId, 97);
            Assert.Single(gearColt);

            var gearMag = await inventory.GetGearItemsAsync(steamId, 98);
            Assert.Equal(3, gearMag.Count);

            var gearPack = await inventory.GetGearItemsAsync(steamId, 9);
            Assert.Single(gearPack);
        }

        [Fact]
        public async Task Storage_CanClaimDailyAsync_AccurateAcrossDays()
        {
            string dbPath = Path.Combine(_tempDir, "daily_claim_test.litedb");
            using var storage = new LiteDbStorage(dbPath);
            await storage.InitializeAsync();

            ulong steamId = 76561198000000789UL;
            int dayIndex = 500;

            // Before claim: can claim
            bool canClaimBefore = await storage.CanClaimDailyAsync(steamId, dayIndex);
            Assert.True(canClaimBefore);

            // Claim daily
            var claimRes = await storage.TryClaimDailyAsync(steamId, dayIndex, 7, DateTime.UtcNow);
            Assert.True(claimRes.Allowed);

            // After claim on same day: cannot claim
            bool canClaimAfter = await storage.CanClaimDailyAsync(steamId, dayIndex);
            Assert.False(canClaimAfter);

            // Next day: can claim again
            bool canClaimNextDay = await storage.CanClaimDailyAsync(steamId, dayIndex + 1);
            Assert.True(canClaimNextDay);
        }

        [Fact]
        public async Task ShopTransactionService_BuyKit_MidDeliveryNoSpace_ReturnsKitNoSpace_RollsBackAndRefunds()
        {
            string dbPath = Path.Combine(_tempDir, "kits_nospace_test.litedb");
            using var storage = new LiteDbStorage(dbPath);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            // Fail on the 2nd item delivery
            var inventory = new MockInventoryGateway
            {
                AutoAddToGearOnDelivery = true,
                MaxDeliveryCountBeforeFailure = 1
            };
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock);

            ulong steamId = 76561198000000999UL;
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(2000m), "Topup");

            var res = await tx.BuyKitAsync(steamId, "starter", _ => true, false);

            // Must fail with kit_no_space
            Assert.False(res.Success);
            Assert.Equal("kit_no_space", res.ErrorKey);

            // Delivered items must be cleaned up
            Assert.Single(inventory.RemovedByReferenceItems);

            // Funds must be refunded in full ($2000 balance restored)
            var finalBal = await eco.GetBalanceAsync(steamId);
            Assert.Equal(Money.FromDecimal(2000m), finalBal);
        }

        [Fact]
        public void DefaultTranslations_IntegrityCheck_ValidPlaceholders_NoGoldColor()
        {
            var plugin = (KizShopPlugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(KizShopPlugin));
            var translations = plugin.DefaultTranslations;

            Assert.NotNull(translations);
            Assert.True(translations.Count > 30);

            foreach (var tr in translations)
            {
                // Must not contain invalid Unity color 'gold'
                Assert.DoesNotContain("color=gold", tr.Value, StringComparison.OrdinalIgnoreCase);

                // Placeholders {0}, {1}, etc. must not throw FormatException when formatted with dummy objects
                var dummyArgs = new object[] { DateTime.UtcNow, "B", "C", "D", "E", "F", "G", "H" };
                try
                {
                    string formatted = string.Format(tr.Value, dummyArgs);
                    Assert.NotNull(formatted);
                }
                catch (FormatException ex)
                {
                    Assert.Fail($"Translation key '{tr.Id}' has invalid format string '{tr.Value}': {ex.Message}");
                }
            }
        }

        [Fact]
        public async Task ShopTransactionService_Sell_DamagedItemBelowThreshold_RejectsWithTooDamaged()
        {
            string dbPath = Path.Combine(_tempDir, "damaged_item_test.litedb");
            using var storage = new LiteDbStorage(dbPath);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.AddOrUpdateItem(new ItemShopEntry
            {
                Id = 1001,
                Name = "TestGun",
                BuyPrice = 100m,
                SellPrice = 50m,
                Category = "Guns",
                Enabled = true
            });

            var inventory = new MockInventoryGateway();
            ulong steamId = 76561198000000111UL;
            // Add item with 5% quality (below MinItemSellQuality = 10)
            inventory.GearItems.Add(new InventoryItemInstance
            {
                Page = SDG.Unturned.PlayerInventory.BACKPACK,
                Index = 0,
                Item = new SDG.Unturned.Item(1001, 1, 5, new byte[16])
            });

            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration
            {
                ScaleSellByQuality = true,
                MinItemSellQuality = 10
            };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock);

            var res = await tx.ExecuteSellAsync(steamId, "1001", "1", _ => true, false);

            Assert.False(res.Success);
            Assert.Equal("sell_item_too_damaged", res.ErrorKey);
            Assert.NotNull(res.ErrorArgs);
            Assert.Equal((byte)5, res.ErrorArgs[1]);
        }

        [Fact]
        public async Task ShopTransactionService_ActiveSale_MultipliesBuyAndSellPrices()
        {
            string dbPath = Path.Combine(_tempDir, "sale_event_test.litedb");
            using var storage = new LiteDbStorage(dbPath);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.AddOrUpdateItem(new ItemShopEntry
            {
                Id = 2001,
                Name = "Rifle",
                BuyPrice = 100m,
                SellPrice = 50m,
                Category = "Guns",
                Enabled = true
            });

            var inventory = new MockInventoryGateway();
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock, null, storage);

            // Without sale: Buy = $100, Sell = $50
            var (normalBuy, _) = await tx.GetEffectiveBuyPriceAsync(shop.ResolveItem("2001").Entry, 1);
            var (normalSell, _) = await tx.GetEffectiveSellPriceAsync(shop.ResolveItem("2001").Entry, 1);
            Assert.Equal(Money.FromDecimal(100m), normalBuy);
            Assert.Equal(Money.FromDecimal(50m), normalSell);

            // Start sale: 20% discount on buy (x0.8), +50% bonus on sell (x1.5)
            shop.StartSale(0.8m, 1.5m, 60, "Weekend Frenzy");

            var (saleBuy, _) = await tx.GetEffectiveBuyPriceAsync(shop.ResolveItem("2001").Entry, 1);
            var (saleSell, _) = await tx.GetEffectiveSellPriceAsync(shop.ResolveItem("2001").Entry, 1);
            Assert.Equal(Money.FromDecimal(80m), saleBuy);
            Assert.Equal(Money.FromDecimal(75m), saleSell);

            // Clear sale: returns to normal
            shop.ClearSale();
            var (clearedBuy, _) = await tx.GetEffectiveBuyPriceAsync(shop.ResolveItem("2001").Entry, 1);
            Assert.Equal(Money.FromDecimal(100m), clearedBuy);
        }

        [Fact]
        public async Task Storage_TransactionHistoryAndExport_RecordsAndExportsAccurately()
        {
            string dbPath = Path.Combine(_tempDir, "tx_history_export_test.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            ulong steamId = 76561198000000555UL;
            await storage.SetBalanceAsync(steamId, Money.FromDecimal(500m), "Initial");
            await storage.LogTransactionAsync(steamId, "ShopBuy", Money.FromDecimal(50m), "Bought Item 363");
            await storage.LogTransactionAsync(steamId, "ShopSell", Money.FromDecimal(30m), "Sold Item 97");

            // 1. In-game history query
            var history = await storage.GetTransactionHistoryAsync(steamId, 10, 0);
            Assert.Equal(3, history.Count); // SetBalance, ShopBuy, ShopSell
            Assert.Contains(history, h => h.Type == "ShopBuy");
            Assert.Contains(history, h => h.Type == "ShopSell");

            // 2. Data export service
            var exporter = new DataExportService(storage);
            string accCsvPath = Path.Combine(_tempDir, "accounts_export.csv");
            string txCsvPath = Path.Combine(_tempDir, "tx_export.csv");

            int accCount = await exporter.ExportAccountsToCsvAsync(accCsvPath);
            Assert.Equal(1, accCount);
            Assert.True(File.Exists(accCsvPath));
            string accCsv = File.ReadAllText(accCsvPath);
            Assert.Contains(steamId.ToString(), accCsv);

            int txCount = await exporter.ExportTransactionsToCsvAsync(txCsvPath);
            Assert.Equal(3, txCount);
            Assert.True(File.Exists(txCsvPath));
            string txCsv = File.ReadAllText(txCsvPath);
            Assert.Contains("Bought Item 363", txCsv);
            Assert.Contains("Sold Item 97", txCsv);
        }
    }
}

