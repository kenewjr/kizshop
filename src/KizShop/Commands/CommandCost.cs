using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;

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
            var plugin = KizShopPlugin.Instance;
            if (plugin == null || plugin.Shop == null) return;

            if (command.Length < 1)
            {
                UnturnedChat.Say(caller, plugin.Translate("cost_usage"), UnityEngine.Color.yellow);
                return;
            }

            string query = command[0];
            var itemEntry = plugin.Shop.FindItem(query);
            if (itemEntry != null)
            {
                string buyStr = itemEntry.CanBuy ? plugin.FormatMoney(itemEntry.BuyPrice) : plugin.Translate("cost_not_available");
                string sellStr = itemEntry.CanSell && itemEntry.SellPrice.HasValue ? plugin.FormatMoney(itemEntry.SellPrice.Value) : plugin.Translate("cost_not_available");

                UnturnedChat.Say(caller, plugin.Translate("cost_item_info", itemEntry.Name, itemEntry.Id, buyStr, sellStr), UnityEngine.Color.cyan);
                return;
            }

            var vehEntry = plugin.Shop.FindVehicle(query);
            if (vehEntry != null)
            {
                string buyStr = vehEntry.CanBuy ? plugin.FormatMoney(vehEntry.BuyPrice) : plugin.Translate("cost_not_available");
                string sellStr = vehEntry.CanSell && vehEntry.SellPrice.HasValue ? plugin.FormatMoney(vehEntry.SellPrice.Value) : plugin.Translate("cost_not_available");

                UnturnedChat.Say(caller, plugin.Translate("cost_item_info", vehEntry.Name, vehEntry.Id, buyStr, sellStr), UnityEngine.Color.cyan);
                return;
            }

            UnturnedChat.Say(caller, plugin.Translate("cost_not_found", query), UnityEngine.Color.red);
        }
    }
}
