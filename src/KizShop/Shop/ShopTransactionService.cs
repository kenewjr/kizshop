using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SDG.Unturned;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Shop.Gateways;
using KizShop.Shop.Models;

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
    }

    public class ShopTransactionService
    {
        private readonly ShopService _shopService;
        private readonly EconomyService _economyService;
        private readonly IInventoryGateway _inventoryGateway;
        private readonly IVehicleGateway _vehicleGateway;
        private readonly IClock _clock;
        private readonly Func<KizShopConfiguration> _configProvider;

        private readonly ConcurrentDictionary<ulong, byte> _inFlightLocks = new ConcurrentDictionary<ulong, byte>();
        private readonly ConcurrentDictionary<ulong, DateTime> _vehicleCooldowns = new ConcurrentDictionary<ulong, DateTime>();

        public ShopTransactionService(
            ShopService shopService,
            EconomyService economyService,
            IInventoryGateway inventoryGateway,
            IVehicleGateway vehicleGateway,
            Func<KizShopConfiguration> configProvider,
            IClock clock = null)
        {
            _shopService = shopService ?? throw new ArgumentNullException(nameof(shopService));
            _economyService = economyService ?? throw new ArgumentNullException(nameof(economyService));
            _inventoryGateway = inventoryGateway ?? throw new ArgumentNullException(nameof(inventoryGateway));
            _vehicleGateway = vehicleGateway ?? throw new ArgumentNullException(nameof(vehicleGateway));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _clock = clock ?? SystemClock.Instance;
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

            decimal totalDecimal = entry.BuyPrice * quantity;
            var cfg = _configProvider();
            if (!Money.TryFromDecimal(totalDecimal, cfg.MaxTransactionAmount, out var totalMoney, out var moneyErr))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "invalid_money_amount", ErrorArgs = new object[] { moneyErr } };
            }

            // In-flight per-player lock
            if (!_inFlightLocks.TryAdd(steamId, 1))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "transaction_in_flight" };
            }

            try
            {
                // Check space simulation BEFORE withdrawing
                if (!_inventoryGateway.CheckSpace(steamId, entry.Id, quantity))
                {
                    return new ShopTransactionResult { Success = false, ErrorKey = "buy_no_space", ErrorArgs = new object[] { quantity, entry.Name } };
                }

                // Atomically withdraw at storage level
                var withdrawRes = await _economyService.TryWithdrawAsync(steamId, totalMoney, $"ShopBuy:{entry.Id}x{quantity}").ConfigureAwait(false);
                if (!withdrawRes.Success)
                {
                    var curBal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
                    return new ShopTransactionResult
                    {
                        Success = false,
                        ErrorKey = "buy_insufficient_funds",
                        ErrorArgs = new object[] { totalMoney, quantity, entry.Name, curBal }
                    };
                }

                // Deliver ONE INSTANCE AT A TIME and track every delivered reference
                var deliveredItems = new List<Item>();
                bool allDelivered = true;

                try
                {
                    byte defaultAmount = _inventoryGateway.GetDefaultAmount(entry.Id);
                    for (int i = 0; i < quantity; i++)
                    {
                        var item = new Item(entry.Id, defaultAmount, 100, new byte[16]);
                        if (_inventoryGateway.TryAddItem(steamId, item))
                        {
                            deliveredItems.Add(item);
                        }
                        else
                        {
                            allDelivered = false;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Exception during item delivery to {steamId}: {ex}");
                    allDelivered = false;
                }

                // Rollback on any failure
                if (!allDelivered || deliveredItems.Count < quantity)
                {
                    foreach (var delivered in deliveredItems)
                    {
                        _inventoryGateway.RemoveItemByReference(steamId, delivered);
                    }

                    // Full refund ignoring max balance
                    await _economyService.DepositAsync(steamId, totalMoney, $"Refund:ShopBuyFail:{entry.Id}x{quantity}", ignoreMaxBalance: true).ConfigureAwait(false);
                    KizLog.Warning($"[KizShop] Buy delivery failed for {steamId} ({entry.Id}x{quantity}). Refunded {totalMoney}.");

                    return new ShopTransactionResult { Success = false, ErrorKey = "buy_failed_refunded" };
                }

                var newBal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
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
                // In case of post-withdraw exception, ensure refund
                await _economyService.DepositAsync(steamId, totalMoney, $"Refund:Exception:{entry.Id}", ignoreMaxBalance: true).ConfigureAwait(false);
                return new ShopTransactionResult { Success = false, ErrorKey = "buy_failed_refunded" };
            }
            finally
            {
                _inFlightLocks.TryRemove(steamId, out _);
            }
        }

        public async Task<ShopTransactionResult> ExecuteSellAsync(
            ulong steamId,
            string query,
            string amountArg,
            Func<string, bool> hasPermission,
            bool isAdmin)
        {
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

            // Scan only player's own gear pages
            var available = _inventoryGateway.GetGearItems(steamId, entry.Id);
            if (available.Count == 0)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "sell_not_enough_items", ErrorArgs = new object[] { 0, entry.Name, 1 } };
            }

            // Check guns with attachments or loaded magazine
            foreach (var instance in available)
            {
                if (_inventoryGateway.HasAttachmentsOrLoadedMagazine(entry.Id, instance.State))
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

            // Calculate unit price per instance based on amount ratio and quality
            var toSell = available.Take(amountToSell).ToList();
            byte defaultAmount = _inventoryGateway.GetDefaultAmount(entry.Id);
            var cfg = _configProvider();

            long totalPayoutCents = 0;
            foreach (var inst in toSell)
            {
                decimal amountRatio = (defaultAmount > 1) ? ((decimal)inst.Amount / defaultAmount) : 1.0m;
                decimal qualityRatio = cfg.ScaleSellByQuality ? (inst.Quality / 100m) : 1.0m;
                decimal unitPrice = entry.SellPrice.Value * amountRatio * qualityRatio;

                long cents = (long)Math.Round(unitPrice * 100m, MidpointRounding.AwayFromZero);
                totalPayoutCents += cents;
            }

            var totalPayout = Money.FromCents(totalPayoutCents);

            // In-flight lock
            if (!_inFlightLocks.TryAdd(steamId, 1))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "transaction_in_flight" };
            }

            try
            {
                // Remove items FIRST
                var removedInstances = new List<InventoryItemInstance>();
                foreach (var inst in toSell)
                {
                    if (_inventoryGateway.RemoveItemByReference(steamId, inst.Item))
                    {
                        removedInstances.Add(inst);
                    }
                }

                if (removedInstances.Count < amountToSell)
                {
                    // Restore any items that were removed
                    foreach (var removed in removedInstances)
                    {
                        _inventoryGateway.TryAddItem(steamId, removed.Item);
                    }
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_failed" };
                }

                // Deposit payout
                var depositRes = await _economyService.DepositAsync(steamId, totalPayout, $"ShopSell:{entry.Id}x{amountToSell}").ConfigureAwait(false);
                if (!depositRes.Success)
                {
                    // Rollback: give items back if payout failed
                    foreach (var removed in removedInstances)
                    {
                        _inventoryGateway.TryAddItem(steamId, removed.Item);
                    }
                    KizLog.Error($"[KizShop] Payout failed for {steamId} selling {entry.Id}x{amountToSell}. Items restored.");
                    return new ShopTransactionResult { Success = false, ErrorKey = "sell_payout_failed" };
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
            finally
            {
                _inFlightLocks.TryRemove(steamId, out _);
            }
        }

        public async Task<ShopTransactionResult> ExecuteBuyVehicleAsync(
            ulong steamId,
            string query,
            Func<string, bool> hasPermission,
            bool isAdmin)
        {
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

            var cfg = _configProvider();

            // Cooldown check
            if (_vehicleCooldowns.TryGetValue(steamId, out var lastSpawn))
            {
                var elapsed = (_clock.UtcNow - lastSpawn).TotalSeconds;
                if (elapsed < cfg.VehicleCooldownSeconds)
                {
                    int remain = (int)Math.Ceiling(cfg.VehicleCooldownSeconds - elapsed);
                    return new ShopTransactionResult { Success = false, ErrorKey = "vehicle_cooldown", ErrorArgs = new object[] { remain } };
                }
            }

            // Max vehicles check
            if (_vehicleGateway.GetPlayerVehicleCount(steamId) >= cfg.MaxVehiclesPerPlayer)
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "vehicle_max_limit", ErrorArgs = new object[] { cfg.MaxVehiclesPerPlayer } };
            }

            if (!Money.TryFromDecimal(entry.BuyPrice, cfg.MaxTransactionAmount, out var costMoney, out var moneyErr))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "invalid_money_amount", ErrorArgs = new object[] { moneyErr } };
            }

            if (!_inFlightLocks.TryAdd(steamId, 1))
            {
                return new ShopTransactionResult { Success = false, ErrorKey = "transaction_in_flight" };
            }

            try
            {
                // Withdraw first
                var withdrawRes = await _economyService.TryWithdrawAsync(steamId, costMoney, $"ShopBuyVehicle:{entry.Id}").ConfigureAwait(false);
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

                // Spawn vehicle
                var spawnRes = _vehicleGateway.TrySpawnVehicle(steamId, entry.Id, cfg.VehicleSpawnDistance);
                if (!spawnRes.Success)
                {
                    // Destroy handle if partially created
                    if (spawnRes.VehicleHandle != null)
                    {
                        _vehicleGateway.DestroyVehicle(spawnRes.VehicleHandle);
                    }

                    // Full refund
                    await _economyService.DepositAsync(steamId, costMoney, $"Refund:VehicleSpawnFail:{entry.Id}", ignoreMaxBalance: true).ConfigureAwait(false);
                    return new ShopTransactionResult
                    {
                        Success = false,
                        ErrorKey = "vehicle_spawn_failed",
                        ErrorArgs = new object[] { spawnRes.ErrorMessage ?? string.Empty }
                    };
                }

                _vehicleCooldowns[steamId] = _clock.UtcNow;
                var newBal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);

                return new ShopTransactionResult
                {
                    Success = true,
                    VehicleEntry = entry,
                    Price = costMoney,
                    NewBalance = newBal
                };
            }
            finally
            {
                _inFlightLocks.TryRemove(steamId, out _);
            }
        }
    }
}
