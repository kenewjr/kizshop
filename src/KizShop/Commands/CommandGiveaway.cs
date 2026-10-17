using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Player;
using SDG.Unturned;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandGiveaway : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "giveaway";
        public string Help => "Admin command to distribute money to all currently online players.";
        public string Syntax => "<amount|confirm>";
        public List<string> Aliases => new List<string> { "giveall" };
        public List<string> Permissions => new List<string> { "kizshop.giveaway", "kizshop.admin" };

        private static readonly ConcurrentDictionary<string, (Money Amount, DateTime ExpiresUtc)> _pendingGiveaways =
            new ConcurrentDictionary<string, (Money, DateTime)>();

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                await ExecuteGiveawayAsync(caller, command).ConfigureAwait(false);
            });
        }

        public static async Task ExecuteGiveawayAsync(IRocketPlayer caller, string[] command)
        {
            var plugin = KizShopPlugin.Instance;
            var cfg = plugin.Configuration.Instance;

            if (command.Length < 1)
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("giveaway_usage"), UnityEngine.Color.yellow));
                return;
            }

            string callerKey = caller?.Id ?? "console";
            bool isConsole = caller is ConsolePlayer || string.Equals(callerKey, "console", StringComparison.OrdinalIgnoreCase);

            Money moneyAmount;

            if (string.Equals(command[0], "confirm", StringComparison.OrdinalIgnoreCase))
            {
                if (!_pendingGiveaways.TryRemove(callerKey, out var pending))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("giveaway_no_pending"), UnityEngine.Color.red));
                    return;
                }

                if (DateTime.UtcNow > pending.ExpiresUtc)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("giveaway_expired"), UnityEngine.Color.red));
                    return;
                }

                moneyAmount = pending.Amount;
            }
            else
            {
                if (!decimal.TryParse(command[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var amountDec) || amountDec <= 0)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("giveaway_invalid_amount", command[0]), UnityEngine.Color.red));
                    return;
                }

                if (cfg.GiveawayMaxPerPlayer > 0m && amountDec > cfg.GiveawayMaxPerPlayer)
                {
                    var formattedCap = plugin.FormatMoney(Money.FromDecimal(cfg.GiveawayMaxPerPlayer));
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("giveaway_exceeds_max", amountDec, formattedCap), UnityEngine.Color.red));
                    return;
                }

                if (!Money.TryFromDecimal(amountDec, cfg.MaxTransactionAmount, out moneyAmount, out var moneyErr))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("invalid_money_amount", moneyErr), UnityEngine.Color.red));
                    return;
                }

                if (!isConsole)
                {
                    var count = await plugin.Dispatcher.RunOnMainThreadAsync(() =>
                    {
                        var clients = Provider.clients;
                        int c = 0;
                        if (clients != null)
                        {
                            foreach (var cl in clients)
                            {
                                if (cl != null && cl.playerID != null) c++;
                            }
                        }
                        return c;
                    }).ConfigureAwait(false);

                    if (count == 0)
                    {
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("giveaway_no_players"), UnityEngine.Color.yellow));
                        return;
                    }

                    _pendingGiveaways[callerKey] = (moneyAmount, DateTime.UtcNow.AddSeconds(30));
                    var totalExpected = Money.FromCents(moneyAmount.Cents * count);
                    plugin.Dispatcher.QueueOnMainThread(() =>
                    {
                        var formattedPer = plugin.FormatMoney(moneyAmount);
                        var formattedTot = plugin.FormatMoney(totalExpected);
                        KizChat.Say(caller, plugin.Translate("giveaway_confirm_prompt", formattedPer, count, formattedTot), UnityEngine.Color.yellow);
                    });
                    return;
                }
            }

            // Distribute to online players
            var onlineSteamIds = await plugin.Dispatcher.RunOnMainThreadAsync(() =>
            {
                var list = new List<ulong>();
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

            if (onlineSteamIds.Count == 0)
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("giveaway_no_players"), UnityEngine.Color.yellow));
                return;
            }

            int successCount = 0;
            string giverName = caller?.DisplayName ?? "Console";

            var tasks = new List<Task>(onlineSteamIds.Count);
            foreach (var steamId in onlineSteamIds)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var dep = await plugin.Economy.DepositAsync(steamId, moneyAmount, $"Giveaway:{giverName}", ignoreMaxBalance: false).ConfigureAwait(false);
                    if (dep.Success)
                    {
                        System.Threading.Interlocked.Increment(ref successCount);
                    }
                }));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Audit log & Discord webhook
            ulong adminSteamId = 0;
            if (caller is UnturnedPlayer up)
            {
                adminSteamId = up.CSteamID.m_SteamID;
            }
            var totalDistributed = Money.FromCents(moneyAmount.Cents * successCount);

            if (plugin.Storage != null)
            {
                try
                {
                    await plugin.Storage.LogTransactionAsync(
                        adminSteamId,
                        "Giveaway",
                        totalDistributed,
                        $"Admin: {giverName} ({adminSteamId}), PerPlayer: {moneyAmount.ToDecimal()}, Recipients: {successCount}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] Failed to log giveaway audit transaction: {ex}");
                }
            }

            if (plugin.DiscordWebhook != null)
            {
                _ = plugin.DiscordWebhook.SendGiveawayAlertAsync(giverName, adminSteamId, moneyAmount, successCount, totalDistributed);
            }

            plugin.Dispatcher.QueueOnMainThread(() =>
            {
                var formatted = plugin.FormatMoney(moneyAmount);
                if (successCount > 0)
                {
                    KizChat.Broadcast(plugin.Translate("giveaway_broadcast", giverName, formatted, successCount), UnityEngine.Color.yellow);
                }
                KizChat.Say(caller, plugin.Translate("giveaway_success", formatted, successCount), UnityEngine.Color.green);
            });
        }
    }
}
