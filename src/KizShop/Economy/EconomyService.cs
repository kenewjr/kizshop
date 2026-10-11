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
        private readonly Func<decimal> _startingBalanceProvider;
        private readonly Func<decimal?> _maxBalanceProvider;
        private readonly ConcurrentDictionary<ulong, decimal> _balanceCache = new ConcurrentDictionary<ulong, decimal>();

        public event Action<ulong, decimal, decimal, string> OnBalanceChanged;

        public EconomyService(
            IKizStorage storage,
            Func<decimal> startingBalanceProvider,
            Func<decimal?> maxBalanceProvider)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _startingBalanceProvider = startingBalanceProvider ?? throw new ArgumentNullException(nameof(startingBalanceProvider));
            _maxBalanceProvider = maxBalanceProvider ?? throw new ArgumentNullException(nameof(maxBalanceProvider));
        }

        public async Task<decimal> GetBalanceAsync(ulong steamId)
        {
            if (_balanceCache.TryGetValue(steamId, out var cached))
            {
                return cached;
            }

            var starting = _startingBalanceProvider();
            var bal = await _storage.GetBalanceAsync(steamId, starting);
            _balanceCache[steamId] = bal;
            return bal;
        }

        public decimal GetCachedBalance(ulong steamId)
        {
            if (_balanceCache.TryGetValue(steamId, out var cached))
            {
                return cached;
            }
            return _startingBalanceProvider();
        }

        public async Task<bool> TryWithdrawAsync(ulong steamId, decimal amount, string reason)
        {
            if (amount <= 0) return false;

            var oldBal = await GetBalanceAsync(steamId);
            if (oldBal < amount) return false;

            var success = await _storage.TryWithdrawAsync(steamId, amount, reason);
            if (success)
            {
                var newBal = oldBal - amount;
                _balanceCache[steamId] = newBal;
                OnBalanceChanged?.Invoke(steamId, oldBal, newBal, reason);
                return true;
            }

            // Sync cache with storage if withdrawal failed due to race condition
            var actual = await _storage.GetBalanceAsync(steamId, _startingBalanceProvider());
            _balanceCache[steamId] = actual;
            return false;
        }

        public async Task<decimal> DepositAsync(ulong steamId, decimal amount, string reason)
        {
            if (amount <= 0)
            {
                return await GetBalanceAsync(steamId);
            }

            var oldBal = await GetBalanceAsync(steamId);
            var maxBal = _maxBalanceProvider();
            var starting = _startingBalanceProvider();

            var newBal = await _storage.DepositAsync(steamId, amount, reason, starting, maxBal);
            _balanceCache[steamId] = newBal;
            OnBalanceChanged?.Invoke(steamId, oldBal, newBal, reason);
            return newBal;
        }

        public async Task<bool> TransferAsync(ulong fromSteamId, ulong toSteamId, decimal amount, string reason)
        {
            if (amount <= 0 || fromSteamId == toSteamId) return false;

            var senderOldBal = await GetBalanceAsync(fromSteamId);
            if (senderOldBal < amount) return false;

            var receiverOldBal = await GetBalanceAsync(toSteamId);
            var maxBal = _maxBalanceProvider();
            var starting = _startingBalanceProvider();

            var success = await _storage.TransferAsync(fromSteamId, toSteamId, amount, reason, starting, maxBal);
            if (success)
            {
                var senderNewBal = senderOldBal - amount;
                var receiverNewBal = receiverOldBal + amount;
                if (maxBal.HasValue && maxBal.Value > 0 && receiverNewBal > maxBal.Value)
                {
                    receiverNewBal = maxBal.Value;
                }

                _balanceCache[fromSteamId] = senderNewBal;
                _balanceCache[toSteamId] = receiverNewBal;

                OnBalanceChanged?.Invoke(fromSteamId, senderOldBal, senderNewBal, $"TransferTo:{toSteamId} {reason}");
                OnBalanceChanged?.Invoke(toSteamId, receiverOldBal, receiverNewBal, $"TransferFrom:{fromSteamId} {reason}");
                return true;
            }

            // Re-sync on failure
            _balanceCache[fromSteamId] = await _storage.GetBalanceAsync(fromSteamId, starting);
            _balanceCache[toSteamId] = await _storage.GetBalanceAsync(toSteamId, starting);
            return false;
        }

        public async Task SetBalanceAsync(ulong steamId, decimal amount, string reason)
        {
            if (amount < 0) amount = 0;
            var maxBal = _maxBalanceProvider();
            if (maxBal.HasValue && maxBal.Value > 0 && amount > maxBal.Value)
            {
                amount = maxBal.Value;
            }

            var oldBal = await GetBalanceAsync(steamId);
            await _storage.SetBalanceAsync(steamId, amount, reason);
            _balanceCache[steamId] = amount;
            OnBalanceChanged?.Invoke(steamId, oldBal, amount, reason);
        }

        public async Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count)
        {
            return await _storage.GetTopBalancesAsync(count);
        }

        public void InvalidateCache(ulong steamId)
        {
            _balanceCache.TryRemove(steamId, out _);
        }

        public void ClearCache()
        {
            _balanceCache.Clear();
        }
    }
}
