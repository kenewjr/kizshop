using System;
using System.IO;
using Xunit;
using KizShop.Shop;
using KizShop.Shop.Models;

namespace KizShop.Tests
{
    public class CatalogSafetyTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _catalogPath;

        public CatalogSafetyTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_cat_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _catalogPath = Path.Combine(_tempDir, "KizShop.shop.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void CorruptJson_PreservesPreviousCatalogAndCreatesBrokenCopy()
        {
            var service = new ShopService(_tempDir);
            // 1. Initial valid load
            Assert.True(service.Load());
            int originalCount = service.GetItems().Count;
            Assert.True(originalCount > 0);

            // 2. Corrupt the file on disk
            File.WriteAllText(_catalogPath, "{ \"Items\": [ { \"Id\": 15, \"Name\": \"BrokenJson... ");

            // 3. Reload
            bool reloaded = service.Load();

            // Previous catalog should still be retained in memory!
            Assert.True(reloaded);
            Assert.Equal(originalCount, service.GetItems().Count);

            // Broken backup file must exist
            var brokenFiles = Directory.GetFiles(_tempDir, "KizShop.shop.json.broken-*");
            Assert.Single(brokenFiles);

            // Original file must NOT be overwritten by empty defaults!
            string diskContent = File.ReadAllText(_catalogPath);
            Assert.Contains("BrokenJson", diskContent);
        }

        [Fact]
        public void SellPriceAboveBuyPrice_IsRejectedByDefault()
        {
            var service = new ShopService(_tempDir, () => false); // AllowSellAboveBuy = false
            service.Load();

            var badEntry = new ItemShopEntry
            {
                Id = 999,
                Name = "ExploitItem",
                BuyPrice = 50m,
                SellPrice = 60m // 60 > 50!
            };

            string error = service.AddOrUpdateItem(badEntry);

            Assert.NotNull(error);
            Assert.Contains("cannot exceed", error);
        }
    }
}
