using System.Collections.Generic;

namespace KizShop.Shop
{
    public enum LookupStatus
    {
        Found,
        NotFound,
        Ambiguous
    }

    public class LookupResult<T>
    {
        public LookupStatus Status { get; }
        public T Entry { get; }
        public List<string> CandidateNames { get; }

        private LookupResult(LookupStatus status, T entry, List<string> candidates)
        {
            Status = status;
            Entry = entry;
            CandidateNames = candidates ?? new List<string>();
        }

        public static LookupResult<T> Success(T entry) => new LookupResult<T>(LookupStatus.Found, entry, null);
        public static LookupResult<T> NotFound() => new LookupResult<T>(LookupStatus.NotFound, default, null);
        public static LookupResult<T> Ambiguous(List<string> candidates) => new LookupResult<T>(LookupStatus.Ambiguous, default, candidates);
    }
}
