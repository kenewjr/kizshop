using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using KizShop.Common;
using KizShop.Shop.Models;

namespace KizShop.Commands
{
    public class CommandShop : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "shop";
        public string Help => "List shop items, view categories, or search catalog.";
        public string Syntax => "[categories|<category>|<page>|search <text>] [page]";
        public List<string> Aliases => new List<string> { "items" };
        public List<string> Permissions => new List<string> { "kizshop.shop" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                int pageSize = plugin.Configuration.Instance.ShopItemsPerPage;
                if (pageSize <= 0) pageSize = 8;

                // 1. /shop (no args) or /shop categories -> category list with item counts
                if (command.Length == 0 || (command.Length == 1 && command[0].Equals("categories", StringComparison.OrdinalIgnoreCase)))
                {
                    var counts = plugin.Shop.GetCategoryItemCounts();
                    plugin.Dispatcher.QueueOnMainThread(() =>
                    {
                        KizChat.Say(caller, plugin.Translate("shop_categories_header"), UnityEngine.Color.cyan);
                        foreach (var kvp in counts)
                        {
                            KizChat.Say(caller, plugin.Translate("shop_category_entry", kvp.Key, kvp.Value), UnityEngine.Color.white);
                        }
                        KizChat.Say(caller, plugin.Translate("shop_categories_footer"), UnityEngine.Color.gray);
                    });
                    return;
                }

                // 2. /shop <page> (numeric first argument) -> all items paged
                if (int.TryParse(command[0], out int pageNum))
                {
                    var (allEntries, totalPages, _) = plugin.Shop.GetPagedItems(null, pageNum, pageSize);
                    await DisplayItemsAsync(caller, allEntries, pageNum, totalPages, "all").ConfigureAwait(false);
                    return;
                }

                string firstArg = command[0];

                // 3. /shop search <text> [page]
                if (firstArg.Equals("search", StringComparison.OrdinalIgnoreCase))
                {
                    if (command.Length < 2)
                    {
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("shop_search_usage"), UnityEngine.Color.yellow));
                        return;
                    }

                    string searchText = command[1];
                    int searchPage = 1;
                    if (command.Length >= 3 && int.TryParse(command[2], out int p))
                    {
                        searchPage = p;
                    }

                    var (searchEntries, searchPages, totalMatches) = plugin.Shop.Search(searchText, searchPage, pageSize);
                    if (totalMatches == 0)
                    {
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("shop_search_no_results", searchText), UnityEngine.Color.yellow));
                        return;
                    }

                    await DisplayItemsAsync(caller, searchEntries, searchPage, searchPages, $"search '{searchText}'").ConfigureAwait(false);
                    return;
                }

                // 4. /shop <category> [page]
                int categoryPage = 1;
                if (command.Length >= 2 && int.TryParse(command[1], out int cp))
                {
                    categoryPage = cp;
                }

                if (!plugin.Shop.ResolveCategory(firstArg, out string resolvedCategory, out var candidates))
                {
                    if (candidates != null && candidates.Count > 1)
                    {
                        string candidateList = string.Join(", ", candidates);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("ambiguous_category", candidateList), UnityEngine.Color.yellow));
                        return;
                    }

                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("category_not_found", firstArg), UnityEngine.Color.red));
                    return;
                }

                var (catEntries, catPages, totalCatItems) = plugin.Shop.GetPagedItems(resolvedCategory, categoryPage, pageSize);
                await DisplayItemsAsync(caller, catEntries, categoryPage, catPages, resolvedCategory).ConfigureAwait(false);
            });
        }

        private async Task DisplayItemsAsync(IRocketPlayer caller, List<ItemShopEntry> entries, int page, int totalPages, string context)
        {
            var plugin = KizShopPlugin.Instance;
            var formattedList = new List<(string Name, ushort Id, string Buy, string Sell)>();

            foreach (var item in entries)
            {
                string buyStr = plugin.Translate("cost_not_available");
                string sellStr = plugin.Translate("cost_not_available");

                if (item.CanBuy)
                {
                    var (effBuy, _) = await plugin.Transactions.GetEffectiveBuyPriceAsync(item, 1).ConfigureAwait(false);
                    buyStr = plugin.FormatMoney(effBuy);
                }

                if (item.CanSell && item.SellPrice.HasValue)
                {
                    var (effSell, _) = await plugin.Transactions.GetEffectiveSellPriceAsync(item, 1).ConfigureAwait(false);
                    sellStr = plugin.FormatMoney(effSell);
                }

                formattedList.Add((item.Name, item.Id, buyStr, sellStr));
            }

            plugin.Dispatcher.QueueOnMainThread(() =>
            {
                KizChat.Say(caller, plugin.Translate("shop_header_context", context, page, totalPages), UnityEngine.Color.cyan);
                foreach (var (name, id, buy, sell) in formattedList)
                {
                    KizChat.Say(caller, plugin.Translate("shop_item_entry", name, id, buy, sell), UnityEngine.Color.white);
                }
                KizChat.Say(caller, plugin.Translate("shop_footer"), UnityEngine.Color.gray);
            });
        }
    }
}
