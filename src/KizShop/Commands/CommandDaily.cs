using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Player;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandDaily : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "daily";
        public string Help => "Claim your daily login reward and maintain your streak.";
        public string Syntax => "";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "kizshop.daily" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                var player = (UnturnedPlayer)caller;
                var cfg = plugin.Configuration.Instance;

                if (!cfg.DailyRewardEnabled)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("daily_disabled"), UnityEngine.Color.red));
                    return;
                }

                ulong steamId = player.CSteamID.m_SteamID;
                var clock = plugin.Clock;
                DateTime nowUtc = clock.UtcNow;

                // Day index calculation using reset offset
                DateTime localNow = nowUtc.AddHours(cfg.DailyResetUtcOffsetHours);
                int currentDayIndex = (int)(localNow.Date - new DateTime(2000, 1, 1)).TotalDays;
                DateTime nextResetLocal = localNow.Date.AddDays(1);
                TimeSpan timeUntilReset = nextResetLocal - localNow;

                var claimRes = await plugin.Storage.TryClaimDailyAsync(
                    steamId,
                    currentDayIndex,
                    cfg.DailyMaxStreakDays,
                    nowUtc).ConfigureAwait(false);

                if (!claimRes.Allowed)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                    {
                        string timeStr = $"{timeUntilReset.Hours}h {timeUntilReset.Minutes}m";
                        KizChat.Say(caller, plugin.Translate("daily_already_claimed", timeStr), UnityEngine.Color.yellow);
                    });
                    return;
                }

                int streak = claimRes.Streak;
                decimal bonusPercent = Math.Max(0, (streak - 1) * cfg.DailyStreakBonusPercentPerDay);
                decimal multiplier = 1.0m + (bonusPercent / 100.0m);
                decimal payoutDec = cfg.DailyRewardAmount * multiplier;

                if (!Money.TryFromDecimal(payoutDec, cfg.MaxTransactionAmount, out var payoutMoney, out _))
                {
                    payoutMoney = Money.FromDecimal(cfg.DailyRewardAmount);
                }

                var dep = await plugin.Economy.DepositAsync(steamId, payoutMoney, $"DailyReward:Streak{streak}").ConfigureAwait(false);
                if (!dep.Success)
                {
                    // Rollback atomic claim state
                    await plugin.Storage.RollbackDailyClaimAsync(steamId, claimRes.PreviousDayIndex, claimRes.PreviousStreak).ConfigureAwait(false);
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("daily_payout_failed"), UnityEngine.Color.red));
                    return;
                }

                plugin.Dispatcher.QueueOnMainThread(() =>
                {
                    var formatted = plugin.FormatMoney(payoutMoney);
                    KizChat.Say(caller, plugin.Translate("daily_success", formatted, streak, cfg.DailyMaxStreakDays), UnityEngine.Color.green);
                });
            });
        }
    }
}
