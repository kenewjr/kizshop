using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandSell : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "sell";
        public string Help => "Sell items to the shop.";
        public string Syntax => "<item name|id|guid> [amount|all]";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "kizshop.sell" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                var player = (UnturnedPlayer)caller;

                if (command.Length < 1)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        UnturnedChat.Say(caller, plugin.Translate("sell_usage"), UnityEngine.Color.yellow));
                    return;
                }

                string query = command[0];
                string amountArg = command.Length >= 2 ? command[1] : "1";

                var result = await plugin.Transactions.ExecuteSellAsync(
                    player.CSteamID.m_SteamID,
                    query,
                    amountArg,
                    node => caller.HasPermission(node),
                    caller.IsAdmin).ConfigureAwait(false);

                plugin.Dispatcher.QueueOnMainThread(() =>
                {
                    if (result.Success)
                    {
                        var payoutStr = plugin.FormatMoney(result.Price);
                        var balStr = plugin.FormatMoney(result.NewBalance);
                        UnturnedChat.Say(caller, plugin.Translate("sell_success", result.Quantity, result.ItemEntry.Name, payoutStr, balStr), UnityEngine.Color.green);
                    }
                    else if (result.CandidateNames != null && result.CandidateNames.Count > 0)
                    {
                        string candidates = string.Join(", ", result.CandidateNames);
                        UnturnedChat.Say(caller, plugin.Translate("ambiguous_match", candidates), UnityEngine.Color.yellow);
                    }
                    else
                    {
                        UnturnedChat.Say(caller, plugin.Translate(result.ErrorKey, result.ErrorArgs ?? new object[0]), UnityEngine.Color.red);
                    }
                });
            });
        }
    }
}
