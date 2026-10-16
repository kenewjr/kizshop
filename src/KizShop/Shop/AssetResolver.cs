using System;
using System.Collections.Generic;
using System.Linq;
using KizShop.Shop.Models;

namespace KizShop.Shop
{
    public static class AssetResolver
    {
        public static LookupResult<ItemShopEntry> ResolveItem(IEnumerable<ItemShopEntry> entries, string query, bool enabledOnly = false)
        {
            if (entries == null || string.IsNullOrWhiteSpace(query))
            {
                return LookupResult<ItemShopEntry>.NotFound();
            }

            query = query.Trim();

            // 1. Numeric ID fast-path (0 allocations)
            if (ushort.TryParse(query, out var id))
            {
                foreach (var e in entries)
                {
                    if (e.Id == id)
                    {
                        if (!enabledOnly || (e.Enabled && e.Available))
                            return LookupResult<ItemShopEntry>.Success(e);
                        return LookupResult<ItemShopEntry>.NotFound();
                    }
                }
                return LookupResult<ItemShopEntry>.NotFound();
            }

            var pool = enabledOnly ? entries.Where(e => e.Enabled && e.Available).ToList() : entries.ToList();

            // 2. GUID
            if (Guid.TryParse(query, out var guid))
            {
                var matchByGuid = pool.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Guid) &&
                    Guid.TryParse(e.Guid, out var eg) && eg == guid);
                if (matchByGuid != null) return LookupResult<ItemShopEntry>.Success(matchByGuid);
            }

            // 3. Exact name match (case-insensitive)
            var exactName = pool.FirstOrDefault(e =>
                string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase));
            if (exactName != null) return LookupResult<ItemShopEntry>.Success(exactName);

            // 4. Prefix match
            var prefixMatches = pool.Where(e =>
                e.Name != null && e.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();

            if (prefixMatches.Count == 1)
            {
                return LookupResult<ItemShopEntry>.Success(prefixMatches[0]);
            }
            if (prefixMatches.Count > 1)
            {
                return LookupResult<ItemShopEntry>.Ambiguous(prefixMatches.Take(5).Select(e => e.Name).ToList());
            }

            // 5. Contains match
            var containsMatches = pool.Where(e =>
                e.Name != null && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (containsMatches.Count == 1)
            {
                return LookupResult<ItemShopEntry>.Success(containsMatches[0]);
            }
            if (containsMatches.Count > 1)
            {
                return LookupResult<ItemShopEntry>.Ambiguous(containsMatches.Take(5).Select(e => e.Name).ToList());
            }

            // 6. Typo tolerance (Levenshtein distance <= 2 for queries >= 4 chars)
            if (query.Length >= 4)
            {
                var typoCandidates = new List<(string Name, int Distance)>();
                foreach (var e in pool)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    int dist = MinWordOrPhraseDistance(query, e.Name);
                    if (dist <= 2)
                    {
                        typoCandidates.Add((e.Name, dist));
                    }
                }

                if (typoCandidates.Count > 0)
                {
                    var bestCandidates = typoCandidates
                        .OrderBy(c => c.Distance)
                        .Take(5)
                        .Select(c => c.Name)
                        .Distinct()
                        .ToList();
                    return LookupResult<ItemShopEntry>.Ambiguous(bestCandidates);
                }
            }

            return LookupResult<ItemShopEntry>.NotFound();
        }

        public static LookupResult<VehicleShopEntry> ResolveVehicle(IEnumerable<VehicleShopEntry> entries, string query, bool enabledOnly = false)
        {
            if (entries == null || string.IsNullOrWhiteSpace(query))
            {
                return LookupResult<VehicleShopEntry>.NotFound();
            }

            query = query.Trim();

            // 1. Numeric ID fast-path (0 allocations)
            if (ushort.TryParse(query, out var id))
            {
                foreach (var e in entries)
                {
                    if (e.Id == id)
                    {
                        if (!enabledOnly || e.Enabled)
                            return LookupResult<VehicleShopEntry>.Success(e);
                        return LookupResult<VehicleShopEntry>.NotFound();
                    }
                }
                return LookupResult<VehicleShopEntry>.NotFound();
            }

            var pool = enabledOnly ? entries.Where(e => e.Enabled).ToList() : entries.ToList();

            // 2. GUID
            if (Guid.TryParse(query, out var guid))
            {
                var matchByGuid = pool.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Guid) &&
                    Guid.TryParse(e.Guid, out var eg) && eg == guid);
                if (matchByGuid != null) return LookupResult<VehicleShopEntry>.Success(matchByGuid);
            }

            // 3. Exact name match
            var exactName = pool.FirstOrDefault(e =>
                string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase));
            if (exactName != null) return LookupResult<VehicleShopEntry>.Success(exactName);

            // 4. Prefix match
            var prefixMatches = pool.Where(e =>
                e.Name != null && e.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();

            if (prefixMatches.Count == 1)
            {
                return LookupResult<VehicleShopEntry>.Success(prefixMatches[0]);
            }
            if (prefixMatches.Count > 1)
            {
                return LookupResult<VehicleShopEntry>.Ambiguous(prefixMatches.Take(5).Select(e => e.Name).ToList());
            }

            // 5. Contains match
            var containsMatches = pool.Where(e =>
                e.Name != null && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (containsMatches.Count == 1)
            {
                return LookupResult<VehicleShopEntry>.Success(containsMatches[0]);
            }
            if (containsMatches.Count > 1)
            {
                return LookupResult<VehicleShopEntry>.Ambiguous(containsMatches.Take(5).Select(e => e.Name).ToList());
            }

            // 6. Typo tolerance (Levenshtein distance <= 2 for queries >= 4 chars)
            if (query.Length >= 4)
            {
                var typoCandidates = new List<(string Name, int Distance)>();
                foreach (var e in pool)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    int dist = MinWordOrPhraseDistance(query, e.Name);
                    if (dist <= 2)
                    {
                        typoCandidates.Add((e.Name, dist));
                    }
                }

                if (typoCandidates.Count > 0)
                {
                    var bestCandidates = typoCandidates
                        .OrderBy(c => c.Distance)
                        .Take(5)
                        .Select(c => c.Name)
                        .Distinct()
                        .ToList();
                    return LookupResult<VehicleShopEntry>.Ambiguous(bestCandidates);
                }
            }

            return LookupResult<VehicleShopEntry>.NotFound();
        }

        private static int MinWordOrPhraseDistance(string query, string target)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(target)) return int.MaxValue;
            int best = LevenshteinDistance(query, target);
            if (best <= 2) return best;

            var words = target.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1)
            {
                foreach (var w in words)
                {
                    if (w.Length >= 3)
                    {
                        int d = LevenshteinDistance(query, w);
                        if (d < best) best = d;
                    }
                }
            }
            return best;
        }

        private static int LevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int n = s.Length;
            int m = t.Length;
            if (Math.Abs(n - m) > 2) return int.MaxValue;

            int[] v0 = new int[m + 1];
            int[] v1 = new int[m + 1];

            for (int i = 0; i <= m; i++) v0[i] = i;

            for (int i = 0; i < n; i++)
            {
                v1[0] = i + 1;
                for (int j = 0; j < m; j++)
                {
                    int cost = char.ToLowerInvariant(s[i]) == char.ToLowerInvariant(t[j]) ? 0 : 1;
                    v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
                }
                for (int j = 0; j <= m; j++) v0[j] = v1[j];
            }

            return v1[m];
        }
    }
}
