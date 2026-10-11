using System;
using System.Linq;
using Xunit;
using KizShop.Shop.Models;

namespace KizShop.Tests
{
    public class PriceMathTests
    {
        [Fact]
        public void TotalBuyPrice_CalculatesCorrectlyWithoutFloatDrift()
        {
            var entry = new ItemShopEntry
            {
                Id = 1,
                Name = "TestItem",
                BuyPrice = 19.99m,
                Enabled = true
            };

            int quantity = 3;
            decimal total = entry.BuyPrice * quantity;

            Assert.Equal(59.97m, total);
        }

        [Fact]
        public void TotalSellPrice_CalculatesCorrectly()
        {
            var entry = new ItemShopEntry
            {
                Id = 1,
                Name = "TestItem",
                SellPrice = 8.75m,
                Enabled = true
            };

            int quantity = 4;
            decimal total = entry.SellPrice.Value * quantity;

            Assert.Equal(35.00m, total);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(-10, false)]
        [InlineData(15.5, true)]
        public void CanBuy_EvaluatesEnabledAndPositivePrice(decimal price, bool expected)
        {
            var entry = new ItemShopEntry
            {
                Id = 10,
                BuyPrice = price,
                Enabled = true
            };

            Assert.Equal(expected, entry.CanBuy);
        }

        [Fact]
        public void CanSell_DisabledWhenSellPriceNullOrZero()
        {
            var nullEntry = new ItemShopEntry { Id = 1, SellPrice = null, Enabled = true };
            var zeroEntry = new ItemShopEntry { Id = 2, SellPrice = 0m, Enabled = true };
            var validEntry = new ItemShopEntry { Id = 3, SellPrice = 12.50m, Enabled = true };
            var disabledEntry = new ItemShopEntry { Id = 4, SellPrice = 12.50m, Enabled = false };

            Assert.False(nullEntry.CanSell);
            Assert.False(zeroEntry.CanSell);
            Assert.True(validEntry.CanSell);
            Assert.False(disabledEntry.CanSell);
        }
    }
}
