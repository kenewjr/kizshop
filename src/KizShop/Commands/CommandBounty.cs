using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandBounty : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "bounty";
        public string Help => "Place, list, or check bounties on players.";
        public string Syntax => "<set|list|me> [player] [amount]";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "kizshop.bounty" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                var cfg = plugin.Configuration.Instance;

                if (!cfg.BountyEnabled)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("bounty_disabled"), UnityEngine.Color.red));
                    return;
                }

                if (command.Length < 1)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("bounty_usage"), UnityEngine.Color.yellow));
                    return;
                }

                string sub = command[0].ToLowerInvariant();

                switch (sub)
                {
                    case "set":
                        await HandleBountySetAsync(caller, command).ConfigureAwait(false);
                        break;

                    case "list":
                        await HandleBountyListAsync(caller, command).ConfigureAwait(false);
                        break;

                    case "me":
                        await HandleBountyMeAsync(caller).ConfigureAwait(false);
                        break;

                    default:
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("bounty_usage"), UnityEngine.Color.yellow));
                        break;
                }
            });
        }

        private async Task HandleBountySetAsync(IRocketPlayer caller, string[] command)
        {
            var plugin = KizShopPlugin.Instance;
            var cfg = plugin.Configuration.Instance;

            if (command.Length < 3)
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("bounty_set_usage"), UnityEngine.Color.yellow));
                return;
            }

            ulong placerSteamId = 0;
            string placerName = "Console";
            if (caller is UnturnedPlayer uPlacer)
            {
                placerSteamId = uPlacer.CSteamID.m_SteamID;
                placerName = uPlacer.CharacterName;
            }

            string targetArg = command[1];
            string amountArg = command[2];

            if (!decimal.TryParse(amountArg, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amountDec) || amountDec < cfg.BountyMinAmount)
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("bounty_amount_invalid", cfg.BountyMinAmount), UnityEngine.Color.red));
                return;
            }

            if (!Money.TryFromDecimal(amountDec, cfg.MaxTransactionAmount, out var escrowAmount, out _))
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("invalid_money_amount", "Amount exceeds max limit"), UnityEngine.Color.red));
                return;
            }

            // Target resolution
            ulong targetSteamId = 0;
            string targetName = targetArg;

            var onlineTarget = UnturnedPlayer.FromName(targetArg);
            if (onlineTarget != null && onlineTarget.Player != null)
            {
                targetSteamId = onlineTarget.CSteamID.m_SteamID;
                targetName = onlineTarget.CharacterName;
            }
            else if (ulong.TryParse(targetArg, out ulong parsedId) && targetArg.Length == 17)
            {
                targetSteamId = parsedId;
            }

            if (targetSteamId == 0)
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("bounty_target_not_found", targetArg), UnityEngine.Color.red));
                return;
            }

            if (placerSteamId != 0 && targetSteamId == placerSteamId)
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("bounty_self"), UnityEngine.Color.red));
                return;
            }

            // Check if target account exists (for offline targets)
            bool targetExists = await plugin.Storage.AccountExistsAsync(targetSteamId).ConfigureAwait(false);
            if (!targetExists && onlineTarget == null)
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, plugin.Translate("bounty_target_not_found", targetArg), UnityEngine.Color.red));
                return;
            }

            // Calculate fee and net bounty
            long feeCents = (escrowAmount.Cents * cfg.BountyFeePercent) / 100;
            long netCents = escrowAmount.Cents - feeCents;
            var netAmount = Money.FromCents(netCents);

            // Check max total bounty on target
            var currentTotal = await plugin.Storage.GetTotalBountyOnTargetAsync(targetSteamId).ConfigureAwait(false);
            if (cfg.BountyMaxPerTarget > 0)
            {
                var maxLimit = Money.FromDecimal(cfg.BountyMaxPerTarget);
                if (currentTotal + netAmount > maxLimit)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("bounty_max_target", plugin.FormatMoney(maxLimit)), UnityEngine.Color.red));
                    return;
                }
            }

            // Withdraw from placer
            if (placerSteamId != 0)
            {
                var withdraw = await plugin.Economy.TryWithdrawAsync(placerSteamId, escrowAmount, $"BountyEscrow:{targetSteamId}").ConfigureAwait(false);
                if (!withdraw.Success)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                    {
                        var curBal = plugin.Economy.GetCachedBalance(placerSteamId);
                        KizChat.Say(caller, plugin.Translate("bounty_insufficient_funds", plugin.FormatMoney(escrowAmount), plugin.FormatMoney(curBal)), UnityEngine.Color.red);
                    });
                    return;
                }
            }

            // Record bounty
            await plugin.Storage.CreateBountyAsync(placerSteamId, targetSteamId, escrowAmount, netAmount, DateTime.UtcNow).ConfigureAwait(false);

            plugin.Dispatcher.QueueOnMainThread(() =>
            {
                var netFormatted = plugin.FormatMoney(netAmount);
                KizChat.Say(caller, plugin.Translate("bounty_set_success", netFormatted, targetName), UnityEngine.Color.green);

                if (cfg.BountyAnnounceInChat)
                {
                    KizChat.Broadcast(plugin.Translate("bounty_broadcast", placerName, netFormatted, targetName), UnityEngine.Color.yellow);
                }
            });
        }

        private async Task HandleBountyListAsync(IRocketPlayer caller, string[] command)
        {
            var plugin = KizShopPlugin.Instance;
            int page = 1;
            if (command.Length >= 2)
            {
                int.TryParse(command[1], out page);
            }
            if (page < 1) page = 1;

            var bounties = await plugin.Storage.GetActiveBountiesAsync(limit: 50).ConfigureAwait(false);

            // Group by target steam id to aggregate total bounty on each target
            var grouped = bounties
                .GroupBy(b => b.TargetSteamId)
                .Select(g => new
                {
                    TargetSteamId = g.Key,
                    Total = Money.FromCents(g.Sum(x => x.NetAmount.Cents))
                })
                .OrderByDescending(x => x.Total)
                .ToList();

            int pageSize = 5;
            int totalPages = (int)Math.Ceiling((double)grouped.Count / pageSize);
            if (totalPages < 1) totalPages = 1;
            if (page > totalPages) page = totalPages;

            var paged = grouped.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            plugin.Dispatcher.QueueOnMainThread(() =>
            {
                KizChat.Say(caller, plugin.Translate("bounty_list_header", page, totalPages), UnityEngine.Color.cyan);
                if (paged.Count == 0)
                {
                    KizChat.Say(caller, plugin.Translate("bounty_list_empty"), UnityEngine.Color.gray);
                    return;
                }

                foreach (var item in paged)
                {
                    var targetPlayer = UnturnedPlayer.FromCSteamID(new CSteamID(item.TargetSteamId));
                    string targetName = targetPlayer != null ? targetPlayer.CharacterName : item.TargetSteamId.ToString();
                    KizChat.Say(caller, plugin.Translate("bounty_list_entry", targetName, plugin.FormatMoney(item.Total)), UnityEngine.Color.white);
                }
            });
        }

        private async Task HandleBountyMeAsync(IRocketPlayer caller)
        {
            var plugin = KizShopPlugin.Instance;
            if (!(caller is UnturnedPlayer player))
            {
                plugin.Dispatcher.QueueOnMainThread(() =>
                    KizChat.Say(caller, "Console cannot have bounties.", UnityEngine.Color.red));
                return;
            }

            ulong steamId = player.CSteamID.m_SteamID;
            var total = await plugin.Storage.GetTotalBountyOnTargetAsync(steamId).ConfigureAwait(false);

            plugin.Dispatcher.QueueOnMainThread(() =>
            {
                var formatted = plugin.FormatMoney(total);
                KizChat.Say(caller, plugin.Translate("bounty_me", formatted), UnityEngine.Color.cyan);
            });
        }
    }
}
