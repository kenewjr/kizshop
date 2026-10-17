using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Player;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandHistory : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "history";
        public string Help => "View your recent transaction history.";
        public string Syntax => "[page]";
        public List<string> Aliases => new List<string> { "transaksi", "txhistory" };
        public List<string> Permissions => new List<string> { "kizshop.history" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                var cfg = plugin.Configuration.Instance;

                if (!plugin.CheckRateLimit(caller, "history", cfg.RateLimitHistoryMs, out double remSecs))
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("rate_limited", DurationFormatter.FormatSeconds(remSecs, true)), UnityEngine.Color.red));
                    return;
                }

                var player = (UnturnedPlayer)caller;
                ulong steamId = player.CSteamID.m_SteamID;

                int page = 1;
                if (command.Length >= 1 && int.TryParse(command[0], out int parsedPage) && parsedPage > 0)
                {
                    page = parsedPage;
                }

                int pageSize = 5;
                int offset = (page - 1) * pageSize;

                var records = await plugin.GetCachedHistoryAsync(steamId, page, pageSize, offset).ConfigureAwait(false);

                plugin.Dispatcher.QueueOnMainThread(() =>
                {
                    if (records == null || records.Count == 0)
                    {
                        KizChat.Say(caller, plugin.Translate("history_empty"), UnityEngine.Color.yellow);
                        return;
                    }

                    KizChat.Say(caller, plugin.Translate("history_header", page), UnityEngine.Color.cyan);
                    foreach (var tx in records)
                    {
                        string color = tx.Amount.Cents >= 0 ? "green" : "red";
                        string sign = tx.Amount.Cents >= 0 ? "+" : "";
                        var formattedAmt = $"{sign}{plugin.FormatMoney(tx.Amount)}";
                        KizChat.Say(caller, plugin.Translate("history_entry", tx.Timestamp, tx.Type, color, formattedAmt, tx.Details), UnityEngine.Color.white);
                    }
                });
            });
        }
    }
}
