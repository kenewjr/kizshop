using System;
using System.Text;

namespace KizShop.Common
{
    public static class DurationFormatter
    {
        public static string FormatSeconds(double totalSeconds, bool includeSubSecond = false)
        {
            if (totalSeconds <= 0) return "0s";
            return Format(TimeSpan.FromSeconds(totalSeconds), includeSubSecond);
        }

        public static string Format(TimeSpan span, bool includeSubSecond = false)
        {
            if (span.TotalSeconds <= 0) return "0s";

            if (span.TotalSeconds < 60)
            {
                if ((includeSubSecond || span.TotalSeconds < 1.0) && span.TotalSeconds < 10)
                {
                    return $"{span.TotalSeconds:0.0}s";
                }
                int secs = (int)Math.Ceiling(span.TotalSeconds);
                return $"{secs}s";
            }

            var sb = new StringBuilder();
            int days = (int)span.TotalDays;
            int hours = span.Hours;
            int minutes = span.Minutes;
            int seconds = span.Seconds;

            if (days > 0)
            {
                sb.Append($"{days}d");
                if (hours > 0) sb.Append($" {hours}h");
                else if (minutes > 0) sb.Append($" {minutes}m");
                return sb.ToString();
            }

            if (hours > 0)
            {
                sb.Append($"{hours}h");
                if (minutes > 0) sb.Append($" {minutes}m");
                else if (seconds > 0) sb.Append($" {seconds}s");
                return sb.ToString();
            }

            if (minutes > 0)
            {
                sb.Append($"{minutes}m");
                if (seconds > 0) sb.Append($" {seconds}s");
                return sb.ToString();
            }

            return $"{seconds}s";
        }
    }
}
