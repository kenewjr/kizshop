using System.Collections.Generic;
using System.Threading.Tasks;
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
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;

                // Check other player
                if (command.Length >= 1)
                {
                    if (!caller.HasPermission("kizshop.balance.others") && !caller.IsAdmin)
                    {
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("balance_no_permission_others"), UnityEngine.Color.red));
                        return;
                    }

                    ulong targetSteamId = 0;
                    string targetName = string.Join(" ", command).Trim();

                    var targetPlayer = UnturnedPlayer.FromName(targetName);
                    if (targetPlayer != null)
                    {
                        targetSteamId = targetPlayer.CSteamID.m_SteamID;
                        targetName = targetPlayer.CharacterName;
                    }
                    else if (ulong.TryParse(targetName, out var parsedId))
                    {
                        if (parsedId < 76561197960265728ul || parsedId > 76561202255233023ul)
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("invalid_steamid"), UnityEngine.Color.red));
                            return;
                        }

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

                    var bal = await plugin.Economy.GetBalanceAsync(targetSteamId).ConfigureAwait(false);
                    var formatted = plugin.FormatMoney(bal);
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("balance_other", targetName, formatted), UnityEngine.Color.yellow));
                    return;
                }

                // Check self
                if (caller is UnturnedPlayer player)
                {
                    var bal = await plugin.Economy.GetBalanceAsync(player.CSteamID.m_SteamID).ConfigureAwait(false);
                    var formatted = plugin.FormatMoney(bal);
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("balance_self", formatted), UnityEngine.Color.green));
                }
                else
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("balance_usage"), UnityEngine.Color.yellow));
                }
            });
        }
    }
}
