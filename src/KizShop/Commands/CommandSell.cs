using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Player;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandSell : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "sell";
        public string Help => "Sell items, held item, or a whole category to the shop.";
        public string Syntax => "<item|hand|all> [amount|category] [confirm]";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "kizshop.sell" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                var player = (UnturnedPlayer)caller;
                ulong steamId = player.CSteamID.m_SteamID;

                if (command.Length < 1)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("sell_usage"), UnityEngine.Color.yellow));
                    return;
                }

                string firstArg = command[0].ToLowerInvariant();

                // 1. /sell hand [amount]
                if (firstArg == "hand")
                {
                    bool hadAmount = command.Length >= 2;
                    var res = await plugin.Transactions.ExecuteSellHandAsync(
                        steamId,
                        node => caller.HasPermission(node),
                        caller.IsAdmin,
                        hadAmount).ConfigureAwait(false);

                    plugin.Dispatcher.QueueOnMainThread(() =>
                    {
                        if (res.Success)
                        {
                            var payoutStr = plugin.FormatMoney(res.Price);
                            var balStr = plugin.FormatMoney(res.NewBalance);
                            KizChat.Say(caller, plugin.Translate("sell_success", res.Quantity, res.ItemEntry.Name, payoutStr, balStr), UnityEngine.Color.green);
                            if (res.Note == "sell_hand_amount_ignored")
                            {
                                KizChat.Say(caller, plugin.Translate("sell_hand_amount_ignored"), UnityEngine.Color.yellow);
                            }
                        }
                        else
                        {
                            KizChat.Say(caller, plugin.Translate(res.ErrorKey, res.ErrorArgs ?? Array.Empty<object>()), UnityEngine.Color.red);
                        }
                    });
                    return;
                }

                // 2. /sell all <category> [confirm]
                if (firstArg == "all")
                {
                    if (command.Length < 2)
                    {
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("sell_all_usage"), UnityEngine.Color.yellow));
                        return;
                    }

                    bool isConfirm = (command.Length >= 3 && command[2].Equals("confirm", StringComparison.OrdinalIgnoreCase)) ||
                                     command[1].Equals("confirm", StringComparison.OrdinalIgnoreCase);

                    if (isConfirm)
                    {
                        var confirmRes = await plugin.Transactions.ExecuteSellAllCategoryConfirmAsync(
                            steamId,
                            node => caller.HasPermission(node),
                            caller.IsAdmin).ConfigureAwait(false);

                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (confirmRes.Success)
                            {
                                var payoutStr = plugin.FormatMoney(confirmRes.Price);
                                var balStr = plugin.FormatMoney(confirmRes.NewBalance);
                                KizChat.Say(caller, plugin.Translate("sell_all_success", confirmRes.Quantity, payoutStr, balStr), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate(confirmRes.ErrorKey, confirmRes.ErrorArgs ?? Array.Empty<object>()), UnityEngine.Color.red);
                            }
                        });
                        return;
                    }

                    // Summary flow
                    string categoryArg = command[1];
                    var summaryRes = await plugin.Transactions.ExecuteSellAllCategorySummaryAsync(
                        steamId,
                        categoryArg,
                        node => caller.HasPermission(node),
                        caller.IsAdmin).ConfigureAwait(false);

                    plugin.Dispatcher.QueueOnMainThread(() =>
                    {
                        if (summaryRes.Success)
                        {
                            var payoutStr = plugin.FormatMoney(summaryRes.ExpectedPayout);
                            KizChat.Say(caller, plugin.Translate("sell_all_summary_header", summaryRes.Category), UnityEngine.Color.cyan);
                            KizChat.Say(caller, plugin.Translate("sell_all_summary_details", summaryRes.TotalItemCount, summaryRes.DistinctItemCount, payoutStr), UnityEngine.Color.white);
                            if (summaryRes.SkippedGunsCount > 0 || summaryRes.SkippedOtherCount > 0)
                            {
                                KizChat.Say(caller, plugin.Translate("sell_all_summary_skipped", summaryRes.SkippedGunsCount, summaryRes.SkippedOtherCount), UnityEngine.Color.gray);
                            }
                            KizChat.Say(caller, plugin.Translate("sell_all_summary_prompt", summaryRes.Category), UnityEngine.Color.yellow);
                        }
                        else if (summaryRes.CandidateNames != null && summaryRes.CandidateNames.Count > 0)
                        {
                            string candidates = string.Join(", ", summaryRes.CandidateNames);
                            KizChat.Say(caller, plugin.Translate("ambiguous_category", candidates), UnityEngine.Color.yellow);
                        }
                        else
                        {
                            KizChat.Say(caller, plugin.Translate(summaryRes.ErrorKey, summaryRes.ErrorArgs ?? Array.Empty<object>()), UnityEngine.Color.red);
                        }
                    });
                    return;
                }

                // 3. Regular item sell: /sell <item> [amount|all]
                string query;
                string amountArg = "1";

                if (command.Length >= 2)
                {
                    string lastArg = command[command.Length - 1];
                    if (lastArg.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                        int.TryParse(lastArg, out _))
                    {
                        amountArg = lastArg;
                        query = string.Join(" ", command, 0, command.Length - 1).Trim();
                    }
                    else
                    {
                        query = string.Join(" ", command).Trim();
                    }
                }
                else
                {
                    query = command[0].Trim();
                }

                if (string.IsNullOrEmpty(query))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("sell_usage"), UnityEngine.Color.yellow));
                    return;
                }

                var result = await plugin.Transactions.ExecuteSellAsync(
                    steamId,
                    query,
                    amountArg,
                    node => caller.HasPermission(node),
                    caller.IsAdmin).ConfigureAwait(false);

                plugin.Dispatcher.QueueOnMainThread(() =>
                {
                    if (result.Success)
                    {
                        var payoutStr = plugin.FormatMoney(result.Price);
                        var balStr = plugin.FormatMoney(result.NewBalance);
                        KizChat.Say(caller, plugin.Translate("sell_success", result.Quantity, result.ItemEntry.Name, payoutStr, balStr), UnityEngine.Color.green);
                    }
                    else if (result.CandidateNames != null && result.CandidateNames.Count > 0)
                    {
                        string candidates = string.Join(", ", result.CandidateNames);
                        KizChat.Say(caller, plugin.Translate("ambiguous_match", candidates), UnityEngine.Color.yellow);
                    }
                    else
                    {
                        KizChat.Say(caller, plugin.Translate(result.ErrorKey, result.ErrorArgs ?? Array.Empty<object>()), UnityEngine.Color.red);
                    }
                });
            });
        }
    }
}
