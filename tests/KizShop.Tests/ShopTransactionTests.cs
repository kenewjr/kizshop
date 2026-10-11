using System;
using System.IO;
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
    public class ShopTransactionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LiteDbStorage _storage;
        private readonly EconomyService _economy;
        private readonly ShopService _shopService;
        private readonly MockInventoryGateway _inventory;
        private readonly MockVehicleGateway _vehicle;
        private readonly KizShopConfiguration _config;
        private readonly ShopTransactionService _service;

        public ShopTransactionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            string dbPath = Path.Combine(_tempDir, "test.litedb");
            _storage = new LiteDbStorage(dbPath);
            _storage.InitializeAsync().GetAwaiter().GetResult();

            _economy = new EconomyService(_storage, () => Money.FromDecimal(100m), () => null, true, new TestMainThreadDispatcher());
            _shopService = new ShopService(_tempDir);
            _shopService.Load();

            _inventory = new MockInventoryGateway();
            _vehicle = new MockVehicleGateway();
            _config = new KizShopConfiguration { ScaleSellByQuality = true, MaxTransactionAmount = 1000000m };

            _service = new ShopTransactionService(_shopService, _economy, _inventory, _vehicle, () => _config);
        }

        public void Dispose()
        {
            _storage?.Dispose();
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public async Task Sell_ThirtyRoundMagazine_CountsAsOneUnitAndScalesByAmountAndQuality()
        {
            ulong steamId = 76561198000000050;
            ushort magId = 20; // Timberwolf Magazine in default shop, SellPrice = 15.00m (or 25.00m)

            var entry = _shopService.ResolveItem(magId.ToString()).Entry;
            decimal baseSellPrice = entry.SellPrice.Value; // 25.00m

            _inventory.DefaultAmounts[magId] = 30; // Max magazine capacity = 30

            // 1 magazine with 15 rounds (50% ammo) and 80% quality
            var item = new Item(magId, 15, 80, new byte[16]);
            _inventory.GearItems.Add(new InventoryItemInstance
            {
                Page = PlayerInventory.BACKPACK,
                Index = 0,
                Item = item
            });

            var result = await _service.ExecuteSellAsync(steamId, magId.ToString(), "1", _ => true, false);

            Assert.True(result.Success);
            Assert.Equal(1, result.Quantity); // Counted as 1 INSTANCE, not 15 or 30!

            // Expected unit price: baseSellPrice * (15/30) * (80/100) = 25.00 * 0.5 * 0.8 = 10.00m
            decimal expected = baseSellPrice * (15m / 30m) * (80m / 100m);
            Assert.Equal(expected, result.Price.ToDecimal());
        }

        [Fact]
        public async Task Buy_Success_DeductsFundsAndDeliversItem()
        {
            ulong steamId = 76561198000000051;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(100m), "Initial");

            // Buy 2 Medkits (ID 15, BuyPrice = 40.00m each) -> Total = 80.00m
            var result = await _service.ExecuteBuyAsync(steamId, "15", 2, _ => true, false);

            Assert.True(result.Success);
            Assert.Equal(2, result.Quantity);
            Assert.Equal(80.00m, result.Price.ToDecimal());
            Assert.Equal(20.00m, result.NewBalance.ToDecimal());
            Assert.Equal(2, _inventory.DeliveredItems.Count);
        }

        [Fact]
        public async Task Buy_InsufficientFunds_FailsWithoutDeliveringOrDeducting()
        {
            ulong steamId = 76561198000000052;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(30m), "Initial");

            // Medkit costs 40.00m
            var result = await _service.ExecuteBuyAsync(steamId, "15", 1, _ => true, false);

            Assert.False(result.Success);
            Assert.Equal("buy_insufficient_funds", result.ErrorKey);
            Assert.Empty(_inventory.DeliveredItems);

            var bal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(30.00m, bal.ToDecimal());
        }

        [Fact]
        public async Task Buy_DeliveryFailsHalfway_RemovesDeliveredByReferenceAndRefundsFully()
        {
            ulong steamId = 76561198000000053;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(200m), "Initial");

            // Allow delivering only 1 item before failure
            _inventory.MaxDeliveryCountBeforeFailure = 1;

            // Attempt to buy 3 Medkits (total 120m)
            var result = await _service.ExecuteBuyAsync(steamId, "15", 3, _ => true, false);

            Assert.False(result.Success);
            Assert.Equal("buy_failed_refunded", result.ErrorKey);

            // Item 1 delivered must be removed by reference!
            Assert.Single(_inventory.RemovedByReferenceItems);

            // Balance must be fully refunded back to 200m!
            var bal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(200.00m, bal.ToDecimal());
        }

        [Fact]
        public async Task Buy_ExceptionDuringDelivery_RefundsAndCancels()
        {
            ulong steamId = 76561198000000054;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(100m), "Initial");

            _inventory.ThrowOnDelivery = true;

            var result = await _service.ExecuteBuyAsync(steamId, "15", 1, _ => true, false);

            Assert.False(result.Success);
            Assert.Equal("buy_failed_refunded", result.ErrorKey);

            var bal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(100.00m, bal.ToDecimal());
        }

        [Fact]
        public async Task Buy_AmbiguousName_RejectsPurchaseAndListsCandidates()
        {
            ulong steamId = 76561198000000055;
            // "Timberwolf" and "Timberwolf Magazine" both contain "Timberwolf"
            var result = await _service.ExecuteBuyAsync(steamId, "Timberwolf", 1, _ => true, false);

            // Note: "Timberwolf" has exact match so it finds Timberwolf.
            // Query "Timber" has prefix match on both:
            var ambiguousRes = await _service.ExecuteBuyAsync(steamId, "Timber", 1, _ => true, false);

            Assert.False(ambiguousRes.Success);
            Assert.Equal("ambiguous_match", ambiguousRes.ErrorKey);
            Assert.NotNull(ambiguousRes.CandidateNames);
            Assert.True(ambiguousRes.CandidateNames.Count >= 2);
        }
    }
}
