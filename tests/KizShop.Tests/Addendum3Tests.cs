using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using SDG.Unturned;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Shop;
using KizShop.Shop.Gateways;
using KizShop.Shop.Models;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class Addendum3Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LiteDbStorage _storage;
        private readonly EconomyService _economy;
        private readonly ShopService _shopService;
        private readonly MockInventoryGateway _inventory;
        private readonly MockVehicleGateway _vehicle;
        private readonly KizShopConfiguration _config;
        private readonly ShopTransactionService _transactionService;

        public Addendum3Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_add3_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            string dbPath = Path.Combine(_tempDir, "test.litedb");
            _storage = new LiteDbStorage(dbPath);
            _storage.InitializeAsync().GetAwaiter().GetResult();

            _economy = new EconomyService(_storage, () => Money.FromDecimal(1000m), () => null, true, new TestMainThreadDispatcher());
            _shopService = new ShopService(_tempDir);
            _shopService.Load();

            _inventory = new MockInventoryGateway();
            _vehicle = new MockVehicleGateway();
            _config = new KizShopConfiguration
            {
                ScaleSellByQuality = true
            };

            _transactionService = new ShopTransactionService(_shopService, _economy, _inventory, _vehicle, () => _config);
        }

        public void Dispose()
        {
            _storage?.Dispose();
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        private static PlayerInventory CreateInventoryWithPages(params (byte page, byte width, byte height)[] pageDims)
        {
            var inv = new PlayerInventory();
            var pages = new Items[PlayerInventory.PAGES];
            for (byte i = 0; i < PlayerInventory.PAGES; i++)
            {
                pages[i] = new Items(i);
            }

            foreach (var (page, width, height) in pageDims)
            {
                if (page < pages.Length)
                {
                    pages[page].resize(width, height);
                }
            }

            var field = typeof(PlayerInventory).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(Items[]));
            field?.SetValue(inv, pages);
            return inv;
        }

        // 1. CheckSpace tests
        [Fact]
        public void CheckSpace_OneItemWithFreeSpace_Fits()
        {
            var inv = CreateInventoryWithPages((PlayerInventory.BACKPACK, 2, 2));
            bool fits = UnturnedInventoryGateway.SimulatePlacement(inv, 1, 1, 1, out string details);
            Assert.True(fits);
            Assert.Contains("Placed 1/1", details);
        }

        [Fact]
        public void CheckSpace_TwentyItemsWithFiveFreeCells_ReturnsNoSpace()
        {
            // Grid of 5 cells (5 width x 1 height)
            var inv = CreateInventoryWithPages((PlayerInventory.BACKPACK, 5, 1));
            bool fits = UnturnedInventoryGateway.SimulatePlacement(inv, 1, 1, 20, out string details);
            Assert.False(fits);
            Assert.Contains("Failed at instance", details);
        }

        [Fact]
        public void CheckSpace_ItemLargerThanAnyFreeRegion_ReturnsNoSpaceEvenIfTotalFreeCellsEnough()
        {
            // Two separate pages: Hands has 1x2 (2 cells), Shirt has 1x2 (2 cells) -> Total 4 cells free
            var inv = CreateInventoryWithPages((PlayerInventory.SLOTS, 1, 2), (PlayerInventory.SHIRT, 1, 2));

            // Item is 2x2 (requires a contiguous 2x2 region)
            bool fits = UnturnedInventoryGateway.SimulatePlacement(inv, 2, 2, 1, out string details);
            Assert.False(fits);
            Assert.Contains("Failed at instance 1/1", details);
        }

        [Fact]
        public async Task CheckSpace_NullPlayer_ReturnsPlayerNullReasonAndMappedErrorKey()
        {
            // UnturnedInventoryGateway without player logged in returns PlayerNull
            var gateway = new UnturnedInventoryGateway();
            var res = await gateway.CheckSpaceAsync(9999999999999999, 15, 1);
            Assert.False(res.Fits);
            Assert.Equal(CheckSpaceReason.PlayerNull, res.Reason);

            // Mock gateway configured with PlayerNull space failure
            _inventory.HasSpace = false;
            _inventory.SpaceReason = CheckSpaceReason.PlayerNull;

            var buyRes = await _transactionService.ExecuteBuyAsync(76561198000000001, "15", 1, _ => true, false);
            Assert.False(buyRes.Success);
            Assert.Equal("buy_player_unavailable", buyRes.ErrorKey);
        }

        // 2. Main-thread dispatcher tests
        [Fact]
        public void MainThreadDispatcher_ExecutesOnCapturedMainThread()
        {
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var queue = new List<System.Action>();

            var dispatcher = new RocketMainThreadDispatcher(mainThreadId, act =>
            {
                lock (queue) { queue.Add(act); }
            });

            int executedFuncThreadId = -1;
            int resultValue = 0;

            // Call from background thread
            var thread = new Thread(() =>
            {
                var task = dispatcher.RunOnMainThreadAsync(() =>
                {
                    executedFuncThreadId = Thread.CurrentThread.ManagedThreadId;
                    return 42;
                });
                resultValue = task.GetAwaiter().GetResult();
            });
            thread.Start();

            // Pump queue on captured main thread
            while (thread.IsAlive)
            {
                System.Action next = null;
                lock (queue)
                {
                    if (queue.Count > 0)
                    {
                        next = queue[0];
                        queue.RemoveAt(0);
                    }
                }
                next?.Invoke();
                Thread.Sleep(5);
            }

            thread.Join();
            Assert.Equal(42, resultValue);
            Assert.Equal(mainThreadId, executedFuncThreadId);
        }

        [Fact]
        public async Task MainThreadDispatcher_ExceptionInsideFunc_FaultsReturnedTask()
        {
            var dispatcher = new RocketMainThreadDispatcher(Thread.CurrentThread.ManagedThreadId);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await dispatcher.RunOnMainThreadAsync<int>(() =>
                {
                    throw new InvalidOperationException("Main thread failed");
                });
            });

            Assert.Equal("Main thread failed", ex.Message);
        }

        // 4. Buy flow rollback test
        [Fact]
        public async Task BuyFlow_DeliveryFailsOnThirdOfFiveItems_RemovesPreviousTwoAndRefundsFully()
        {
            ulong steamId = 76561198000000090;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(500m), "Initial");

            // Allow only 2 deliveries before failing on the 3rd
            _inventory.MaxDeliveryCountBeforeFailure = 2;

            // Purchase 5 Medkits (40m each = 200m)
            var result = await _transactionService.ExecuteBuyAsync(steamId, "15", 5, _ => true, false);

            Assert.False(result.Success);
            Assert.Equal("buy_failed_refunded", result.ErrorKey);

            // Items 1 and 2 must have been removed by reference
            Assert.Equal(2, _inventory.RemovedByReferenceItems.Count);

            // Full 500m balance restored
            var finalBal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(500.00m, finalBal.ToDecimal());
        }
    }
}
