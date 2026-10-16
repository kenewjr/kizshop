using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
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
using KizShop.Combat;
using KizShop.Economy;
using KizShop.Shop;
using KizShop.Shop.Gateways;
using KizShop.Storage;

namespace KizShop
{
    public class KizShopPlugin : RocketPlugin<KizShopConfiguration>
    {
        public static KizShopPlugin Instance { get; private set; }

        public IKizStorage Storage { get; private set; }
        public EconomyService Economy { get; private set; }
        public ShopService Shop { get; private set; }
        public ShopTransactionService Transactions { get; private set; }
        public DiscordWebhookService DiscordWebhook { get; private set; }
        public DiscordBotService DiscordBot { get; private set; }
        public ICombatTagService CombatTag { get; private set; }
        public IClock Clock { get; private set; } = SystemClock.Instance;
        public IMainThreadDispatcher Dispatcher { get; private set; } = new RocketMainThreadDispatcher();

        private CancellationTokenSource _playtimeCts;
        private CancellationTokenSource _logPruneCts;
        private CancellationTokenSource _bountySweepCts;

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

            { "eco_usage", "Usage: /eco <give|take|set> <player|steamid> <amount> | /eco giveaway <amount>" },
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

            { "kit_list_header", "<color=cyan>--- Available KizShop Kits ---</color>" },
            { "kit_list_entry", "<color=yellow>{0}</color> ({1}) - <color=green>{2}</color>: {3}" },
            { "kit_list_footer", "Use /buykit <name> or /kit <name> to purchase." },
            { "kit_none_available", "<color=yellow>No kits are currently available in the shop.</color>" },
            { "kit_not_found", "<color=red>Kit '{0}' not found in shop.</color>" },
            { "kit_disabled", "<color=red>This kit is currently disabled.</color>" },
            { "kit_no_permission", "<color=red>You do not have permission to purchase this kit.</color>" },
            { "kit_cooldown", "<color=red>You must wait {0}s before purchasing this kit again.</color>" },
            { "kit_insufficient_funds", "<color=red>You need {0} to purchase {1}, but have {2}.</color>" },
            { "kit_no_space", "<color=red>You do not have enough inventory space to receive this kit.</color>" },
            { "kit_delivery_failed", "<color=red>Failed to deliver kit items into your inventory. Payment was refunded.</color>" },
            { "kit_success", "<color=green>Successfully purchased {0} for {1}! New balance: {2}.</color>" },
            { "daily_reminder", "<color=yellow>⭐ Your daily login reward is ready! Type <color=green>/daily</color> to claim it!</color>" },

            { "reward_player_kill", "<color=green>+ {0} for eliminating {1}!</color>" },
            { "reward_zombie_kill", "<color=green>+ {0} for zombie kill.</color>" },
            { "reward_mega_zombie_kill", "<color=yellow>+ {0} for MEGA zombie kill!</color>" },
            { "reward_playtime", "<color=green>+ {0} playtime income reward!</color>" },

            { "kizshop_admin_usage", "Usage: /kizshop <version|reload|additem|removeitem|setitemprice|addveh|removeveh|setvehprice|setcategory|resetpricing|import|giveaway>" },
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
            { "kizshop_veh_price_set", "<color=green>Shop vehicle ID {0} prices updated.</color>" },
            { "kizshop_setcategory_usage", "Usage: /kizshop setcategory <id> <category>" },
            { "kizshop_category_set", "<color=green>Category for item ID {0} set to '{1}'.</color>" },
            { "kizshop_resetpricing_usage", "Usage: /kizshop resetpricing <id|all>" },
            { "kizshop_resetpricing_all_success", "<color=green>Dynamic pricing pressure reset for all items.</color>" },
            { "kizshop_resetpricing_item_success", "<color=green>Dynamic pricing pressure reset for item {0}.</color>" },
            { "kizshop_import_usage", "Usage: /kizshop import uconomy <mysql|csv> [overwrite] [apply]" },
            { "no_permission", "<color=red>You do not have permission to execute this command.</color>" },

            { "combat_tagged", "You cannot use the shop for {0}s after combat." },

