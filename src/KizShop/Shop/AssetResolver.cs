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

            var pool = enabledOnly ? entries.Where(e => e.Enabled && e.Available).ToList() : entries.ToList();
            query = query.Trim();

            // 1. Numeric ID
            if (ushort.TryParse(query, out var id))
            {
                var matchById = pool.FirstOrDefault(e => e.Id == id);
                if (matchById != null) return LookupResult<ItemShopEntry>.Success(matchById);
            }

            // 2. GUID
            if (Guid.TryParse(query, out var guid))
            {
                var guidStr = guid.ToString("N");
                var guidHyphen = guid.ToString("D");
                var matchByGuid = pool.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Guid) &&
                    (string.Equals(e.Guid, guidStr, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(e.Guid, guidHyphen, StringComparison.OrdinalIgnoreCase)));
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

            return LookupResult<ItemShopEntry>.NotFound();
        }

        public static LookupResult<VehicleShopEntry> ResolveVehicle(IEnumerable<VehicleShopEntry> entries, string query, bool enabledOnly = false)
        {
            if (entries == null || string.IsNullOrWhiteSpace(query))
            {
                return LookupResult<VehicleShopEntry>.NotFound();
            }

            var pool = enabledOnly ? entries.Where(e => e.Enabled).ToList() : entries.ToList();
            query = query.Trim();

            // 1. Numeric ID
            if (ushort.TryParse(query, out var id))
            {
                var matchById = pool.FirstOrDefault(e => e.Id == id);
                if (matchById != null) return LookupResult<VehicleShopEntry>.Success(matchById);
            }

            // 2. GUID
            if (Guid.TryParse(query, out var guid))
            {
                var guidStr = guid.ToString("N");
                var guidHyphen = guid.ToString("D");
                var matchByGuid = pool.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Guid) &&
                    (string.Equals(e.Guid, guidStr, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(e.Guid, guidHyphen, StringComparison.OrdinalIgnoreCase)));
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

            return LookupResult<VehicleShopEntry>.NotFound();
        }
    }
}
