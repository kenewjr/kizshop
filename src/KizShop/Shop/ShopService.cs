using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using KizShop.Shop.Models;

namespace KizShop.Shop
{
    public class ShopService
    {
        private readonly string _catalogPath;
        private readonly object _lock = new object();
        private ShopData _data = new ShopData();

        public ShopService(string dataFolder)
        {
            _catalogPath = Path.Combine(dataFolder, "KizShop.shop.json");
        }

        public void Load()
        {
            lock (_lock)
            {
                if (File.Exists(_catalogPath))
                {
                    try
                    {
                        var json = File.ReadAllText(_catalogPath);
                        _data = JsonConvert.DeserializeObject<ShopData>(json) ?? new ShopData();
                        return;
                    }
                    catch
                    {
                        // Fall back to default if corrupt
                    }
                }

                _data = CreateDefaultShopData();
                Save();
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

                var json = JsonConvert.SerializeObject(_data, Formatting.Indented);
                File.WriteAllText(_catalogPath, json);
            }
        }

        public ItemShopEntry FindItem(string query)
        {
            lock (_lock)
            {
                return AssetResolver.FindItemEntry(_data.Items, query);
            }
        }

        public VehicleShopEntry FindVehicle(string query)
        {
            lock (_lock)
            {
                return AssetResolver.FindVehicleEntry(_data.Vehicles, query);
            }
        }

        public List<ItemShopEntry> GetItems()
        {
            lock (_lock)
            {
                return new List<ItemShopEntry>(_data.Items);
            }
        }

        public List<VehicleShopEntry> GetVehicles()
        {
            lock (_lock)
            {
                return new List<VehicleShopEntry>(_data.Vehicles);
            }
        }

        public (List<ItemShopEntry> entries, int totalPages) GetPagedItems(int page, int pageSize)
        {
            lock (_lock)
            {
                var enabledItems = _data.Items.Where(i => i.Enabled).ToList();
                int totalPages = (int)Math.Ceiling((double)enabledItems.Count / pageSize);
                if (totalPages < 1) totalPages = 1;
                if (page < 1) page = 1;
                if (page > totalPages) page = totalPages;

                var entries = enabledItems
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                return (entries, totalPages);
            }
        }

        public bool AddOrUpdateItem(ItemShopEntry entry)
        {
            if (entry == null || entry.Id == 0) return false;

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
                }
                else
                {
                    _data.Items.Add(entry);
                }
                Save();
                return true;
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

        public bool SetItemPrice(ushort id, decimal buyPrice, decimal? sellPrice)
        {
            lock (_lock)
            {
                var entry = _data.Items.FirstOrDefault(i => i.Id == id);
                if (entry == null) return false;

                entry.BuyPrice = buyPrice;
                entry.SellPrice = sellPrice;
                Save();
                return true;
            }
        }

        public bool AddOrUpdateVehicle(VehicleShopEntry entry)
        {
            if (entry == null || entry.Id == 0) return false;

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
                }
                else
                {
                    _data.Vehicles.Add(entry);
                }
                Save();
                return true;
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

        public bool SetVehiclePrice(ushort id, decimal buyPrice, decimal? sellPrice)
        {
            lock (_lock)
            {
                var entry = _data.Vehicles.FirstOrDefault(v => v.Id == id);
                if (entry == null) return false;

                entry.BuyPrice = buyPrice;
                entry.SellPrice = sellPrice;
                Save();
                return true;
            }
        }

        private static ShopData CreateDefaultShopData()
        {
            return new ShopData
            {
                Items = new List<ItemShopEntry>
                {
                    new ItemShopEntry { Id = 15, Name = "Medkit", BuyPrice = 40.00m, SellPrice = 15.00m, Enabled = true },
                    new ItemShopEntry { Id = 394, Name = "Dressing", BuyPrice = 15.00m, SellPrice = 5.00m, Enabled = true },
                    new ItemShopEntry { Id = 253, Name = "Alicepack", BuyPrice = 250.00m, SellPrice = 80.00m, Enabled = true },
                    new ItemShopEntry { Id = 18, Name = "Timberwolf", BuyPrice = 500.00m, SellPrice = 150.00m, Enabled = true },
                    new ItemShopEntry { Id = 20, Name = "Timberwolf Magazine", BuyPrice = 75.00m, SellPrice = 25.00m, Enabled = true },
                    new ItemShopEntry { Id = 81, Name = "Military MRE", BuyPrice = 20.00m, SellPrice = 5.00m, Enabled = true },
                    new ItemShopEntry { Id = 14, Name = "Bottled Water", BuyPrice = 10.00m, SellPrice = 2.00m, Enabled = true }
                },
                Vehicles = new List<VehicleShopEntry>
                {
                    new VehicleShopEntry { Id = 1, Name = "Black Offroader", BuyPrice = 300.00m, SellPrice = 100.00m, Enabled = true },
                    new VehicleShopEntry { Id = 33, Name = "Police Car", BuyPrice = 450.00m, SellPrice = 150.00m, Enabled = true },
                    new VehicleShopEntry { Id = 59, Name = "Jeep", BuyPrice = 400.00m, SellPrice = 120.00m, Enabled = true },
                    new VehicleShopEntry { Id = 93, Name = "Forest Huey", BuyPrice = 1500.00m, SellPrice = 500.00m, Enabled = true }
                }
            };
        }
    }
}
