using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SDG.Unturned;
using KizShop.Common;
using KizShop.Shop.Models;

namespace KizShop.Shop
{
    public class ShopService
    {
        private readonly string _catalogPath;
        private readonly Func<bool> _allowSellAboveBuy;
        private readonly object _lock = new object();
        private ShopData _data = new ShopData();

        public ShopService(string dataFolder, Func<bool> allowSellAboveBuy = null)
        {
            _catalogPath = Path.Combine(dataFolder, "KizShop.shop.json");
            _allowSellAboveBuy = allowSellAboveBuy ?? (() => false);
        }

        public bool Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_catalogPath))
                {
                    _data = CreateDefaultShopData();
                    Save();
                    return true;
                }

                try
                {
                    var json = File.ReadAllText(_catalogPath);
                    var parsed = JsonConvert.DeserializeObject<ShopData>(json);
                    if (parsed == null) throw new JsonException("Parsed shop data is null.");

                    // Validate parsed entries
                    string valError = ValidateShopData(parsed);
                    if (valError != null)
                    {
                        throw new InvalidDataException(valError);
                    }

                    foreach (var item in parsed.Items)
                    {
                        if (string.IsNullOrWhiteSpace(item.Category)) item.Category = "General";
                        item.DynamicPricing?.Normalize();
                    }

                    _data = parsed;
                    return true;
                }
                catch (Exception ex)
                {
                    string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    string brokenPath = $"{_catalogPath}.broken-{timestamp}";

                    try
                    {
                        File.Copy(_catalogPath, brokenPath, true);
                    }
                    catch { }

                    if (ex is JsonReaderException jex)
                    {
                        KizLog.Error($"[KizShop] Corrupt shop catalog at line {jex.LineNumber}, position {jex.LinePosition}: {jex.Message}. Preserving existing catalog. Copied corrupt file to {brokenPath}");
                    }
                    else
                    {
                        KizLog.Error($"[KizShop] Invalid shop catalog: {ex.Message}. Preserving existing catalog. Copied corrupt file to {brokenPath}");
                    }

                    // If memory catalog is already loaded from previous run, keep it.
                    // If initial load, keep empty and return false.
                    return _data.Items.Count > 0 || _data.Vehicles.Count > 0;
                }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_catalogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tempFile = $"{_catalogPath}.tmp-{Guid.NewGuid():N}";
                var json = JsonConvert.SerializeObject(_data, Formatting.Indented);
                File.WriteAllText(tempFile, json);

                if (File.Exists(_catalogPath))
                {
                    File.Replace(tempFile, _catalogPath, null);
                }
                else
                {
                    File.Move(tempFile, _catalogPath);
                }
            }
        }

        public string ValidateShopData(ShopData data)
        {
            if (data == null) return "Catalog data is null.";

            var itemIds = new HashSet<ushort>();
            foreach (var item in data.Items)
            {
                if (item.Id == 0) return $"Invalid item ID: {item.Id}.";
                if (!itemIds.Add(item.Id)) return $"Duplicate item ID found in catalog: {item.Id}.";

                if (!Money.TryFromDecimal(item.BuyPrice, 0, out _, out var buyErr))
                {
                    return $"Item {item.Id} invalid BuyPrice ({item.BuyPrice}): {buyErr}";
                }

                if (item.SellPrice.HasValue)
                {
                    if (!Money.TryFromDecimalAllowZero(item.SellPrice.Value, out _, out var sellErr))
                    {
                        return $"Item {item.Id} invalid SellPrice ({item.SellPrice.Value}): {sellErr}";
                    }

                    if (!_allowSellAboveBuy() && item.SellPrice.Value > item.BuyPrice)
                    {
                        return $"Item {item.Id} SellPrice ({item.SellPrice.Value}) cannot exceed BuyPrice ({item.BuyPrice}).";
                    }
                }
            }

            var vehIds = new HashSet<ushort>();
            foreach (var veh in data.Vehicles)
            {
                if (veh.Id == 0) return $"Invalid vehicle ID: {veh.Id}.";
                if (!vehIds.Add(veh.Id)) return $"Duplicate vehicle ID found in catalog: {veh.Id}.";

                if (!Money.TryFromDecimal(veh.BuyPrice, 0, out _, out var buyErr))
                {
                    return $"Vehicle {veh.Id} invalid BuyPrice ({veh.BuyPrice}): {buyErr}";
                }

                if (veh.SellPrice.HasValue)
                {
                    if (!Money.TryFromDecimalAllowZero(veh.SellPrice.Value, out _, out var sellErr))
                    {
                        return $"Vehicle {veh.Id} invalid SellPrice ({veh.SellPrice.Value}): {sellErr}";
                    }

                    if (!_allowSellAboveBuy() && veh.SellPrice.Value > veh.BuyPrice)
                    {
                        return $"Vehicle {veh.Id} SellPrice ({veh.SellPrice.Value}) cannot exceed BuyPrice ({veh.BuyPrice}).";
                    }
                }
            }

            return null;
        }

        public LookupResult<ItemShopEntry> ResolveItem(string query, bool enabledOnly = false)
        {
            lock (_lock)
            {
                return AssetResolver.ResolveItem(_data.Items, query, enabledOnly);
            }
        }

        public LookupResult<VehicleShopEntry> ResolveVehicle(string query, bool enabledOnly = false)
        {
            lock (_lock)
            {
                return AssetResolver.ResolveVehicle(_data.Vehicles, query, enabledOnly);
            }
        }

        public List<ItemShopEntry> GetItems(bool enabledOnly = false)
        {
            lock (_lock)
            {
                return enabledOnly
                    ? _data.Items.Where(i => i.Enabled && i.Available).ToList()
                    : new List<ItemShopEntry>(_data.Items);
            }
        }

        public List<VehicleShopEntry> GetVehicles()
        {
            lock (_lock)
            {
                return new List<VehicleShopEntry>(_data.Vehicles);
            }
        }

        public static bool ValidateCategory(string category, out string error)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                error = "Category cannot be empty.";
                return false;
            }
            category = category.Trim();
            if (category.Length > 32)
            {
                error = "Category must be 32 characters or less.";
                return false;
            }
            foreach (char c in category)
            {
                if (char.IsControl(c))
                {
                    error = "Category cannot contain control characters.";
                    return false;
                }
            }
            if (int.TryParse(category, out _))
            {
                error = "Category cannot be purely numeric.";
                return false;
            }
            error = null;
            return true;
        }

        public List<string> GetCategories()
        {
            lock (_lock)
            {
                return _data.Items
                    .Where(i => i.Enabled && i.Available && !string.IsNullOrWhiteSpace(i.Category))
                    .Select(i => i.Category.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c)
                    .ToList();
            }
        }

        public Dictionary<string, int> GetCategoryItemCounts()
        {
            lock (_lock)
            {
                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _data.Items.Where(i => i.Enabled && i.Available))
                {
                    string cat = string.IsNullOrWhiteSpace(item.Category) ? "General" : item.Category.Trim();
                    if (dict.ContainsKey(cat)) dict[cat]++;
                    else dict[cat] = 1;
                }
                return dict;
            }
        }

        public bool ResolveCategory(string query, out string matchedCategory, out List<string> candidates)
        {
            matchedCategory = null;
            candidates = new List<string>();
            if (string.IsNullOrWhiteSpace(query)) return false;

            query = query.Trim();
            var allCats = GetCategories();

            // 1. Exact match (case-insensitive)
            var exact = allCats.FirstOrDefault(c => c.Equals(query, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                matchedCategory = exact;
                return true;
            }

            // 2. Prefix match (case-insensitive)
            var prefixMatches = allCats.Where(c => c.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prefixMatches.Count == 1)
            {
                matchedCategory = prefixMatches[0];
                return true;
            }
            if (prefixMatches.Count > 1)
            {
                candidates = prefixMatches;
                return false;
            }

            return false;
        }

        public (List<ItemShopEntry> entries, int totalPages, int totalItems) GetPagedItems(string category, int page, int pageSize)
        {
            lock (_lock)
            {
                var items = _data.Items.Where(i => i.Enabled && i.Available);
                if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    items = items.Where(i => (i.Category ?? "General").Equals(category, StringComparison.OrdinalIgnoreCase));
                }

                var list = items.ToList();
                int totalItems = list.Count;
                int totalPages = (int)Math.Ceiling((double)totalItems / pageSize);
                if (totalPages < 1) totalPages = 1;
                if (page < 1) page = 1;
                if (page > totalPages) page = totalPages;

                var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                return (paged, totalPages, totalItems);
            }
        }

        public (List<ItemShopEntry> entries, int totalPages, int totalItems) Search(string text, int page, int pageSize)
        {
            lock (_lock)
            {
                text = text?.Trim() ?? string.Empty;
                bool isId = ushort.TryParse(text, out ushort searchId);

                var matches = _data.Items
                    .Where(i => i.Enabled && i.Available &&
                                ((isId && i.Id == searchId) ||
                                 (!string.IsNullOrEmpty(i.Name) && i.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)))
                    .ToList();

                int totalItems = matches.Count;
                int totalPages = (int)Math.Ceiling((double)totalItems / pageSize);
                if (totalPages < 1) totalPages = 1;
                if (page < 1) page = 1;
                if (page > totalPages) page = totalPages;

                var paged = matches.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                return (paged, totalPages, totalItems);
            }
        }

        public bool SetItemCategory(ushort id, string category, out string error)
        {
            if (!ValidateCategory(category, out error)) return false;

            lock (_lock)
            {
                var item = _data.Items.FirstOrDefault(i => i.Id == id);
                if (item == null)
                {
                    error = $"Item ID {id} not found in shop.";
                    return false;
                }

                item.Category = category.Trim();
                Save();
                error = null;
                return true;
            }
        }

        public (List<ItemShopEntry> entries, int totalPages) GetPagedItems(int page, int pageSize)
        {
            var res = GetPagedItems(null, page, pageSize);
            return (res.entries, res.totalPages);
        }

        public string AddOrUpdateItem(ItemShopEntry entry)
        {
            if (entry == null || entry.Id == 0) return "Invalid item entry or ID.";

            if (!Money.TryFromDecimal(entry.BuyPrice, 0, out _, out var buyErr))
            {
                return $"BuyPrice error: {buyErr}";
            }

            if (entry.SellPrice.HasValue)
            {
                if (!Money.TryFromDecimalAllowZero(entry.SellPrice.Value, out _, out var sellErr))
                {
                    return $"SellPrice error: {sellErr}";
                }
                if (!_allowSellAboveBuy() && entry.SellPrice.Value > entry.BuyPrice)
                {
                    return "Sell price cannot exceed buy price.";
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.Category) && !ValidateCategory(entry.Category, out var catErr))
            {
                return $"Category error: {catErr}";
            }

            lock (_lock)
            {
                var existing = _data.Items.FirstOrDefault(i => i.Id == entry.Id);
                if (existing != null)
                {
                    existing.Name = string.IsNullOrEmpty(entry.Name) ? existing.Name : entry.Name;
                    existing.Guid = string.IsNullOrEmpty(entry.Guid) ? existing.Guid : entry.Guid;
                    existing.BuyPrice = entry.BuyPrice;
                    existing.SellPrice = entry.SellPrice;
                    existing.Enabled = entry.Enabled;
                    existing.Permission = entry.Permission;
                    existing.MaxAmountPerPurchase = entry.MaxAmountPerPurchase;
                    if (!string.IsNullOrWhiteSpace(entry.Category))
                    {
                        existing.Category = entry.Category.Trim();
                    }
                    if (entry.DynamicPricing != null)
                    {
                        entry.DynamicPricing.Normalize();
                        existing.DynamicPricing = entry.DynamicPricing;
                    }
                }
                else
                {
                    entry.DynamicPricing?.Normalize();
                    if (string.IsNullOrWhiteSpace(entry.Category))
                    {
                        entry.Category = "General";
                    }
                    _data.Items.Add(entry);
                }
                Save();
                return null;
            }
        }

        public bool RemoveItem(ushort id)
        {
            lock (_lock)
            {
                int removed = _data.Items.RemoveAll(i => i.Id == id);
                if (removed > 0)
                {
                    Save();
                    return true;
                }
                return false;
            }
        }

        public string SetItemPrice(ushort id, decimal buyPrice, decimal? sellPrice)
        {
            if (!Money.TryFromDecimal(buyPrice, 0, out _, out var buyErr))
            {
                return $"BuyPrice error: {buyErr}";
            }

            if (sellPrice.HasValue)
            {
                if (!Money.TryFromDecimalAllowZero(sellPrice.Value, out _, out var sellErr))
                {
                    return $"SellPrice error: {sellErr}";
                }
                if (!_allowSellAboveBuy() && sellPrice.Value > buyPrice)
                {
                    return "Sell price cannot exceed buy price.";
                }
            }

            lock (_lock)
            {
                var entry = _data.Items.FirstOrDefault(i => i.Id == id);
                if (entry == null) return "Item not found.";

                entry.BuyPrice = buyPrice;
                entry.SellPrice = sellPrice;
                Save();
                return null;
            }
        }

        public string AddOrUpdateVehicle(VehicleShopEntry entry)
        {
            if (entry == null || entry.Id == 0) return "Invalid vehicle entry or ID.";

            if (!Money.TryFromDecimal(entry.BuyPrice, 0, out _, out var buyErr))
            {
                return $"BuyPrice error: {buyErr}";
            }

            if (entry.SellPrice.HasValue)
            {
                if (!Money.TryFromDecimalAllowZero(entry.SellPrice.Value, out _, out var sellErr))
                {
                    return $"SellPrice error: {sellErr}";
                }
                if (!_allowSellAboveBuy() && entry.SellPrice.Value > entry.BuyPrice)
                {
                    return "Sell price cannot exceed buy price.";
                }
            }

            lock (_lock)
            {
                var existing = _data.Vehicles.FirstOrDefault(v => v.Id == entry.Id);
                if (existing != null)
                {
                    existing.Name = string.IsNullOrEmpty(entry.Name) ? existing.Name : entry.Name;
                    existing.Guid = string.IsNullOrEmpty(entry.Guid) ? existing.Guid : entry.Guid;
                    existing.BuyPrice = entry.BuyPrice;
                    existing.SellPrice = entry.SellPrice;
                    existing.Enabled = entry.Enabled;
                    existing.Permission = entry.Permission;
                    if (!string.IsNullOrWhiteSpace(entry.Category))
                    {
                        existing.Category = entry.Category.Trim();
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(entry.Category))
                    {
                        entry.Category = "Vehicles";
                    }
                    _data.Vehicles.Add(entry);
                }
                Save();
                return null;
            }
        }

        public bool RemoveVehicle(ushort id)
        {
            lock (_lock)
            {
                int removed = _data.Vehicles.RemoveAll(v => v.Id == id);
                if (removed > 0)
                {
                    Save();
                    return true;
                }
                return false;
            }
        }

        public string SetVehiclePrice(ushort id, decimal buyPrice, decimal? sellPrice)
        {
            if (!Money.TryFromDecimal(buyPrice, 0, out _, out var buyErr))
            {
                return $"BuyPrice error: {buyErr}";
            }

            if (sellPrice.HasValue)
            {
                if (!Money.TryFromDecimalAllowZero(sellPrice.Value, out _, out var sellErr))
                {
                    return $"SellPrice error: {sellErr}";
                }
                if (!_allowSellAboveBuy() && sellPrice.Value > buyPrice)
                {
                    return "Sell price cannot exceed buy price.";
                }
            }

            lock (_lock)
            {
                var entry = _data.Vehicles.FirstOrDefault(v => v.Id == id);
                if (entry == null) return "Vehicle not found.";

                entry.BuyPrice = buyPrice;
                entry.SellPrice = sellPrice;
                Save();
                return null;
            }
        }

        private static ShopData CreateDefaultShopData()
        {
            return new ShopData
            {
                Items = new List<ItemShopEntry>
                {
                    new ItemShopEntry { Id = 15, Name = "Medkit", BuyPrice = 40.00m, SellPrice = 15.00m, Category = "Medical", Enabled = true },
                    new ItemShopEntry { Id = 394, Name = "Dressing", BuyPrice = 15.00m, SellPrice = 5.00m, Category = "Medical", Enabled = true },
                    new ItemShopEntry { Id = 253, Name = "Alicepack", BuyPrice = 250.00m, SellPrice = 80.00m, Category = "Clothing", Enabled = true },
                    new ItemShopEntry { Id = 18, Name = "Timberwolf", BuyPrice = 500.00m, SellPrice = 150.00m, Category = "Weapons", Enabled = true },
                    new ItemShopEntry { Id = 20, Name = "Timberwolf Magazine", BuyPrice = 75.00m, SellPrice = 25.00m, Category = "Ammo", Enabled = true },
                    new ItemShopEntry { Id = 81, Name = "MRE", BuyPrice = 20.00m, SellPrice = 5.00m, Category = "Food", Enabled = true },
                    new ItemShopEntry { Id = 14, Name = "Bottled Water", BuyPrice = 10.00m, SellPrice = 2.00m, Category = "Food", Enabled = true }
                },
                Vehicles = new List<VehicleShopEntry>
                {
                    new VehicleShopEntry { Id = 1, Name = "Black Offroader", BuyPrice = 300.00m, SellPrice = 100.00m, Category = "Vehicles", Enabled = true },
                    new VehicleShopEntry { Id = 33, Name = "Police Car", BuyPrice = 450.00m, SellPrice = 150.00m, Category = "Vehicles", Enabled = true },
                    new VehicleShopEntry { Id = 59, Name = "Jeep", BuyPrice = 400.00m, SellPrice = 120.00m, Category = "Vehicles", Enabled = true },
                    new VehicleShopEntry { Id = 93, Name = "Forest Huey", BuyPrice = 1500.00m, SellPrice = 500.00m, Category = "Vehicles", Enabled = true }
                }
            };
        }
    }
}
