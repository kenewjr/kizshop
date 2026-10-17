using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace KizShop.Common
{
    public class TtlCache<TKey, TValue>
    {
        private class CacheEntry
        {
            public TValue Value { get; set; }
            public DateTime ExpiresUtc { get; set; }
            public DateTime LastAccessedUtc { get; set; }
        }

        private readonly ConcurrentDictionary<TKey, CacheEntry> _cache;
        private readonly IClock _clock;
        private readonly int _maxEntries;
        private readonly object _lock = new object();

        private long _hits;
        private long _misses;

        public long Hits => System.Threading.Interlocked.Read(ref _hits);
        public long Misses => System.Threading.Interlocked.Read(ref _misses);
        public int Count => _cache.Count;

        public TtlCache(int maxEntries = 256, IClock clock = null, IEqualityComparer<TKey> comparer = null)
        {
            _maxEntries = Math.Max(1, maxEntries);
            _clock = clock ?? SystemClock.Instance;
            _cache = comparer != null 
                ? new ConcurrentDictionary<TKey, CacheEntry>(comparer)
                : new ConcurrentDictionary<TKey, CacheEntry>();
        }

        public bool TryGet(TKey key, out TValue value)
        {
            var now = _clock.UtcNow;
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresUtc > now)
                {
                    entry.LastAccessedUtc = now;
                    System.Threading.Interlocked.Increment(ref _hits);
                    value = entry.Value;
                    return true;
                }

                _cache.TryRemove(key, out _);
            }

            System.Threading.Interlocked.Increment(ref _misses);
            value = default;
            return false;
        }

        public bool TryGetValue(TKey key, out TValue value) => TryGet(key, out value);

        public TValue GetOrAdd(TKey key, int ttlSeconds, Func<TValue> factory)
        {
            if (ttlSeconds <= 0) return factory();
            if (TryGet(key, out var val)) return val;
            val = factory();
            Set(key, val, ttlSeconds);
            return val;
        }

        public void Set(TKey key, TValue value, int ttlSeconds)
        {
            if (ttlSeconds <= 0) return;

            var now = _clock.UtcNow;
            var entry = new CacheEntry
            {
                Value = value,
                ExpiresUtc = now.AddSeconds(ttlSeconds),
                LastAccessedUtc = now
            };

            lock (_lock)
            {
                if (_cache.Count >= _maxEntries && !_cache.ContainsKey(key))
                {
                    EvictOldestOrExpired(now);
                }

                _cache[key] = entry;
            }
        }

        public void Remove(TKey key)
        {
            _cache.TryRemove(key, out _);
        }

        public void RemoveWhere(Func<TKey, bool> predicate)
        {
            if (predicate == null) return;
            foreach (var kvp in _cache.ToArray())
            {
                if (predicate(kvp.Key))
                {
                    _cache.TryRemove(kvp.Key, out _);
                }
            }
        }

        public void Clear()
        {
            _cache.Clear();
            System.Threading.Interlocked.Exchange(ref _hits, 0);
            System.Threading.Interlocked.Exchange(ref _misses, 0);
        }

        private void EvictOldestOrExpired(DateTime now)
        {
            // 1. Remove expired
            var expired = _cache.Where(x => x.Value.ExpiresUtc <= now).Select(x => x.Key).ToList();
            foreach (var k in expired)
            {
                _cache.TryRemove(k, out _);
            }

            if (_cache.Count < _maxEntries) return;

            // 2. Remove oldest accessed
            var oldest = _cache.OrderBy(x => x.Value.LastAccessedUtc).Take(Math.Max(1, _cache.Count / 4)).Select(x => x.Key).ToList();
            foreach (var k in oldest)
            {
                _cache.TryRemove(k, out _);
            }
        }
    }
}
