using System;
using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Player;
using KizShop.Common;
using KizShop.Shop;

namespace KizShop.Commands
{
    public class CommandKit : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "kizkit";
        public string Help => "View available shop kits or purchase a bundle kit.";
        public string Syntax => "[kit name]";
        public List<string> Aliases => new List<string> { "buykit" };
        public List<string> Permissions => new List<string> { "kizshop.kit" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            var plugin = KizShopPlugin.Instance;
            var playerGroups = plugin?.GetPlayerGroupIds(caller) ?? new List<string>();
            bool isAdmin = caller.HasPermission("kizshop.admin");

            CommandRunner.Run(caller, async () =>
            {
                var player = (UnturnedPlayer)caller;
                ulong steamId = player.CSteamID.m_SteamID;
                var cfg = plugin.Configuration.Instance;

                // 1. List kits if no argument provided
                if (command.Length == 0)
                {
                    var allKits = plugin.Shop.GetKits(enabledOnly: true);
                    plugin.Dispatcher.QueueOnMainThread(() =>
                    {
                        var visibleKits = new List<KizShop.Shop.Models.KitShopEntry>();
                        foreach (var k in allKits)
                        {
                            if (isAdmin || string.IsNullOrEmpty(k.RequiredPermission) || caller.HasPermission(k.RequiredPermission))
                            {
                                visibleKits.Add(k);
                            }
                        }

                        if (visibleKits.Count == 0)
                        {
                            KizChat.Say(caller, plugin.Translate("kit_none_available"), UnityEngine.Color.yellow);
                            return;
                        }

                        KizChat.Say(caller, plugin.Translate("kit_list_header"), UnityEngine.Color.cyan);
                        foreach (var kit in visibleKits)
                        {
                            decimal effPrice = plugin.Shop.GetEffectiveKitPrice(kit, playerGroups);
                            var price = Money.FromDecimal(effPrice);
                            var formattedPrice = plugin.FormatMoney(price);
                            KizChat.Say(caller, plugin.Translate("kit_list_entry", kit.Name, kit.DisplayName ?? kit.Name, formattedPrice, kit.Description ?? ""), UnityEngine.Color.yellow);
                        }
                        KizChat.Say(caller, plugin.Translate("kit_list_footer"), UnityEngine.Color.white);
                    });
                    return;
                }

                // Rate limit kit purchases
                if (!plugin.CheckRateLimit(caller, "buy", cfg.RateLimitBuyMs, out double remSecs))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("rate_limited", DurationFormatter.FormatSeconds(remSecs, true)), UnityEngine.Color.red));
                    return;
                }

                // 2. Buy kit
                string kitName = command[0];
                if (kitName.Equals("buy", StringComparison.OrdinalIgnoreCase) && command.Length > 1)
                {
                    kitName = string.Join(" ", command, 1, command.Length - 1).Trim();
                }
                else
                {
                    kitName = string.Join(" ", command).Trim();
                }

                var result = await plugin.Transactions.BuyKitAsync(
                    steamId,
                    kitName,
                    caller.HasPermission,
                    isAdmin,
                    playerGroups).ConfigureAwait(false);

                plugin.Dispatcher.QueueOnMainThread(() =>
                {
                    if (result.Success)
                    {
                        var formattedPrice = plugin.FormatMoney(result.Price);
                        var formattedBal = plugin.FormatMoney(result.NewBalance);
                        string dName = result.KitEntry.DisplayName ?? result.KitEntry.Name;
                        KizChat.Say(caller, plugin.Translate("kit_success", dName, formattedPrice, formattedBal), UnityEngine.Color.green);
                    }
                    else
                    {
                        string errKey = result.ErrorKey;
                        switch (errKey)
                        {
                            case "kit_not_found":
                                KizChat.Say(caller, plugin.Translate("kit_not_found", kitName), UnityEngine.Color.red);
                                var suggestions = plugin.Shop.GetSuggestions(kitName, "Kit", 3);
                                if (suggestions.Count > 0)
                                {
                                    KizChat.Say(caller, plugin.Translate("did_you_mean", string.Join(", ", suggestions)), UnityEngine.Color.yellow);
                                }
                                break;
                            case "kit_disabled":
                                KizChat.Say(caller, plugin.Translate("kit_disabled"), UnityEngine.Color.red);
                                break;
                            case "kit_no_permission":
                                KizChat.Say(caller, plugin.Translate("kit_no_permission"), UnityEngine.Color.red);
                                break;
                            case "combat_tagged":
                                KizChat.Say(caller, plugin.Translate("combat_tagged", result.ErrorArgs != null && result.ErrorArgs.Length > 0 ? result.ErrorArgs[0] : 0), UnityEngine.Color.red);
                                break;
                            case "kit_cooldown":
                                {
                                    string formattedWait = (result.ErrorArgs != null && result.ErrorArgs.Length > 1)
                                        ? result.ErrorArgs[1].ToString()
                                        : (result.ErrorArgs != null && result.ErrorArgs.Length > 0 ? DurationFormatter.FormatSeconds(Convert.ToInt32(result.ErrorArgs[0])) : "0s");
                                    KizChat.Say(caller, plugin.Translate("kit_cooldown", formattedWait), UnityEngine.Color.red);
                                }
                                break;
                            case "invalid_money_amount":
                                KizChat.Say(caller, plugin.Translate("invalid_money_amount", result.ErrorArgs != null && result.ErrorArgs.Length > 0 ? result.ErrorArgs[0] : ""), UnityEngine.Color.red);
                                break;
                            case "transaction_in_flight":
                                KizChat.Say(caller, plugin.Translate("transaction_in_flight"), UnityEngine.Color.yellow);
                                break;
                            case "kit_no_space":
                                KizChat.Say(caller, plugin.Translate("kit_no_space"), UnityEngine.Color.red);
                                break;
                            case "kit_insufficient_funds":
                                {
                                    var price = result.ErrorArgs != null && result.ErrorArgs.Length > 0 ? plugin.FormatMoney((Money)result.ErrorArgs[0]) : "$0";
                                    string name = result.ErrorArgs != null && result.ErrorArgs.Length > 1 ? result.ErrorArgs[1].ToString() : kitName;
                                    var bal = result.ErrorArgs != null && result.ErrorArgs.Length > 2 ? plugin.FormatMoney((Money)result.ErrorArgs[2]) : "$0";
                                    var shortfall = result.ErrorArgs != null && result.ErrorArgs.Length > 3 ? plugin.FormatMoney((Money)result.ErrorArgs[3]) : "$0";
                                    KizChat.Say(caller, plugin.Translate("kit_insufficient_funds", price, name, bal, shortfall), UnityEngine.Color.red);
                                }
                                break;
                            case "kit_delivery_failed":
                                KizChat.Say(caller, plugin.Translate("kit_delivery_failed"), UnityEngine.Color.red);
                                break;
                            case "buy_item_unknown":
                                KizChat.Say(caller, plugin.Translate("buy_item_unknown", result.ErrorArgs != null && result.ErrorArgs.Length > 0 ? result.ErrorArgs[0] : 0), UnityEngine.Color.red);
                                break;
                            default:
                                KizChat.Say(caller, plugin.Translate(string.IsNullOrEmpty(errKey) ? "command_error_generic" : errKey, result.ErrorArgs ?? Array.Empty<object>()), UnityEngine.Color.red);
                                break;
                        }
                    }
                });
            });
        }
    }
}
