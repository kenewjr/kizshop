using System;
using System.Threading;
using System.Threading.Tasks;
using Rocket.API.Collections;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using Rocket.Core.Utils;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Events;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using KizShop.API;
using KizShop.Economy;
using KizShop.Shop;
using KizShop.Storage;

namespace KizShop
{
    public class KizShopPlugin : RocketPlugin<KizShopConfiguration>
    {
        public static KizShopPlugin Instance { get; private set; }

        public IKizStorage Storage { get; private set; }
        public EconomyService Economy { get; private set; }
        public ShopService Shop { get; private set; }

        private CancellationTokenSource _playtimeCts;

        public override TranslationList DefaultTranslations => new TranslationList
        {
            { "currency_format", "{0}{1:N2} {2}" },
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

            { "buy_usage", "Usage: /buy <item name|id|guid> [amount]" },
            { "buy_item_not_found", "<color=red>Item '{0}' not found in shop.</color>" },
            { "buy_item_disabled", "<color=red>This item is currently not available for purchase.</color>" },
            { "buy_no_permission", "<color=red>You do not have permission to buy this item.</color>" },
            { "buy_max_amount", "<color=red>You cannot purchase more than {0} at a time.</color>" },
            { "buy_insufficient_funds", "<color=red>You need {0} to buy {1}x {2}, but only have {3}.</color>" },
            { "buy_no_space", "<color=red>Not enough inventory space to receive {0}x {1}.</color>" },
            { "buy_success", "<color=green>Bought {0}x {1} for {2}. Remaining balance: {3}.</color>" },
            { "buy_failed", "<color=red>Purchase transaction failed. No funds were deducted.</color>" },

            { "sell_usage", "Usage: /sell <item name|id|guid> [amount|all]" },
            { "sell_item_not_found", "<color=red>Item '{0}' not found in shop.</color>" },
            { "sell_item_disabled", "<color=red>This item cannot be sold to the shop.</color>" },
            { "sell_no_permission", "<color=red>You do not have permission to sell this item.</color>" },
            { "sell_not_enough_items", "<color=red>You only have {0}x {1} in your inventory, but tried to sell {2}.</color>" },
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
            { "vehicle_spawn_failed", "<color=red>Vehicle failed to spawn. No funds were deducted.</color>" },
            { "vehicle_success", "<color=green>Successfully purchased and spawned {0} for {1}!</color>" },

            { "reward_player_kill", "<color=green>+ {0} for eliminating {1}!</color>" },
            { "reward_zombie_kill", "<color=green>+ {0} for zombie kill.</color>" },
            { "reward_mega_zombie_kill", "<color=gold>+ {0} for MEGA zombie kill!</color>" },
            { "reward_playtime", "<color=green>+ {0} playtime income reward!</color>" },

            { "kizshop_admin_usage", "Usage: /kizshop <reload|additem|removeitem|setitemprice|addveh|removeveh|setvehprice>" },
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

            try
            {
                // 1. Initialize Storage
                Storage = StorageFactory.Create(
                    Configuration.Instance.DatabaseType,
                    Configuration.Instance.ConnectionString,
                    Directory);
                Storage.InitializeAsync().GetAwaiter().GetResult();

                // Prune logs if configured
                if (Configuration.Instance.LogRetentionDays > 0)
                {
                    _ = Storage.PruneLogsAsync(Configuration.Instance.LogRetentionDays);
                }

                // 2. Initialize Economy Service
                Economy = new EconomyService(
                    Storage,
                    () => Configuration.Instance.StartingBalance,
                    () => Configuration.Instance.MaxBalance > 0 ? (decimal?)Configuration.Instance.MaxBalance : null);

                Economy.OnBalanceChanged += (steamId, oldBal, newBal, reason) =>
                {
                    KizShopAPI.RaiseBalanceChanged(steamId, oldBal, newBal, reason);
                };

                // 3. Initialize Shop Service
                Shop = new ShopService(Directory);
                Shop.Load();

                // 4. Hook Game Events
                UnturnedPlayerEvents.OnPlayerDeath += HandlePlayerDeath;
                UnturnedPlayerEvents.OnPlayerUpdateStat += HandlePlayerStat;

                // 5. Start Playtime Income Loop
                StartPlaytimeLoop();

                Logger.Log("[KizShop] Plugin loaded successfully!");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[KizShop] Critical error loading plugin: {ex}");
            }
        }

