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
                var cfg = plugin.Configuration.Instance;

                if (!plugin.CheckRateLimit(caller, "buy", cfg.RateLimitBuyMs, out double remSecs))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("rate_limited", DurationFormatter.FormatSeconds(remSecs, true)), UnityEngine.Color.red));
                    return;
                }

                var player = (UnturnedPlayer)caller;

                if (command.Length < 1)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("buy_usage"), UnityEngine.Color.yellow));
                    return;
                }

                int amount = 1;
                string query;
                string lastArg = command[command.Length - 1];

                if (command.Length >= 2 && int.TryParse(lastArg, out var parsedAmt))
                {
                    if (parsedAmt <= 0)
                    {
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("buy_invalid_amount"), UnityEngine.Color.red));
                        return;
                    }
                    amount = parsedAmt;
                    query = string.Join(" ", command, 0, command.Length - 1).Trim();
                }
                else
                {
                    query = string.Join(" ", command).Trim();
                }

                if (string.IsNullOrEmpty(query))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("buy_usage"), UnityEngine.Color.yellow));
                    return;
                }
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
                        KizChat.Say(caller, plugin.Translate("buy_success", result.Quantity, result.ItemEntry.Name, costStr, remStr), UnityEngine.Color.green);
                    }
                    else if (result.CandidateNames != null && result.CandidateNames.Count > 0)
                    {
                        string candidates = string.Join(", ", result.CandidateNames);
                        KizChat.Say(caller, plugin.Translate("ambiguous_match", candidates), UnityEngine.Color.yellow);
                    }
                    else
                    {
                        KizChat.Say(caller, plugin.Translate(result.ErrorKey, result.ErrorArgs ?? new object[0]), UnityEngine.Color.red);
                        if (result.ErrorKey == "buy_item_not_found")
                        {
                            var suggestions = plugin.Shop.GetSuggestions(query, "Item", 3);
                            if (suggestions.Count > 0)
                            {
                                KizChat.Say(caller, plugin.Translate("did_you_mean", string.Join(", ", suggestions)), UnityEngine.Color.yellow);
                            }
                        }
                    }
                });
            });
        }
    }
}
