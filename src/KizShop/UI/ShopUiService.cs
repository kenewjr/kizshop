using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Shop;
using KizShop.Shop.Models;

namespace KizShop.UI
{
    public class ShopUiService
    {
        private readonly ShopService _shopService;
        private readonly EconomyService _economyService;
        private readonly ShopTransactionService _transactionService;
        private readonly IShopUiGateway _uiGateway;
        private readonly Func<KizShopConfiguration> _configProvider;
        private readonly ConcurrentDictionary<ulong, ShopUiPlayerState> _states =
            new ConcurrentDictionary<ulong, ShopUiPlayerState>();

        public ShopUiService(
            ShopService shopService,
            EconomyService economyService,
            ShopTransactionService transactionService,
            IShopUiGateway uiGateway,
            Func<KizShopConfiguration> configProvider)
        {
            _shopService = shopService ?? throw new ArgumentNullException(nameof(shopService));
            _economyService = economyService ?? throw new ArgumentNullException(nameof(economyService));
            _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
            _uiGateway = uiGateway ?? throw new ArgumentNullException(nameof(uiGateway));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        }

        public bool IsUiOpen(ulong steamId) => _states.ContainsKey(steamId);

        public async Task<bool> OpenShopAsync(ulong steamId)
        {
            var cfg = _configProvider();
            if (!cfg.EnableVisualUI) return false;

            bool opened = _uiGateway.OpenUi(steamId, cfg.UIEffectId, cfg.UIKey);
            if (!opened) return false;

            var state = _states.GetOrAdd(steamId, _ => new ShopUiPlayerState());
            await RenderUiAsync(steamId).ConfigureAwait(false);
            return true;
        }

        public void CloseShop(ulong steamId)
        {
            var cfg = _configProvider();
            _states.TryRemove(steamId, out _);
            _uiGateway.CloseUi(steamId, cfg.UIEffectId, cfg.UIKey);
        }

        public async Task HandleButtonClickedAsync(
            ulong steamId,
            string buttonName,
            Func<string, bool> hasPermission,
            bool isAdmin)
        {
            if (string.IsNullOrEmpty(buttonName) || !_states.TryGetValue(steamId, out var state))
            {
                return;
            }

            var cfg = _configProvider();

            if (buttonName.Equals("KizShop_Close", StringComparison.OrdinalIgnoreCase))
            {
                CloseShop(steamId);
                return;
            }

            if (buttonName.Equals("KizShop_Tab_Items", StringComparison.OrdinalIgnoreCase))
            {
                state.Tab = ShopUiTab.Items;
                state.Page = 1;
                await RenderUiAsync(steamId).ConfigureAwait(false);
                return;
            }

            if (buttonName.Equals("KizShop_Tab_Vehicles", StringComparison.OrdinalIgnoreCase))
            {
                state.Tab = ShopUiTab.Vehicles;
                state.Page = 1;
                await RenderUiAsync(steamId).ConfigureAwait(false);
                return;
            }

            if (buttonName.Equals("KizShop_Next", StringComparison.OrdinalIgnoreCase))
            {
                int maxPage = GetTotalPages(state.Tab, cfg.UIItemsPerPage);
                if (state.Page < maxPage)
                {
                    state.Page++;
                    await RenderUiAsync(steamId).ConfigureAwait(false);
                }
                return;
            }

            if (buttonName.Equals("KizShop_Prev", StringComparison.OrdinalIgnoreCase))
            {
                if (state.Page > 1)
                {
                    state.Page--;
                    await RenderUiAsync(steamId).ConfigureAwait(false);
                }
                return;
            }

            if (buttonName.Equals("KizShop_Qty_1", StringComparison.OrdinalIgnoreCase))
            {
                state.Quantity = 1;
                await RenderUiAsync(steamId).ConfigureAwait(false);
                return;
            }
            if (buttonName.Equals("KizShop_Qty_5", StringComparison.OrdinalIgnoreCase))
            {
                state.Quantity = 5;
                await RenderUiAsync(steamId).ConfigureAwait(false);
                return;
            }
            if (buttonName.Equals("KizShop_Qty_10", StringComparison.OrdinalIgnoreCase))
            {
                state.Quantity = 10;
                await RenderUiAsync(steamId).ConfigureAwait(false);
                return;
            }

            // Handle Buy slot button e.g. "KizShop_Buy_0"
            if (buttonName.StartsWith("KizShop_Buy_", StringComparison.OrdinalIgnoreCase))
            {
                string slotStr = buttonName.Substring("KizShop_Buy_".Length);
                if (int.TryParse(slotStr, out int slotIndex))
                {
                    await ProcessSlotActionAsync(steamId, slotIndex, isBuy: true, hasPermission, isAdmin).ConfigureAwait(false);
                }
                return;
            }

            // Handle Sell slot button e.g. "KizShop_Sell_0"
            if (buttonName.StartsWith("KizShop_Sell_", StringComparison.OrdinalIgnoreCase))
            {
                string slotStr = buttonName.Substring("KizShop_Sell_".Length);
                if (int.TryParse(slotStr, out int slotIndex))
                {
                    await ProcessSlotActionAsync(steamId, slotIndex, isBuy: false, hasPermission, isAdmin).ConfigureAwait(false);
                }
                return;
            }
        }