            { "shop_categories_header", "<color=cyan>--- KizShop Categories ---</color>" },
            { "shop_category_entry", "<color=yellow>{0}</color>: <color=green>{1}</color> items" },
            { "shop_categories_footer", "View category: /shop <category> [page] | View all: /shop <page>" },
            { "shop_search_usage", "Usage: /shop search <text> [page]" },
            { "shop_search_no_results", "<color=yellow>No items found matching '{0}'.</color>" },
            { "shop_header_context", "<color=cyan>--- KizShop: {0} (Page {1}/{2}) ---</color>" },
            { "category_not_found", "<color=red>Category '{0}' not found.</color>" },
            { "ambiguous_category", "<color=yellow>Multiple matching categories: {0}. Please be more specific.</color>" },

            { "boss_reward_received", "<color=yellow>You received {0} for defeating {1}!</color>" },

            { "sell_hand_empty", "<color=red>You are not holding any item in your hands.</color>" },
            { "sell_hand_not_sellable", "<color=red>The item in your hands cannot be sold.</color>" },
            { "sell_hand_amount_ignored", "<color=yellow>Note: Held items are sold one at a time. Amount parameter was ignored.</color>" },
            { "sell_all_usage", "Usage: /sell all <category> [confirm]" },
            { "sell_all_no_items", "<color=yellow>No sellable items found in category '{0}'.</color>" },
            { "sell_all_summary_header", "<color=cyan>--- Sell All Summary: {0} ---</color>" },
            { "sell_all_summary_details", "Items found: <color=yellow>{0}</color> ({1} distinct). Total expected payout: <color=green>{2}</color>." },
            { "sell_all_summary_skipped", "<color=gray>Skipped: {0} modified guns, {1} other non-sellable items.</color>" },
            { "sell_all_summary_prompt", "Type <color=green>/sell all {0} confirm</color> within 30 seconds to proceed." },
            { "sell_all_no_pending", "<color=red>No pending sell all confirmation found. Run /sell all <category> first.</color>" },
            { "sell_all_expired", "<color=red>Confirmation expired. Run /sell all <category> again.</color>" },
            { "sell_all_inventory_changed", "<color=red>Your inventory changed since the summary. Run /sell all {0} again.</color>" },
            { "sell_all_success", "<color=green>Sold {0} items for a total of {1}. New balance: {2}.</color>" },

            { "bounty_disabled", "<color=red>Bounties are currently disabled.</color>" },
            { "bounty_usage", "Usage: /bounty <set|list|me> [player] [amount]" },
            { "bounty_set_usage", "Usage: /bounty set <player|steamid> <amount>" },
            { "bounty_amount_invalid", "<color=red>Bounty amount must be at least {0}.</color>" },
            { "bounty_target_not_found", "<color=red>Target player '{0}' not found.</color>" },
            { "bounty_self", "<color=red>You cannot place a bounty on yourself.</color>" },
            { "bounty_max_target", "<color=red>This would exceed the maximum active bounty on this target ({0}).</color>" },
            { "bounty_insufficient_funds", "<color=red>You need {0} to place this bounty, but only have {1}.</color>" },
            { "bounty_set_success", "<color=green>Bounty of {0} placed on {1}!</color>" },
            { "bounty_failed", "<color=red>Failed to place bounty, escrow refunded.</color>" },
            { "bounty_broadcast", "<color=yellow>[BOUNTY] {0} placed a bounty of {1} on {2}!</color>" },
            { "bounty_list_header", "<color=cyan>--- Active Bounties (Page {0}/{1}) ---</color>" },
            { "bounty_list_entry", "<color=yellow>{0}</color>: <color=green>{1}</color>" },
            { "bounty_list_empty", "No active bounties." },
            { "bounty_me", "Active bounty on you: <color=yellow>{0}</color>" },
            { "bounty_claimed_killer", "<color=yellow>+ {0} bounty claimed for eliminating {1}!</color>" },
            { "bounty_claimed_broadcast", "<color=yellow>[BOUNTY CLAIMED] {0} collected the {1} bounty on {2}!</color>" },

            { "daily_disabled", "<color=red>Daily rewards are currently disabled.</color>" },
            { "daily_already_claimed", "<color=yellow>You already claimed your daily reward today! Next reset in {0}.</color>" },
            { "daily_payout_failed", "<color=red>Failed to deposit daily reward. Please contact an admin.</color>" },
            { "daily_success", "<color=green>Claimed daily reward: {0}! Current streak: {1}/{2} days.</color>" },

