using System;
using System.Collections.Generic;
using System.Globalization;
using Rocket.API;
using Rocket.Core.Utils;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;

namespace KizShop.Commands
{
    public class CommandEco : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "eco";
        public string Help => "Admin command to manage player balances.";
        public string Syntax => "<give|take|set> <player|steamid> <amount>";
        public List<string> Aliases => new List<string> { "economy" };
        public List<string> Permissions => new List<string> { "kizshop.eco", "kizshop.admin" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            var plugin = KizShopPlugin.Instance;
            if (plugin == null || plugin.Economy == null) return;

            if (command.Length < 3)
            {
                UnturnedChat.Say(caller, plugin.Translate("eco_usage"), UnityEngine.Color.yellow);
                return;
            }

            string subAction = command[0].ToLowerInvariant();
            string targetArg = command[1];
            if (!decimal.TryParse(command[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            {
                UnturnedChat.Say(caller, plugin.Translate("pay_invalid_amount"), UnityEngine.Color.red);
                return;
            }

            ulong targetSteamId = 0;
            string targetName = targetArg;
            var targetPlayer = UnturnedPlayer.FromName(targetArg);
            if (targetPlayer != null)
            {
                targetSteamId = targetPlayer.CSteamID.m_SteamID;
                targetName = targetPlayer.CharacterName;
            }
            else if (ulong.TryParse(targetArg, out var parsedId))
            {
                targetSteamId = parsedId;
            }

            if (targetSteamId == 0)
            {
                UnturnedChat.Say(caller, plugin.Translate("balance_player_not_found", targetArg), UnityEngine.Color.red);
                return;
            }

            switch (subAction)
            {
                case "give":
                    _ = plugin.Economy.DepositAsync(targetSteamId, amount, $"EcoGive by {caller.DisplayName}").ContinueWith(t =>
                    {
                        var newBal = t.Result;
                        TaskDispatcher.QueueOnMainThread(() =>
                        {
                            var formattedBal = plugin.FormatMoney(newBal);
                            UnturnedChat.Say(caller, plugin.Translate("eco_success", targetName, formattedBal), UnityEngine.Color.green);
                        });
                    });
                    break;

                case "take":
                    _ = plugin.Economy.TryWithdrawAsync(targetSteamId, amount, $"EcoTake by {caller.DisplayName}").ContinueWith(t =>
                    {
                        TaskDispatcher.QueueOnMainThread(async () =>
                        {
                            var newBal = await plugin.Economy.GetBalanceAsync(targetSteamId);
                            var formattedBal = plugin.FormatMoney(newBal);
                            UnturnedChat.Say(caller, plugin.Translate("eco_success", targetName, formattedBal), UnityEngine.Color.green);
                        });
                    });
                    break;

                case "set":
                    _ = plugin.Economy.SetBalanceAsync(targetSteamId, amount, $"EcoSet by {caller.DisplayName}").ContinueWith(t =>
                    {
                        TaskDispatcher.QueueOnMainThread(() =>
                        {
                            var formattedBal = plugin.FormatMoney(amount);
                            UnturnedChat.Say(caller, plugin.Translate("eco_success", targetName, formattedBal), UnityEngine.Color.green);
                        });
                    });
                    break;

                default:
                    UnturnedChat.Say(caller, plugin.Translate("eco_usage"), UnityEngine.Color.yellow);
                    break;
            }
        }
    }
}
