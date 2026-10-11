using System;
using System.Collections.Generic;
using Rocket.API;
using Rocket.Core.Utils;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using KizShop.Shop;

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
            var plugin = KizShopPlugin.Instance;
            if (plugin == null || plugin.Shop == null || plugin.Economy == null) return;

            var player = (UnturnedPlayer)caller;
            if (command.Length < 1)
            {
                UnturnedChat.Say(caller, plugin.Translate("sell_usage"), UnityEngine.Color.yellow);
                return;
            }

            string query = command[0];
            var entry = plugin.Shop.FindItem(query);
            if (entry == null)
            {
                UnturnedChat.Say(caller, plugin.Translate("sell_item_not_found", query), UnityEngine.Color.red);
                return;
            }

            if (!entry.CanSell || !entry.SellPrice.HasValue || entry.SellPrice.Value <= 0)
            {
                UnturnedChat.Say(caller, plugin.Translate("sell_item_disabled"), UnityEngine.Color.red);
                return;
            }

            if (!string.IsNullOrEmpty(entry.Permission) && !caller.HasPermission(entry.Permission) && !caller.IsAdmin)
            {
                UnturnedChat.Say(caller, plugin.Translate("sell_no_permission"), UnityEngine.Color.red);
                return;
            }

            int availableCount = InventoryHelper.CountItems(player.Inventory, entry.Id);
            if (availableCount <= 0)
            {
                UnturnedChat.Say(caller, plugin.Translate("sell_not_enough_items", 0, entry.Name, 1), UnityEngine.Color.red);
                return;
            }

            int amountToSell = 1;
            if (command.Length >= 2)
            {
                if (string.Equals(command[1], "all", StringComparison.OrdinalIgnoreCase))
                {
                    amountToSell = availableCount;
                }
                else if (!int.TryParse(command[1], out amountToSell) || amountToSell <= 0)
                {
                    UnturnedChat.Say(caller, plugin.Translate("sell_usage"), UnityEngine.Color.yellow);
                    return;
                }
            }

            if (availableCount < amountToSell)
            {
                UnturnedChat.Say(caller, plugin.Translate("sell_not_enough_items", availableCount, entry.Name, amountToSell), UnityEngine.Color.red);
                return;
            }

            // Remove items first from player inventory
            bool removed = InventoryHelper.RemoveItems(player.Inventory, entry.Id, amountToSell);
            if (!removed)
            {
                UnturnedChat.Say(caller, plugin.Translate("sell_not_enough_items", availableCount, entry.Name, amountToSell), UnityEngine.Color.red);
                return;
            }

            // Pay the player
            decimal totalPayout = entry.SellPrice.Value * amountToSell;
            _ = plugin.Economy.DepositAsync(player.CSteamID.m_SteamID, totalPayout, $"ShopSell:{entry.Id}x{amountToSell}").ContinueWith(t =>
            {
                var newBal = t.Result;
                TaskDispatcher.QueueOnMainThread(() =>
                {
                    var payoutStr = plugin.FormatMoney(totalPayout);
                    var balStr = plugin.FormatMoney(newBal);
                    UnturnedChat.Say(caller, plugin.Translate("sell_success", amountToSell, entry.Name, payoutStr, balStr), UnityEngine.Color.green);
                });
            });
        }
    }
}
