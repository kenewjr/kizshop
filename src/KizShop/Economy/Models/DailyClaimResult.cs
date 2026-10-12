using System;

namespace KizShop.Economy.Models
{
    public class DailyClaimResult
    {
        public bool Allowed { get; set; }
        public int Streak { get; set; }
        public int PreviousDayIndex { get; set; }
        public int PreviousStreak { get; set; }
        public TimeSpan TimeUntilReset { get; set; }
    }
}