            { "giveaway_usage", "Usage: /giveaway <amount>" },
            { "giveaway_invalid_amount", "<color=red>Invalid giveaway amount: {0}</color>" },
            { "giveaway_no_players", "<color=yellow>No online players found to receive giveaway.</color>" },
            { "giveaway_broadcast", "<color=yellow>[GIVEAWAY] {0} gave {1} to all online players ({2} players)!</color>" },
            { "giveaway_success", "<color=green>Successfully distributed {0} to {1} online players.</color>" }
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
                Configuration.Instance.Normalize();
                MigrateTranslationsOnDisk();

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

                CombatTag = new CombatTagService(() => Configuration.Instance, Clock);

                Transactions = new ShopTransactionService(
                    Shop,
                    Economy,
                    new UnturnedInventoryGateway(),
                    new UnturnedVehicleGateway(),
                    () => Configuration.Instance,
                    Clock,
                    CombatTag,
                    Storage);

                // 4. Initialize Discord Services & Webhooks
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

                Transactions.OnKitPurchased += (steamId, entry, price, newBal) =>
                {
                    _ = DiscordWebhook?.SendPurchaseAlertAsync(steamId, $"Kit {entry.DisplayName ?? entry.Name}", 1, price, newBal);
                    if (price.ToDecimal() >= Configuration.Instance.DiscordLargeTransactionThreshold)
                    {
                        _ = DiscordWebhook?.SendLargeTransactionAlertAsync(steamId, "KitPurchase", price, entry.DisplayName ?? entry.Name);
                    }
                };

                // 5. Hook Game Events
                UnturnedPlayerEvents.OnPlayerDeath += HandlePlayerDeath;
                UnturnedPlayerEvents.OnPlayerUpdateStat += HandlePlayerStat;
                DamageTool.damagePlayerRequested += HandleDamagePlayerRequested;
                Provider.onEnemyConnected += HandleEnemyConnected;
                Provider.onEnemyDisconnected += HandleEnemyDisconnected;
                Level.onPostLevelLoaded += HandlePostLevelLoaded;

                if (Level.isLoaded)
                {
                    Dispatcher.QueueOnMainThread(ValidateCatalog);
                }

                // 6. Start Loops and background recovery
                StartPlaytimeLoop();
                StartBountySweepLoop();
                _ = ResumeUnpaidRewardPayoutsAsync();
                _ = ProcessExpiredBountiesSweepAsync();

