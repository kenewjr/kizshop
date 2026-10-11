using System;
using System.Collections.Generic;
using System.Globalization;
using Rocket.API;
using Rocket.Unturned.Chat;
using KizShop.Shop.Models;

namespace KizShop.Commands
{
    public class CommandKizShop : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "kizshop";
        public string Help => "Admin command to manage KizShop.";
        public string Syntax => "<reload|additem|removeitem|setitemprice|addveh|removeveh|setvehprice>";
        public List<string> Aliases => new List<string> { "ks" };
        public List<string> Permissions => new List<string> { "kizshop.admin" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            var plugin = KizShopPlugin.Instance;
            if (plugin == null) return;

            if (command.Length < 1)
            {
                UnturnedChat.Say(caller, plugin.Translate("kizshop_admin_usage"), UnityEngine.Color.yellow);
                return;
            }

            string action = command[0].ToLowerInvariant();
            switch (action)
            {
                case "reload":
                    plugin.ReloadPlugin();
                    UnturnedChat.Say(caller, plugin.Translate("kizshop_reloaded"), UnityEngine.Color.green);
                    break;

                case "additem":
                    // /kizshop additem <id> <name> <buyPrice> [sellPrice] [permission]
                    if (command.Length < 4 || !ushort.TryParse(command[1], out var itemId) ||
                        !decimal.TryParse(command[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var buyPrice))
                    {
                        UnturnedChat.Say(caller, "Usage: /kizshop additem <id> <name> <buyPrice> [sellPrice] [permission]", UnityEngine.Color.yellow);
                        return;
                    }

                    decimal? sellPrice = null;
                    if (command.Length >= 5 && decimal.TryParse(command[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedSell))
                    {
                        sellPrice = parsedSell;
                    }

                    string itemPerm = command.Length >= 6 ? command[5] : string.Empty;

                    plugin.Shop.AddOrUpdateItem(new ItemShopEntry
                    {
                        Id = itemId,
                        Name = command[2],
                        BuyPrice = buyPrice,
                        SellPrice = sellPrice,
                        Permission = itemPerm,
                        Enabled = true
                    });
                    UnturnedChat.Say(caller, plugin.Translate("kizshop_item_added", command[2], itemId), UnityEngine.Color.green);
                    break;

                case "removeitem":
                    if (command.Length < 2 || !ushort.TryParse(command[1], out var remId))
                    {
                        UnturnedChat.Say(caller, "Usage: /kizshop removeitem <id>", UnityEngine.Color.yellow);
                        return;
                    }

                    if (plugin.Shop.RemoveItem(remId))
                    {
                        UnturnedChat.Say(caller, plugin.Translate("kizshop_item_removed", remId), UnityEngine.Color.green);
                    }
                    else
                    {
                        UnturnedChat.Say(caller, plugin.Translate("cost_not_found", remId), UnityEngine.Color.red);
                    }
                    break;

                case "setitemprice":
                    if (command.Length < 3 || !ushort.TryParse(command[1], out var priceItemId) ||
                        !decimal.TryParse(command[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var newBuy))
                    {
                        UnturnedChat.Say(caller, "Usage: /kizshop setitemprice <id> <buyPrice> [sellPrice]", UnityEngine.Color.yellow);
                        return;
                    }

                    decimal? newSell = null;
                    if (command.Length >= 4 && decimal.TryParse(command[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNewSell))
                    {
                        newSell = parsedNewSell;
                    }

                    if (plugin.Shop.SetItemPrice(priceItemId, newBuy, newSell))
                    {
                        UnturnedChat.Say(caller, plugin.Translate("kizshop_item_price_set", priceItemId), UnityEngine.Color.green);
                    }
                    else
                    {
                        UnturnedChat.Say(caller, plugin.Translate("cost_not_found", priceItemId), UnityEngine.Color.red);
                    }
                    break;

                case "addveh":
                    if (command.Length < 4 || !ushort.TryParse(command[1], out var vehId) ||
                        !decimal.TryParse(command[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var vehBuyPrice))
                    {
                        UnturnedChat.Say(caller, "Usage: /kizshop addveh <id> <name> <buyPrice> [sellPrice] [permission]", UnityEngine.Color.yellow);
                        return;
                    }

                    decimal? vehSellPrice = null;
                    if (command.Length >= 5 && decimal.TryParse(command[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedVehSell))
                    {
                        vehSellPrice = parsedVehSell;
                    }

                    string vehPerm = command.Length >= 6 ? command[5] : string.Empty;

                    plugin.Shop.AddOrUpdateVehicle(new VehicleShopEntry
                    {
                        Id = vehId,
                        Name = command[2],
                        BuyPrice = vehBuyPrice,
                        SellPrice = vehSellPrice,
                        Permission = vehPerm,
                        Enabled = true
                    });
                    UnturnedChat.Say(caller, plugin.Translate("kizshop_veh_added", command[2], vehId), UnityEngine.Color.green);
                    break;

                case "removeveh":
                    if (command.Length < 2 || !ushort.TryParse(command[1], out var remVehId))
                    {
                        UnturnedChat.Say(caller, "Usage: /kizshop removeveh <id>", UnityEngine.Color.yellow);
                        return;
                    }

                    if (plugin.Shop.RemoveVehicle(remVehId))
                    {
                        UnturnedChat.Say(caller, plugin.Translate("kizshop_veh_removed", remVehId), UnityEngine.Color.green);
                    }
                    else
                    {
                        UnturnedChat.Say(caller, plugin.Translate("cost_not_found", remVehId), UnityEngine.Color.red);
                    }
                    break;

                case "setvehprice":
                    if (command.Length < 3 || !ushort.TryParse(command[1], out var priceVehId) ||
                        !decimal.TryParse(command[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var newVehBuy))
                    {
                        UnturnedChat.Say(caller, "Usage: /kizshop setvehprice <id> <buyPrice> [sellPrice]", UnityEngine.Color.yellow);
                        return;
                    }

                    decimal? newVehSell = null;
                    if (command.Length >= 4 && decimal.TryParse(command[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNewVehSell))
                    {
                        newVehSell = parsedNewVehSell;
                    }

                    if (plugin.Shop.SetVehiclePrice(priceVehId, newVehBuy, newVehSell))
                    {
                        UnturnedChat.Say(caller, plugin.Translate("kizshop_veh_price_set", priceVehId), UnityEngine.Color.green);
                    }
                    else
                    {
                        UnturnedChat.Say(caller, plugin.Translate("cost_not_found", priceVehId), UnityEngine.Color.red);
                    }
                    break;

                default:
                    UnturnedChat.Say(caller, plugin.Translate("kizshop_admin_usage"), UnityEngine.Color.yellow);
                    break;
            }
        }
    }
}
