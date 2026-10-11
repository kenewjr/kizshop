using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandBuy : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "buy";
        public string Help => "Buy items from the shop.";
        public string Syntax => "<item name|id|guid> [amount]";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "kizshop.buy" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                var player = (UnturnedPlayer)caller;

                if (command.Length < 1)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        UnturnedChat.Say(caller, plugin.Translate("buy_usage"), UnityEngine.Color.yellow));
                    return;
                }

                int amount = 1;
                if (command.Length >= 2 && (!int.TryParse(command[1], out amount) || amount <= 0))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        UnturnedChat.Say(caller, plugin.Translate("buy_usage"), UnityEngine.Color.yellow));
                    return;
                }

                string query = command[0];
                var result = await plugin.Transactions.ExecuteBuyAsync(
                    player.CSteamID.m_SteamID,
                    query,
                    amount,
                    node => caller.HasPermission(node),
                    caller.IsAdmin).ConfigureAwait(false);

                plugin.Dispatcher.QueueOnMainThread(() =>
                {
                    if (result.Success)
                    {
                        var costStr = plugin.FormatMoney(result.Price);
                        var remStr = plugin.FormatMoney(result.NewBalance);
                        UnturnedChat.Say(caller, plugin.Translate("buy_success", result.Quantity, result.ItemEntry.Name, costStr, remStr), UnityEngine.Color.green);
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
