using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.API.Collections;
using Rocket.Core;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Events;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using KizShop.API;
using KizShop.Common;
using KizShop.Discord;
using KizShop.Economy;
using KizShop.Shop;
using KizShop.Shop.Gateways;
using KizShop.Storage;
using KizShop.UI;

namespace KizShop
{
    public class KizShopPlugin : RocketPlugin<KizShopConfiguration>
    {
        public static KizShopPlugin Instance { get; private set; }

        public IKizStorage Storage { get; private set; }
        public EconomyService Economy { get; private set; }
        public ShopService Shop { get; private set; }
        public ShopTransactionService Transactions { get; private set; }
        public ShopUiService ShopUi { get; private set; }
        public DiscordWebhookService DiscordWebhook { get; private set; }
        public DiscordBotService DiscordBot { get; private set; }
        public IMainThreadDispatcher Dispatcher { get; private set; } = new RocketMainThreadDispatcher();

        private CancellationTokenSource _playtimeCts;
        private CancellationTokenSource _logPruneCts;

        private readonly ConcurrentDictionary<(ulong killer, ulong victim), DateTime> _killCooldowns =
            new ConcurrentDictionary<(ulong killer, ulong victim), DateTime>();

        public override TranslationList DefaultTranslations => new TranslationList
        {
            { "currency_format", "{0}{1:N2} {2}" },
            { "plugin_offline", "<color=red>KizShop is currently offline.</color>" },
            { "command_error_generic", "<color=red>An error occurred while processing your request. Please try again later.</color>" },
            { "invalid_money_amount", "<color=red>Invalid amount: {0}</color>" },
            { "invalid_steamid", "<color=red>Invalid SteamID64 specified.</color>" },
            { "invalid_catalog_entry", "<color=red>Catalog validation error: {0}</color>" },
            { "ambiguous_match", "<color=yellow>Multiple matches found: {0}. Please be more specific.</color>" },
            { "transaction_in_flight", "<color=yellow>Your previous transaction is still processing. Please wait.</color>" },

            { "balance_self", "Your balance is <color=green>{0}</color>." },
            { "balance_other", "<color=yellow>{0}</color>'s balance is <color=green>{1}</color>." },
            { "balance_player_not_found", "<color=red>Player '{0}' was not found.</color>" },
            { "balance_no_permission_others", "<color=red>You do not have permission to view other players' balance.</color>" },
            { "balance_usage", "Usage: /balance [player]" },

            { "pay_usage", "Usage: /pay <player|steamid> <amount>" },
            { "pay_invalid_amount", "<color=red>Amount must be greater than zero.</color>" },
            { "pay_self", "<color=red>You cannot pay yourself.</color>" },
            { "pay_insufficient_funds", "<color=red>You do not have enough funds to pay {0}.</color>" },
            { "pay_receiver_max_balance", "<color=red>Payment failed: {0} would exceed maximum balance.</color>" },
            { "pay_sent", "You sent <color=green>{0}</color> to <color=yellow>{1}</color>. Remaining balance: <color=green>{2}</color>." },
            { "pay_received", "You received <color=green>{0}</color> from <color=yellow>{1}</color>. New balance: <color=green>{2}</color>." },

            { "baltop_header", "<color=cyan>--- Top {0} Richest Players ---</color>" },
            { "baltop_entry", "<color=yellow>#{0}</color> {1}: <color=green>{2}</color>" },
            { "baltop_empty", "No accounts found." },

            { "eco_usage", "Usage: /eco <give|take|set> <player|steamid> <amount>" },
            { "eco_success", "<color=green>Updated {0}'s balance. New balance: {1}.</color>" },
            { "eco_insufficient_funds", "<color=red>{0} only has {1}, cannot deduct amount.</color>" },

            { "buy_usage", "Usage: /buy <item name|id|guid> [amount]" },
            { "buy_invalid_amount", "<color=red>Quantity must be at least 1.</color>" },
            { "buy_item_not_found", "<color=red>Item '{0}' not found in shop.</color>" },
            { "buy_item_disabled", "<color=red>This item is currently not available for purchase.</color>" },
            { "buy_no_permission", "<color=red>You do not have permission to buy this item.</color>" },
            { "buy_max_amount", "<color=red>You cannot purchase more than {0} at a time.</color>" },
            { "buy_insufficient_funds", "<color=red>You need {0} to buy {1}x {2}, but only have {3}.</color>" },
            { "buy_no_space", "<color=red>Not enough inventory space to receive {0}x {1}.</color>" },
            { "buy_player_unavailable", "<color=red>Player unavailable.</color>" },
            { "buy_item_unknown", "<color=red>Item asset ID {0} does not exist on server.</color>" },
            { "shop_ui_unavailable", "<color=yellow>Visual UI is unavailable. Using chat catalog.</color>" },
            { "buy_success", "<color=green>Bought {0}x {1} for {2}. Remaining balance: {3}.</color>" },
            { "buy_failed_refunded", "<color=red>Item delivery failed. Your purchase was canceled and refunded in full.</color>" },

            { "sell_usage", "Usage: /sell <item name|id|guid> [amount|all]" },
            { "sell_item_not_found", "<color=red>Item '{0}' not found in shop.</color>" },
            { "sell_item_disabled", "<color=red>This item cannot be sold to the shop.</color>" },
            { "sell_no_permission", "<color=red>You do not have permission to sell this item.</color>" },
            { "sell_not_enough_items", "<color=red>You only have {0}x {1} in your gear pages, but tried to sell {2}.</color>" },
            { "sell_gun_has_attachments", "<color=red>Cannot sell gun with attachments or loaded magazine. Remove them first.</color>" },
            { "sell_failed", "<color=red>Failed to remove items from inventory.</color>" },
            { "sell_payout_failed", "<color=red>Payout failed. Removed items have been restored to your inventory.</color>" },
            { "sell_success", "<color=green>Sold {0}x {1} for {2}. New balance: {3}.</color>" },

            { "cost_usage", "Usage: /cost <item name|id|guid>" },
            { "cost_item_info", "<color=yellow>{0}</color> (ID: {1}) - Buy: <color=green>{2}</color> | Sell: <color=green>{3}</color>" },
            { "cost_not_found", "<color=red>Item or vehicle '{0}' not found in shop.</color>" },
            { "cost_not_available", "N/A" },

            { "shop_header", "<color=cyan>--- KizShop Catalog (Page {0}/{1}) ---</color>" },
            { "shop_item_entry", "<color=yellow>{0}</color> (ID: {1}) - Buy: <color=green>{2}</color> | Sell: <color=green>{3}</color>" },
            { "shop_footer", "Use /buy <item> [amount] to purchase. Page navigation: /shop [page]" },

            { "vehicle_usage", "Usage: /buyvehicle <name|id|guid>" },
            { "vehicle_not_found", "<color=red>Vehicle '{0}' not found in shop.</color>" },
            { "vehicle_disabled", "<color=red>This vehicle cannot be purchased.</color>" },
            { "vehicle_no_permission", "<color=red>You do not have permission to purchase this vehicle.</color>" },
            { "vehicle_insufficient_funds", "<color=red>You need {0} to purchase {1}, but have {2}.</color>" },
            { "vehicle_cooldown", "<color=red>You must wait {0}s before purchasing another vehicle.</color>" },
            { "vehicle_max_limit", "<color=red>You already own the maximum allowed number of vehicles ({0}).</color>" },
            { "vehicle_spawn_failed", "<color=red>Vehicle failed to spawn ({0}). Funds were refunded.</color>" },
            { "vehicle_success", "<color=green>Successfully purchased and spawned {0} for {1}!</color>" },

            { "reward_player_kill", "<color=green>+ {0} for eliminating {1}!</color>" },
            { "reward_zombie_kill", "<color=green>+ {0} for zombie kill.</color>" },
            { "reward_mega_zombie_kill", "<color=gold>+ {0} for MEGA zombie kill!</color>" },
            { "reward_playtime", "<color=green>+ {0} playtime income reward!</color>" },

            { "kizshop_admin_usage", "Usage: /kizshop <reload|additem|removeitem|setitemprice|addveh|removeveh|setvehprice>" },
            { "kizshop_additem_usage", "Usage: /kizshop additem <id> \"<name>\" <buyPrice> [sellPrice] [permission]" },
            { "kizshop_removeitem_usage", "Usage: /kizshop removeitem <id>" },
            { "kizshop_setitemprice_usage", "Usage: /kizshop setitemprice <id> <buyPrice> [sellPrice]" },
            { "kizshop_addveh_usage", "Usage: /kizshop addveh <id> \"<name>\" <buyPrice> [sellPrice] [permission]" },
            { "kizshop_removeveh_usage", "Usage: /kizshop removeveh <id>" },
            { "kizshop_setvehprice_usage", "Usage: /kizshop setvehprice <id> <buyPrice> [sellPrice]" },
            { "kizshop_reloaded", "<color=green>KizShop configuration and shop catalog reloaded successfully.</color>" },
            { "kizshop_item_added", "<color=green>Shop item {0} (ID: {1}) added/updated.</color>" },
            { "kizshop_item_removed", "<color=green>Shop item ID {0} removed.</color>" },
            { "kizshop_item_price_set", "<color=green>Shop item ID {0} prices updated.</color>" },
            { "kizshop_veh_added", "<color=green>Shop vehicle {0} (ID: {1}) added/updated.</color>" },
            { "kizshop_veh_removed", "<color=green>Shop vehicle ID {0} removed.</color>" },
            { "kizshop_veh_price_set", "<color=green>Shop vehicle ID {0} prices updated.</color>" }
        };

