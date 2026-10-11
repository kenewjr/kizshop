using System;
using System.Collections.Generic;
using Xunit;
using KizShop.Shop;
using KizShop.Shop.Models;

namespace KizShop.Tests
{
    public class AssetLookupTests
    {
        private readonly List<ItemShopEntry> _items = new List<ItemShopEntry>
        {
            new ItemShopEntry { Id = 18, Guid = "a1b2c3d4e5f60718293a4b5c6d7e8f90", Name = "Timberwolf", BuyPrice = 500m },
            new ItemShopEntry { Id = 20, Guid = "11223344556677889900aabbccddeeff", Name = "Timberwolf Magazine", BuyPrice = 75m },
            new ItemShopEntry { Id = 253, Name = "Alicepack", BuyPrice = 250m },
            new ItemShopEntry { Id = 15, Name = "Medkit", BuyPrice = 40m }
        };

        [Fact]
        public void ResolveItem_ByNumericId_FindsExact()
        {
            var res = AssetResolver.ResolveItem(_items, "18");
            Assert.Equal(LookupStatus.Found, res.Status);
            Assert.Equal("Timberwolf", res.Entry.Name);
        }

        [Fact]
        public void ResolveItem_ByGuid_FindsExact()
        {
            var res = AssetResolver.ResolveItem(_items, "a1b2c3d4e5f60718293a4b5c6d7e8f90");
            Assert.Equal(LookupStatus.Found, res.Status);
            Assert.Equal(18, res.Entry.Id);
        }

        [Fact]
        public void ResolveItem_ByHyphenatedGuid_FindsExact()
        {
            var guid = Guid.Parse("11223344-5566-7788-9900-aabbccddeeff");
            var res = AssetResolver.ResolveItem(_items, guid.ToString("D"));
            Assert.Equal(LookupStatus.Found, res.Status);
            Assert.Equal(20, res.Entry.Id);
        }

        [Fact]
        public void ResolveItem_ByExactName_CaseInsensitive()
        {
            var res = AssetResolver.ResolveItem(_items, "medkit");
            Assert.Equal(LookupStatus.Found, res.Status);
            Assert.Equal(15, res.Entry.Id);
        }

        [Fact]
        public void ResolveItem_ByUniquePrefixName_Matches()
        {
            var res = AssetResolver.ResolveItem(_items, "Alice");
            Assert.Equal(LookupStatus.Found, res.Status);
            Assert.Equal(253, res.Entry.Id);
        }

        [Fact]
        public void ResolveItem_ByUniquePartialName_Matches()
        {
            var res = AssetResolver.ResolveItem(_items, "Magazine");
            Assert.Equal(LookupStatus.Found, res.Status);
            Assert.Equal(20, res.Entry.Id);
        }

        [Fact]
        public void ResolveItem_NonExistent_ReturnsNotFound()
        {
            var res = AssetResolver.ResolveItem(_items, "Dragonfang");
            Assert.Equal(LookupStatus.NotFound, res.Status);
            Assert.Null(res.Entry);
        }
    }
}
