using System;
using System.Collections.Generic;
using System.Linq;
using KizShop.Shop.Models;

namespace KizShop.Shop
{
    public static class AssetResolver
    {
        public static ItemShopEntry FindItemEntry(IEnumerable<ItemShopEntry> entries, string query)
        {
            if (entries == null || string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            query = query.Trim();

            // 1. Check numeric ID
            if (ushort.TryParse(query, out var id))
            {
                var matchById = entries.FirstOrDefault(e => e.Id == id);
                if (matchById != null) return matchById;
            }

            // 2. Check GUID
            if (Guid.TryParse(query, out var guid))
            {
                var guidStr = guid.ToString("N");
                var guidHyphen = guid.ToString("D");
                var matchByGuid = entries.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Guid) &&
                    (string.Equals(e.Guid, guidStr, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(e.Guid, guidHyphen, StringComparison.OrdinalIgnoreCase)));
                if (matchByGuid != null) return matchByGuid;
            }

            // 3. Exact name match (case-insensitive)
            var exactName = entries.FirstOrDefault(e =>
                string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase));
            if (exactName != null) return exactName;

            // 4. Starts with name match
            var startsWithName = entries.FirstOrDefault(e =>
                e.Name != null && e.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase));
            if (startsWithName != null) return startsWithName;

            // 5. Contains name match
            return entries.FirstOrDefault(e =>
                e.Name != null && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static VehicleShopEntry FindVehicleEntry(IEnumerable<VehicleShopEntry> entries, string query)
        {
            if (entries == null || string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            query = query.Trim();

            // 1. Check numeric ID
            if (ushort.TryParse(query, out var id))
            {
                var matchById = entries.FirstOrDefault(e => e.Id == id);
                if (matchById != null) return matchById;
            }

            // 2. Check GUID
            if (Guid.TryParse(query, out var guid))
            {
                var guidStr = guid.ToString("N");
                var guidHyphen = guid.ToString("D");
                var matchByGuid = entries.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Guid) &&
                    (string.Equals(e.Guid, guidStr, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(e.Guid, guidHyphen, StringComparison.OrdinalIgnoreCase)));
                if (matchByGuid != null) return matchByGuid;
            }

            // 3. Exact name match
            var exactName = entries.FirstOrDefault(e =>
                string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase));
            if (exactName != null) return exactName;

            // 4. Starts with name match
            var startsWithName = entries.FirstOrDefault(e =>
                e.Name != null && e.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase));
            if (startsWithName != null) return startsWithName;

            // 5. Contains name match
            return entries.FirstOrDefault(e =>
                e.Name != null && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
