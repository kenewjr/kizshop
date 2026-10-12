using System;
using System.Collections.Concurrent;
using KizShop.Common;

namespace KizShop.Combat
{
    public class CombatTagService : ICombatTagService
    {
        private readonly Func<KizShopConfiguration> _configProvider;
        private readonly IClock _clock;
        private readonly ConcurrentDictionary<ulong, DateTime> _tags = new ConcurrentDictionary<ulong, DateTime>();

        public CombatTagService(Func<KizShopConfiguration> configProvider, IClock clock = null)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _clock = clock ?? SystemClock.Instance;
        }

        public void Tag(ulong steamId1, ulong steamId2)
        {
            var cfg = _configProvider();
            if (!cfg.CombatTagEnabled || cfg.CombatTagSeconds <= 0) return;

            DateTime expiry = _clock.UtcNow.AddSeconds(cfg.CombatTagSeconds);
            if (steamId1 != 0) _tags[steamId1] = expiry;
            if (steamId2 != 0) _tags[steamId2] = expiry;
        }

        public bool IsTagged(ulong steamId, out int secondsLeft)
        {
            secondsLeft = 0;
            var cfg = _configProvider();
            if (!cfg.CombatTagEnabled) return false;

            if (_tags.TryGetValue(steamId, out DateTime expiry))
            {
                if (expiry > _clock.UtcNow)
                {
                    secondsLeft = (int)Math.Ceiling((expiry - _clock.UtcNow).TotalSeconds);
                    return true;
                }
                _tags.TryRemove(steamId, out _);
            }

            return false;
        }

        public void Clear(ulong steamId)
        {
            _tags.TryRemove(steamId, out _);
        }
    }
}
