using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;

namespace KizShop.Commands
{
    public class CommandShop : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "shop";
        public string Help => "List items available in the shop.";
        public string Syntax => "[page]";
        public List<string> Aliases => new List<string> { "items" };
        public List<string> Permissions => new List<string> { "kizshop.shop" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            var plugin = KizShopPlugin.Instance;
            if (plugin == null || plugin.Shop == null) return;

            int page = 1;
            if (command.Length >= 1)
            {
                int.TryParse(command[0], out page);
            }

            int pageSize = plugin.Configuration.Instance.ShopItemsPerPage;
            if (pageSize <= 0) pageSize = 8;

            var (entries, totalPages) = plugin.Shop.GetPagedItems(page, pageSize);

            UnturnedChat.Say(caller, plugin.Translate("shop_header", page, totalPages), UnityEngine.Color.cyan);
            foreach (var item in entries)
            {
                string buyStr = item.CanBuy ? plugin.FormatMoney(item.BuyPrice) : plugin.Translate("cost_not_available");
                string sellStr = item.CanSell && item.SellPrice.HasValue ? plugin.FormatMoney(item.SellPrice.Value) : plugin.Translate("cost_not_available");

                UnturnedChat.Say(caller, plugin.Translate("shop_item_entry", item.Name, item.Id, buyStr, sellStr), UnityEngine.Color.white);
            }
            UnturnedChat.Say(caller, plugin.Translate("shop_footer"), UnityEngine.Color.gray);
        }
    }
}
