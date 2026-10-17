using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Rocket.API;
using KizShop.Commands;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Economy.Models;
using KizShop.Shop;
using KizShop.Shop.Models;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class Addendum8Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly MockClock _clock;

        public Addendum8Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_add8_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _clock = new MockClock { UtcNow = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc) };
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        // =================================================================
        // 1. BUYKIT HARDENING & ROLLBACK GUARANTEE
        // =================================================================

        [Fact]
        public async Task BuyKitAsync_DeliveryException_RollsBackDeliveredItems_RefundsOnce_AndClearsCooldown()
        {
            string dbPath = Path.Combine(_tempDir, "kit_harden_ex.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            var inventory = new MockInventoryGateway
            {
                AutoAddToGearOnDelivery = true,
                ThrowOnDelivery = false
            };
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock, null, storage);

            ulong steamId = 76561198000000201UL;
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(500m), "Initial");

            // Custom kit with 2 items and a 60s cooldown
            var kit = new KitShopEntry
            {
                Name = "custom_kit",
                BuyPrice = 100m,
                CooldownSeconds = 60,
                Enabled = true,
                Items = new List<KitItemEntry>
                {
                    new KitItemEntry(97, 1),
                    new KitItemEntry(98, 1)
                }
            };
            shop.AddKit(kit);

            // First item will deliver, but second delivery throws exception
            inventory.MaxDeliveryCountBeforeFailure = 1;
            var throwingGateway = new ThrowingInventoryGateway(throwAfterCount: 1);
            var txThrowing = new ShopTransactionService(shop, eco, throwingGateway, vehicle, () => cfg, _clock, null, storage);

            var res = await txThrowing.BuyKitAsync(steamId, "custom_kit", _ => true, false);

            Assert.False(res.Success);
            Assert.Equal("kit_delivery_failed", res.ErrorKey);

            // Verify the 1st delivered item was removed via RemoveItemByReferenceAsync
            Assert.Single(throwingGateway.RemovedByReferenceItems);

            // Verify full kit price was refunded with ignoreMaxBalance
            var bal = await eco.GetBalanceAsync(steamId);
            Assert.Equal(500m, bal.ToDecimal());

            // Verify cooldown was NOT persisted in storage
            var cooldown = await storage.GetKitCooldownAsync(steamId, "custom_kit");
            Assert.Null(cooldown);
        }

        [Fact]
        public async Task BuyKitAsync_TryAddItemFails_RollsBackDeliveredItems_RefundsFullPrice()
        {
            string dbPath = Path.Combine(_tempDir, "kit_harden_fail.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            var inventory = new MockInventoryGateway
            {
                AutoAddToGearOnDelivery = true,
                MaxDeliveryCountBeforeFailure = 1 // 1st succeeds, 2nd fails
            };
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock, null, storage);

            ulong steamId = 76561198000000202UL;
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(300m), "Initial");

            var kit = new KitShopEntry
            {
                Name = "multi_item_kit",
                BuyPrice = 50m,
                CooldownSeconds = 30,
                Enabled = true,
                Items = new List<KitItemEntry>
                {
                    new KitItemEntry(97, 1),
                    new KitItemEntry(98, 1)
                }
            };
            shop.AddKit(kit);

            var res = await tx.BuyKitAsync(steamId, "multi_item_kit", _ => true, false);

            Assert.False(res.Success);
            Assert.Equal("kit_no_space", res.ErrorKey);

            // Delivered item removed
            Assert.Single(inventory.RemovedByReferenceItems);

            // Refunded back to 300
            var bal = await eco.GetBalanceAsync(steamId);
            Assert.Equal(300m, bal.ToDecimal());

            // Cooldown not persisted
            var cooldown = await storage.GetKitCooldownAsync(steamId, "multi_item_kit");
            Assert.Null(cooldown);
        }

        [Fact]
        public async Task BuyKitAsync_EventSubscriberThrows_DoesNotFailPurchase()
        {
            string dbPath = Path.Combine(_tempDir, "kit_event_throw.litedb");
            using var storage = new LiteDbStorage(dbPath, _clock);
            await storage.InitializeAsync();

            var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
            var shop = new ShopService(_tempDir);
            shop.Load();

            var inventory = new MockInventoryGateway { AutoAddToGearOnDelivery = true };
            var vehicle = new MockVehicleGateway();
            var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
            var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock, null, storage);

            ulong steamId = 76561198000000203UL;
            await eco.SetBalanceAsync(steamId, Money.FromDecimal(200m), "Initial");

            var kit = new KitShopEntry
            {
                Name = "safe_event_kit",
                BuyPrice = 100m,
                Enabled = true,
                Items = new List<KitItemEntry> { new KitItemEntry(97, 1) }
            };
            shop.AddKit(kit);

            // Throwing event handler
            tx.OnKitPurchased += (sId, k, price, newBal) =>
            {
                throw new InvalidOperationException("Crashing event handler!");
            };

            var res = await tx.BuyKitAsync(steamId, "safe_event_kit", _ => true, false);

            Assert.True(res.Success);
            Assert.Equal(Money.FromDecimal(100m), res.NewBalance);
        }

        // =================================================================
        // 2. PERSIST KIT COOLDOWNS ACROSS REBOOTS
        // =================================================================

        [Fact]
        public async Task KitCooldown_PersistsInStorage_AndEnforcedAcrossServiceInstances()
        {
            string dbPath = Path.Combine(_tempDir, "kit_cooldown_reboot.litedb");
            using (var storage = new LiteDbStorage(dbPath, _clock))
            {
                await storage.InitializeAsync();
                var eco = new EconomyService(storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
                var shop = new ShopService(_tempDir);
                shop.Load();

                var inventory = new MockInventoryGateway { AutoAddToGearOnDelivery = true };
                var vehicle = new MockVehicleGateway();
                var cfg = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
                var tx = new ShopTransactionService(shop, eco, inventory, vehicle, () => cfg, _clock, null, storage);

                ulong steamId = 76561198000000204UL;
                await eco.SetBalanceAsync(steamId, Money.FromDecimal(1000m), "Initial");

                var kit = new KitShopEntry
                {
                    Name = "cooldown_kit",
                    BuyPrice = 50m,
                    CooldownSeconds = 120,
                    Enabled = true,
                    Items = new List<KitItemEntry> { new KitItemEntry(97, 1) }
                };
                shop.AddKit(kit);

                var buyRes = await tx.BuyKitAsync(steamId, "cooldown_kit", _ => true, false);
                Assert.True(buyRes.Success);
            }

            // Simulate server reboot by creating brand new storage and transaction service
            using (var storage2 = new LiteDbStorage(dbPath, _clock))
            {
                await storage2.InitializeAsync();
                var eco2 = new EconomyService(storage2, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
                var shop2 = new ShopService(_tempDir);
                shop2.Load();

                var inventory2 = new MockInventoryGateway { AutoAddToGearOnDelivery = true };
                var vehicle2 = new MockVehicleGateway();
                var cfg2 = new KizShopConfiguration { MaxTransactionAmount = 1000000m };
                var tx2 = new ShopTransactionService(shop2, eco2, inventory2, vehicle2, () => cfg2, _clock, null, storage2);

                ulong steamId = 76561198000000204UL;
                var kit2 = new KitShopEntry
                {
                    Name = "cooldown_kit",
                    BuyPrice = 50m,
                    CooldownSeconds = 120,
                    Enabled = true,
                    Items = new List<KitItemEntry> { new KitItemEntry(97, 1) }
                };
                shop2.AddKit(kit2);

                // Advance clock by only 30s (cooldown is 120s)
                _clock.UtcNow = _clock.UtcNow.AddSeconds(30);

                var rejectRes = await tx2.BuyKitAsync(steamId, "cooldown_kit", _ => true, false);
                Assert.False(rejectRes.Success);
                Assert.Equal("kit_cooldown", rejectRes.ErrorKey);
                Assert.Equal(90, (int)rejectRes.ErrorArgs[0]);

                // Advance clock past 120s
                _clock.UtcNow = _clock.UtcNow.AddSeconds(100);

                var allowRes = await tx2.BuyKitAsync(steamId, "cooldown_kit", _ => true, false);
                Assert.True(allowRes.Success);
            }
        }

        // =================================================================
        // 3. RESOLVE COMMAND COLLISION WITH uEssentials
        // =================================================================

        [Fact]
        public void CommandKit_HasExactName_AndDisallowsConflictingAliases()
        {
            var cmd = new CommandKit();
            Assert.Equal("kizkit", cmd.Name);
            Assert.Contains("buykit", cmd.Aliases);
            Assert.DoesNotContain("kit", cmd.Aliases);
            Assert.DoesNotContain("kits", cmd.Aliases);
            Assert.DoesNotContain("kitlist", cmd.Aliases);
            Assert.Contains("kizshop.kit", cmd.Permissions);
        }

        // =================================================================
        // 4. SALE VALIDATION & PRICE INVERSION CLAMPING
        // =================================================================

        [Fact]
        public void KizShopConfiguration_Normalize_ClampsSaleMultipliers()
        {
            var cfg = new KizShopConfiguration
            {
                SaleMinBuyMultiplier = 0.001m, // below 0.01
                SaleMaxSellMultiplier = 0.50m  // below 1.00
            };

            cfg.Normalize();

            Assert.Equal(0.01m, cfg.SaleMinBuyMultiplier);
            Assert.Equal(1.00m, cfg.SaleMaxSellMultiplier);
        }

        [Fact]
        public void PriceCalculator_WhenAllowSellAboveBuyFalse_ClampsSellPriceToBuyPriceUnderSale()
        {
            decimal baseBuy = 100m;
            decimal baseSell = 90m;
            var dynCfg = new ItemDynamicPricingConfig { Enabled = false };

            // Sale: buyMultiplier = 0.50 (buy is $50), sellMultiplier = 1.50 (sell would be $135)
            // But AllowSellAboveBuy is false, so sell MUST be clamped to buy price ($50)
            var singleSell = PriceCalculator.CalculateSingleUnitSellPrice(
                baseBuy,
                baseSell,
                dynCfg,
                0m,
                0m,
                allowSellAboveBuy: false,
                buyMultiplier: 0.50m,
                sellMultiplier: 1.50m);

            Assert.Equal(Money.FromDecimal(50m), singleSell);

            // If AllowSellAboveBuy is true, no clamp ($90 * 1.50 = $135)
            var singleSellAllowed = PriceCalculator.CalculateSingleUnitSellPrice(
                baseBuy,
                baseSell,
                dynCfg,
                0m,
                0m,
                allowSellAboveBuy: true,
                buyMultiplier: 0.50m,
                sellMultiplier: 1.50m);

            Assert.Equal(Money.FromDecimal(135m), singleSellAllowed);
        }

        // =================================================================
        // 5. GIVEAWAY SAFEGUARDS
        // =================================================================

        [Fact]
        public void KizShopConfiguration_Normalize_ClampsGiveawayMax()
        {
            var cfg = new KizShopConfiguration
            {
                GiveawayMaxPerPlayer = -50m
            };

            cfg.Normalize();

            Assert.Equal(0m, cfg.GiveawayMaxPerPlayer);
        }

        // =================================================================
        // 6. CSV INJECTION PROTECTION
        // =================================================================

        [Theory]
        [InlineData("=SUM(A1:A10)", "\"'=SUM(A1:A10)\"")]
        [InlineData("+12345", "\"'+12345\"")]
        [InlineData("-cmd|' /C calc'!A0", "\"'-cmd|' /C calc'!A0\"")]
        [InlineData("@special", "\"'@special\"")]
        [InlineData("\tTabPrefixed", "\"'\tTabPrefixed\"")]
        [InlineData("Normal text", "\"Normal text\"")]
        [InlineData("Text with \"quotes\"", "\"Text with \"\"quotes\"\"\"")]
        [InlineData("Multi\r\nLine\nText", "\"Multi Line Text\"")]
        public void DataExportService_EscapeCsvText_PreventsFormulaInjection(string input, string expected)
        {
            string actual = DataExportService.EscapeCsvText(input);
            Assert.Equal(expected, actual);
        }

        // =================================================================
        // 7. TRANSLATIONS MIGRATION WITH XDOCUMENT & ATOMIC REPLACE
        // =================================================================

        [Fact]
        public void MigrateTranslationsOnDisk_OnlyReplacesKnownKeys_CreatesBakFile_AndAtomicallyReplaces()
        {
            string transFile = Path.Combine(_tempDir, "test.en.translation.xml");
            string originalXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Translations xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <Translation Id=""kit_list_entry"" Value=""&lt;color=gold&gt;{0}&lt;/color&gt; - {1}"" />
  <Translation Id=""custom_unknown_key"" Value=""&lt;color=gold&gt;Do Not Touch&lt;/color&gt;"" />
</Translations>";

            File.WriteAllText(transFile, originalXml);

            // Call migration directly
            KizShopPlugin.MigrateTranslations(_tempDir);

            // Verify .bak exists
            string bakFile = transFile + ".bak";
            Assert.True(File.Exists(bakFile));
            Assert.Equal(originalXml, File.ReadAllText(bakFile));

            // Verify updated XML
            string updatedXml = File.ReadAllText(transFile);
            Assert.Contains("kit_list_entry", updatedXml);
            Assert.Contains("&lt;color=yellow&gt;{0}&lt;/color&gt;", updatedXml);

            // Unknown key should NOT be changed
            Assert.Contains(@"<Translation Id=""custom_unknown_key"" Value=""&lt;color=gold&gt;Do Not Touch&lt;/color&gt;"" />", updatedXml);
        }

        private class ThrowingInventoryGateway : MockInventoryGateway
        {
            private readonly int _throwAfterCount;
            private int _deliveredCount;

            public ThrowingInventoryGateway(int throwAfterCount)
            {
                _throwAfterCount = throwAfterCount;
            }

            public override Task<bool> TryAddItemAsync(ulong steamId, SDG.Unturned.Item item, bool autoEquip = false)
            {
                _deliveredCount++;
                if (_deliveredCount > _throwAfterCount)
                {
                    throw new InvalidOperationException("Simulated delivery crash on item #" + _deliveredCount);
                }
                return base.TryAddItemAsync(steamId, item, autoEquip);
            }
        }
    }
}