        protected override void Unload()
        {
            StopPlaytimeLoop();

            UnturnedPlayerEvents.OnPlayerDeath -= HandlePlayerDeath;
            UnturnedPlayerEvents.OnPlayerUpdateStat -= HandlePlayerStat;

            if (Economy != null)
            {
                Economy.ClearCache();
            }

            if (Storage != null)
            {
                Storage.Dispose();
                Storage = null;
            }

            Instance = null;
            Logger.Log("[KizShop] Plugin unloaded.");
        }

        public new void ReloadPlugin()
        {
            base.ReloadPlugin();
            Shop.Load();
            StopPlaytimeLoop();
            StartPlaytimeLoop();
        }

        public string FormatMoney(decimal amount)
        {
            return Translate("currency_format", Configuration.Instance.CurrencySymbol, amount, Configuration.Instance.CurrencyName);
        }

        private void HandlePlayerDeath(UnturnedPlayer player, EDeathCause cause, ELimb limb, CSteamID murderer)
        {
            if (!Configuration.Instance.PlayerKillRewardEnabled) return;
            if (murderer == CSteamID.Nil || murderer == player.CSteamID) return;

            var killer = UnturnedPlayer.FromCSteamID(murderer);
            if (killer == null) return;

            decimal reward = Configuration.Instance.PlayerKillRewardAmount;
            if (reward <= 0) return;

            _ = Economy.DepositAsync(killer.CSteamID.m_SteamID, reward, $"KillReward:{player.CharacterName}").ContinueWith(t =>
            {
                TaskDispatcher.QueueOnMainThread(() =>
                {
                    var formatted = FormatMoney(reward);
                    UnturnedChat.Say(killer, Translate("reward_player_kill", formatted, player.CharacterName), UnityEngine.Color.green);
                });
            });
        }

        private void HandlePlayerStat(UnturnedPlayer player, EPlayerStat stat)
        {
            if (!Configuration.Instance.ZombieKillRewardEnabled) return;
            if (player == null) return;

            decimal reward = 0;
            string key = null;

            if (stat == EPlayerStat.KILLS_ZOMBIES_NORMAL)
            {
                reward = Configuration.Instance.ZombieKillRewardAmount;
                key = "reward_zombie_kill";
            }
            else if (stat == EPlayerStat.KILLS_ZOMBIES_MEGA)
            {
                reward = Configuration.Instance.MegaZombieKillRewardAmount;
                key = "reward_mega_zombie_kill";
            }

            if (reward <= 0 || key == null) return;

            _ = Economy.DepositAsync(player.CSteamID.m_SteamID, reward, $"ZombieReward:{stat}").ContinueWith(t =>
            {
                TaskDispatcher.QueueOnMainThread(() =>
                {
                    var formatted = FormatMoney(reward);
                    UnturnedChat.Say(player, Translate(key, formatted), UnityEngine.Color.green);
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
                        await Task.Delay(TimeSpan.FromSeconds(interval), token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested) break;
                    if (!Configuration.Instance.PlaytimeIncomeEnabled) continue;

                    decimal amount = Configuration.Instance.PlaytimeIncomeAmount;
                    if (amount <= 0) continue;

                    TaskDispatcher.QueueOnMainThread(() =>
                    {
                        var clients = Provider.clients;
                        if (clients == null) return;

                        foreach (var client in clients)
                        {
                            if (client == null) continue;
                            var steamId = client.playerID.steamID.m_SteamID;
                            var uPlayer = UnturnedPlayer.FromSteamPlayer(client);

                            _ = Economy.DepositAsync(steamId, amount, "PlaytimeIncome").ContinueWith(t =>
                            {
                                TaskDispatcher.QueueOnMainThread(() =>
                                {
                                    if (uPlayer != null)
                                    {
                                        var formatted = FormatMoney(amount);
                                        UnturnedChat.Say(uPlayer, Translate("reward_playtime", formatted), UnityEngine.Color.green);
                                    }
                                });
                            });
                        }
                    });
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
    }
}
