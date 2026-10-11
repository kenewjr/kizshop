using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Shop;
using KizShop.Storage;
using KizShop.Tests.Mocks;
using KizShop.UI;

namespace KizShop.Tests
{
    public class ShopUiTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LiteDbStorage _storage;
        private readonly EconomyService _economy;
        private readonly ShopService _shopService;
        private readonly MockInventoryGateway _inventory;
        private readonly MockVehicleGateway _vehicle;
        private readonly KizShopConfiguration _config;
        private readonly ShopTransactionService _transactionService;
        private readonly MockShopUiGateway _uiGateway;
        private readonly ShopUiService _uiService;

        public ShopUiTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_uitest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            string dbPath = Path.Combine(_tempDir, "test.litedb");
            _storage = new LiteDbStorage(dbPath);
            _storage.InitializeAsync().GetAwaiter().GetResult();

            _economy = new EconomyService(_storage, () => Money.FromDecimal(500m), () => null, true, new TestMainThreadDispatcher());
            _shopService = new ShopService(_tempDir);
            _shopService.Load();

            _inventory = new MockInventoryGateway();
            _vehicle = new MockVehicleGateway();
            _config = new KizShopConfiguration
            {
                EnableVisualUI = true,
                UIEffectId = 32001,
                UIKey = 32001,
                UIItemsPerPage = 6
            };

            _transactionService = new ShopTransactionService(_shopService, _economy, _inventory, _vehicle, () => _config);
            _uiGateway = new MockShopUiGateway();
            _uiService = new ShopUiService(_shopService, _economy, _transactionService, _uiGateway, () => _config);
        }

        public void Dispose()
        {
            _storage?.Dispose();
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public async Task OpenShopAsync_OpensUiAndRendersElements()
        {
            ulong steamId = 76561198000000001;
            await _uiService.OpenShopAsync(steamId);

            Assert.True(_uiService.IsUiOpen(steamId));
            Assert.Single(_uiGateway.OpenCalls);
            Assert.Equal((ushort)32001, _uiGateway.OpenCalls[0].effectId);

            Assert.True(_uiGateway.TextUpdates.ContainsKey("KizShop_Balance"));
            Assert.True(_uiGateway.TextUpdates.ContainsKey("KizShop_Page"));
            Assert.True(_uiGateway.TextUpdates.ContainsKey("KizShop_TabLabel"));
            Assert.Equal("Items", _uiGateway.TextUpdates["KizShop_TabLabel"]);
        }

        [Fact]
        public async Task CloseShop_ClosesUiAndRemovesState()
        {
            ulong steamId = 76561198000000002;
            await _uiService.OpenShopAsync(steamId);
            Assert.True(_uiService.IsUiOpen(steamId));

            _uiService.CloseShop(steamId);
            Assert.False(_uiService.IsUiOpen(steamId));
            Assert.Single(_uiGateway.CloseCalls);
        }

        [Fact]
        public async Task HandleButtonClicked_TabSwitch_UpdatesTabLabelAndSlots()
        {
            ulong steamId = 76561198000000003;
            await _uiService.OpenShopAsync(steamId);

            await _uiService.HandleButtonClickedAsync(steamId, "KizShop_Tab_Vehicles", _ => true, false);

            Assert.Equal("Vehicles", _uiGateway.TextUpdates["KizShop_TabLabel"]);

            await _uiService.HandleButtonClickedAsync(steamId, "KizShop_Tab_Items", _ => true, false);
            Assert.Equal("Items", _uiGateway.TextUpdates["KizShop_TabLabel"]);
        }

        [Fact]
        public async Task HandleButtonClicked_QuantityButtons_UpdatesQtyDisplay()
        {
            ulong steamId = 76561198000000004;
            await _uiService.OpenShopAsync(steamId);

            await _uiService.HandleButtonClickedAsync(steamId, "KizShop_Qty_5", _ => true, false);
            Assert.Equal("Qty: 5", _uiGateway.TextUpdates["KizShop_Qty"]);

            await _uiService.HandleButtonClickedAsync(steamId, "KizShop_Qty_10", _ => true, false);
            Assert.Equal("Qty: 10", _uiGateway.TextUpdates["KizShop_Qty"]);

            await _uiService.HandleButtonClickedAsync(steamId, "KizShop_Qty_1", _ => true, false);
            Assert.Equal("Qty: 1", _uiGateway.TextUpdates["KizShop_Qty"]);
        }

        [Fact]
        public async Task HandleButtonClicked_Pagination_ChangesPageNumber()
        {
            ulong steamId = 76561198000000005;
            await _uiService.OpenShopAsync(steamId);

            int totalPages = _uiService.GetTotalPages(ShopUiTab.Items, _config.UIItemsPerPage);

            if (totalPages > 1)
            {
                await _uiService.HandleButtonClickedAsync(steamId, "KizShop_Next", _ => true, false);
                Assert.Contains("Page 2", _uiGateway.TextUpdates["KizShop_Page"]);

                await _uiService.HandleButtonClickedAsync(steamId, "KizShop_Prev", _ => true, false);
                Assert.Contains("Page 1", _uiGateway.TextUpdates["KizShop_Page"]);
            }
        }

        [Fact]
        public async Task HandleButtonClicked_BuySlot_ExecutesBuyAndDeliversItem()
        {
            ulong steamId = 76561198000000006;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(1000m), "TestInit");
            await _uiService.OpenShopAsync(steamId);

            // Buy slot 0 (Medkit in default catalog, buy price is 40.00m)
            await _uiService.HandleButtonClickedAsync(steamId, "KizShop_Buy_0", _ => true, false);

            Assert.Single(_inventory.DeliveredItems);
            var bal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(960m, bal.ToDecimal());
        }

        [Fact]
        public async Task HandleButtonClicked_Close_ClosesUi()
        {
            ulong steamId = 76561198000000007;
            await _uiService.OpenShopAsync(steamId);

            await _uiService.HandleButtonClickedAsync(steamId, "KizShop_Close", _ => true, false);

            Assert.False(_uiService.IsUiOpen(steamId));
            Assert.Single(_uiGateway.CloseCalls);
        }
    }
}
