using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rocket.Unturned.Player;
using Steamworks;
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
        private readonly Action<ulong, string, UnityEngine.Color> _feedbackSender;
        private readonly ConcurrentDictionary<ulong, ShopUiPlayerState> _states =
            new ConcurrentDictionary<ulong, ShopUiPlayerState>();

        public ShopUiService(
            ShopService shopService,
            EconomyService economyService,
            ShopTransactionService transactionService,
            IShopUiGateway uiGateway,
            Func<KizShopConfiguration> configProvider,
            Action<ulong, string, UnityEngine.Color> feedbackSender = null)
        {
            _shopService = shopService ?? throw new ArgumentNullException(nameof(shopService));
            _economyService = economyService ?? throw new ArgumentNullException(nameof(economyService));
            _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
            _uiGateway = uiGateway ?? throw new ArgumentNullException(nameof(uiGateway));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _feedbackSender = feedbackSender;
        }

        public bool IsUiOpen(ulong steamId) => _states.ContainsKey(steamId);

        public async Task<bool> OpenShopAsync(ulong steamId)
        {
            var cfg = _configProvider();
            if (!cfg.EnableVisualUI) return false;

            bool opened = await _uiGateway.OpenUiAsync(steamId, cfg.UIEffectId, cfg.UIKey).ConfigureAwait(false);
            if (!opened) return false;

            var state = _states.GetOrAdd(steamId, _ => new ShopUiPlayerState());
            await RenderUiAsync(steamId).ConfigureAwait(false);
            return true;
        }

        public async Task CloseShopAsync(ulong steamId)
        {
            var cfg = _configProvider();
            _states.TryRemove(steamId, out _);
            await _uiGateway.CloseUiAsync(steamId, cfg.UIEffectId, cfg.UIKey).ConfigureAwait(false);
        }

        public void CloseShop(ulong steamId)
        {
            var cfg = _configProvider();
            _states.TryRemove(steamId, out _);
            _ = _uiGateway.CloseUiAsync(steamId, cfg.UIEffectId, cfg.UIKey);
        }

        public void ClearAllStates()
        {
            var cfg = _configProvider();
            foreach (var steamId in _states.Keys)
            {
                _ = _uiGateway.CloseUiAsync(steamId, cfg.UIEffectId, cfg.UIKey);
            }
            _states.Clear();
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
                await CloseShopAsync(steamId).ConfigureAwait(false);
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
                    ShopTransactionResult res;
                    if (isBuy)
                    {
                        res = await _transactionService.ExecuteBuyAsync(steamId, item.Id.ToString(), state.Quantity, hasPermission, isAdmin).ConfigureAwait(false);
                        SendFeedback(steamId, res, isBuy: true, isVehicle: false);
                    }
                    else
                    {
                        res = await _transactionService.ExecuteSellAsync(steamId, item.Id.ToString(), state.Quantity.ToString(), hasPermission, isAdmin).ConfigureAwait(false);
                        SendFeedback(steamId, res, isBuy: false, isVehicle: false);
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
                        var res = await _transactionService.ExecuteBuyVehicleAsync(steamId, veh.Id.ToString(), hasPermission, isAdmin).ConfigureAwait(false);
                        SendFeedback(steamId, res, isBuy: true, isVehicle: true);
                    }
                    await RenderUiAsync(steamId).ConfigureAwait(false);
                }
            }
        }

        private void SendFeedback(ulong steamId, ShopTransactionResult result, bool isBuy, bool isVehicle)
        {
            var plugin = KizShopPlugin.Instance;
            string msg = null;
            var color = UnityEngine.Color.white;

            if (result.Success)
            {
                color = UnityEngine.Color.green;
                if (isVehicle)
                {
                    var costStr = plugin != null ? plugin.FormatMoney(result.Price) : result.Price.ToString();
                    msg = plugin != null ? plugin.Translate("vehicle_success", result.VehicleEntry.Name, costStr) : $"Purchased vehicle {result.VehicleEntry.Name}";
                }
                else if (isBuy)
                {
                    var costStr = plugin != null ? plugin.FormatMoney(result.Price) : result.Price.ToString();
                    var remStr = plugin != null ? plugin.FormatMoney(result.NewBalance) : result.NewBalance.ToString();
                    msg = plugin != null ? plugin.Translate("buy_success", result.Quantity, result.ItemEntry.Name, costStr, remStr) : $"Bought {result.Quantity}x {result.ItemEntry.Name}";
                }
                else
                {
                    var payoutStr = plugin != null ? plugin.FormatMoney(result.Price) : result.Price.ToString();
                    var balStr = plugin != null ? plugin.FormatMoney(result.NewBalance) : result.NewBalance.ToString();
                    msg = plugin != null ? plugin.Translate("sell_success", result.Quantity, result.ItemEntry.Name, payoutStr, balStr) : $"Sold {result.Quantity}x {result.ItemEntry.Name}";
                }
            }
            else if (result.CandidateNames != null && result.CandidateNames.Count > 0)
            {
                color = UnityEngine.Color.yellow;
                string candidates = string.Join(", ", result.CandidateNames);
                msg = plugin != null ? plugin.Translate("ambiguous_match", candidates) : $"Multiple matches: {candidates}";
            }
            else if (!string.IsNullOrEmpty(result.ErrorKey))
            {
                color = UnityEngine.Color.red;
                msg = plugin != null ? plugin.Translate(result.ErrorKey, result.ErrorArgs ?? new object[0]) : result.ErrorKey;
            }

            if (msg == null) return;

            if (_feedbackSender != null)
            {
                _feedbackSender(steamId, msg, color);
            }
            else if (plugin != null)
            {
                plugin.Dispatcher?.QueueOnMainThread(() =>
                {
                    var player = UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
                    if (player != null)
                    {
                        KizChat.Say(player, msg, color);
                    }
                });
            }
        }

        public async Task RenderUiAsync(ulong steamId)
        {
            if (!_states.TryGetValue(steamId, out var state)) return;
            var cfg = _configProvider();

            var bal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
            await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, "KizShop_Balance", $"{cfg.CurrencySymbol}{bal.ToDecimal():N2}").ConfigureAwait(false);
            await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, "KizShop_Qty", $"Qty: {state.Quantity}").ConfigureAwait(false);

            int totalPages = GetTotalPages(state.Tab, cfg.UIItemsPerPage);
            await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, "KizShop_Page", $"Page {state.Page}/{Math.Max(1, totalPages)}").ConfigureAwait(false);
            await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, "KizShop_TabLabel", state.Tab == ShopUiTab.Items ? "Items" : "Vehicles").ConfigureAwait(false);

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
                        await _uiGateway.UpdateVisibilityAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}", true).ConfigureAwait(false);
                        await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Name", it.Name).ConfigureAwait(false);
                        await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Buy", $"Buy: {cfg.CurrencySymbol}{it.BuyPrice:N2}").ConfigureAwait(false);
                        string sellText = it.SellPrice.HasValue ? $"Sell: {cfg.CurrencySymbol}{it.SellPrice.Value:N2}" : "Sell: N/A";
                        await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Sell", sellText).ConfigureAwait(false);
                    }
                    else
                    {
                        await _uiGateway.UpdateVisibilityAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}", false).ConfigureAwait(false);
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
                        await _uiGateway.UpdateVisibilityAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}", true).ConfigureAwait(false);
                        await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Name", v.Name).ConfigureAwait(false);
                        await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Buy", $"Buy: {cfg.CurrencySymbol}{v.BuyPrice:N2}").ConfigureAwait(false);
                        await _uiGateway.UpdateTextAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}_Sell", "Vehicle").ConfigureAwait(false);
                    }
                    else
                    {
                        await _uiGateway.UpdateVisibilityAsync(steamId, cfg.UIKey, $"KizShop_Slot_{i}", false).ConfigureAwait(false);
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
