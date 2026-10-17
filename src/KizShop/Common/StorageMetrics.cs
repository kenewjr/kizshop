using System;
using System.Collections.Concurrent;

namespace KizShop.Common
{
    public class StorageMetrics
    {
        public static readonly StorageMetrics Instance = new StorageMetrics();

        private struct Sample
        {
            public DateTime TimestampUtc;
            public double Milliseconds;
        }

        private const int BufferCapacity = 1024;
        private readonly Sample[] _ringBuffer = new Sample[BufferCapacity];
        private int _writeIndex = 0;
        private int _count = 0;
        private readonly object _lock = new object();

        private readonly ConcurrentDictionary<string, DateTime> _lastWarnPerCallType =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public void Record(string callType, double elapsedMs, double warnThresholdMs)
        {
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                _ringBuffer[_writeIndex] = new Sample { TimestampUtc = now, Milliseconds = elapsedMs };
                _writeIndex = (_writeIndex + 1) % BufferCapacity;
                if (_count < BufferCapacity) _count++;
            }

            if (warnThresholdMs > 0 && elapsedMs >= warnThresholdMs)
            {
                string key = callType ?? "Unknown";
                bool shouldWarn = false;
                _lastWarnPerCallType.AddOrUpdate(
                    key,
                    k =>
                    {
                        shouldWarn = true;
                        return now;
                    },
                    (k, last) =>
                    {
                        if ((now - last).TotalSeconds >= 30)
                        {
                            shouldWarn = true;
                            return now;
                        }
                        return last;
                    });

                if (shouldWarn)
                {
                    KizLog.Warning($"[KizShop] Slow storage call: {key} took {elapsedMs:F1}ms (warn threshold: {warnThresholdMs:F0}ms).");
                }
            }
        }

        public (double AvgMs, double MaxMs, int Count) GetStats(TimeSpan window)
        {
            var now = DateTime.UtcNow;
            var cutoff = now - window;

            double sum = 0;
            double max = 0;
            int matched = 0;

            lock (_lock)
            {
                for (int i = 0; i < _count; i++)
                {
                    var s = _ringBuffer[i];
                    if (s.TimestampUtc >= cutoff)
                    {
                        sum += s.Milliseconds;
                        if (s.Milliseconds > max) max = s.Milliseconds;
                        matched++;
                    }
                }
            }

            double avg = matched > 0 ? (sum / matched) : 0;
            return (avg, max, matched);
        }
    }
}
