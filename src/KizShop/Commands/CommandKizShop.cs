using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using KizShop.Common;
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
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;

                if (command.Length < 1)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("kizshop_admin_usage"), UnityEngine.Color.yellow));
                    return;
                }

                // Re-parse arguments to handle quoted strings with spaces
                var args = ParseQuotedArgs(command);
                string action = args[0].ToLowerInvariant();

                switch (action)
                {
                    case "reload":
                        plugin.ReloadPlugin();
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("kizshop_reloaded"), UnityEngine.Color.green));
                        break;

                    case "additem":
                        // /kizshop additem <id> "<name>" <buyPrice> [sellPrice] [permission]
                        if (args.Count < 4 || !ushort.TryParse(args[1], out var itemId) ||
                            !decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var buyPrice))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_additem_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        decimal? sellPrice = null;
                        if (args.Count >= 5 && decimal.TryParse(args[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedSell))
                        {
                            sellPrice = parsedSell;
                        }

                        string itemPerm = args.Count >= 6 ? args[5] : string.Empty;

                        string itemValError = plugin.Shop.AddOrUpdateItem(new ItemShopEntry
                        {
                            Id = itemId,
                            Name = args[2],
                            BuyPrice = buyPrice,
                            SellPrice = sellPrice,
                            Permission = itemPerm,
                            Enabled = true
                        });

                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (itemValError == null)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_item_added", args[2], itemId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("invalid_catalog_entry", itemValError), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "removeitem":
                        if (args.Count < 2 || !ushort.TryParse(args[1], out var remId))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_removeitem_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        bool remSuccess = plugin.Shop.RemoveItem(remId);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (remSuccess)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_item_removed", remId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("cost_not_found", remId), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "setitemprice":
                        if (args.Count < 3 || !ushort.TryParse(args[1], out var priceItemId) ||
                            !decimal.TryParse(args[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var newBuy))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_setitemprice_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        decimal? newSell = null;
                        if (args.Count >= 4 && decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNewSell))
                        {
                            newSell = parsedNewSell;
                        }

                        string priceErr = plugin.Shop.SetItemPrice(priceItemId, newBuy, newSell);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (priceErr == null)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_item_price_set", priceItemId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("invalid_catalog_entry", priceErr), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "addveh":
                        if (args.Count < 4 || !ushort.TryParse(args[1], out var vehId) ||
                            !decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var vehBuyPrice))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_addveh_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        decimal? vehSellPrice = null;
                        if (args.Count >= 5 && decimal.TryParse(args[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedVehSell))
                        {
                            vehSellPrice = parsedVehSell;
                        }

                        string vehPerm = args.Count >= 6 ? args[5] : string.Empty;

                        string vehValErr = plugin.Shop.AddOrUpdateVehicle(new VehicleShopEntry
                        {
                            Id = vehId,
                            Name = args[2],
                            BuyPrice = vehBuyPrice,
                            SellPrice = vehSellPrice,
                            Permission = vehPerm,
                            Enabled = true
                        });

                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (vehValErr == null)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_veh_added", args[2], vehId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("invalid_catalog_entry", vehValErr), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "removeveh":
                        if (args.Count < 2 || !ushort.TryParse(args[1], out var remVehId))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_removeveh_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        bool remVehSuccess = plugin.Shop.RemoveVehicle(remVehId);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (remVehSuccess)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_veh_removed", remVehId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("cost_not_found", remVehId), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "setvehprice":
                        if (args.Count < 3 || !ushort.TryParse(args[1], out var priceVehId) ||
                            !decimal.TryParse(args[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var newVehBuy))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_setvehprice_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        decimal? newVehSell = null;
                        if (args.Count >= 4 && decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNewVehSell))
                        {
                            newVehSell = parsedNewVehSell;
                        }

                        string vehPriceErr = plugin.Shop.SetVehiclePrice(priceVehId, newVehBuy, newVehSell);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (vehPriceErr == null)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_veh_price_set", priceVehId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("invalid_catalog_entry", vehPriceErr), UnityEngine.Color.red);
                            }
                        });
                        break;

                    default:
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("kizshop_admin_usage"), UnityEngine.Color.yellow));
                        break;
                }

                await Task.CompletedTask;
            });
        }

        private static List<string> ParseQuotedArgs(string[] rawArgs)
        {
            var joined = string.Join(" ", rawArgs);
            var matches = Regex.Matches(joined, @"[\""].+?[\""]|[^ ]+");
            var result = new List<string>();
            foreach (Match m in matches)
            {
                result.Add(m.Value.Trim('"'));
            }
            return result;
        }
    }
}
