using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SDG.Unturned;
using KizShop.Combat;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Shop.Gateways;
using KizShop.Shop.Models;
using KizShop.Storage;

namespace KizShop.Shop
{
    public class ShopTransactionResult
    {
        public bool Success { get; set; }
        public string ErrorKey { get; set; }
        public object[] ErrorArgs { get; set; }
        public ItemShopEntry ItemEntry { get; set; }
        public VehicleShopEntry VehicleEntry { get; set; }
        public int Quantity { get; set; }
        public Money Price { get; set; }
        public Money NewBalance { get; set; }
        public List<string> CandidateNames { get; set; }
        public string Note { get; set; }
    }

    public class ShopKitTransactionResult
    {
        public bool Success { get; set; }
        public string ErrorKey { get; set; }
        public object[] ErrorArgs { get; set; }
        public KitShopEntry KitEntry { get; set; }
        public Money Price { get; set; }
        public Money NewBalance { get; set; }
    }

    public class SellAllSummaryResult
    {
        public bool Success { get; set; }
        public string ErrorKey { get; set; }
        public object[] ErrorArgs { get; set; }
        public string Category { get; set; }
        public int TotalItemCount { get; set; }
        public int DistinctItemCount { get; set; }
        public Money ExpectedPayout { get; set; }
        public int SkippedGunsCount { get; set; }
        public int SkippedOtherCount { get; set; }
        public List<string> CandidateNames { get; set; }
    }

    public class SellAllPendingConfirmation
    {
        public string Category { get; set; }
        public DateTime CreatedUtc { get; set; }
        public Money ExpectedPayout { get; set; }
        public int ItemCount { get; set; }
        public Dictionary<ushort, int> ItemQuantities { get; set; }
    }

    public class ShopTransactionService
    {
        private readonly ShopService _shopService;
        private readonly EconomyService _economyService;
        private readonly IInventoryGateway _inventoryGateway;
        private readonly IVehicleGateway _vehicleGateway;
        private readonly IClock _clock;
        private readonly Func<KizShopConfiguration> _configProvider;
        private readonly ICombatTagService _combatTagService;
        private readonly IKizStorage _storage;

        private readonly ConcurrentDictionary<ulong, DateTime> _vehicleCooldowns = new ConcurrentDictionary<ulong, DateTime>();
        private readonly ConcurrentDictionary<(ulong, string), DateTime> _kitCooldowns = new ConcurrentDictionary<(ulong, string), DateTime>();
        private readonly ConcurrentDictionary<ulong, SellAllPendingConfirmation> _pendingSellAll = new ConcurrentDictionary<ulong, SellAllPendingConfirmation>();
        private readonly ConcurrentDictionary<ushort, SemaphoreSlim> _priceSemaphores = new ConcurrentDictionary<ushort, SemaphoreSlim>();

        public event Action<ulong, ItemShopEntry, int, Money, Money> OnItemPurchased;
        public event Action<ulong, ItemShopEntry, int, Money, Money> OnItemSold;
        public event Action<ulong, VehicleShopEntry, Money, Money> OnVehiclePurchased;
        public event Action<ulong, KitShopEntry, Money, Money> OnKitPurchased;

        public ShopTransactionService(
            ShopService shopService,
            EconomyService economyService,
            IInventoryGateway inventoryGateway,
            IVehicleGateway vehicleGateway,
            Func<KizShopConfiguration> configProvider,
            IClock clock = null,
            ICombatTagService combatTagService = null,
            IKizStorage storage = null)
        {
            _shopService = shopService ?? throw new ArgumentNullException(nameof(shopService));
            _economyService = economyService ?? throw new ArgumentNullException(nameof(economyService));
            _inventoryGateway = inventoryGateway ?? throw new ArgumentNullException(nameof(inventoryGateway));
            _vehicleGateway = vehicleGateway ?? throw new ArgumentNullException(nameof(vehicleGateway));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _clock = clock ?? SystemClock.Instance;
            _combatTagService = combatTagService;
            _storage = storage;
        }

        public void ClearPlayerState(ulong steamId)
        {
            _pendingSellAll.TryRemove(steamId, out _);
            _vehicleCooldowns.TryRemove(steamId, out _);
            foreach (var key in _kitCooldowns.Keys)
            {
                if (key.Item1 == steamId)
                {
                    _kitCooldowns.TryRemove(key, out _);
                }
            }
            PlayerLock.Release(steamId);
        }

        private bool IsCombatBlocked(ulong steamId, bool blocksAction, Func<string, bool> hasPermission, bool isAdmin, out int secondsLeft)
        {
            secondsLeft = 0;
            var cfg = _configProvider();
            if (!cfg.CombatTagEnabled || !blocksAction || isAdmin || _combatTagService == null)
            {
                return false;
            }

            if (hasPermission != null && hasPermission("kizshop.bypass.combattag"))
            {
                return false;
            }

            return _combatTagService.IsTagged(steamId, out secondsLeft);
        }

