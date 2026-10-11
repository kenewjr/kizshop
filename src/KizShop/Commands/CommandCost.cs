using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using KizShop.Common;
using KizShop.Shop;

namespace KizShop.Commands
{
    public class CommandCost : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "cost";
        public string Help => "Check the buy and sell prices for an item or vehicle.";
        public string Syntax => "<item name|id|guid>";
        public List<string> Aliases => new List<string> { "price" };
        public List<string> Permissions => new List<string> { "kizshop.cost", "kizshop.buy", "kizshop.sell" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;

                if (command.Length < 1)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("cost_usage"), UnityEngine.Color.yellow));
                    return;
                }

                string query = command[0];
                var itemLookup = plugin.Shop.ResolveItem(query);

                if (itemLookup.Status == LookupStatus.Ambiguous)
                {
                    string candidates = string.Join(", ", itemLookup.CandidateNames);
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("ambiguous_match", candidates), UnityEngine.Color.yellow));
                    return;
                }

                if (itemLookup.Status == LookupStatus.Found)
                {
                    var itemEntry = itemLookup.Entry;
                    string buyStr = itemEntry.CanBuy ? plugin.FormatMoney(Money.FromDecimal(itemEntry.BuyPrice)) : plugin.Translate("cost_not_available");
                    string sellStr = itemEntry.CanSell && itemEntry.SellPrice.HasValue ? plugin.FormatMoney(Money.FromDecimal(itemEntry.SellPrice.Value)) : plugin.Translate("cost_not_available");

                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("cost_item_info", itemEntry.Name, itemEntry.Id, buyStr, sellStr), UnityEngine.Color.cyan));
                    return;
                }

                var vehLookup = plugin.Shop.ResolveVehicle(query);
                if (vehLookup.Status == LookupStatus.Ambiguous)
                {
                    string candidates = string.Join(", ", vehLookup.CandidateNames);
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("ambiguous_match", candidates), UnityEngine.Color.yellow));
                    return;
                }

                if (vehLookup.Status == LookupStatus.Found)
                {
                    var vehEntry = vehLookup.Entry;
                    string buyStr = vehEntry.CanBuy ? plugin.FormatMoney(Money.FromDecimal(vehEntry.BuyPrice)) : plugin.Translate("cost_not_available");
                    string sellStr = vehEntry.CanSell && vehEntry.SellPrice.HasValue ? plugin.FormatMoney(Money.FromDecimal(vehEntry.SellPrice.Value)) : plugin.Translate("cost_not_available");

                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("cost_item_info", vehEntry.Name, vehEntry.Id, buyStr, sellStr), UnityEngine.Color.cyan));
                    return;
                }

                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("cost_not_found", query), UnityEngine.Color.red));

                await Task.CompletedTask;
            });
        }
    }
}
