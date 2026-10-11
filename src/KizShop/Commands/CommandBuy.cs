using System;
using System.Collections.Generic;
using Rocket.API;
using Rocket.Core.Utils;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using KizShop.Shop;

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
            var plugin = KizShopPlugin.Instance;
            if (plugin == null || plugin.Shop == null || plugin.Economy == null) return;

            var player = (UnturnedPlayer)caller;
            if (command.Length < 1)
            {
                UnturnedChat.Say(caller, plugin.Translate("buy_usage"), UnityEngine.Color.yellow);
                return;
            }

            int amount = 1;
            if (command.Length >= 2 && (!int.TryParse(command[1], out amount) || amount <= 0))
            {
                UnturnedChat.Say(caller, plugin.Translate("buy_usage"), UnityEngine.Color.yellow);
                return;
            }

            string query = command[0];
            var entry = plugin.Shop.FindItem(query);
            if (entry == null)
            {
                UnturnedChat.Say(caller, plugin.Translate("buy_item_not_found", query), UnityEngine.Color.red);
                return;
            }

            if (!entry.CanBuy)
            {
                UnturnedChat.Say(caller, plugin.Translate("buy_item_disabled"), UnityEngine.Color.red);
                return;
            }

            if (!string.IsNullOrEmpty(entry.Permission) && !caller.HasPermission(entry.Permission) && !caller.IsAdmin)
            {
                UnturnedChat.Say(caller, plugin.Translate("buy_no_permission"), UnityEngine.Color.red);
                return;
            }

            if (entry.MaxAmountPerPurchase.HasValue && entry.MaxAmountPerPurchase.Value > 0 && amount > entry.MaxAmountPerPurchase.Value)
            {
                UnturnedChat.Say(caller, plugin.Translate("buy_max_amount", entry.MaxAmountPerPurchase.Value), UnityEngine.Color.red);
                return;
            }

            decimal totalCost = entry.BuyPrice * amount;

            // Pre-check balance async
            _ = plugin.Economy.GetBalanceAsync(player.CSteamID.m_SteamID).ContinueWith(t =>
            {
                var curBal = t.Result;
                TaskDispatcher.QueueOnMainThread(async () =>
                {
                    if (curBal < totalCost)
                    {
                        var req = plugin.FormatMoney(totalCost);
                        var have = plugin.FormatMoney(curBal);
                        UnturnedChat.Say(caller, plugin.Translate("buy_insufficient_funds", req, amount, entry.Name, have), UnityEngine.Color.red);
                        return;
                    }

                    // Check inventory space
                    if (!InventoryHelper.CheckSpaceForItem(player.Inventory, entry.Id, amount))
                    {
                        UnturnedChat.Say(caller, plugin.Translate("buy_no_space", amount, entry.Name), UnityEngine.Color.red);
                        return;
                    }

                    // Attempt giving items
                    var asset = Assets.find(EAssetType.ITEM, entry.Id) as ItemAsset;
                    byte maxStack = (asset != null && asset.amount > 0) ? asset.amount : (byte)1;

                    int remainingToGive = amount;
                    int successfullyGiven = 0;

                    while (remainingToGive > 0)
                    {
                        byte giveThisTime = (byte)Math.Min(remainingToGive, (int)maxStack);
                        bool given = player.GiveItem(entry.Id, giveThisTime);
                        if (!given)
                        {
                            break;
                        }
                        successfullyGiven += giveThisTime;
                        remainingToGive -= giveThisTime;
                    }

                    if (successfullyGiven < amount)
                    {
                        // Rollback: remove partially given items
                        if (successfullyGiven > 0)
                        {
                            InventoryHelper.RemoveItems(player.Inventory, entry.Id, successfullyGiven);
                        }
                        UnturnedChat.Say(caller, plugin.Translate("buy_no_space", amount, entry.Name), UnityEngine.Color.red);
                        return;
                    }

                    // Items securely in player inventory. Now deduct funds atomically.
                    bool deducted = await plugin.Economy.TryWithdrawAsync(player.CSteamID.m_SteamID, totalCost, $"ShopBuy:{entry.Id}x{amount}");
                    if (!deducted)
                    {
                        // Rollback items to prevent free items
                        InventoryHelper.RemoveItems(player.Inventory, entry.Id, amount);
                        UnturnedChat.Say(caller, plugin.Translate("buy_failed"), UnityEngine.Color.red);
                        return;
                    }

                    var newBal = await plugin.Economy.GetBalanceAsync(player.CSteamID.m_SteamID);
                    var costStr = plugin.FormatMoney(totalCost);
                    var remStr = plugin.FormatMoney(newBal);
                    UnturnedChat.Say(caller, plugin.Translate("buy_success", amount, entry.Name, costStr, remStr), UnityEngine.Color.green);
                });
            });
        }
    }
}
