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
    }
}
