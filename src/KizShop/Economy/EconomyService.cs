using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using KizShop.Common;
using KizShop.Storage;

namespace KizShop.Economy
{
    public class EconomyService
    {
        private readonly IKizStorage _storage;
        private readonly Func<Money> _startingBalanceProvider;
        private readonly Func<Money?> _maxBalanceProvider;
        private readonly bool _enableCache;
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly ConcurrentDictionary<ulong, Money> _balanceCache = new ConcurrentDictionary<ulong, Money>();

        public event Action<ulong, Money, Money, string> OnBalanceChanged;

        public EconomyService(
            IKizStorage storage,
            Func<Money> startingBalanceProvider,
            Func<Money?> maxBalanceProvider,
            bool enableCache = true,
            IMainThreadDispatcher dispatcher = null)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _startingBalanceProvider = startingBalanceProvider ?? throw new ArgumentNullException(nameof(startingBalanceProvider));
            _maxBalanceProvider = maxBalanceProvider ?? throw new ArgumentNullException(nameof(maxBalanceProvider));
            _enableCache = enableCache;
            _dispatcher = dispatcher ?? new RocketMainThreadDispatcher();
        }

        public async Task<bool> AccountExistsAsync(ulong steamId)
        {
            if (_enableCache && _balanceCache.ContainsKey(steamId))
            {
                return true;
            }
            return await _storage.AccountExistsAsync(steamId).ConfigureAwait(false);
        }

        public async Task<Money> GetBalanceAsync(ulong steamId)
        {
            if (_enableCache && _balanceCache.TryGetValue(steamId, out var cached))
            {
                return cached;
            }

            var starting = _startingBalanceProvider();
            var bal = await _storage.GetBalanceAsync(steamId, starting).ConfigureAwait(false);
            if (_enableCache)
            {
                _balanceCache[steamId] = bal;
            }
            return bal;
        }

        public Money GetCachedBalance(ulong steamId)
        {
            if (_enableCache && _balanceCache.TryGetValue(steamId, out var cached))
            {
                return cached;
            }
            return _startingBalanceProvider();
        }

        public async Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason)
        {
            if (amount.Cents <= 0) return WithdrawResult.Failed(Money.Zero);

            var starting = _startingBalanceProvider();
            var result = await _storage.TryWithdrawAsync(steamId, amount, reason, starting).ConfigureAwait(false);
            if (result.Success)
            {
                if (_enableCache)
                {
                    _balanceCache[steamId] = result.NewBalance;
                }
                RaiseBalanceChanged(steamId, result.OldBalance, result.NewBalance, reason);
            }
            else if (_enableCache)
            {
                _balanceCache[steamId] = result.NewBalance;
            }

            return result;
        }

        public async Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, bool ignoreMaxBalance = false)
        {
            if (amount.Cents <= 0)
            {
                var cur = await GetBalanceAsync(steamId).ConfigureAwait(false);
                return new DepositResult(true, cur, cur);
            }

            var maxBal = _maxBalanceProvider();
            var starting = _startingBalanceProvider();

            var result = await _storage.DepositAsync(steamId, amount, reason, starting, maxBal, ignoreMaxBalance).ConfigureAwait(false);
            if (result.Success)
            {
                if (_enableCache)
                {
                    _balanceCache[steamId] = result.NewBalance;
                }
                RaiseBalanceChanged(steamId, result.OldBalance, result.NewBalance, reason);
            }

            return result;
        }

        public async Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason)
        {
            if (amount.Cents <= 0 || fromSteamId == toSteamId)
            {
                return TransferResult.Failed(Money.Zero, Money.Zero);
            }

            var maxBal = _maxBalanceProvider();
            var starting = _startingBalanceProvider();

            var result = await _storage.TransferAsync(fromSteamId, toSteamId, amount, reason, starting, maxBal).ConfigureAwait(false);
            if (result.Success)
            {
                if (_enableCache)
                {
                    _balanceCache[fromSteamId] = result.FromNewBalance;
                    _balanceCache[toSteamId] = result.ToNewBalance;
                }

                RaiseBalanceChanged(fromSteamId, result.FromOldBalance, result.FromNewBalance, $"TransferTo:{toSteamId} {reason}");
                RaiseBalanceChanged(toSteamId, result.ToOldBalance, result.ToNewBalance, $"TransferFrom:{fromSteamId} {reason}");
            }

            return result;
        }

        public async Task SetBalanceAsync(ulong steamId, Money amount, string reason)
        {
            if (amount.Cents < 0) amount = Money.Zero;
            var maxBal = _maxBalanceProvider();
            if (maxBal.HasValue && maxBal.Value.Cents > 0 && amount > maxBal.Value)
            {
                amount = maxBal.Value;
            }

            var oldBal = await GetBalanceAsync(steamId).ConfigureAwait(false);
            await _storage.SetBalanceAsync(steamId, amount, reason).ConfigureAwait(false);
            if (_enableCache)
            {
                _balanceCache[steamId] = amount;
            }
            RaiseBalanceChanged(steamId, oldBal, amount, reason);
        }

        public async Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count)
        {
            return await _storage.GetTopBalancesAsync(count).ConfigureAwait(false);
        }

        public void InvalidateCache(ulong steamId)
        {
            _balanceCache.TryRemove(steamId, out _);
        }

        public void ClearCache()
        {
            _balanceCache.Clear();
        }

        private void RaiseBalanceChanged(ulong steamId, Money oldBal, Money newBal, string reason)
        {
            _dispatcher.QueueOnMainThread(() =>
            {
                if (OnBalanceChanged == null) return;
                foreach (var del in OnBalanceChanged.GetInvocationList())
                {
                    try
                    {
                        ((Action<ulong, Money, Money, string>)del)(steamId, oldBal, newBal, reason);
                    }
                    catch (Exception ex)
                    {
                        KizLog.Error($"[KizShop] Error in OnBalanceChanged subscriber: {ex}");
                    }
                }
            });
        }
    }
}
