using System;
using System.Collections.Concurrent;
using System.Linq;

namespace KizShop.Common
{
    public class CommandRateLimiter
    {
        private readonly ConcurrentDictionary<(ulong SteamId, string Bucket), DateTime> _lastExecution =
            new ConcurrentDictionary<(ulong SteamId, string Bucket), DateTime>();

        private readonly IClock _clock;
        private DateTime _lastPruneUtc;

        public CommandRateLimiter(IClock clock = null)
        {
            _clock = clock ?? SystemClock.Instance;
            _lastPruneUtc = _clock.UtcNow;
        }

        public bool CheckRateLimit(ulong steamId, string bucketName, int windowMs, bool enabled, bool isAdmin, out double remainingSeconds)
        {
            remainingSeconds = 0;
            if (!enabled || isAdmin || windowMs <= 0) return true;
            return TryAcquire(steamId, bucketName, windowMs, out remainingSeconds);
        }

        public bool TryAcquire(ulong steamId, string bucketName, int windowMs, out double remainingSeconds)
        {
            remainingSeconds = 0;
            if (windowMs <= 0) return true;

            var now = _clock.UtcNow;
            string bucket = bucketName?.ToLowerInvariant() ?? string.Empty;
            var key = (steamId, bucket);

            // Periodic cleanup of stale entries every 60 seconds
            if ((now - _lastPruneUtc).TotalSeconds > 60)
            {
                PruneStale(now);
            }

            if (_lastExecution.TryGetValue(key, out var lastUtc))
            {
                var elapsedMs = (now - lastUtc).TotalMilliseconds;
                if (elapsedMs < windowMs)
                {
                    remainingSeconds = Math.Round((windowMs - elapsedMs) / 1000.0, 1, MidpointRounding.AwayFromZero);
                    if (remainingSeconds < 0.1) remainingSeconds = 0.1;
                    return false;
                }
            }

            _lastExecution[key] = now;
            return true;
        }

        public void ClearPlayer(ulong steamId)
        {
            foreach (var key in _lastExecution.Keys.Where(k => k.SteamId == steamId).ToArray())
            {
                _lastExecution.TryRemove(key, out _);
            }
        }

        public void Clear()
        {
            _lastExecution.Clear();
        }

        private void PruneStale(DateTime now)
        {
            _lastPruneUtc = now;
            // Any entry older than 60 seconds is safe to remove since max window is 30s
            foreach (var kvp in _lastExecution.ToArray())
            {
                if ((now - kvp.Value).TotalSeconds > 60)
                {
                    _lastExecution.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
