using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using KizShop.Common;

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
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;

                if (command.Length < 3)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        UnturnedChat.Say(caller, plugin.Translate("eco_usage"), UnityEngine.Color.yellow));
                    return;
                }

                string subAction = command[0].ToLowerInvariant();
                string targetArg = command[1];

                if (!decimal.TryParse(command[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var rawAmount))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        UnturnedChat.Say(caller, plugin.Translate("pay_invalid_amount"), UnityEngine.Color.red));
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
                    if (parsedId < 76561197960265728ul || parsedId > 76561202255233023ul)
                    {
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            UnturnedChat.Say(caller, plugin.Translate("invalid_steamid"), UnityEngine.Color.red));
                        return;
                    }
                    targetSteamId = parsedId;
                }

                if (targetSteamId == 0)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        UnturnedChat.Say(caller, plugin.Translate("balance_player_not_found", targetArg), UnityEngine.Color.red));
                    return;
                }

                var maxTx = plugin.Configuration.Instance.MaxTransactionAmount;

                switch (subAction)
                {
                    case "give":
                        if (!Money.TryFromDecimal(rawAmount, maxTx, out var giveMoney, out var giveErr))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                UnturnedChat.Say(caller, plugin.Translate("invalid_money_amount", giveErr), UnityEngine.Color.red));
                            return;
                        }

                        var depRes = await plugin.Economy.DepositAsync(targetSteamId, giveMoney, $"EcoGive by {caller.DisplayName}").ConfigureAwait(false);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            var formattedBal = plugin.FormatMoney(depRes.NewBalance);
                            UnturnedChat.Say(caller, plugin.Translate("eco_success", targetName, formattedBal), UnityEngine.Color.green);
                        });
                        break;

                    case "take":
                        if (!Money.TryFromDecimal(rawAmount, maxTx, out var takeMoney, out var takeErr))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                UnturnedChat.Say(caller, plugin.Translate("invalid_money_amount", takeErr), UnityEngine.Color.red));
                            return;
                        }

                        var withRes = await plugin.Economy.TryWithdrawAsync(targetSteamId, takeMoney, $"EcoTake by {caller.DisplayName}").ConfigureAwait(false);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (withRes.Success)
                            {
                                var formattedBal = plugin.FormatMoney(withRes.NewBalance);
                                UnturnedChat.Say(caller, plugin.Translate("eco_success", targetName, formattedBal), UnityEngine.Color.green);
                            }
                            else
                            {
                                var currentBal = plugin.FormatMoney(withRes.OldBalance);
                                UnturnedChat.Say(caller, plugin.Translate("eco_insufficient_funds", targetName, currentBal), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "set":
                        if (!Money.TryFromDecimalAllowZero(rawAmount, out var setMoney, out var setErr))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                UnturnedChat.Say(caller, plugin.Translate("invalid_money_amount", setErr), UnityEngine.Color.red));
                            return;
                        }

                        await plugin.Economy.SetBalanceAsync(targetSteamId, setMoney, $"EcoSet by {caller.DisplayName}").ConfigureAwait(false);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            var formattedBal = plugin.FormatMoney(setMoney);
                            UnturnedChat.Say(caller, plugin.Translate("eco_success", targetName, formattedBal), UnityEngine.Color.green);
                        });
                        break;

                    default:
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            UnturnedChat.Say(caller, plugin.Translate("eco_usage"), UnityEngine.Color.yellow));
                        break;
                }
            });
        }
    }
}
