using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SDG.Unturned;
using Xunit;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Shop;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class Addendum5Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LiteDbStorage _storage;
        private readonly EconomyService _economy;
        private readonly ShopService _shopService;
        private readonly MockInventoryGateway _inventory;
        private readonly MockVehicleGateway _vehicle;
        private readonly KizShopConfiguration _config;
        private readonly ShopTransactionService _transactionService;

        public Addendum5Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_add5_{Guid.NewGuid():N}");
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
                ScaleSellByQuality = true,
                AutoEquipPurchasedItems = false
            };
            _inventory.ConfigProvider = () => _config;

            _transactionService = new ShopTransactionService(_shopService, _economy, _inventory, _vehicle, () => _config);
        }

        public void Dispose()
        {
            _storage?.Dispose();
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        // ==========================================
        // 1. BUILD IDENTIFICATION
        // ==========================================

        [Fact]
        public void BuildIdentification_FormatsCorrectly_WithVersionBuildAndThreadId()
        {
            int testThreadId = 42;
            string buildInfo = KizShopPlugin.GetBuildIdentification(testThreadId);

            Assert.NotNull(buildInfo);
            Assert.StartsWith("[KizShop] Loaded v", buildInfo);
            Assert.Contains("0.5.", buildInfo);
            Assert.Contains("build", buildInfo);
            Assert.Contains("main thread id 42", buildInfo);
        }

        [Fact]
        public void Assembly_HasInformationalVersionAttribute_SetToVersion()
        {
            var asm = typeof(KizShopPlugin).Assembly;
            var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            Assert.NotNull(attr);
            Assert.StartsWith("0.5.", attr.InformationalVersion);
        }

        // ==========================================
        // 2. AUTO-EQUIP ON DELIVERY CONFIG & LOGIC
        // ==========================================

        [Fact]
        public void Config_AutoEquipPurchasedItems_DefaultsToFalse_AndMatchesLoadDefaults()
        {
            var fresh = new KizShopConfiguration();
            Assert.False(fresh.AutoEquipPurchasedItems);

            var reset = new KizShopConfiguration();
            reset.AutoEquipPurchasedItems = true;
            reset.LoadDefaults();
            Assert.False(reset.AutoEquipPurchasedItems);

            var props = typeof(KizShopConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                Assert.Equal(prop.GetValue(fresh), prop.GetValue(reset));
            }
        }

        [Fact]
        public async Task Delivery_WhenAutoEquipFalse_PassesFalseToInventoryGateway()
        {
            ulong steamId = 76561198000000101;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(100m), "Initial");

            _config.AutoEquipPurchasedItems = false;

            var result = await _transactionService.ExecuteBuyAsync(steamId, "15", 2, _ => true, false);

            Assert.True(result.Success);
            Assert.Equal(2, _inventory.RecordedAutoEquipFlags.Count);
            Assert.All(_inventory.RecordedAutoEquipFlags, flag => Assert.False(flag));
        }

        [Fact]
        public async Task Delivery_WhenAutoEquipTrue_PassesTrueToInventoryGateway()
        {
            ulong steamId = 76561198000000102;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(100m), "Initial");

            _config.AutoEquipPurchasedItems = true;

            var result = await _transactionService.ExecuteBuyAsync(steamId, "15", 1, _ => true, false);

            Assert.True(result.Success);
            Assert.Single(_inventory.RecordedAutoEquipFlags);
            Assert.True(_inventory.RecordedAutoEquipFlags[0]);
        }

        [Fact]
        public void PlayerInventory_HasTryAddItemOverload_TakingItemAndBool()
        {
            var method = typeof(PlayerInventory).GetMethod("tryAddItem", new[] { typeof(Item), typeof(bool) });
            Assert.NotNull(method);
            Assert.Equal(typeof(bool), method.ReturnType);
        }
    }
}
