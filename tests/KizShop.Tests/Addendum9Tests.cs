using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Economy.Models;
using KizShop.Shop;
using KizShop.Shop.Models;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class Addendum9Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly MockClock _clock;

        public Addendum9Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_add9_{Guid.NewGuid():N}");
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
        // 1. BUYKIT COOLDOWN RACE CONDITION & POST-LOCK RE-CHECK
        // =================================================================

        [Fact]
        public async Task BuyKitAsync_ConcurrentCalls_PreLockRace_ExactlyOneSucceeds_OtherGetsCooldown()
        {
            string dbPath = Path.Combine(_tempDir, "race_cooldown.litedb");
            using var raceStorage = new RaceControllableStorage(dbPath, _clock);
            await raceStorage.InitializeAsync();

            var eco = new EconomyService(raceStorage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            var inventory = new MockInventoryGateway { AutoAddToGearOnDelivery = true };
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock, null, raceStorage);

            ulong steamId = 76561198000000301UL;
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(1000m), "Initial");

            var kit = new KitShopEntry
            {
                Name = "race_kit",
                BuyPrice = 100m,
                CooldownSeconds = 60,
                Enabled = true,
                Items = new List<KitItemEntry> { new KitItemEntry(97, 1) }
            };
            shop.AddKit(kit);

            // Task 1 and Task 2 both fire concurrently.
            // RaceControllableStorage ensures both complete their pre-lock cooldown check
            // before Task 1 acquires PlayerLock, and Task 2 acquires PlayerLock after Task 1 releases it.
            var t1 = Task.Run(() => tx.BuyKitAsync(steamId, "race_kit", _ => true, false));
            var t2 = Task.Run(() => tx.BuyKitAsync(steamId, "race_kit", _ => true, false));

            var results = await Task.WhenAll(t1, t2);
            var res1 = results[0];
            var res2 = results[1];

            // Exactly one must succeed, the other must return kit_cooldown error
            var successful = (res1.Success ? res1 : (res2.Success ? res2 : null));
            var rejected = (!res1.Success ? res1 : (!res2.Success ? res2 : null));

            Assert.NotNull(successful);
            Assert.NotNull(rejected);
            Assert.True(successful.Success);
            Assert.False(rejected.Success);
            Assert.Equal("kit_cooldown", rejected.ErrorKey);

            // Balance must be debited exactly once: 1000 - 100 = 900
            var finalBal = await eco.GetBalanceAsync(steamId);
            Assert.Equal(900m, finalBal.ToDecimal());

            // Delivered items count must be 1 item
            Assert.Single(inventory.DeliveredItems);
        }

        // =================================================================
        // 2. ROLLBACK LOGGING ON FAILURE
        // =================================================================

        [Fact]
        public async Task BuyKitAsync_Rollback_WhenRemoveItemThrows_StillRefundsFullPrice()
        {
            string dbPath = Path.Combine(_tempDir, "rollback_remove_throw.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            var throwingGateway = new ThrowingRemoveInventoryGateway();
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, throwingGateway, vehicle, () => cfg, _clock, null, storage);

            ulong steamId = 76561198000000302UL;
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(500m), "Initial");

            var kit = new KitShopEntry
            {
                Name = "rollback_fail_kit",
                BuyPrice = 100m,
                CooldownSeconds = 30,
                Enabled = true,
                Items = new List<KitItemEntry>
                {
                    new KitItemEntry(97, 1),
                    new KitItemEntry(98, 1)
                }
            };
            shop.AddKit(kit);

            // Gateway fails on second item delivery
            throwingGateway.MaxDeliveryCountBeforeFailure = 1;

            var res = await tx.BuyKitAsync(steamId, "rollback_fail_kit", _ => true, false);

            Assert.False(res.Success);
            Assert.Equal("kit_no_space", res.ErrorKey);

            // Despite RemoveItemByReferenceAsync throwing, full refund must still be processed
            var bal = await eco.GetBalanceAsync(steamId);
            Assert.Equal(500m, bal.ToDecimal());
        }

        [Fact]
        public async Task BuyKitAsync_PreLockFastPath_RejectsWithoutTouchingLockOrBalance()
        {
            string dbPath = Path.Combine(_tempDir, "fast_path_cooldown.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            var inventory = new MockInventoryGateway { AutoAddToGearOnDelivery = true };
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock, null, storage);

            ulong steamId = 76561198000000303UL;
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(500m), "Initial");

            var kit = new KitShopEntry
            {
                Name = "fast_path_kit",
                BuyPrice = 100m,
                CooldownSeconds = 120,
                Enabled = true,
                Items = new List<KitItemEntry> { new KitItemEntry(97, 1) }
            };
            shop.AddKit(kit);

            // Pre-seed storage cooldown
            await storage.SetKitCooldownAsync(steamId, "fast_path_kit", _clock.UtcNow.AddSeconds(-30));

            var res = await tx.BuyKitAsync(steamId, "fast_path_kit", _ => true, false);

            Assert.False(res.Success);
            Assert.Equal("kit_cooldown", res.ErrorKey);
            Assert.Equal(90, (int)res.ErrorArgs[0]);

            // Balance intact
            var bal = await eco.GetBalanceAsync(steamId);
            Assert.Equal(500m, bal.ToDecimal());
        }

        private class ThrowingRemoveInventoryGateway : MockInventoryGateway
        {
            public override Task<bool> RemoveItemByReferenceAsync(ulong steamId, SDG.Unturned.Item item)
            {
                throw new InvalidOperationException("Simulated inventory remove failure during rollback");
            }
        }

        private class RaceControllableStorage : LiteDbStorage
        {
            private int _getCooldownCallCount = 0;
            private readonly TaskCompletionSource<bool> _call1PreCheckDone = new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<bool> _call2PreCheckDone = new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<bool> _setCooldownDone = new TaskCompletionSource<bool>();

            public RaceControllableStorage(string dbPath, IClock clock) : base(dbPath, clock)
            {
            }

            public override async Task<DateTime?> GetKitCooldownAsync(ulong steamId, string kitName)
            {
                int callIndex = System.Threading.Interlocked.Increment(ref _getCooldownCallCount);

                if (callIndex == 1)
                {
                    var result = await base.GetKitCooldownAsync(steamId, kitName).ConfigureAwait(false);
                    _call1PreCheckDone.TrySetResult(true);
                    // Ensure Task 2 completes its pre-lock check before Task 1 proceeds to acquire PlayerLock
                    await _call2PreCheckDone.Task.ConfigureAwait(false);
                    return result;
                }
                else if (callIndex == 2)
                {
                    await _call1PreCheckDone.Task.ConfigureAwait(false);
                    var result = await base.GetKitCooldownAsync(steamId, kitName).ConfigureAwait(false);
                    _call2PreCheckDone.TrySetResult(true);

                    // Wait until Call 1 finishes purchase and releases PlayerLock
                    await _setCooldownDone.Task.ConfigureAwait(false);
                    while (PlayerLock.IsLocked(steamId))
                    {
                        await Task.Delay(5).ConfigureAwait(false);
                    }
                    return result;
                }
                else
                {
                    return await base.GetKitCooldownAsync(steamId, kitName).ConfigureAwait(false);
                }
            }

            public override async Task SetKitCooldownAsync(ulong steamId, string kitName, DateTime lastPurchaseUtc)
            {
                await base.SetKitCooldownAsync(steamId, kitName, lastPurchaseUtc).ConfigureAwait(false);
                _setCooldownDone.TrySetResult(true);
            }
        }
    }
}