        protected override void Load()
        {
            Instance = this;

            if (Dispatcher is RocketMainThreadDispatcher rDispatcher)
            {
                rDispatcher.SetMainThreadId(Thread.CurrentThread.ManagedThreadId);
            }

            try
            {
                // 1. Initialize Storage
                try
                {
                    Storage = StorageFactory.Create(
                        Configuration.Instance.DatabaseType,
                        Configuration.Instance.ConnectionString,
                        Directory);
                    Storage.InitializeAsync().GetAwaiter().GetResult();
                }
                catch (Exception storageEx)
                {
                    Logger.LogError($"[KizShop] FATAL: Storage initialization failed ({Configuration.Instance.DatabaseType}): {storageEx}. Check database path or connection credentials. Unloading plugin.");
                    UnloadPlugin();
                    return;
                }

                // Initial prune & start 6-hour prune cycle
                if (Configuration.Instance.LogRetentionDays > 0)
                {
                    _ = Storage.PruneLogsAsync(Configuration.Instance.LogRetentionDays);
                    StartLogPruneLoop();
                }

                // 2. Initialize Economy Service (disable cache when DatabaseType == MySQL)
                bool isMySql = string.Equals(Configuration.Instance.DatabaseType, "MySQL", StringComparison.OrdinalIgnoreCase);
                Economy = new EconomyService(
                    Storage,
                    () => Money.FromDecimal(Configuration.Instance.StartingBalance),
                    () => Configuration.Instance.MaxBalance > 0 ? (Money?)Money.FromDecimal(Configuration.Instance.MaxBalance) : null,
                    enableCache: !isMySql,
                    dispatcher: Dispatcher);

                Economy.OnBalanceChanged += (steamId, oldBal, newBal, reason) =>
                {
                    KizShopAPI.RaiseBalanceChanged(steamId, oldBal, newBal, reason);
                };

                // 3. Initialize Shop Service & Transactions
                Shop = new ShopService(Directory, () => Configuration.Instance.AllowSellAboveBuy);
                Shop.Load();
                ValidateCatalogOnLoad();

                Transactions = new ShopTransactionService(
                    Shop,
                    Economy,
                    new UnturnedInventoryGateway(),
                    new UnturnedVehicleGateway(),
                    () => Configuration.Instance);

                // 4. Initialize Visual UI
                var uiGateway = new UnturnedShopUiGateway();
                ShopUi = new ShopUiService(Shop, Economy, Transactions, uiGateway, () => Configuration.Instance);
                EffectManager.onEffectButtonClicked += OnEffectButtonClicked;

                // 5. Initialize Discord Services & Webhooks
                DiscordWebhook = new DiscordWebhookService(() => Configuration.Instance);
                DiscordBot = new DiscordBotService(Economy, Storage, () => Configuration.Instance);
                DiscordBot.Start();

                // Connect transaction alerts to webhooks
                Transactions.OnItemPurchased += (steamId, entry, qty, price, newBal) =>
                {
                    _ = DiscordWebhook?.SendPurchaseAlertAsync(steamId, entry.Name, qty, price, newBal);
                    if (price.ToDecimal() >= Configuration.Instance.DiscordLargeTransactionThreshold)
                    {
                        _ = DiscordWebhook?.SendLargeTransactionAlertAsync(steamId, "Purchase", price, $"{qty}x {entry.Name}");
                    }
                };

                Transactions.OnItemSold += (steamId, entry, qty, payout, newBal) =>
                {
                    _ = DiscordWebhook?.SendSaleAlertAsync(steamId, entry.Name, qty, payout, newBal);
                    if (payout.ToDecimal() >= Configuration.Instance.DiscordLargeTransactionThreshold)
                    {
                        _ = DiscordWebhook?.SendLargeTransactionAlertAsync(steamId, "Sale", payout, $"{qty}x {entry.Name}");
                    }
                };

                Transactions.OnVehiclePurchased += (steamId, entry, price, newBal) =>
                {
                    _ = DiscordWebhook?.SendPurchaseAlertAsync(steamId, $"Vehicle {entry.Name}", 1, price, newBal);
                    if (price.ToDecimal() >= Configuration.Instance.DiscordLargeTransactionThreshold)
                    {
                        _ = DiscordWebhook?.SendLargeTransactionAlertAsync(steamId, "VehiclePurchase", price, entry.Name);
                    }
                };

                // 6. Hook Game Events
                UnturnedPlayerEvents.OnPlayerDeath += HandlePlayerDeath;
                UnturnedPlayerEvents.OnPlayerUpdateStat += HandlePlayerStat;
                Rocket.Unturned.U.Events.OnPlayerDisconnected += HandlePlayerDisconnected;

                // 7. Start Playtime Income Loop
                StartPlaytimeLoop();

                Logger.Log("[KizShop] Plugin loaded successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[KizShop] Unexpected error loading plugin: {ex}. Unloading.");
                UnloadPlugin();
            }
        }