        public async Task<(Money TotalPrice, Money FirstUnitPrice)> GetEffectiveBuyPriceAsync(ItemShopEntry entry, int quantity)
        {
            var cfg = _configProvider();
            bool dynamicActive = (entry.DynamicPricing != null && entry.DynamicPricing.Enabled) || (entry.DynamicPricing == null && cfg.DynamicPricingDefaultEnabled);
            if (_storage == null || !dynamicActive)
            {
                long unitCents = (long)Math.Round(entry.BuyPrice * 100m, MidpointRounding.AwayFromZero);
                return (Money.FromCents(unitCents * quantity), Money.FromCents(unitCents));
            }

            var dynCfg = entry.DynamicPricing ?? new ItemDynamicPricingConfig { Enabled = true };
            var state = await _storage.GetItemPriceStateAsync(entry.Id).ConfigureAwait(false);
            decimal recSell = PriceCalculator.ComputeRecoveredPressure(state.SellPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
            decimal recBuy = PriceCalculator.ComputeRecoveredPressure(state.BuyPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);

            return PriceCalculator.CalculateSequentialBuyPrice(entry.BuyPrice, entry.SellPrice ?? 0m, dynCfg, recBuy, recSell, quantity, cfg.AllowSellAboveBuy);
        }

        public async Task<(Money TotalPrice, Money FirstUnitPrice)> GetEffectiveSellPriceAsync(ItemShopEntry entry, int quantity)
        {
            var cfg = _configProvider();
            decimal baseSell = entry.SellPrice ?? 0m;
            bool dynamicActive = (entry.DynamicPricing != null && entry.DynamicPricing.Enabled) || (entry.DynamicPricing == null && cfg.DynamicPricingDefaultEnabled);
            if (_storage == null || !dynamicActive)
            {
                long unitCents = (long)Math.Round(baseSell * 100m, MidpointRounding.AwayFromZero);
                return (Money.FromCents(unitCents * quantity), Money.FromCents(unitCents));
            }

            var dynCfg = entry.DynamicPricing ?? new ItemDynamicPricingConfig { Enabled = true };
            var state = await _storage.GetItemPriceStateAsync(entry.Id).ConfigureAwait(false);
            decimal recSell = PriceCalculator.ComputeRecoveredPressure(state.SellPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
            decimal recBuy = PriceCalculator.ComputeRecoveredPressure(state.BuyPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);

            return PriceCalculator.CalculateSequentialSellPrice(entry.BuyPrice, baseSell, dynCfg, recBuy, recSell, quantity, cfg.AllowSellAboveBuy);
        }

        private void SafeRaiseItemPurchased(ulong steamId, ItemShopEntry entry, int quantity, Money totalMoney, Money newBal)
        {
            var handlers = OnItemPurchased?.GetInvocationList();
            if (handlers == null) return;
            foreach (var handler in handlers)
            {
                try
                {
                    ((Action<ulong, ItemShopEntry, int, Money, Money>)handler)(steamId, entry, quantity, totalMoney, newBal);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Error in OnItemPurchased subscriber: {ex}");
                }
            }
        }

        private void SafeRaiseItemSold(ulong steamId, ItemShopEntry entry, int quantity, Money totalPayout, Money newBal)
        {
            var handlers = OnItemSold?.GetInvocationList();
            if (handlers == null) return;
            foreach (var handler in handlers)
            {
                try
                {
                    ((Action<ulong, ItemShopEntry, int, Money, Money>)handler)(steamId, entry, quantity, totalPayout, newBal);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Error in OnItemSold subscriber: {ex}");
                }
            }
        }

        private void SafeRaiseVehiclePurchased(ulong steamId, VehicleShopEntry entry, Money cost, Money newBal)
        {
            var handlers = OnVehiclePurchased?.GetInvocationList();
            if (handlers == null) return;
            foreach (var handler in handlers)
            {
                try
                {
                    ((Action<ulong, VehicleShopEntry, Money, Money>)handler)(steamId, entry, cost, newBal);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Error in OnVehiclePurchased subscriber: {ex}");
                }
            }
        }

        private async Task UpdatePressureAfterTransactionAsync(ushort itemId, decimal sellDelta, decimal buyDelta, decimal recoveryPerHour)
        {
            if (_storage == null) return;
            try
            {
                var sem = _priceSemaphores.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
                await sem.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _storage.UpdateItemPricePressureAsync(itemId, sellDelta, buyDelta, _clock.UtcNow, recoveryPerHour).ConfigureAwait(false);
                }
                finally
                {
                    sem.Release();
                }
            }
            catch (Exception ex)
            {
                KizLog.Error($"[KizShop] Error updating price pressure for item {itemId}: {ex}");
            }
        }

        public async Task<ShopTransactionResult> ExecuteBuyAsync(
            ulong steamId,
            string query,
            int quantity,
            Func<string, bool> hasPermission,
            bool isAdmin)
        {
            if (quantity <= 0)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "buy_invalid_amount" };
            }