        private async Task ProcessSlotActionAsync(
            ulong steamId,
            int slotIndex,
            bool isBuy,
            Func<string, bool> hasPermission,
            bool isAdmin)
        {
            if (!_states.TryGetValue(steamId, out var state)) return;
            var cfg = _configProvider();

            int skip = (state.Page - 1) * cfg.UIItemsPerPage + slotIndex;

            if (state.Tab == ShopUiTab.Items)
            {
                var items = _shopService.GetItems().Where(i => i.Enabled).ToList();
                if (skip >= 0 && skip < items.Count)
                {
                    var item = items[skip];
                    if (isBuy)
                    {
                        await _transactionService.ExecuteBuyAsync(steamId, item.Id.ToString(), state.Quantity, hasPermission, isAdmin).ConfigureAwait(false);
                    }
                    else
                    {
                        await _transactionService.ExecuteSellAsync(steamId, item.Id.ToString(), state.Quantity.ToString(), hasPermission, isAdmin).ConfigureAwait(false);
                    }
                    await RenderUiAsync(steamId).ConfigureAwait(false);
                }
            }
            else
            {
                var vehicles = _shopService.GetVehicles().Where(v => v.Enabled).ToList();
                if (skip >= 0 && skip < vehicles.Count)
                {
                    var veh = vehicles[skip];
                    if (isBuy)
                    {
                        await _transactionService.ExecuteBuyVehicleAsync(steamId, veh.Id.ToString(), hasPermission, isAdmin).ConfigureAwait(false);
                    }
                    await RenderUiAsync(steamId).ConfigureAwait(false);
                }
            }
        }

        public async Task RenderUiAsync(ulong steamId)
        {
            if (!_states.TryGetValue(steamId, out var state)) return;
            var cfg = _configProvider();

            var bal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
            _uiGateway.UpdateText(steamId, cfg.UIKey, "KizShop_Balance", $"{cfg.CurrencySymbol}{bal.ToDecimal():N2}");
            _uiGateway.UpdateText(steamId, cfg.UIKey, "KizShop_Qty", $"Qty: {state.Quantity}");

            int totalPages = GetTotalPages(state.Tab, cfg.UIItemsPerPage);
            _uiGateway.UpdateText(steamId, cfg.UIKey, "KizShop_Page", $"Page {state.Page}/{Math.Max(1, totalPages)}");
            _uiGateway.UpdateText(steamId, cfg.UIKey, "KizShop_TabLabel", state.Tab == ShopUiTab.Items ? "Items" : "Vehicles");

            int pageSize = cfg.UIItemsPerPage;
            int skip = (state.Page - 1) * pageSize;

            if (state.Tab == ShopUiTab.Items)
            {
                var items = _shopService.GetItems().Where(i => i.Enabled).Skip(skip).Take(pageSize).ToList();
                for (int i = 0; i < pageSize; i++)
                {
                    if (i < items.Count)
                    {
                        var it = items[i];
                        _uiGateway.UpdateVisibility(steamId, cfg.UIKey, $"KizShop_Slot_{i}", true);
                        _uiGateway.UpdateText(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Name", it.Name);
                        _uiGateway.UpdateText(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Buy", $"Buy: {cfg.CurrencySymbol}{it.BuyPrice:N2}");
                        string sellText = it.SellPrice.HasValue ? $"Sell: {cfg.CurrencySymbol}{it.SellPrice.Value:N2}" : "Sell: N/A";
                        _uiGateway.UpdateText(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Sell", sellText);
                    }
                    else
                    {
                        _uiGateway.UpdateVisibility(steamId, cfg.UIKey, $"KizShop_Slot_{i}", false);
                    }
                }
            }
            else
            {
                var vehicles = _shopService.GetVehicles().Where(v => v.Enabled).Skip(skip).Take(pageSize).ToList();
                for (int i = 0; i < pageSize; i++)
                {
                    if (i < vehicles.Count)
                    {
                        var v = vehicles[i];
                        _uiGateway.UpdateVisibility(steamId, cfg.UIKey, $"KizShop_Slot_{i}", true);
                        _uiGateway.UpdateText(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Name", v.Name);
                        _uiGateway.UpdateText(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Buy", $"Buy: {cfg.CurrencySymbol}{v.BuyPrice:N2}");
                        _uiGateway.UpdateText(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Sell", "Vehicle");
                    }
                    else
                    {
                        _uiGateway.UpdateVisibility(steamId, cfg.UIKey, $"KizShop_Slot_{i}", false);
                    }
                }
            }
        }

        public int GetTotalPages(ShopUiTab tab, int pageSize)
        {
            if (pageSize <= 0) pageSize = 6;
            int count = tab == ShopUiTab.Items
                ? _shopService.GetItems().Count(i => i.Enabled)
                : _shopService.GetVehicles().Count(v => v.Enabled);

            return Math.Max(1, (int)Math.Ceiling((double)count / pageSize));
        }
    }
}