        protected override void Unload()
        {
            EffectManager.onEffectButtonClicked -= OnEffectButtonClicked;
            Rocket.Unturned.U.Events.OnPlayerDisconnected -= HandlePlayerDisconnected;

            StopPlaytimeLoop();
            StopLogPruneLoop();

            UnturnedPlayerEvents.OnPlayerDeath -= HandlePlayerDeath;
            UnturnedPlayerEvents.OnPlayerUpdateStat -= HandlePlayerStat;

            ShopUi?.ClearAllStates();

            if (DiscordBot != null)
            {
                DiscordBot.Dispose();
                DiscordBot = null;
            }

            if (DiscordWebhook != null)
            {
                DiscordWebhook.Dispose();
                DiscordWebhook = null;
            }

            ShopUi = null;

            if (Economy != null)
            {
                Economy.ClearCache();
            }

            if (Storage != null)
            {
                Storage.Dispose();
                Storage = null;
            }

            Economy = null;
            Shop = null;
            Transactions = null;
            Instance = null;

            Logger.Log("[KizShop] Plugin unloaded.");
        }

        public new void ReloadPlugin()
        {
            base.ReloadPlugin();
            Shop?.Load();
            StopPlaytimeLoop();
            StartPlaytimeLoop();
            StopLogPruneLoop();
            StartLogPruneLoop();

            DiscordBot?.Stop();
            DiscordBot?.Start();
        }

