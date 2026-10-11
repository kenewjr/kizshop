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
        public void FindItemEntry_ByNumericId_FindsExact()
        {
            var match = AssetResolver.FindItemEntry(_items, "18");
            Assert.NotNull(match);
            Assert.Equal("Timberwolf", match.Name);
        }

        [Fact]
        public void FindItemEntry_ByGuid_FindsExact()
        {
            var match = AssetResolver.FindItemEntry(_items, "a1b2c3d4e5f60718293a4b5c6d7e8f90");
            Assert.NotNull(match);
            Assert.Equal(18, match.Id);
        }

        [Fact]
        public void FindItemEntry_ByHyphenatedGuid_FindsExact()
        {
            var guid = Guid.Parse("11223344-5566-7788-9900-aabbccddeeff");
            var match = AssetResolver.FindItemEntry(_items, guid.ToString("D"));
            Assert.NotNull(match);
            Assert.Equal(20, match.Id);
        }

        [Fact]
        public void FindItemEntry_ByExactName_CaseInsensitive()
        {
            var match = AssetResolver.FindItemEntry(_items, "medkit");
            Assert.NotNull(match);
            Assert.Equal(15, match.Id);
        }

        [Fact]
        public void FindItemEntry_ByPrefixName_Matches()
        {
            var match = AssetResolver.FindItemEntry(_items, "Alice");
            Assert.NotNull(match);
            Assert.Equal(253, match.Id);
        }

        [Fact]
        public void FindItemEntry_ByPartialName_Matches()
        {
            var match = AssetResolver.FindItemEntry(_items, "Magazine");
            Assert.NotNull(match);
            Assert.Equal(20, match.Id);
        }

        [Fact]
        public void FindItemEntry_NonExistent_ReturnsNull()
        {
            var match = AssetResolver.FindItemEntry(_items, "Dragonfang");
            Assert.Null(match);
        }
    }
}
