using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Rocket.API;
using SDG.Unturned;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandGiveaway : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "giveaway";
        public string Help => "Admin command to distribute money to all currently online players.";
        public string Syntax => "<amount>";
        public List<string> Aliases => new List<string> { "giveall" };
        public List<string> Permissions => new List<string> { "kizshop.giveaway", "kizshop.admin" };

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

            if (!decimal.TryParse(command[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var amountDec) || amountDec <= 0)
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("giveaway_invalid_amount", command[0]), UnityEngine.Color.red));
                return;
            }

            if (!Money.TryFromDecimal(amountDec, cfg.MaxTransactionAmount, out var moneyAmount, out var moneyErr))
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("invalid_money_amount", moneyErr), UnityEngine.Color.red));
                return;
            }

            // Collect currently online players only
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