            var cfg = _configProvider();
            if (IsCombatBlocked(steamId, cfg.CombatTagBlocksBuy, hasPermission, isAdmin, out int secondsLeft))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "combat_tagged", ErrorArgs = new object[] { secondsLeft } };
            }

            var lookup = _shopService.ResolveItem(query, enabledOnly: true);
            if (lookup.Status == LookupStatus.NotFound)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "buy_item_not_found", ErrorArgs = new object[] { query } };
            }
            if (lookup.Status == LookupStatus.Ambiguous)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "ambiguous_match", CandidateNames = lookup.CandidateNames };
            }

            var entry = lookup.Entry;
            if (!entry.CanBuy)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "buy_item_disabled" };
            }

            if (!string.IsNullOrEmpty(entry.Permission) && !isAdmin && !hasPermission(entry.Permission))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "buy_no_permission" };
            }

            if (entry.MaxAmountPerPurchase.HasValue && entry.MaxAmountPerPurchase.Value > 0 && quantity > entry.MaxAmountPerPurchase.Value)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "buy_max_amount", ErrorArgs = new object[] { entry.MaxAmountPerPurchase.Value } };
            }

            var (totalMoney, _) = await GetEffectiveBuyPriceAsync(entry, quantity).ConfigureAwait(false);
            if (totalMoney.Cents <= 0 || (cfg.MaxTransactionAmount > 0 && totalMoney.ToDecimal() > cfg.MaxTransactionAmount))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "invalid_money_amount", ErrorArgs = new object[] { "Amount exceeds limits" } };
            }

            if (!PlayerLock.TryAcquire(steamId))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "transaction_in_flight" };
            }

            bool delivered = false;
            var deliveredItems = new System.Collections.Generic.List<SDG.Unturned.Item>();
            WithdrawResult withdrawRes = null;

            try
            {
                var spaceRes = await _inventoryGateway.CheckSpaceAsync(steamId, entry.Id, quantity).ConfigureAwait(false);
                if (!spaceRes.Fits)
                {
                    string errKey;
                    object[] errArgs = null;
                    switch (spaceRes.Reason)
                    {
                        case CheckSpaceReason.PlayerNull:
                        case CheckSpaceReason.InventoryNull:
                            errKey = "buy_player_unavailable";
                            break;
                        case CheckSpaceReason.AssetNotFound:
                            errKey = "buy_item_unknown";
                            errArgs = new object[] { entry.Id };
                            break;
                        case CheckSpaceReason.NoSpace:
                        default:
                            errKey = "buy_no_space";
                            errArgs = new object[] { quantity, entry.Name };
                            break;
                    }
                    return new ShopTransactionResult { Success = false, ErrorKey = errKey, ErrorArgs = errArgs };
                }

                withdrawRes = await _economyService.TryWithdrawAsync(steamId, totalMoney, $"ShopBuy:{entry.Id}x{quantity}").ConfigureAwait(false);
                if (!withdrawRes.Success)
                {
                    var curBal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
                    return new ShopTransactionResult
                    {
                        Success = false,
                        ErrorKey = "buy_insufficient_funds",
                        ErrorArgs = new object[] { totalMoney, entry.Name, curBal }
                    };
                }

                bool deliverySucceeded = true;
                bool autoEquip = cfg.AutoEquipPurchasedItems;
                for (int i = 0; i < quantity; i++)
                {
                    var item = await _inventoryGateway.CreateItemAsync(entry.Id).ConfigureAwait(false);
                    if (item == null)
                    {
                        deliverySucceeded = false;
                        break;
                    }

                    bool added = await _inventoryGateway.TryAddItemAsync(steamId, item, autoEquip).ConfigureAwait(false);
                    if (!added)
                    {
                        deliverySucceeded = false;
                        break;
                    }

                    deliveredItems.Add(item);
                }

                if (!deliverySucceeded)
                {
                    foreach (var deliveredItem in deliveredItems)
                    {
                        await _inventoryGateway.RemoveItemByReferenceAsync(steamId, deliveredItem).ConfigureAwait(false);
                    }
                    await _economyService.DepositAsync(steamId, totalMoney, $"Refund:ShopBuyFail:{entry.Id}x{quantity}", ignoreMaxBalance: true).ConfigureAwait(false);
                    KizLog.Warning($"[KizShop] Buy delivery failed for {steamId} ({entry.Id}x{quantity}). Refunded {totalMoney}.");
                    return new ShopTransactionResult { Success = false, ErrorKey = "buy_failed_refunded" };
                }

                delivered = true;

                // Post-delivery step: pressure update
                var dynCfg = entry.DynamicPricing ?? new ItemDynamicPricingConfig { Enabled = cfg.DynamicPricingDefaultEnabled };
                if (dynCfg.Enabled && dynCfg.BuyIncreasePerUnit > 0m)
                {
                    try
                    {
                        await UpdatePressureAfterTransactionAsync(entry.Id, sellDelta: 0, buyDelta: quantity, dynCfg.RecoveryPerHour).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        KizLog.Error($"[KizShop] Post-delivery pressure update failed for {entry.Id}: {ex}");
                    }
                }

                // Balance read with fallback to withdrawRes.NewBalance
                Money newBal = withdrawRes.NewBalance;
                try
                {
                    newBal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Failed to fetch balance after delivery for {steamId}: {ex}");
                }

                // Event subscribers invoked safely one by one
                try
                {
                    SafeRaiseItemPurchased(steamId, entry, quantity, totalMoney, newBal);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Post-delivery event invocation failed: {ex}");
                }

                return new ShopTransactionResult
                {
                    Success = true,
                    ItemEntry = entry,
                    Quantity = quantity,
                    Price = totalMoney,
                    NewBalance = newBal
                };
            }
            catch (Exception ex)
            {
                KizLog.Error($"[KizShop] Unexpected exception in ExecuteBuyAsync for {steamId}: {ex}");
                if (!delivered)
                {
                    foreach (var deliveredItem in deliveredItems)
                    {
                        try
                        {
                            await _inventoryGateway.RemoveItemByReferenceAsync(steamId, deliveredItem).ConfigureAwait(false);
                        }
                        catch { }
                    }

                    if (withdrawRes != null && withdrawRes.Success)
                    {
                        await _economyService.DepositAsync(steamId, totalMoney, $"Refund:Exception:{entry.Id}", ignoreMaxBalance: true).ConfigureAwait(false);
                    }
                    return new ShopTransactionResult { Success = false, ErrorKey = "buy_failed_refunded" };
                }

                // If delivered is true, NEVER refund. Return SUCCESS with last known balance.
                Money fallbackBal = withdrawRes?.NewBalance ?? Money.Zero;
                return new ShopTransactionResult
                {
                    Success = true,
                    ItemEntry = entry,
                    Quantity = quantity,
                    Price = totalMoney,
                    NewBalance = fallbackBal
                };
            }
            finally
            {
                PlayerLock.Release(steamId);
            }
        }

        public async Task<ShopTransactionResult> ExecuteSellAsync(
            ulong steamId,
            string query,
            string amountArg,
            Func<string, bool> hasPermission,
            bool isAdmin)
        {
            var cfg = _configProvider();
            if (IsCombatBlocked(steamId, cfg.CombatTagBlocksSell, hasPermission, isAdmin, out int secondsLeft))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "combat_tagged", ErrorArgs = new object[] { secondsLeft } };
            }

            var lookup = _shopService.ResolveItem(query, enabledOnly: true);
            if (lookup.Status == LookupStatus.NotFound)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_item_not_found", ErrorArgs = new object[] { query } };
            }
            if (lookup.Status == LookupStatus.Ambiguous)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "ambiguous_match", CandidateNames = lookup.CandidateNames };
            }

            var entry = lookup.Entry;
            if (!entry.CanSell || !entry.SellPrice.HasValue || entry.SellPrice.Value <= 0)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_item_disabled" };
            }

            if (!string.IsNullOrEmpty(entry.Permission) && !isAdmin && !hasPermission(entry.Permission))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_no_permission" };
            }

            var available = await _inventoryGateway.GetGearItemsAsync(steamId, entry.Id).ConfigureAwait(false);
            if (available.Count == 0)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_not_enough_items", ErrorArgs = new object[] { 0, entry.Name, 1 } };
            }

            foreach (var instance in available)
            {
                if (await _inventoryGateway.HasAttachmentsOrLoadedMagazineAsync(entry.Id, instance.State).ConfigureAwait(false))
                {
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_gun_has_attachments" };
                }
            }

            int amountToSell = 1;
            if (string.Equals(amountArg, "all", StringComparison.OrdinalIgnoreCase))
            {
                amountToSell = available.Count;
            }
            else if (!string.IsNullOrEmpty(amountArg))
            {
                if (!int.TryParse(amountArg, out amountToSell) || amountToSell <= 0)
                {
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_usage" };
                }
            }

            if (available.Count < amountToSell)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_not_enough_items", ErrorArgs = new object[] { available.Count, entry.Name, amountToSell } };
            }

            var toSell = available.Take(amountToSell).ToList();
            byte defaultAmount = await _inventoryGateway.GetDefaultAmountAsync(entry.Id).ConfigureAwait(false);

            // Fetch dynamic price state
            decimal startingSellPressure = 0m;
            decimal startingBuyPressure = 0m;
            var dynCfg = entry.DynamicPricing ?? new ItemDynamicPricingConfig { Enabled = cfg.DynamicPricingDefaultEnabled };
            if (_storage != null && dynCfg.Enabled)
            {
                var state = await _storage.GetItemPriceStateAsync(entry.Id).ConfigureAwait(false);
                startingSellPressure = PriceCalculator.ComputeRecoveredPressure(state.SellPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
                startingBuyPressure = PriceCalculator.ComputeRecoveredPressure(state.BuyPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
            }

            long totalPayoutCents = 0;
            decimal curSellPressure = startingSellPressure;
            foreach (var inst in toSell)
            {
                decimal amountRatio = (defaultAmount > 1) ? ((decimal)inst.Amount / defaultAmount) : 1.0m;
                decimal qualityRatio = cfg.ScaleSellByQuality ? (inst.Quality / 100m) : 1.0m;

                var singleUnitMoney = PriceCalculator.CalculateSingleUnitSellPrice(
                    entry.BuyPrice,
                    entry.SellPrice.Value,
                    dynCfg,
                    startingBuyPressure,
                    curSellPressure,
                    cfg.AllowSellAboveBuy);

                decimal instanceVal = singleUnitMoney.ToDecimal() * amountRatio * qualityRatio;
                long cents = (long)Math.Round(instanceVal * 100m, MidpointRounding.AwayFromZero);
                totalPayoutCents += cents;

                if (dynCfg.Enabled) curSellPressure += 1.0m;
            }

            var totalPayout = Money.FromCents(totalPayoutCents);

            if (!PlayerLock.TryAcquire(steamId))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "transaction_in_flight" };
            }

            DepositResult depositRes = null;
            bool payoutDone = false;
            try
            {
                var removedInstances = new List<InventoryItemInstance>();
                foreach (var inst in toSell)
                {
                    if (await _inventoryGateway.RemoveItemByReferenceAsync(steamId, inst.Item).ConfigureAwait(false))
                    {
                        removedInstances.Add(inst);
                    }
                }

                if (removedInstances.Count < amountToSell)
                {
                    foreach (var removed in removedInstances)
                    {
                        await _inventoryGateway.TryAddItemAsync(steamId, removed.Item).ConfigureAwait(false);
                    }
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_failed" };
                }

                depositRes = await _economyService.DepositAsync(steamId, totalPayout, $"ShopSell:{entry.Id}x{amountToSell}").ConfigureAwait(false);
                if (!depositRes.Success)
                {
                    foreach (var removed in removedInstances)
                    {
                        await _inventoryGateway.TryAddItemAsync(steamId, removed.Item).ConfigureAwait(false);
                    }
                    KizLog.Error($"[KizShop] Payout failed for {steamId} selling {entry.Id}x{amountToSell}. Items restored.");
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_payout_failed" };
                }

                payoutDone = true;

                if (dynCfg.Enabled)
                {
                    try
                    {
                        await UpdatePressureAfterTransactionAsync(entry.Id, sellDelta: amountToSell, buyDelta: 0, dynCfg.RecoveryPerHour).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        KizLog.Error($"[KizShop] Post-sell pressure update failed for {entry.Id}: {ex}");
                    }
                }

                try
                {
                    SafeRaiseItemSold(steamId, entry, amountToSell, totalPayout, depositRes.NewBalance);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Post-sell event invocation failed: {ex}");
                }

                return new ShopTransactionResult
                {
                    Success = true,
                    ItemEntry = entry,
                    Quantity = amountToSell,
                    Price = totalPayout,
                    NewBalance = depositRes.NewBalance
                };
            }
            catch (Exception ex)
            {
                KizLog.Error($"[KizShop] Unexpected exception in ExecuteSellAsync for {steamId}: {ex}");
                if (payoutDone)
                {
                    return new ShopTransactionResult
                    {
                        Success = true,
                        ItemEntry = entry,
                        Quantity = amountToSell,
                        Price = totalPayout,
                        NewBalance = depositRes?.NewBalance ?? Money.Zero
                    };
                }
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_failed" };
            }
            finally
            {
                PlayerLock.Release(steamId);
            }
        }

        public async Task<ShopTransactionResult> ExecuteSellHandAsync(
            ulong steamId,
            Func<string, bool> hasPermission,
            bool isAdmin,
            bool hadAmountArgument = false)
        {
            var cfg = _configProvider();
            if (IsCombatBlocked(steamId, cfg.CombatTagBlocksSell, hasPermission, isAdmin, out int secondsLeft))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "combat_tagged", ErrorArgs = new object[] { secondsLeft } };
            }

            var equipped = await _inventoryGateway.GetEquippedItemAsync(steamId).ConfigureAwait(false);
            if (equipped == null || equipped.Item == null)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_hand_empty" };
            }

            var entry = _shopService.GetItems().FirstOrDefault(i => i.Id == equipped.ItemId);
            if (entry == null || !entry.CanSell || !entry.SellPrice.HasValue || entry.SellPrice.Value <= 0)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_hand_not_sellable" };
            }

            if (!string.IsNullOrEmpty(entry.Permission) && !isAdmin && !hasPermission(entry.Permission))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_no_permission" };
            }

            if (await _inventoryGateway.HasAttachmentsOrLoadedMagazineAsync(entry.Id, equipped.State).ConfigureAwait(false))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_gun_has_attachments" };
            }

            var dynCfg = entry.DynamicPricing ?? new ItemDynamicPricingConfig { Enabled = cfg.DynamicPricingDefaultEnabled };
            decimal sellPressure = 0m;
            decimal buyPressure = 0m;
            if (_storage != null && dynCfg.Enabled)
            {
                var state = await _storage.GetItemPriceStateAsync(entry.Id).ConfigureAwait(false);
                sellPressure = PriceCalculator.ComputeRecoveredPressure(state.SellPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
                buyPressure = PriceCalculator.ComputeRecoveredPressure(state.BuyPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
            }

            byte defaultAmount = await _inventoryGateway.GetDefaultAmountAsync(entry.Id).ConfigureAwait(false);
            decimal amountRatio = (defaultAmount > 1) ? ((decimal)equipped.Amount / defaultAmount) : 1.0m;
            decimal qualityRatio = cfg.ScaleSellByQuality ? (equipped.Quality / 100m) : 1.0m;

            var singleUnitMoney = PriceCalculator.CalculateSingleUnitSellPrice(entry.BuyPrice, entry.SellPrice.Value, dynCfg, buyPressure, sellPressure, cfg.AllowSellAboveBuy);
            decimal finalDec = singleUnitMoney.ToDecimal() * amountRatio * qualityRatio;
            var payout = Money.FromCents((long)Math.Round(finalDec * 100m, MidpointRounding.AwayFromZero));

            if (!PlayerLock.TryAcquire(steamId))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "transaction_in_flight" };
            }

            DepositResult dep = null;
            bool payoutDone = false;
            try
            {
                bool removed = await _inventoryGateway.RemoveItemByReferenceAsync(steamId, equipped.Item).ConfigureAwait(false);
                if (!removed)
                {
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_failed" };
                }

                dep = await _economyService.DepositAsync(steamId, payout, $"SellHand:{entry.Id}").ConfigureAwait(false);
                if (!dep.Success)
                {
                    await _inventoryGateway.TryAddItemAsync(steamId, equipped.Item).ConfigureAwait(false);
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_payout_failed" };
                }

                payoutDone = true;

                if (dynCfg.Enabled)
                {
                    try
                    {
                        await UpdatePressureAfterTransactionAsync(entry.Id, sellDelta: 1, buyDelta: 0, dynCfg.RecoveryPerHour).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        KizLog.Error($"[KizShop] Post-sell pressure update failed for {entry.Id}: {ex}");
                    }
                }

                try
                {
                    SafeRaiseItemSold(steamId, entry, 1, payout, dep.NewBalance);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Post-sell event invocation failed: {ex}");
                }

                return new ShopTransactionResult
                {
                    Success = true,
                    ItemEntry = entry,
                    Quantity = 1,
                    Price = payout,
                    NewBalance = dep.NewBalance,
                    Note = hadAmountArgument ? "sell_hand_amount_ignored" : null
                };
            }
            catch (Exception ex)
            {
                KizLog.Error($"[KizShop] Unexpected exception in ExecuteSellHandAsync for {steamId}: {ex}");
                if (payoutDone)
                {
                    return new ShopTransactionResult
                    {
                        Success = true,
                        ItemEntry = entry,
                        Quantity = 1,
                        Price = payout,
                        NewBalance = dep?.NewBalance ?? Money.Zero,
                        Note = hadAmountArgument ? "sell_hand_amount_ignored" : null
                    };
                }
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_failed" };
            }
            finally
            {
                PlayerLock.Release(steamId);
            }
        }

        public async Task<SellAllSummaryResult> ExecuteSellAllCategorySummaryAsync(
            ulong steamId,
            string categoryQuery,
            Func<string, bool> hasPermission,
            bool isAdmin)
        {
            var cfg = _configProvider();
            if (IsCombatBlocked(steamId, cfg.CombatTagBlocksSell, hasPermission, isAdmin, out int secondsLeft))
            {
                return new SellAllSummaryResult { Success = false, ErrorKey = "combat_tagged", ErrorArgs = new object[] { secondsLeft } };
            }

            if (!_shopService.ResolveCategory(categoryQuery, out string matchedCategory, out var candidates))
            {
                if (candidates != null && candidates.Count > 1)
                {
                    return new SellAllSummaryResult { Success = false, ErrorKey = "ambiguous_category", CandidateNames = candidates };
                }
                return new SellAllSummaryResult { Success = false, ErrorKey = "category_not_found", ErrorArgs = new object[] { categoryQuery } };
            }

            var allGear = await _inventoryGateway.GetAllGearItemsAsync(steamId).ConfigureAwait(false);
            var catalogItems = _shopService.GetItems(enabledOnly: true)
                .Where(i => i.CanSell && i.Category.Equals(matchedCategory, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(i => i.Id);

            int skippedGuns = 0;
            int skippedOther = 0;
            var eligibleItems = new List<InventoryItemInstance>();

            foreach (var inst in allGear)
            {
                if (!catalogItems.TryGetValue(inst.ItemId, out var entry))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.Permission) && !isAdmin && !hasPermission(entry.Permission))
                {
                    skippedOther++;
                    continue;
                }

                if (await _inventoryGateway.HasAttachmentsOrLoadedMagazineAsync(inst.ItemId, inst.State).ConfigureAwait(false))
                {
                    skippedGuns++;
                    continue;
                }

                eligibleItems.Add(inst);
            }

            if (eligibleItems.Count == 0)
            {
                return new SellAllSummaryResult
                {
                    Success = false,
                    ErrorKey = "sell_all_no_items",
                    Category = matchedCategory,
                    SkippedGunsCount = skippedGuns,
                    SkippedOtherCount = skippedOther
                };
            }

            // Calculate sequential dynamic pricing across grouped items
            long totalExpectedCents = 0;
            var itemQuantities = new Dictionary<ushort, int>();
            var grouped = eligibleItems.GroupBy(x => x.ItemId);

            foreach (var grp in grouped)
            {
                ushort itemId = grp.Key;
                var entry = catalogItems[itemId];
                int count = grp.Count();
                itemQuantities[itemId] = count;

                byte defAmount = await _inventoryGateway.GetDefaultAmountAsync(itemId).ConfigureAwait(false);
                var dynCfg = entry.DynamicPricing ?? new ItemDynamicPricingConfig { Enabled = cfg.DynamicPricingDefaultEnabled };

                decimal sellPressure = 0m;
                decimal buyPressure = 0m;
                if (_storage != null && dynCfg.Enabled)
                {
                    var state = await _storage.GetItemPriceStateAsync(itemId).ConfigureAwait(false);
                    sellPressure = PriceCalculator.ComputeRecoveredPressure(state.SellPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
                    buyPressure = PriceCalculator.ComputeRecoveredPressure(state.BuyPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
                }

                decimal curSell = sellPressure;
                foreach (var inst in grp)
                {
                    decimal amountRatio = (defAmount > 1) ? ((decimal)inst.Amount / defAmount) : 1.0m;
                    decimal qualityRatio = cfg.ScaleSellByQuality ? (inst.Quality / 100m) : 1.0m;

                    var unitMoney = PriceCalculator.CalculateSingleUnitSellPrice(entry.BuyPrice, entry.SellPrice.Value, dynCfg, buyPressure, curSell, cfg.AllowSellAboveBuy);
                    decimal finalDec = unitMoney.ToDecimal() * amountRatio * qualityRatio;
                    totalExpectedCents += (long)Math.Round(finalDec * 100m, MidpointRounding.AwayFromZero);

                    if (dynCfg.Enabled) curSell += 1.0m;
                }
            }

            var totalMoney = Money.FromCents(totalExpectedCents);
            _pendingSellAll[steamId] = new SellAllPendingConfirmation
            {
                Category = matchedCategory,
                CreatedUtc = _clock.UtcNow,
                ExpectedPayout = totalMoney,
                ItemCount = eligibleItems.Count,
                ItemQuantities = itemQuantities
            };

            return new SellAllSummaryResult
            {
                Success = true,
                Category = matchedCategory,
                TotalItemCount = eligibleItems.Count,
                DistinctItemCount = itemQuantities.Count,
                ExpectedPayout = totalMoney,
                SkippedGunsCount = skippedGuns,
                SkippedOtherCount = skippedOther
            };
        }

        public async Task<ShopTransactionResult> ExecuteSellAllCategoryConfirmAsync(
            ulong steamId,
            Func<string, bool> hasPermission,
            bool isAdmin)
        {
            var cfg = _configProvider();
            if (IsCombatBlocked(steamId, cfg.CombatTagBlocksSell, hasPermission, isAdmin, out int secondsLeft))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "combat_tagged", ErrorArgs = new object[] { secondsLeft } };
            }

            if (!_pendingSellAll.TryGetValue(steamId, out var conf))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_all_no_pending" };
            }

            if ((_clock.UtcNow - conf.CreatedUtc).TotalSeconds > 30.0)
            {
                _pendingSellAll.TryRemove(steamId, out _);
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_all_expired" };
            }

            // Recompute inventory items and payout
            var allGear = await _inventoryGateway.GetAllGearItemsAsync(steamId).ConfigureAwait(false);
            var catalogItems = _shopService.GetItems(enabledOnly: true)
                .Where(i => i.CanSell && i.Category.Equals(conf.Category, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(i => i.Id);

            var eligibleItems = new List<InventoryItemInstance>();
            foreach (var inst in allGear)
            {
                if (!catalogItems.TryGetValue(inst.ItemId, out var entry)) continue;
                if (!string.IsNullOrEmpty(entry.Permission) && !isAdmin && !hasPermission(entry.Permission)) continue;
                if (await _inventoryGateway.HasAttachmentsOrLoadedMagazineAsync(inst.ItemId, inst.State).ConfigureAwait(false)) continue;
                eligibleItems.Add(inst);
            }

            long recomputedCents = 0;
            var itemCounts = new Dictionary<ushort, int>();
            foreach (var grp in eligibleItems.GroupBy(x => x.ItemId))
            {
                ushort itemId = grp.Key;
                var entry = catalogItems[itemId];
                itemCounts[itemId] = grp.Count();
                byte defAmount = await _inventoryGateway.GetDefaultAmountAsync(itemId).ConfigureAwait(false);
                var dynCfg = entry.DynamicPricing ?? new ItemDynamicPricingConfig { Enabled = cfg.DynamicPricingDefaultEnabled };

                decimal sellPressure = 0m;
                decimal buyPressure = 0m;
                if (_storage != null && dynCfg.Enabled)
                {
                    var state = await _storage.GetItemPriceStateAsync(itemId).ConfigureAwait(false);
                    sellPressure = PriceCalculator.ComputeRecoveredPressure(state.SellPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
                    buyPressure = PriceCalculator.ComputeRecoveredPressure(state.BuyPressure, state.LastUpdatedUtc, _clock.UtcNow, dynCfg.RecoveryPerHour);
                }

                decimal curSell = sellPressure;
                foreach (var inst in grp)
                {
                    decimal amountRatio = (defAmount > 1) ? ((decimal)inst.Amount / defAmount) : 1.0m;
                    decimal qualityRatio = cfg.ScaleSellByQuality ? (inst.Quality / 100m) : 1.0m;
                    var unitMoney = PriceCalculator.CalculateSingleUnitSellPrice(entry.BuyPrice, entry.SellPrice.Value, dynCfg, buyPressure, curSell, cfg.AllowSellAboveBuy);
                    decimal finalDec = unitMoney.ToDecimal() * amountRatio * qualityRatio;
                    recomputedCents += (long)Math.Round(finalDec * 100m, MidpointRounding.AwayFromZero);
                    if (dynCfg.Enabled) curSell += 1.0m;
                }
            }

            var recomputedPayout = Money.FromCents(recomputedCents);
            if (recomputedPayout.Cents != conf.ExpectedPayout.Cents)
            {
                _pendingSellAll.TryRemove(steamId, out _);
                return new ShopTransactionResult
                {
                    Success = false,
                    ErrorKey = "sell_all_inventory_changed",
                    ErrorArgs = new object[] { conf.Category }
                };
            }

            if (!PlayerLock.TryAcquire(steamId))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "transaction_in_flight" };
            }

            DepositResult dep = null;
            bool payoutDone = false;
            try
            {
                var removedList = new List<InventoryItemInstance>();
                foreach (var inst in eligibleItems)
                {
                    if (await _inventoryGateway.RemoveItemByReferenceAsync(steamId, inst.Item).ConfigureAwait(false))
                    {
                        removedList.Add(inst);
                    }
                }

                if (removedList.Count < eligibleItems.Count)
                {
                    foreach (var removed in removedList)
                    {
                        await _inventoryGateway.TryAddItemAsync(steamId, removed.Item).ConfigureAwait(false);
                    }
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_failed" };
                }

                dep = await _economyService.DepositAsync(steamId, conf.ExpectedPayout, $"SellAll:{conf.Category}x{eligibleItems.Count}").ConfigureAwait(false);
                if (!dep.Success)
                {
                    foreach (var removed in removedList)
                    {
                        await _inventoryGateway.TryAddItemAsync(steamId, removed.Item).ConfigureAwait(false);
                    }
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_payout_failed" };
                }

                payoutDone = true;

                // Update pressures sequentially
                foreach (var kvp in itemCounts)
                {
                    if (catalogItems.TryGetValue(kvp.Key, out var entry))
                    {
                        var dynCfg = entry.DynamicPricing ?? new ItemDynamicPricingConfig { Enabled = cfg.DynamicPricingDefaultEnabled };
                        if (dynCfg.Enabled)
                        {
                            try
                            {
                                await UpdatePressureAfterTransactionAsync(kvp.Key, sellDelta: kvp.Value, buyDelta: 0, dynCfg.RecoveryPerHour).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                KizLog.Error($"[KizShop] Post-sell pressure update failed for item {kvp.Key}: {ex}");
                            }
                        }
                    }
                }

                _pendingSellAll.TryRemove(steamId, out _);
                return new ShopTransactionResult
                {
                    Success = true,
                    Quantity = eligibleItems.Count,
                    Price = conf.ExpectedPayout,
                    NewBalance = dep.NewBalance
                };
            }
            catch (Exception ex)
            {
                KizLog.Error($"[KizShop] Unexpected exception in ExecuteSellAllCategoryConfirmAsync for {steamId}: {ex}");
                _pendingSellAll.TryRemove(steamId, out _);
                if (payoutDone)
                {
                    return new ShopTransactionResult
                    {
                        Success = true,
                        Quantity = eligibleItems.Count,
                        Price = conf.ExpectedPayout,
                        NewBalance = dep?.NewBalance ?? Money.Zero
                    };
                }
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_failed" };
            }
            finally
            {
                PlayerLock.Release(steamId);
            }
        }

        public async Task<ShopTransactionResult> ExecuteBuyVehicleAsync(
            ulong steamId,
            string query,
            Func<string, bool> hasPermission,
            bool isAdmin)
        {
            var cfg = _configProvider();
            if (IsCombatBlocked(steamId, cfg.CombatTagBlocksVehicle, hasPermission, isAdmin, out int secondsLeft))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "combat_tagged", ErrorArgs = new object[] { secondsLeft } };
            }

            var lookup = _shopService.ResolveVehicle(query, enabledOnly: true);
            if (lookup.Status == LookupStatus.NotFound)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "vehicle_not_found", ErrorArgs = new object[] { query } };
            }
            if (lookup.Status == LookupStatus.Ambiguous)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "ambiguous_match", CandidateNames = lookup.CandidateNames };
            }

            var entry = lookup.Entry;
            if (!entry.CanBuy)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "vehicle_disabled" };
            }

            if (!string.IsNullOrEmpty(entry.Permission) && !isAdmin && !hasPermission(entry.Permission))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "vehicle_no_permission" };
            }

            if (_vehicleCooldowns.TryGetValue(steamId, out var lastSpawn))
            {
                var elapsed = (_clock.UtcNow - lastSpawn).TotalSeconds;
                if (elapsed < cfg.VehicleCooldownSeconds)
                {
                    int remain = (int)Math.Ceiling(cfg.VehicleCooldownSeconds - elapsed);
                    return new ShopTransactionResult { Success = false, ErrorKey = "vehicle_cooldown", ErrorArgs = new object[] { remain } };
                }
            }

            var vehicleCount = await _vehicleGateway.GetPlayerVehicleCountAsync(steamId).ConfigureAwait(false);
            if (vehicleCount >= cfg.MaxVehiclesPerPlayer)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "vehicle_max_limit", ErrorArgs = new object[] { cfg.MaxVehiclesPerPlayer } };
            }

            if (!Money.TryFromDecimal(entry.BuyPrice, cfg.MaxTransactionAmount, out var costMoney, out var moneyErr))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "invalid_money_amount", ErrorArgs = new object[] { moneyErr } };
            }

            if (!PlayerLock.TryAcquire(steamId))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "transaction_in_flight" };
            }

            bool spawned = false;
            WithdrawResult withdrawRes = null;
            try
            {
                withdrawRes = await _economyService.TryWithdrawAsync(steamId, costMoney, $"ShopBuyVehicle:{entry.Id}").ConfigureAwait(false);
                if (!withdrawRes.Success)
                {
                    var curBal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
                    return new ShopTransactionResult
                    {
                        Success = false,
                        ErrorKey = "vehicle_insufficient_funds",
                        ErrorArgs = new object[] { costMoney, entry.Name, curBal }
                    };
                }

                var spawnRes = await _vehicleGateway.TrySpawnVehicleAsync(steamId, entry.Id, cfg.VehicleSpawnDistance).ConfigureAwait(false);
                if (!spawnRes.Success)
                {
                    if (spawnRes.VehicleHandle != null)
                    {
                        await _vehicleGateway.DestroyVehicleAsync(spawnRes.VehicleHandle).ConfigureAwait(false);
                    }

                    await _economyService.DepositAsync(steamId, costMoney, $"Refund:VehicleSpawnFail:{entry.Id}", ignoreMaxBalance: true).ConfigureAwait(false);
                    return new ShopTransactionResult
                    {
                        Success = false,
                        ErrorKey = "vehicle_spawn_failed",
                        ErrorArgs = new object[] { spawnRes.ErrorMessage ?? string.Empty }
                    };
                }

                spawned = true;

                try
                {
                    _vehicleCooldowns[steamId] = _clock.UtcNow;
                }
                catch { }

                Money newBal = withdrawRes.NewBalance;
                try
                {
                    newBal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Failed to fetch balance after vehicle spawn for {steamId}: {ex}");
                }

                try
                {
                    SafeRaiseVehiclePurchased(steamId, entry, costMoney, newBal);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Post-spawn event invocation failed: {ex}");
                }

                return new ShopTransactionResult
                {
                    Success = true,
                    VehicleEntry = entry,
                    Price = costMoney,
                    NewBalance = newBal
                };
            }
            catch (Exception ex)
            {
                KizLog.Error($"[KizShop] Unexpected exception in ExecuteBuyVehicleAsync for {steamId}: {ex}");
                if (!spawned)
                {
                    if (withdrawRes != null && withdrawRes.Success)
                    {
                        await _economyService.DepositAsync(steamId, costMoney, $"Refund:VehicleSpawnException:{entry.Id}", ignoreMaxBalance: true).ConfigureAwait(false);
                    }
                    return new ShopTransactionResult { Success = false, ErrorKey = "vehicle_spawn_failed", ErrorArgs = new object[] { ex.Message } };
                }

                return new ShopTransactionResult
                {
                    Success = true,
                    VehicleEntry = entry,
                    Price = costMoney,
                    NewBalance = withdrawRes?.NewBalance ?? Money.Zero
                };
            }
            finally
            {
                PlayerLock.Release(steamId);
            }
        }

        public async Task<ShopKitTransactionResult> BuyKitAsync(
            ulong steamId,
            string kitName,
            Func<string, bool> hasPermission,
            bool isAdmin = false)
        {
            if (string.IsNullOrWhiteSpace(kitName))
            {
                return new ShopKitTransactionResult { Success = false, ErrorKey = "kit_not_found" };
            }

            var kit = _shopService.ResolveKit(kitName, enabledOnly: true);
            if (kit == null)
            {
                return new ShopKitTransactionResult { Success = false, ErrorKey = "kit_not_found", ErrorArgs = new object[] { kitName } };
            }

            if (!kit.Enabled)
            {
                return new ShopKitTransactionResult { Success = false, ErrorKey = "kit_disabled" };
            }

            if (!string.IsNullOrEmpty(kit.Permission) && !isAdmin && (hasPermission == null || !hasPermission(kit.Permission)))
            {
                return new ShopKitTransactionResult { Success = false, ErrorKey = "kit_no_permission" };
            }

            var cfg = _configProvider();
            if (IsCombatBlocked(steamId, cfg.CombatTagBlocksBuy, hasPermission, isAdmin, out int combatSecs))
            {
                return new ShopKitTransactionResult { Success = false, ErrorKey = "combat_tagged", ErrorArgs = new object[] { combatSecs } };
            }

            var coolKey = (steamId, kit.Name.ToLowerInvariant());
            if (kit.CooldownSeconds > 0 && !isAdmin && _kitCooldowns.TryGetValue(coolKey, out var lastBuy))
            {
                var elapsed = (_clock.UtcNow - lastBuy).TotalSeconds;
                if (elapsed < kit.CooldownSeconds)
                {
                    int rem = (int)Math.Ceiling(kit.CooldownSeconds - elapsed);
                    return new ShopKitTransactionResult { Success = false, ErrorKey = "kit_cooldown", ErrorArgs = new object[] { rem } };
                }
            }

            if (!Money.TryFromDecimal(kit.BuyPrice, cfg.MaxTransactionAmount, out var kitPrice, out var moneyErr))
            {
                return new ShopKitTransactionResult { Success = false, ErrorKey = "invalid_money_amount", ErrorArgs = new object[] { moneyErr } };
            }

            if (!PlayerLock.TryAcquire(steamId))
            {
                return new ShopKitTransactionResult { Success = false, ErrorKey = "transaction_in_flight" };
            }

            try
            {
                if (kit.Items != null)
                {
                    foreach (var ki in kit.Items)
                    {
                        var spaceRes = await _inventoryGateway.CheckSpaceAsync(steamId, ki.Id, ki.Amount).ConfigureAwait(false);
                        if (!spaceRes.Fits)
                        {
                            return new ShopKitTransactionResult { Success = false, ErrorKey = "kit_no_space" };
                        }
                    }
                }

                var withdrawRes = await _economyService.TryWithdrawAsync(steamId, kitPrice, $"KitBuy:{kit.Name}").ConfigureAwait(false);
                if (!withdrawRes.Success)
                {
                    var curBal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
                    return new ShopKitTransactionResult
                    {
                        Success = false,
                        ErrorKey = "kit_insufficient_funds",
                        ErrorArgs = new object[] { kitPrice, kit.DisplayName ?? kit.Name, curBal }
                    };
                }

                var deliveredItems = new List<SDG.Unturned.Item>();
                bool deliveryFailed = false;
                string failReason = "kit_delivery_failed";
                object[] failArgs = null;
                bool autoEquip = cfg.AutoEquipPurchasedItems;

                if (kit.Items != null)
                {
                    foreach (var ki in kit.Items)
                    {
                        for (int i = 0; i < ki.Amount; i++)
                        {
                            var item = await _inventoryGateway.CreateItemAsync(ki.Id).ConfigureAwait(false);
                            if (item == null)
                            {
                                deliveryFailed = true;
                                failReason = "buy_item_unknown";
                                failArgs = new object[] { ki.Id };
                                break;
                            }
                            bool added = await _inventoryGateway.TryAddItemAsync(steamId, item, autoEquip).ConfigureAwait(false);
                            if (!added)
                            {
                                deliveryFailed = true;
                                failReason = "kit_no_space";
                                break;
                            }
                            deliveredItems.Add(item);
                        }
                        if (deliveryFailed) break;
                    }
                }

                if (deliveryFailed)
                {
                    foreach (var d in deliveredItems)
                    {
                        await _inventoryGateway.RemoveItemByReferenceAsync(steamId, d).ConfigureAwait(false);
                    }
                    await _economyService.DepositAsync(steamId, kitPrice, $"KitRefund:{kit.Name}", ignoreMaxBalance: true).ConfigureAwait(false);
                    return new ShopKitTransactionResult { Success = false, ErrorKey = failReason, ErrorArgs = failArgs };
                }

                if (kit.CooldownSeconds > 0)
                {
                    _kitCooldowns[coolKey] = _clock.UtcNow;
                }

                OnKitPurchased?.Invoke(steamId, kit, kitPrice, withdrawRes.NewBalance);

                return new ShopKitTransactionResult
                {
                    Success = true,
                    KitEntry = kit,
                    Price = kitPrice,
                    NewBalance = withdrawRes.NewBalance
                };
            }
            finally
            {
                PlayerLock.Release(steamId);
            }
        }
    }
}
