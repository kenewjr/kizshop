using System;
using System.Threading.Tasks;
using KizShop.Common;
using KizShop.Shop.Models;

namespace KizShop.API
{
    public static class KizShopAPI
    {
        public static event Action<ulong, decimal, decimal, string> OnBalanceChanged;

        internal static void RaiseBalanceChanged(ulong steamId, Money oldBalance, Money newBalance, string reason)
        {
            OnBalanceChanged?.Invoke(steamId, oldBalance.ToDecimal(), newBalance.ToDecimal(), reason);
        }

        // --- Synchronous methods (deadlock-proof, cache-first for reads) ---

        public static decimal GetBalance(ulong steamId)
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return 0m;
            }

            // Cache-first fast path
            var cached = KizShopPlugin.Instance.Economy.GetCachedBalance(steamId);
            if (cached.Cents > 0)
            {
                return cached.ToDecimal();
            }

            return Task.Run(() => KizShopPlugin.Instance.Economy.GetBalanceAsync(steamId)).GetAwaiter().GetResult().ToDecimal();
        }

        public static bool TryWithdraw(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return false;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(amount, maxTx, out var money, out _))
            {
                return false;
            }

            return Task.Run(() => KizShopPlugin.Instance.Economy.TryWithdrawAsync(steamId, money, reason)).GetAwaiter().GetResult().Success;
        }

        public static void Deposit(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(amount, maxTx, out var money, out _))
            {
                return;
            }

            Task.Run(() => KizShopPlugin.Instance.Economy.DepositAsync(steamId, money, reason)).GetAwaiter().GetResult();
        }

        // --- Recommended Asynchronous Methods ---

        public static async Task<decimal> GetBalanceAsync(ulong steamId)
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return 0m;
            }
            var m = await KizShopPlugin.Instance.Economy.GetBalanceAsync(steamId).ConfigureAwait(false);
            return m.ToDecimal();
        }

        public static async Task<bool> TryWithdrawAsync(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return false;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(amount, maxTx, out var money, out _))
            {
                return false;
            }

            var result = await KizShopPlugin.Instance.Economy.TryWithdrawAsync(steamId, money, reason).ConfigureAwait(false);
            return result.Success;
        }

        public static async Task<decimal> DepositAsync(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return 0m;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(amount, maxTx, out var money, out _))
            {
                return 0m;
            }

            var result = await KizShopPlugin.Instance.Economy.DepositAsync(steamId, money, reason).ConfigureAwait(false);
            return result.NewBalance.ToDecimal();
        }

        public static async Task<bool> RewardAsync(ulong steamId, decimal amount, string reason, string eventKey = null)
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null || KizShopPlugin.Instance.Storage == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(eventKey) && await KizShopPlugin.Instance.Storage.HasRewardEventAsync(eventKey).ConfigureAwait(false))
            {
                return false;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(amount, maxTx, out var money, out _) || money <= Money.Zero)
            {
                return false;
            }

            var dep = await KizShopPlugin.Instance.Economy.DepositAsync(steamId, money, reason ?? "API_Reward", ignoreMaxBalance: false).ConfigureAwait(false);
            if (dep.Success && !string.IsNullOrEmpty(eventKey))
            {
                await KizShopPlugin.Instance.Storage.RecordRewardEventAsync(eventKey, reason ?? "Reward", money, DateTime.UtcNow).ConfigureAwait(false);
            }

            return dep.Success;
        }

        public static async Task<BossRewardResult> DistributeBossRewardAsync(
            string eventKey,
            string bossName,
            decimal pool,
            System.Collections.Generic.IReadOnlyDictionary<ulong, float> damageByPlayer,
            ulong lastHitSteamId = 0)
        {
            var result = new BossRewardResult
            {
                EventKey = eventKey,
                BossName = bossName,
                TotalPool = Money.FromDecimal(pool)
            };

            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null || KizShopPlugin.Instance.Storage == null)
            {
                result.Success = false;
                result.Message = "Plugin not initialized";
                return result;
            }

            if (!string.IsNullOrEmpty(eventKey) && await KizShopPlugin.Instance.Storage.HasRewardEventAsync(eventKey).ConfigureAwait(false))
            {
                result.Success = true;
                result.Message = "Event already processed";
                return result;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(pool, maxTx, out var poolMoney, out var poolErr) || poolMoney <= Money.Zero)
            {
                result.Success = false;
                result.Message = poolErr ?? "Invalid pool amount";
                return result;
            }

            var cfg = KizShopPlugin.Instance.Configuration.Instance;
            var distribution = Shop.BossRewardCalculator.CalculateDistribution(
                poolMoney,
                damageByPlayer,
                lastHitSteamId,
                cfg.BossRewardTopDamagePercent,
                cfg.BossRewardLastHitPercent,
                cfg.BossRewardParticipationPercent,
                cfg.BossRewardMinDamageSharePercent);

            if (!string.IsNullOrEmpty(eventKey))
            {
                await KizShopPlugin.Instance.Storage.RecordRewardEventAsync(eventKey, bossName, poolMoney, DateTime.UtcNow).ConfigureAwait(false);
            }

            foreach (var kvp in distribution)
            {
                ulong targetSteamId = kvp.Key;
                Money payout = kvp.Value;

                var dep = await KizShopPlugin.Instance.Economy.DepositAsync(targetSteamId, payout, $"BossReward:{bossName}", ignoreMaxBalance: false).ConfigureAwait(false);
                result.Payouts.Add(new Shop.Models.BossRewardPayout
                {
                    SteamId = targetSteamId,
                    Amount = payout,
                    Success = dep.Success,
                    Role = (targetSteamId == lastHitSteamId) ? "LastHit" : "Participant"
                });

                if (dep.Success)
                {
                    KizShopPlugin.Instance.Dispatcher?.QueueOnMainThread(() =>
                    {
                        var player = Rocket.Unturned.Player.UnturnedPlayer.FromCSteamID(new Steamworks.CSteamID(targetSteamId));
                        if (player != null && player.Player != null)
                        {
                            var formatted = KizShopPlugin.Instance.FormatMoney(payout);
                            KizChat.Say(player, KizShopPlugin.Instance.Translate("boss_reward_received", formatted, bossName), UnityEngine.Color.yellow);
                        }
                    });
                }
            }

            result.Success = true;
            return result;
        }
    }
}
