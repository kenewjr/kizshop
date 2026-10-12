using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Newtonsoft.Json;
using SDG.Unturned;
using Xunit;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Shop;
using KizShop.Shop.Gateways;
using KizShop.Shop.Models;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class Addendum4Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LiteDbStorage _storage;
        private readonly EconomyService _economy;
        private readonly ShopService _shopService;
        private readonly MockInventoryGateway _inventory;
        private readonly MockVehicleGateway _vehicle;
        private readonly KizShopConfiguration _config;
        private readonly ShopTransactionService _transactionService;

        public Addendum4Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_add4_{Guid.NewGuid():N}");
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
                GunPurchaseIncludesMagazine = false
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
        // 1. CONFIG DEFAULT BUG & XML COMPATIBILITY
        // ==========================================

        [Fact]
        public void Config_DefaultsMatchPropertyInitializers_ForAllPublicProperties()
        {
            var fresh = new KizShopConfiguration();
            var reset = new KizShopConfiguration();
            reset.LoadDefaults();

            var properties = typeof(KizShopConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Assert.NotEmpty(properties);

            foreach (var prop in properties)
            {
                if (!prop.CanRead || !prop.CanWrite) continue;

                var freshVal = prop.GetValue(fresh);
                var resetVal = prop.GetValue(reset);

                Assert.True(
                    Equals(freshVal, resetVal),
                    $"Property '{prop.Name}' mismatch: initializer had '{freshVal}', but LoadDefaults() produced '{resetVal}'");
            }
        }

        [Fact]
        public void Config_OldXmlWithVisualUiElements_DeserializesWithoutErrors()
        {
            string oldXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<KizShopConfiguration xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <DatabaseType>LiteDB</DatabaseType>
  <ConnectionString>custom_path.litedb</ConnectionString>
  <CurrencyName>Coins</CurrencyName>
  <CurrencySymbol>C$</CurrencySymbol>
  <StartingBalance>250.00</StartingBalance>
  <EnableVisualUI>true</EnableVisualUI>
  <UIEffectId>32001</UIEffectId>
  <UIKey>32001</UIKey>
  <UseVisualUiForShopCommand>true</UseVisualUiForShopCommand>
  <UIItemsPerPage>6</UIItemsPerPage>
  <GunPurchaseIncludesMagazine>false</GunPurchaseIncludesMagazine>
</KizShopConfiguration>";

            var serializer = new XmlSerializer(typeof(KizShopConfiguration));
            using var reader = new StringReader(oldXml);
            var deserialized = (KizShopConfiguration)serializer.Deserialize(reader);

            Assert.NotNull(deserialized);
            Assert.Equal("custom_path.litedb", deserialized.ConnectionString);
            Assert.Equal("Coins", deserialized.CurrencyName);
            Assert.Equal("C$", deserialized.CurrencySymbol);
            Assert.Equal(250.00m, deserialized.StartingBalance);
            Assert.False(deserialized.GunPurchaseIncludesMagazine);
        }

        // ==========================================
        // 2. GUN BUY / SELL SEMANTICS
        // ==========================================

        [Fact]
        public async Task Gun_FreshlyBought_SellBackSucceeds()
        {
            ulong steamId = 76561198000000091;
            ushort gunId = 18; // Timberwolf in catalog
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(1000m), "Initial");

            // Setup mock gun with default attachments (sight=100, mag=20, ammo=6)
            byte[] defaultState = new byte[16];
            defaultState[0] = 100; // sight ID byte 0
            defaultState[8] = 20;  // magazine ID byte 0
            defaultState[10] = 6;  // ammo

            _inventory.GunItemIds.Add(gunId);
            _inventory.DefaultGunStates[gunId] = defaultState;
            _inventory.AutoAddToGearOnDelivery = true;
            _config.GunPurchaseIncludesMagazine = false;

            // Buy gun
            var buyResult = await _transactionService.ExecuteBuyAsync(steamId, gunId.ToString(), 1, _ => true, false);
            Assert.True(buyResult.Success);
            Assert.Single(_inventory.DeliveredItems);

            var delivered = _inventory.DeliveredItems[0];
            // Since GunPurchaseIncludesMagazine is false, magazine bytes 8, 9 and ammo 10 should be stripped
            Assert.Equal(0, delivered.state[8]);
            Assert.Equal(0, delivered.state[9]);
            Assert.Equal(0, delivered.state[10]);
            Assert.Equal(100, delivered.state[0]); // Sight preserved

            // Sell it back -> must succeed!
            var sellResult = await _transactionService.ExecuteSellAsync(steamId, gunId.ToString(), "1", _ => true, false);
            Assert.True(sellResult.Success);
            Assert.Equal(1, sellResult.Quantity);
        }

        [Fact]
        public async Task Gun_WithDifferentSight_SellRefused()
        {
            ulong steamId = 76561198000000092;
            ushort gunId = 18;

            byte[] defaultState = new byte[16];
            defaultState[0] = 100; // default sight

            _inventory.GunItemIds.Add(gunId);
            _inventory.DefaultGunStates[gunId] = defaultState;

            // Gun in inventory with non-default sight (sight = 200)
            byte[] modifiedState = (byte[])defaultState.Clone();
            modifiedState[0] = 200;

            _inventory.GearItems.Add(new InventoryItemInstance
            {
                Page = PlayerInventory.BACKPACK,
                Index = 0,
                Item = new Item(gunId, 1, 100, modifiedState)
            });

            var sellResult = await _transactionService.ExecuteSellAsync(steamId, gunId.ToString(), "1", _ => true, false);
            Assert.False(sellResult.Success);
            Assert.Equal("sell_gun_has_attachments", sellResult.ErrorKey);
        }

        [Fact]
        public async Task Gun_WithLoadedNonDefaultMagazine_SellRefused()
        {
            ulong steamId = 76561198000000093;
            ushort gunId = 18;

            byte[] defaultState = new byte[16];
            defaultState[8] = 20; // default magazine

            _inventory.GunItemIds.Add(gunId);
            _inventory.DefaultGunStates[gunId] = defaultState;
            _config.GunPurchaseIncludesMagazine = true; // keeping full default

            // Gun with non-default magazine (mag = 99)
            byte[] modifiedState = (byte[])defaultState.Clone();
            modifiedState[8] = 99;

            _inventory.GearItems.Add(new InventoryItemInstance
            {
                Page = PlayerInventory.BACKPACK,
                Index = 0,
                Item = new Item(gunId, 1, 100, modifiedState)
            });

            var sellResult = await _transactionService.ExecuteSellAsync(steamId, gunId.ToString(), "1", _ => true, false);
            Assert.False(sellResult.Success);
            Assert.Equal("sell_gun_has_attachments", sellResult.ErrorKey);
        }

        [Fact]
        public void UnturnedInventoryGateway_HasAttachmentsOrLoadedMagazine_DirectStateComparison()
        {
            var dispatcher = new TestMainThreadDispatcher();
            var gateway = new UnturnedInventoryGateway(dispatcher, () => _config);

            ushort gunId = 18;
            byte[] defaultState = new byte[16];
            defaultState[0] = 50; // default sight
            defaultState[8] = 20; // default magazine
            defaultState[10] = 10; // ammo

            gateway.IsGunChecker = id => id == gunId;
            gateway.DefaultGunStateResolver = id =>
            {
                byte[] state = (byte[])defaultState.Clone();
                if (!_config.GunPurchaseIncludesMagazine)
                {
                    state[8] = 0;
                    state[9] = 0;
                    state[10] = 0;
                }
                return state;
            };

            // Test 1: GunPurchaseIncludesMagazine = false
            _config.GunPurchaseIncludesMagazine = false;
            byte[] strippedDefault = (byte[])defaultState.Clone();
            strippedDefault[8] = 0;
            strippedDefault[9] = 0;
            strippedDefault[10] = 0;

            // Stripped matches default -> false (no unauthorized attachments)
            Assert.False(gateway.HasAttachmentsOrLoadedMagazine(gunId, strippedDefault));

            // With magazine attached -> true (rejected)
            Assert.True(gateway.HasAttachmentsOrLoadedMagazine(gunId, defaultState));

            // Test 2: GunPurchaseIncludesMagazine = true
            _config.GunPurchaseIncludesMagazine = true;
            // Original default matches -> false
            Assert.False(gateway.HasAttachmentsOrLoadedMagazine(gunId, defaultState));

            // Modified sight -> true (rejected)
            byte[] changedSight = (byte[])defaultState.Clone();
            changedSight[0] = 99;
            Assert.True(gateway.HasAttachmentsOrLoadedMagazine(gunId, changedSight));
        }

        // ==========================================
        // 3. DB WORK OFF THE MAIN THREAD
        // ==========================================

        private class ThreadRecordingStorage : IKizStorage
        {
            public int TryWithdrawThreadId { get; private set; } = -1;

            public Task InitializeAsync() => Task.CompletedTask;
            public Task<bool> AccountExistsAsync(ulong steamId) => Task.FromResult(true);
            public Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance) => Task.FromResult(Money.FromDecimal(500m));

            public async Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance)
            {
                return await Task.Run(() =>
                {
                    TryWithdrawThreadId = Thread.CurrentThread.ManagedThreadId;
                    return WithdrawResult.Succeeded(Money.FromDecimal(500m), Money.FromDecimal(500m) - amount);
                }).ConfigureAwait(false);
            }

            public Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null, bool ignoreMaxBalance = false)
                => Task.FromResult(new DepositResult(true, Money.Zero, amount));

            public Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null)
                => Task.FromResult(TransferResult.Succeeded(Money.Zero, Money.Zero, Money.Zero, Money.Zero));

            public Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count) => Task.FromResult(new List<TopBalanceEntry>());
            public Task SetBalanceAsync(ulong steamId, Money amount, string reason) => Task.CompletedTask;
            public Task LogTransactionAsync(ulong steamId, string type, Money amount, string details) => Task.CompletedTask;
            public Task PruneLogsAsync(int retentionDays) => Task.CompletedTask;
            public void Dispose() { }
        }

        [Fact]
        public void DatabaseWork_RunsOffMainThread_WhenInvokedFromMainThreadCommand()
        {
            var recordingStorage = new ThreadRecordingStorage();
            TestMainThreadDispatcher dispatcher = null;
            int storageThreadId = -1;
            bool buySuccess = false;

            var mainThread = new Thread(() =>
            {
                int currentThreadId = Thread.CurrentThread.ManagedThreadId;
                dispatcher = new TestMainThreadDispatcher { CapturedThreadId = currentThreadId };
                var eco = new EconomyService(recordingStorage, () => Money.FromDecimal(1000m), () => null, true, dispatcher);
                var tx = new ShopTransactionService(_shopService, eco, _inventory, _vehicle, () => _config);

                ulong steamId = 76561198000000094;
                var result = tx.ExecuteBuyAsync(steamId, "15", 1, _ => true, false).GetAwaiter().GetResult();
                buySuccess = result.Success;
                storageThreadId = recordingStorage.TryWithdrawThreadId;
            });

            mainThread.Start();
            mainThread.Join();

            Assert.True(buySuccess);
            Assert.NotNull(dispatcher);
            Assert.NotEqual(-1, storageThreadId);
            Assert.NotEqual(dispatcher.CapturedThreadId, storageThreadId);
        }

        // ==========================================
        // 4. CATALOG VALIDATION & AVAILABLE FLAG
        // ==========================================

        [Fact]
        public void ItemShopEntry_Available_DefaultsToTrue_AndIsNotSerializedToJson()
        {
            var entry = new ItemShopEntry
            {
                Id = 15,
                Name = "Medkit",
                BuyPrice = 40m,
                Available = false // explicitly false
            };

            string json = JsonConvert.SerializeObject(entry);
            Assert.DoesNotContain("Available", json, StringComparison.OrdinalIgnoreCase);

            var deserialized = JsonConvert.DeserializeObject<ItemShopEntry>(json);
            Assert.True(deserialized.Available); // defaults back to true
        }

        [Fact]
        public void ShopService_UnavailableItems_ExcludedFromResolveAndGetItems()
        {
            var entry = _shopService.ResolveItem("15").Entry;
            Assert.NotNull(entry);
            Assert.True(entry.Available);

            // Mark unavailable (as lazy validation does for missing game assets)
            entry.Available = false;

            // When enabledOnly is true, ResolveItem returns NotFound
            var lookup = _shopService.ResolveItem("15", enabledOnly: true);
            Assert.Equal(LookupStatus.NotFound, lookup.Status);

            // GetItems with enabledOnly excludes it
            var items = _shopService.GetItems(enabledOnly: true);
            Assert.DoesNotContain(items, i => i.Id == 15);

            // GetPagedItems also excludes it
            var (paged, _) = _shopService.GetPagedItems(1, 10);
            Assert.DoesNotContain(paged, i => i.Id == 15);

            // CanBuy and CanSell return false
            Assert.False(entry.CanBuy);
            Assert.False(entry.CanSell);

            // Reset back
            entry.Available = true;
            Assert.True(entry.CanBuy);
        }
    }
}
