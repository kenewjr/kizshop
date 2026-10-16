using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using KizShop.Common;

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
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                var sender = (UnturnedPlayer)caller;

                if (command.Length < 2)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("pay_usage"), UnityEngine.Color.yellow));
                    return;
                }

                string rawAmountArg = command[command.Length - 1];
                string targetName = string.Join(" ", command, 0, command.Length - 1).Trim();

                if (!decimal.TryParse(rawAmountArg, NumberStyles.Number, CultureInfo.InvariantCulture, out var rawAmount))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("pay_invalid_amount"), UnityEngine.Color.red));
                    return;
                }

                var maxTx = plugin.Configuration.Instance.MaxTransactionAmount;
                if (!Money.TryFromDecimal(rawAmount, maxTx, out var amount, out var moneyErr))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("invalid_money_amount", moneyErr), UnityEngine.Color.red));
                    return;
                }

                // Target resolution
                ulong targetSteamId = 0;
                var targetPlayer = UnturnedPlayer.FromName(targetName);

                if (targetPlayer != null)
                {
                    targetSteamId = targetPlayer.CSteamID.m_SteamID;
                    targetName = targetPlayer.CharacterName;
                }
                else if (ulong.TryParse(targetName, out var parsedId))
                {
                    // Validate SteamID64 range
                    if (parsedId < 76561197960265728ul || parsedId > 76561202255233023ul)
                    {
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("invalid_steamid"), UnityEngine.Color.red));
                        return;
                    }

                    // Must already exist in database — never create accounts for arbitrary IDs
                    bool exists = await plugin.Economy.AccountExistsAsync(parsedId).ConfigureAwait(false);
                    if (!exists)
                    {
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("balance_player_not_found", targetName), UnityEngine.Color.red));
                        return;
                    }
                    targetSteamId = parsedId;
                }

                if (targetSteamId == 0)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("balance_player_not_found", targetName), UnityEngine.Color.red));
                    return;
                }

                if (targetSteamId == sender.CSteamID.m_SteamID)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("pay_self"), UnityEngine.Color.red));
                    return;
                }

                var fromSteamId = sender.CSteamID.m_SteamID;
                var fromName = sender.CharacterName;

                if (!PlayerLock.TryAcquire(fromSteamId))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("transaction_in_flight"), UnityEngine.Color.red));
                    return;
                }

                try
                {
                    var result = await plugin.Economy.TransferAsync(fromSteamId, targetSteamId, amount, "PayCommand").ConfigureAwait(false);

                    plugin.Dispatcher.QueueOnMainThread(() =>
                    {
                        if (result.Success)
                        {
                            var formattedAmount = plugin.FormatMoney(amount);
                            var formattedBal = plugin.FormatMoney(result.FromNewBalance);

                            KizChat.Say(caller, plugin.Translate("pay_sent", formattedAmount, targetName, formattedBal), UnityEngine.Color.green);

                            if (targetPlayer != null)
                            {
                                var formattedTargetBal = plugin.FormatMoney(result.ToNewBalance);
                                KizChat.Say(targetPlayer, plugin.Translate("pay_received", formattedAmount, fromName, formattedTargetBal), UnityEngine.Color.green);
                            }
                        }
                        else
                        {
                            if (result.FromOldBalance < amount)
                            {
                                KizChat.Say(caller, plugin.Translate("pay_insufficient_funds", plugin.FormatMoney(amount)), UnityEngine.Color.red);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("pay_receiver_max_balance", targetName), UnityEngine.Color.red);
                            }
                        }
                    });
                }
                finally
                {
                    PlayerLock.Release(fromSteamId);
                }
            });
        }
    }
}