        private void OnEffectButtonClicked(Player player, string buttonName)
        {
            if (player == null || ShopUi == null) return;
            var steamId = player.channel.owner.playerID.steamID.m_SteamID;
            var uPlayer = UnturnedPlayer.FromCSteamID(player.channel.owner.playerID.steamID);
            bool isAdmin = player.channel.owner.isAdmin;

            Task.Run(async () =>
            {
                try
                {
                    await ShopUi.HandleButtonClickedAsync(
                        steamId,
                        buttonName,
                        perm => uPlayer != null && uPlayer.HasPermission(perm),
                        isAdmin).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Error in OnEffectButtonClicked for {steamId}: {ex}");
                }
            });
        }

        private void HandlePlayerDisconnected(UnturnedPlayer player)
        {
            if (player != null)
            {
                ShopUi?.CloseShop(player.CSteamID.m_SteamID);
            }
        }

        private void ValidateCatalogOnLoad()
        {
            try
            {
                var items = Shop.GetItems();
                foreach (var item in items)
                {
                    var asset = Assets.find(EAssetType.ITEM, item.Id) as ItemAsset;
                    if (asset == null)
                    {
                        Logger.LogWarning($"[KizShop] Catalog item ID {item.Id} ('{item.Name}') not found in game assets! Marking unavailable.");
                        item.Enabled = false;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(asset.itemName))
                        {
                            item.Name = asset.itemName;
                        }
                        Logger.Log($"[KizShop] Catalog item ID {item.Id}: assetName='{asset.itemName}', size={asset.size_x}x{asset.size_y}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[KizShop] Catalog asset validation encountered error: {ex.Message}");
            }
        }

        public string FormatMoney(Money amount)
        {
            return Translate("currency_format", Configuration.Instance.CurrencySymbol, amount.ToDecimal(), Configuration.Instance.CurrencyName);
        }

        private void HandlePlayerDeath(UnturnedPlayer player, EDeathCause cause, ELimb limb, CSteamID murderer)
        {
            if (!Configuration.Instance.PlayerKillRewardEnabled) return;
            if (murderer == CSteamID.Nil || murderer == player.CSteamID) return; // No suicide

            var killer = UnturnedPlayer.FromCSteamID(murderer);
            if (killer == null || killer.Player == null) return;

            // Check same-group members (Unturned quests groupID)
            if (killer.Player.quests != null && player.Player.quests != null &&
                killer.Player.quests.groupID != CSteamID.Nil &&
                killer.Player.quests.groupID == player.Player.quests.groupID)
            {
                return; // Same group: no reward
            }

            // Per (killer, victim) cooldown check
            var key = (killer: killer.CSteamID.m_SteamID, victim: player.CSteamID.m_SteamID);
            var now = DateTime.UtcNow;
            if (_killCooldowns.TryGetValue(key, out var lastKill))
            {
                if ((now - lastKill).TotalSeconds < Configuration.Instance.PlayerKillRewardCooldownSeconds)
                {
                    return; // On cooldown
                }
            }

            if (!Money.TryFromDecimal(Configuration.Instance.PlayerKillRewardAmount, Configuration.Instance.MaxTransactionAmount, out var reward, out _))
            {
                return;
            }

            _killCooldowns[key] = now;

            // Off main thread DB work
            Task.Run(async () =>
            {
                await Economy.DepositAsync(killer.CSteamID.m_SteamID, reward, $"KillReward:{player.CharacterName}").ConfigureAwait(false);
                Dispatcher.QueueOnMainThread(() =>
                {
                    var formatted = FormatMoney(reward);
                    KizChat.Say(killer, Translate("reward_player_kill", formatted, player.CharacterName), UnityEngine.Color.green);
                });
            });
        }

        private void HandlePlayerStat(UnturnedPlayer player, EPlayerStat stat)
        {
            if (!Configuration.Instance.ZombieKillRewardEnabled) return;
            if (player == null) return;

            decimal rewardDecimal = 0;
            string key = null;

            if (stat == EPlayerStat.KILLS_ZOMBIES_NORMAL)
            {
                rewardDecimal = Configuration.Instance.ZombieKillRewardAmount;
                key = "reward_zombie_kill";
            }
            else if (stat == EPlayerStat.KILLS_ZOMBIES_MEGA)
            {
                rewardDecimal = Configuration.Instance.MegaZombieKillRewardAmount;
                key = "reward_mega_zombie_kill";
            }

            if (rewardDecimal <= 0 || key == null) return;

            if (!Money.TryFromDecimal(rewardDecimal, Configuration.Instance.MaxTransactionAmount, out var reward, out _))
            {
                return;
            }

            // Off main thread DB work
            Task.Run(async () =>
            {
                await Economy.DepositAsync(player.CSteamID.m_SteamID, reward, $"ZombieReward:{stat}").ConfigureAwait(false);
                Dispatcher.QueueOnMainThread(() =>
                {
                    var formatted = FormatMoney(reward);
                    KizChat.Say(player, Translate(key, formatted), UnityEngine.Color.green);
                });
            });
        }

        private void StartPlaytimeLoop()
        {
            if (!Configuration.Instance.PlaytimeIncomeEnabled) return;

            _playtimeCts = new CancellationTokenSource();
            var token = _playtimeCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    int interval = Configuration.Instance.PlaytimeIncomeIntervalSeconds;
                    if (interval <= 0) interval = 600;

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(interval), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested) break;
                    if (!Configuration.Instance.PlaytimeIncomeEnabled) continue;

                    if (!Money.TryFromDecimal(Configuration.Instance.PlaytimeIncomeAmount, Configuration.Instance.MaxTransactionAmount, out var amount, out _))
                    {
                        continue;
                    }

                    // Gather steam IDs on main thread
                    var steamIds = new System.Collections.Generic.List<ulong>();
                    Dispatcher.QueueOnMainThread(() =>
                    {
                        var clients = Provider.clients;
                        if (clients != null)
                        {
                            foreach (var client in clients)
                            {
                                if (client != null) steamIds.Add(client.playerID.steamID.m_SteamID);
                            }
                        }
                    });

                    // Wait small tick for main thread collection
                    await Task.Delay(200, token).ConfigureAwait(false);

                    // Perform DB work off main thread
                    foreach (var sId in steamIds)
                    {
                        await Economy.DepositAsync(sId, amount, "PlaytimeIncome").ConfigureAwait(false);
                        Dispatcher.QueueOnMainThread(() =>
                        {
                            var uPlayer = UnturnedPlayer.FromCSteamID(new CSteamID(sId));
                            if (uPlayer != null)
                            {
                                var formatted = FormatMoney(amount);
                                KizChat.Say(uPlayer, Translate("reward_playtime", formatted), UnityEngine.Color.green);
                            }
                        });
                    }
                }
            }, token);
        }

        private void StopPlaytimeLoop()
        {
            if (_playtimeCts != null)
            {
                _playtimeCts.Cancel();
                _playtimeCts.Dispose();
                _playtimeCts = null;
            }
        }

        private void StartLogPruneLoop()
        {
            _logPruneCts = new CancellationTokenSource();
            var token = _logPruneCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromHours(6), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested) break;
                    if (Storage != null && Configuration.Instance.LogRetentionDays > 0)
                    {
                        await Storage.PruneLogsAsync(Configuration.Instance.LogRetentionDays).ConfigureAwait(false);
                    }
                }
            }, token);
        }

        private void StopLogPruneLoop()
        {
            if (_logPruneCts != null)
            {
                _logPruneCts.Cancel();
                _logPruneCts.Dispose();
                _logPruneCts = null;
            }
        }
    }
}
