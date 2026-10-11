using System;
using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandBalance : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "balance";
        public string Help => "Check your balance or another player's balance.";
        public string Syntax => "[player]";
        public List<string> Aliases => new List<string> { "bal" };
        public List<string> Permissions => new List<string> { "kizshop.balance" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            var plugin = KizShopPlugin.Instance;
            if (plugin == null || plugin.Economy == null) return;

            // Target someone else
            if (command.Length >= 1)
            {
                if (!caller.HasPermission("kizshop.balance.others") && !caller.IsAdmin)
                {
                    UnturnedChat.Say(caller, plugin.Translate("balance_no_permission_others"), UnityEngine.Color.red);
                    return;
                }

                ulong targetSteamId = 0;
                string targetName = command[0];

                var targetPlayer = UnturnedPlayer.FromName(targetName);
                if (targetPlayer != null)
                {
                    targetSteamId = targetPlayer.CSteamID.m_SteamID;
                    targetName = targetPlayer.CharacterName;
                }
                else if (ulong.TryParse(targetName, out var parsedId))
                {
                    targetSteamId = parsedId;
                }

                if (targetSteamId == 0)
                {
                    UnturnedChat.Say(caller, plugin.Translate("balance_player_not_found", command[0]), UnityEngine.Color.red);
                    return;
                }

                _ = plugin.Economy.GetBalanceAsync(targetSteamId).ContinueWith(t =>
                {
                    var bal = t.Result;
                    Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                    {
                        var formatted = plugin.FormatMoney(bal);
                        UnturnedChat.Say(caller, plugin.Translate("balance_other", targetName, formatted), UnityEngine.Color.yellow);
                    });
                });
                return;
            }

            // Check own balance
            if (caller is UnturnedPlayer player)
            {
                _ = plugin.Economy.GetBalanceAsync(player.CSteamID.m_SteamID).ContinueWith(t =>
                {
                    var bal = t.Result;
                    Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                    {
                        var formatted = plugin.FormatMoney(bal);
                        UnturnedChat.Say(caller, plugin.Translate("balance_self", formatted), UnityEngine.Color.green);
                    });
                });
            }
            else
            {
                UnturnedChat.Say(caller, plugin.Translate("balance_usage"), UnityEngine.Color.yellow);
            }
        }
    }
}
