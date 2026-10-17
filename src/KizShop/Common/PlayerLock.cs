using System.Collections.Concurrent;

namespace KizShop.Common
{
    public static class PlayerLock
    {
        private static readonly ConcurrentDictionary<ulong, byte> _locks = new ConcurrentDictionary<ulong, byte>();

        public static bool TryAcquire(ulong steamId) => _locks.TryAdd(steamId, 1);

        public static void Release(ulong steamId) => _locks.TryRemove(steamId, out _);

        public static bool IsLocked(ulong steamId) => _locks.ContainsKey(steamId);
    }
}
