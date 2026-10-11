using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using KizShop.Common;

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
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;

                if (command.Length == 0 &&
                    caller is Rocket.Unturned.Player.UnturnedPlayer uPlayer &&
                    plugin.Configuration.Instance.UseVisualUiForShopCommand &&
                    plugin.Configuration.Instance.EnableVisualUI &&
                    plugin.ShopUi != null)
                {
                    await plugin.ShopUi.OpenShopAsync(uPlayer.CSteamID.m_SteamID).ConfigureAwait(false);
                    return;
                }

                int page = 1;
                if (command.Length >= 1)
                {
                    int.TryParse(command[0], out page);
                }

                int pageSize = plugin.Configuration.Instance.ShopItemsPerPage;
                if (pageSize <= 0) pageSize = 8;

                var (entries, totalPages) = plugin.Shop.GetPagedItems(page, pageSize);

                plugin.Dispatcher.QueueOnMainThread(() =>
                {
                    UnturnedChat.Say(caller, plugin.Translate("shop_header", page, totalPages), UnityEngine.Color.cyan);
                    foreach (var item in entries)
                    {
                        string buyStr = item.CanBuy ? plugin.FormatMoney(Money.FromDecimal(item.BuyPrice)) : plugin.Translate("cost_not_available");
                        string sellStr = item.CanSell && item.SellPrice.HasValue ? plugin.FormatMoney(Money.FromDecimal(item.SellPrice.Value)) : plugin.Translate("cost_not_available");

                        UnturnedChat.Say(caller, plugin.Translate("shop_item_entry", item.Name, item.Id, buyStr, sellStr), UnityEngine.Color.white);
                    }
                    UnturnedChat.Say(caller, plugin.Translate("shop_footer"), UnityEngine.Color.gray);
                });

                await Task.CompletedTask;
            });
        }
    }
}