                int mainThreadId = (Dispatcher as RocketMainThreadDispatcher)?.MainThreadId ?? Thread.CurrentThread.ManagedThreadId;
                Logger.Log(GetBuildIdentification(mainThreadId));
            }
            catch (Exception ex)
            {
                Logger.LogError($"[KizShop] Unexpected error loading plugin: {ex}. Unloading.");
                UnloadPlugin();
            }
        }

        protected override void Unload()
        {
            Level.onPostLevelLoaded -= HandlePostLevelLoaded;

            StopPlaytimeLoop();
            StopLogPruneLoop();
            StopBountySweepLoop();

            UnturnedPlayerEvents.OnPlayerDeath -= HandlePlayerDeath;
            UnturnedPlayerEvents.OnPlayerUpdateStat -= HandlePlayerStat;
            DamageTool.damagePlayerRequested -= HandleDamagePlayerRequested;
            Provider.onEnemyConnected -= HandleEnemyConnected;
            Provider.onEnemyDisconnected -= HandleEnemyDisconnected;

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
            Dispatcher.QueueOnMainThread(ValidateCatalog);
            StopPlaytimeLoop();
            StartPlaytimeLoop();
            StopLogPruneLoop();
            StartLogPruneLoop();

            DiscordBot?.Stop();
            DiscordBot?.Start();
        }

        private void HandlePostLevelLoaded(int level)
        {
            Dispatcher.QueueOnMainThread(ValidateCatalog);
        }

        public void ValidateCatalog()
        {
            try
            {
                var items = Shop?.GetItems();
                if (items == null) return;

                foreach (var item in items)
                {
                    var asset = Assets.find(EAssetType.ITEM, item.Id) as ItemAsset;
                    if (asset == null)
                    {
                        Logger.LogWarning($"[KizShop] Catalog item ID {item.Id} ('{item.Name}') not found in game assets! Marking unavailable.");
                        item.Available = false;
                    }
                    else
                    {
                        item.Available = true;
                        Logger.Log($"[KizShop] Catalog item ID {item.Id}: assetName='{asset.itemName}', size={asset.size_x}x{asset.size_y}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[KizShop] Catalog asset validation encountered error: {ex.Message}");
            }
        }

        public static string GetBuildIdentification(int mainThreadId)
        {
            var asm = typeof(KizShopPlugin).Assembly;
            var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? asm.GetName().Version?.ToString();

            DateTime buildTime;
            try
            {
                string loc = asm.Location;
                buildTime = !string.IsNullOrEmpty(loc) && File.Exists(loc)
                    ? File.GetLastWriteTimeUtc(loc)
                    : DateTime.UtcNow;
            }
            catch
            {
                buildTime = DateTime.UtcNow;
            }

            return $"[KizShop] Loaded v{infoVer} (build {buildTime:yyyy-MM-dd HH:mm:ss} UTC), main thread id {mainThreadId}";
        }

        public string FormatMoney(Money amount)
        {
            return Translate("currency_format", Configuration.Instance.CurrencySymbol, amount.ToDecimal(), Configuration.Instance.CurrencyName);
        }

        public void MigrateTranslationsOnDisk(string directoryPath = null)
        {
            try
            {
                string targetDir = directoryPath ?? Directory;
                if (string.IsNullOrEmpty(targetDir) || !System.IO.Directory.Exists(targetDir)) return;

                foreach (var file in System.IO.Directory.GetFiles(targetDir, "*.translation.xml"))
                {
                    string content = System.IO.File.ReadAllText(file);
                    if (content.IndexOf("gold", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string updated = System.Text.RegularExpressions.Regex.Replace(
                            content,
                            @"(&lt;|<)color\s*=\s*['""]?gold['""]?(&gt;|>)",
                            "$1color=yellow$2",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (content != updated)
                        {
                            System.IO.File.WriteAllText(file, updated);
                        }
                    }
                }
            }
            catch
            {
                // Ignore migration exceptions during headless or permission-restricted environments
            }
        }

        private void HandlePlayerDeath(UnturnedPlayer player, EDeathCause cause, ELimb limb, CSteamID murderer)
        {
            if (player == null || player.Player == null) return;

            CombatTag?.Clear(player.CSteamID.m_SteamID);

            if (murderer == CSteamID.Nil || murderer == player.CSteamID) return; // No suicide

            var killer = UnturnedPlayer.FromCSteamID(murderer);
            if (killer == null || killer.Player == null) return;

            // Check same-group members (Unturned quests groupID)
            if (killer.Player.quests != null && player.Player.quests != null &&
                killer.Player.quests.groupID != CSteamID.Nil &&
                killer.Player.quests.groupID == player.Player.quests.groupID)
            {
                return; // Same group: no reward or bounty
            }

            // 1. Process Kill Reward (if enabled and not on cooldown)
            if (Configuration.Instance.PlayerKillRewardEnabled &&
                Money.TryFromDecimal(Configuration.Instance.PlayerKillRewardAmount, Configuration.Instance.MaxTransactionAmount, out var reward, out _))
            {
                var key = (killer: killer.CSteamID.m_SteamID, victim: player.CSteamID.m_SteamID);
                var now = DateTime.UtcNow;
                bool onCooldown = false;
                if (_killCooldowns.TryGetValue(key, out var lastKill))
                {
                    if ((now - lastKill).TotalSeconds < Configuration.Instance.PlayerKillRewardCooldownSeconds)
                    {
                        onCooldown = true;
                    }
                }

                if (!onCooldown)
                {
                    _killCooldowns[key] = now;
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
            }

            // 2. Process Bounty Claim (bounties are NOT blocked by kill reward cooldown)
            if (Storage != null && Configuration.Instance.BountyEnabled)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var (claimedEntries, claimedTotal) = await Storage.ClaimBountiesAsync(player.CSteamID.m_SteamID, killer.CSteamID.m_SteamID).ConfigureAwait(false);
                        if (claimedTotal > Money.Zero)
                        {
                            var dep = await Economy.DepositAsync(killer.CSteamID.m_SteamID, claimedTotal, $"BountyClaim:{player.CharacterName}").ConfigureAwait(false);
                            if (!dep.Success)
                            {
                                Logger.LogError($"[KizShop] Deposit failed for claimed bounty of {claimedTotal} to {killer.CSteamID}. Re-creating original bounty rows.");
                                foreach (var entry in claimedEntries)
                                {
                                    await Storage.CreateBountyAsync(entry.PlacerSteamId, entry.TargetSteamId, entry.EscrowAmount, entry.NetAmount, entry.CreatedUtc).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                Dispatcher.QueueOnMainThread(() =>
                                {
                                    var formatted = FormatMoney(claimedTotal);
                                    KizChat.Say(killer, Translate("bounty_claimed_killer", formatted, player.CharacterName), UnityEngine.Color.green);
                                    if (Configuration.Instance.BountyAnnounceInChat)
                                    {
                                        KizChat.Broadcast(Translate("bounty_claimed_broadcast", killer.CharacterName, formatted, player.CharacterName), UnityEngine.Color.yellow);
                                    }
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"[KizShop] Error processing bounty claim: {ex}");
                    }
                });
            }
        }

        private void HandleDamagePlayerRequested(ref DamagePlayerParameters parameters, ref bool shouldAllow)
        {
            if (!shouldAllow) return;
            if (parameters.player == null || parameters.killer == CSteamID.Nil) return;

            ulong victimSteamId = 0;
            try
            {
                victimSteamId = parameters.player.channel.owner.playerID.steamID.m_SteamID;
            }
            catch { }

            ulong attackerSteamId = parameters.killer.m_SteamID;
            if (victimSteamId == 0 || attackerSteamId == 0 || victimSteamId == attackerSteamId) return;

            // Defer combat tagging to ensure other damage handlers did not cancel/modify damage,
            // and skip tagging if victim's health did not change (compare player.life.health before/after).
            var life = parameters.player.life;
            if (life == null) return;
            byte healthBefore = life.health;

            Dispatcher.QueueOnMainThread(() =>
            {
                try
                {
                    if (life == null) return;
                    if (life.isDead || life.health < healthBefore)
                    {
                        CombatTag?.Tag(attackerSteamId, victimSteamId);
                    }
                }
                catch { }
            });
        }

        private void HandleEnemyConnected(SteamPlayer player)
        {
            try
            {
                if (player != null && player.playerID != null)
                {
                    ulong steamId = player.playerID.steamID.m_SteamID;
                    _ = Economy?.GetBalanceAsync(steamId);

                    if (Configuration.Instance.DailyRewardEnabled && Configuration.Instance.DailyReminderOnJoin && Storage != null)
                    {
                        Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(4000).ConfigureAwait(false);
                                var clock = Clock;
                                DateTime nowUtc = clock.UtcNow;
                                DateTime localNow = nowUtc.AddHours(Configuration.Instance.DailyResetUtcOffsetHours);
                                int currentDayIndex = (int)(localNow.Date - new DateTime(2000, 1, 1)).TotalDays;

                                if (Storage != null && await Storage.CanClaimDailyAsync(steamId, currentDayIndex).ConfigureAwait(false))
                                {
                                    Dispatcher.QueueOnMainThread(() =>
                                    {
                                        var uPlayer = UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
                                        if (uPlayer != null)
                                        {
                                            KizChat.Say(uPlayer, Translate("daily_reminder"), UnityEngine.Color.yellow);
                                        }
                                    });
                                }
                            }
                            catch { }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[KizShop] Error in HandleEnemyConnected: {ex.Message}");
            }
        }

        private void HandleEnemyDisconnected(SteamPlayer player)
        {
            try
            {
                if (player != null && player.playerID != null)
                {
                    ulong steamId = player.playerID.steamID.m_SteamID;
                    PlayerLock.Release(steamId);
                    CombatTag?.Clear(steamId);
                    Economy?.InvalidateCache(steamId);
                    Transactions?.ClearPlayerState(steamId);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[KizShop] Error in HandleEnemyDisconnected: {ex.Message}");
            }
        }

        private void StartBountySweepLoop()
        {
            _bountySweepCts = new CancellationTokenSource();
            var token = _bountySweepCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromHours(1), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested) break;

                    await ProcessExpiredBountiesSweepAsync().ConfigureAwait(false);
                }
            }, token);
        }

        private async Task ProcessExpiredBountiesSweepAsync()
        {
            if (Storage == null || Economy == null || !Configuration.Instance.BountyEnabled) return;
            try
            {
                int expiryDays = Configuration.Instance.BountyExpiryDays;
                if (expiryDays <= 0) expiryDays = 7;
                var cutoff = Clock.UtcNow.AddDays(-expiryDays);

                var expiredList = await Storage.GetAndMarkExpiredBountiesAsync(cutoff).ConfigureAwait(false);
                foreach (var b in expiredList)
                {
                    if (b.PlacerSteamId == 0)
                    {
                        await Storage.ConfirmBountyRefundAsync(b.Id).ConfigureAwait(false);
                        continue;
                    }

                    if (b.NetAmount > Money.Zero)
                    {
                        var dep = await Economy.DepositAsync(b.PlacerSteamId, b.NetAmount, "BountyExpiredRefund", ignoreMaxBalance: true).ConfigureAwait(false);
                        if (dep.Success)
                        {
                            await Storage.ConfirmBountyRefundAsync(b.Id).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await Storage.ConfirmBountyRefundAsync(b.Id).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[KizShop] Error in bounty expiry sweep: {ex}");
            }
        }

        private async Task ResumeUnpaidRewardPayoutsAsync()
        {
            if (Storage == null || Economy == null) return;
            try
            {
                var unpaidList = await Storage.GetUnpaidRewardPayoutsAsync().ConfigureAwait(false);
                foreach (var payout in unpaidList)
                {
                    var dep = await Economy.DepositAsync(payout.SteamId, payout.Amount, $"BossRewardResume:{payout.EventKey}", ignoreMaxBalance: true).ConfigureAwait(false);
                    if (dep.Success)
                    {
                        await Storage.MarkRewardPayoutPaidAsync(payout.Id, DateTime.UtcNow).ConfigureAwait(false);
                    }
                    else
                    {
                        Logger.LogError($"[KizShop] Failed to resume boss reward payout of {payout.Amount} to {payout.SteamId} ({payout.EventKey})!");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[KizShop] Error resuming unpaid boss reward payouts: {ex}");
            }
        }

        private void StopBountySweepLoop()
        {
            if (_bountySweepCts != null)
            {
                _bountySweepCts.Cancel();
                _bountySweepCts.Dispose();
                _bountySweepCts = null;
            }
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
                try
                {
                    if (Economy == null) return;
                    await Economy.DepositAsync(player.CSteamID.m_SteamID, reward, $"ZombieReward:{stat}").ConfigureAwait(false);
                    Dispatcher.QueueOnMainThread(() =>
                    {
                        var formatted = FormatMoney(reward);
                        KizChat.Say(player, Translate(key, formatted), UnityEngine.Color.green);
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[KizShop] Error processing zombie reward: {ex.Message}");
                }
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
                    var steamIds = await Dispatcher.RunOnMainThreadAsync(() =>
                    {
                        var list = new System.Collections.Generic.List<ulong>();
                        var clients = Provider.clients;
                        if (clients != null)
                        {
                            foreach (var client in clients)
                            {
                                if (client != null && client.playerID != null)
                                {
                                    list.Add(client.playerID.steamID.m_SteamID);
                                }
                            }
                        }
                        return list;
                    }).ConfigureAwait(false);

                    if (Economy == null) break;

                    // Perform DB work off main thread concurrently
                    var payoutTasks = new System.Collections.Generic.List<Task>(steamIds.Count);
                    foreach (var sId in steamIds)
                    {
                        if (token.IsCancellationRequested || Economy == null) break;
                        payoutTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                if (Economy == null) return;
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
                            catch (Exception ex)
                            {
                                Logger.LogError($"[KizShop] Error in playtime income distribution: {ex.Message}");
                            }
                        }));
                    }
                    await Task.WhenAll(payoutTasks).ConfigureAwait(false);
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

                    try
                    {
                        var now = DateTime.UtcNow;
                        int cooldownSecs = Configuration.Instance.PlayerKillRewardCooldownSeconds;
                        foreach (var kvp in _killCooldowns)
                        {
                            if ((now - kvp.Value).TotalSeconds >= cooldownSecs)
                            {
                                _killCooldowns.TryRemove(kvp.Key, out _);
                            }
                        }
                    }
                    catch { }
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
