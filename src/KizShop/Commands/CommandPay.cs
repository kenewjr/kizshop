using System;
using System.Collections.Generic;
using System.Globalization;
using Rocket.API;
using Rocket.Core.Utils;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;

namespace KizShop.Commands
{
    public class CommandPay : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "pay";
        public string Help => "Transfer money to another player.";
        public string Syntax => "<player|steamid> <amount>";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "kizshop.pay" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            var plugin = KizShopPlugin.Instance;
            if (plugin == null || plugin.Economy == null) return;

            var sender = (UnturnedPlayer)caller;
            if (command.Length < 2)
            {
                UnturnedChat.Say(caller, plugin.Translate("pay_usage"), UnityEngine.Color.yellow);
                return;
            }

            if (!decimal.TryParse(command[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            {
                UnturnedChat.Say(caller, plugin.Translate("pay_invalid_amount"), UnityEngine.Color.red);
                return;
            }

            // Target resolution
            ulong targetSteamId = 0;
            string targetName = command[0];
            UnturnedPlayer targetPlayer = UnturnedPlayer.FromName(targetName);

            if (targetPlayer != null)
            {
                targetSteamId = targetPlayer.CSteamID.m_SteamID;
                targetName = targetPlayer.CharacterName;
            }
            else if (ulong.TryParse(command[0], out var parsedId))
            {
                targetSteamId = parsedId;
            }

            if (targetSteamId == 0)
            {
                UnturnedChat.Say(caller, plugin.Translate("balance_player_not_found", command[0]), UnityEngine.Color.red);
                return;
            }

            if (targetSteamId == sender.CSteamID.m_SteamID)
            {
                UnturnedChat.Say(caller, plugin.Translate("pay_self"), UnityEngine.Color.red);
                return;
            }

            var fromSteamId = sender.CSteamID.m_SteamID;
            var fromName = sender.CharacterName;

            _ = plugin.Economy.TransferAsync(fromSteamId, targetSteamId, amount, "PayCommand").ContinueWith(t =>
            {
                var success = t.Result;
                TaskDispatcher.QueueOnMainThread(async () =>
                {
                    if (success)
                    {
                        var newBal = await plugin.Economy.GetBalanceAsync(fromSteamId);
                        var formattedAmount = plugin.FormatMoney(amount);
                        var formattedBal = plugin.FormatMoney(newBal);

                        UnturnedChat.Say(caller, plugin.Translate("pay_sent", formattedAmount, targetName, formattedBal), UnityEngine.Color.green);

                        if (targetPlayer != null)
                        {
                            var targetBal = await plugin.Economy.GetBalanceAsync(targetSteamId);
                            var formattedTargetBal = plugin.FormatMoney(targetBal);
                            UnturnedChat.Say(targetPlayer, plugin.Translate("pay_received", formattedAmount, fromName, formattedTargetBal), UnityEngine.Color.green);
                        }
                    }
                    else
                    {
                        var curBal = await plugin.Economy.GetBalanceAsync(fromSteamId);
                        if (curBal < amount)
                        {
                            UnturnedChat.Say(caller, plugin.Translate("pay_insufficient_funds", plugin.FormatMoney(amount)), UnityEngine.Color.red);
                        }
                        else
                        {
                            UnturnedChat.Say(caller, plugin.Translate("pay_receiver_max_balance", targetName), UnityEngine.Color.red);
                        }
                    }
                });
            });
        }
    }
}
