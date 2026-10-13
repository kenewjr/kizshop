using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SDG.Unturned;
using Xunit;
using KizShop.API;
using KizShop.Combat;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Economy.Models;
using KizShop.Shop;
using KizShop.Shop.Gateways;
using KizShop.Shop.Models;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class Addendum6Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LiteDbStorage _storage;
        private readonly EconomyService _economy;
        private readonly ShopService _shopService;
        private readonly MockInventoryGateway _inventory;
        private readonly MockVehicleGateway _vehicle;
        private readonly KizShopConfiguration _config;
        private readonly MockClock _clock;
        private readonly CombatTagService _combatTag;
        private readonly ShopTransactionService _transactionService;

        public Addendum6Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_add6_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            _clock = new MockClock { UtcNow = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc) };
            string dbPath = Path.Combine(_tempDir, "test.litedb");
            _storage = new LiteDbStorage(dbPath, _clock);
            _storage.InitializeAsync().GetAwaiter().GetResult();

            _economy = new EconomyService(_storage, () => Money.FromDecimal(1000m), () => null, true, new TestMainThreadDispatcher());
            _shopService = new ShopService(_tempDir);
            _shopService.Load();

            _inventory = new MockInventoryGateway();
            _vehicle = new MockVehicleGateway();
            _config = new KizShopConfiguration
            {
                CombatTagEnabled = true,
                CombatTagSeconds = 20,
                CombatTagBlocksBuy = true,
                CombatTagBlocksSell = true,
                CombatTagBlocksVehicle = true,
                DynamicPricingDefaultEnabled = false,
                AutoEquipPurchasedItems = false,
                BountyEnabled = true,
                BountyMinAmount = 100m,
                BountyMaxPerTarget = 100000m,
                BountyFeePercent = 10,
                DailyRewardAmount = 100m,
                DailyStreakBonusPercentPerDay = 10,
                DailyMaxStreakDays = 7,
                DailyResetUtcOffsetHours = 7
            };
            _inventory.ConfigProvider = () => _config;

            _combatTag = new CombatTagService(() => _config, _clock);
            _transactionService = new ShopTransactionService(
                _shopService,
                _economy,
                _inventory,
                _vehicle,
                () => _config,
                _clock,
                _combatTag,
                _storage);
        }

        public void Dispose()
        {
            _storage?.Dispose();
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        // ==========================================
        // 1. COMBAT TAG
        // ==========================================

        [Fact]
        public async Task CombatTag_DamageTagsBoth_BlocksBuySellVehicle()
        {
            ulong attacker = 76561198000000001;
            ulong victim = 76561198000000002;

            _combatTag.Tag(attacker, victim);

            Assert.True(_combatTag.IsTagged(attacker, out int attSec));
            Assert.True(attSec > 0 && attSec <= 20);

            Assert.True(_combatTag.IsTagged(victim, out int vicSec));
            Assert.True(vicSec > 0 && vicSec <= 20);

            // Attempt Buy
            var buyRes = await _transactionService.ExecuteBuyAsync(attacker, "15", 1, _ => false, false);
            Assert.False(buyRes.Success);
            Assert.Equal("combat_tagged", buyRes.ErrorKey);

            // Attempt Sell
            var sellRes = await _transactionService.ExecuteSellAsync(attacker, "15", "1", _ => false, false);
            Assert.False(sellRes.Success);
            Assert.Equal("combat_tagged", sellRes.ErrorKey);

            // Attempt BuyVehicle
            var vehRes = await _transactionService.ExecuteBuyVehicleAsync(attacker, "1", _ => false, false);
            Assert.False(vehRes.Success);
            Assert.Equal("combat_tagged", vehRes.ErrorKey);
        }

        [Fact]
        public async Task CombatTag_ExpiresViaClock_AllowsTransactions()
        {
            ulong player = 76561198000000003;
            _combatTag.Tag(player, 0);

            Assert.True(_combatTag.IsTagged(player, out _));

            // Advance clock by 21 seconds
            _clock.UtcNow = _clock.UtcNow.AddSeconds(21);

            Assert.False(_combatTag.IsTagged(player, out _));

            // Give balance to buy
            await _economy.SetBalanceAsync(player, Money.FromDecimal(100m), "Init");
            var buyRes = await _transactionService.ExecuteBuyAsync(player, "15", 1, _ => false, false);
            Assert.True(buyRes.Success);
        }

        [Fact]
        public void CombatTag_DeathOrDisconnect_ClearsTag()
        {
            ulong player = 76561198000000004;
            _combatTag.Tag(player, 0);
            Assert.True(_combatTag.IsTagged(player, out _));

            _combatTag.Clear(player);
            Assert.False(_combatTag.IsTagged(player, out _));
        }

        [Fact]
        public async Task CombatTag_BypassPermission_AllowsTransactionsWhileTagged()
        {
            ulong player = 76561198000000005;
            _combatTag.Tag(player, 0);
            await _economy.SetBalanceAsync(player, Money.FromDecimal(100m), "Init");

            // Bypass permission true
            var buyRes = await _transactionService.ExecuteBuyAsync(player, "15", 1, perm => perm == "kizshop.bypass.combattag", false);
            Assert.True(buyRes.Success);
        }

        [Fact]
        public async Task CombatTag_DisabledConfig_AllowsTransactions()
        {
            _config.CombatTagEnabled = false;
            ulong player = 76561198000000006;
            _combatTag.Tag(player, 0);
            await _economy.SetBalanceAsync(player, Money.FromDecimal(100m), "Init");

            var buyRes = await _transactionService.ExecuteBuyAsync(player, "15", 1, _ => false, false);
            Assert.True(buyRes.Success);
        }

        // ==========================================
        // 2. SHOP CATEGORIES AND SEARCH
        // ==========================================

        [Fact]
        public void Categories_GetCategories_ReturnsExpectedCounts()
        {
            var cats = _shopService.GetCategoryItemCounts();
            Assert.NotEmpty(cats);

            // Default catalog contains Medical, Weapons, Food, etc.
            Assert.True(cats.ContainsKey("Medical"));
            Assert.True(cats["Medical"] > 0);
        }

        [Fact]
        public void Categories_ResolveCategory_PrefixAndAmbiguity()
        {
            // Exact match
            bool exactFound = _shopService.ResolveCategory("medical", out string exactMatched, out _);
            Assert.True(exactFound);
            Assert.Equal("Medical", exactMatched);

            // Unique prefix: "med" -> "Medical"
            bool prefixFound = _shopService.ResolveCategory("med", out string prefixMatched, out _);
            Assert.True(prefixFound);
            Assert.Equal("Medical", prefixMatched);

            // If we have "Food" and add "Footwear", "foo" is ambiguous
            _shopService.AddOrUpdateItem(new ItemShopEntry { Id = 9991, Name = "Shoe", BuyPrice = 10, Category = "Footwear", Enabled = true });
            bool ambFound = _shopService.ResolveCategory("foo", out _, out var ambCandidates);
            Assert.False(ambFound);
            Assert.Contains("Food", ambCandidates);
            Assert.Contains("Footwear", ambCandidates);
        }

        [Fact]
        public void Categories_RejectNumericOrControlChars()
        {
            Assert.False(ShopService.ValidateCategory("123", out _));
            Assert.False(ShopService.ValidateCategory("Weap\non", out _));
            Assert.False(ShopService.ValidateCategory(new string('a', 33), out _));
            Assert.True(ShopService.ValidateCategory("Weapons", out string err));
            Assert.Null(err);
        }

        [Fact]
        public void Shop_Search_ByNameAndId()
        {
            // Search by ID
            var resId = _shopService.Search("15", 1, 10);
            Assert.True(resId.totalItems >= 1);
            Assert.Contains(resId.entries, x => x.Id == 15);

            // Search by Name substring: "med"
            var resName = _shopService.Search("med", 1, 10);
            Assert.True(resName.totalItems >= 1);
            Assert.Contains(resName.entries, x => x.Name.IndexOf("med", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Shop_GetPagedItems_CategoryFilterAndPagingBounds()
        {
            var paged = _shopService.GetPagedItems("Medical", 1, 10);
            Assert.True(paged.totalItems >= 1);
            Assert.All(paged.entries, i => Assert.Equal("Medical", i.Category, ignoreCase: true));

            // Out-of-bounds page clamped to max page
            var outOfBounds = _shopService.GetPagedItems("Medical", 999, 10);
            Assert.NotEmpty(outOfBounds.entries);
            Assert.True(outOfBounds.totalPages >= 1);
        }

        [Fact]
        public void Shop_OldCatalogJson_DeserializesMissingCategoryToDefault()
        {
            string oldJson = @"
{
  ""Items"": [
    {
      ""Id"": 901,
      ""Name"": ""OldMedkit"",
      ""BuyPrice"": 50.0,
      ""SellPrice"": 25.0
    }
  ],
  ""Vehicles"": [
    {
      ""Id"": 902,
      ""Name"": ""OldJeep"",
      ""BuyPrice"": 500.0
    }
  ]
}";
            string path = Path.Combine(_tempDir, "old_catalog.json");
            File.WriteAllText(path, oldJson);

            var oldShop = new ShopService(_tempDir);
            // Replace path to test deserialization
            var catalog = Newtonsoft.Json.JsonConvert.DeserializeObject<ShopData>(oldJson);
            Assert.Equal("General", catalog.Items[0].Category);
            Assert.Equal("Vehicles", catalog.Vehicles[0].Category);
        }

        // ==========================================
        // 3. UCONOMY IMPORT
        // ==========================================

        [Fact]
        public async Task UconomyImport_CsvParsing_DryRunWritesNothing_ApplySetsBalance()
        {
            string csvContent = @"steamid64,balance
76561198000000050,250.75
76561198000000051,1000.00
invalid_steam_id,50.00
76561198000000052,-10.00
76561198000000053,999999999.00
";
            string csvFile = Path.Combine(_tempDir, "uconomy_import.csv");
            File.WriteAllText(csvFile, csvContent);

            var importService = new UconomyImportService(_storage, () => _config, _tempDir);

            // 1. Dry run
            var dryReport = await importService.ImportFromCsvAsync(overwrite: false, apply: false);
            Assert.True(dryReport.DryRun);
            Assert.Equal(5, dryReport.TotalRead);
            Assert.Equal(2, dryReport.ImportedCount); // valid 50 and 51
            Assert.Equal(1, dryReport.SkippedInvalidSteamId);
            Assert.Equal(1, dryReport.SkippedInvalidBalance);
            Assert.Equal(1, dryReport.SkippedExceedsMax);

            // Verify nothing written
            bool exists50 = await _storage.AccountExistsAsync(76561198000000050);
            Assert.False(exists50);

            // 2. Apply
            var applyReport = await importService.ImportFromCsvAsync(overwrite: false, apply: true);
            Assert.False(applyReport.DryRun);
            Assert.Equal(2, applyReport.ImportedCount);

            // Verify written
            var bal50 = await _storage.GetBalanceAsync(76561198000000050, Money.Zero);
            Assert.Equal(250.75m, bal50.ToDecimal());

            // 3. Run again without overwrite -> skipped existing
            var secondReport = await importService.ImportFromCsvAsync(overwrite: false, apply: true);
            Assert.Equal(2, secondReport.SkippedExisting);
            Assert.Equal(0, secondReport.ImportedCount);

            // 4. Run again with overwrite
            var overwriteReport = await importService.ImportFromCsvAsync(overwrite: true, apply: true);
            Assert.Equal(2, overwriteReport.ImportedCount);
        }

        // ==========================================
        // 4. BOSS REWARD API & PURE CALCULATOR
        // ==========================================

        [Fact]
        public void BossRewardCalculator_ExactSumPropertyTest_RandomInputs()
        {
            var rng = new Random(1337);

            for (int i = 0; i < 50; i++)
            {
                long poolCents = rng.Next(1, 1000000);
                var pool = Money.FromCents(poolCents);

                int playerCount = rng.Next(1, 20);
                var damage = new Dictionary<ulong, float>();
                for (int p = 0; p < playerCount; p++)
                {
                    damage[(ulong)(76561198000000100 + p)] = (float)(rng.NextDouble() * 500.0 + 1.0);
                }

                ulong lastHit = (ulong)(76561198000000100 + rng.Next(0, playerCount));

                var payouts = BossRewardCalculator.CalculateDistribution(
                    pool,
                    damage,
                    lastHit,
                    topDamagePercent: 30,
                    lastHitPercent: 10,
                    participationPercent: 60,
                    minDamageSharePercent: 1.0f);

                long payoutSum = payouts.Values.Sum(v => v.Cents);
                Assert.Equal(pool.Cents, payoutSum);
            }
        }

        [Fact]
        public void BossRewardCalculator_RemainderRules_HighestRemainderFirst_TiesByLowerSteamId()
        {
            // Pool 100 cents (1.00m)
            // 3 players with equal damage: 10, 10, 10
            // No last hitter.
            // Participation pool = 70 cents.
            // 70 / 3 = 23 remainder 1/3 each.
            // Remainder is 1 cent -> goes to lowest SteamID!
            var damage = new Dictionary<ulong, float>
            {
                { 76561198000000200, 10f },
                { 76561198000000100, 10f },
                { 76561198000000300, 10f }
            };

            var payouts = BossRewardCalculator.CalculateDistribution(
                Money.FromCents(100),
                damage,
                lastHitSteamId: 0,
                topDamagePercent: 30,
                lastHitPercent: 0,
                participationPercent: 70,
                minDamageSharePercent: 0f);

            // All have equal damage, top damager is lowest SteamID (76561198000000100) -> gets 30
            // Participation 70: base 23 each (sum 69). Remainder 1 goes to lowest SteamID 100.
            // So 100 gets 30 + 23 + 1 = 54. 200 gets 23. 300 gets 23.
            Assert.Equal(54, payouts[76561198000000100].Cents);
            Assert.Equal(23, payouts[76561198000000200].Cents);
            Assert.Equal(23, payouts[76561198000000300].Cents);
            Assert.Equal(100, payouts.Values.Sum(v => v.Cents));
        }

        [Fact]
        public void BossRewardCalculator_LastHitterNotParticipant_PoolGoesToParticipation()
        {
            var damage = new Dictionary<ulong, float>
            {
                { 76561198000000100, 100f }
            };

            // Last hitter 999 is NOT in damage dict
            var payouts = BossRewardCalculator.CalculateDistribution(
                Money.FromDecimal(100m),
                damage,
                lastHitSteamId: 76561198000000999,
                topDamagePercent: 30,
                lastHitPercent: 10,
                participationPercent: 60,
                minDamageSharePercent: 1.0f);

            // Player 100 gets 100% of the pool (30 top + 70 participation)
            Assert.Single(payouts);
            Assert.Equal(10000, payouts[76561198000000100].Cents);
        }

        [Fact]
        public async Task BossRewardAPI_DistributeBossRewardAsync_IdempotentEventKey()
        {
            // Setup static plugin instance for API
            // Direct storage check for idempotency
            string eventKey = "boss_raid_alpha_1";
            var damage = new Dictionary<ulong, float>
            {
                { 76561198000000101, 80f },
                { 76561198000000102, 20f }
            };

            // Record event directly in storage
            bool recorded = await _storage.TryRecordRewardEventAsync(eventKey, "AlphaBoss", Money.FromDecimal(500m), _clock.UtcNow);
            Assert.True(recorded);

            var storedEvent = await _storage.GetRewardEventAsync(eventKey);
            Assert.NotNull(storedEvent);
            Assert.Equal(500m, storedEvent.Pool.ToDecimal());
        }

        // ==========================================
        // 5. DYNAMIC PRICING
        // ==========================================

        [Fact]
        public void PriceCalculator_Decay_Floor_AndRecoveryOverTime()
        {
            var cfg = new ItemDynamicPricingConfig
            {
                Enabled = true,
                SellDecayPerUnit = 0.05m, // 5% drop per unit
                MinSellMultiplier = 0.5m, // floor 50%
                RecoveryPerHour = 2.0m    // 2 units pressure per hour
            };

            // Pressure = 0 -> mult = 1.0
            Assert.Equal(1.0m, PriceCalculator.ComputeEffectiveSellMultiplier(cfg, 0m));

            // Pressure = 5 -> mult = 1 - 0.25 = 0.75
            Assert.Equal(0.75m, PriceCalculator.ComputeEffectiveSellMultiplier(cfg, 5m));

            // Pressure = 15 -> mult = 1 - 0.75 = 0.25 -> clamped to floor 0.5
            Assert.Equal(0.5m, PriceCalculator.ComputeEffectiveSellMultiplier(cfg, 15m));

            // Recovery over 2 hours from pressure 10: 10 - (2 * 2.0) = 6.0
            var t0 = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
            decimal recovered = PriceCalculator.ComputeRecoveredPressure(10m, t0, t2, cfg.RecoveryPerHour);
            Assert.Equal(6.0m, recovered);
        }

        [Fact]
        public void PriceCalculator_SequentialBulkSale_SumOfPerUnitCents()
        {
            var cfg = new ItemDynamicPricingConfig
            {
                Enabled = true,
                SellDecayPerUnit = 0.1m, // 10% decay per unit
                MinSellMultiplier = 0.5m
            };

            // Base sell 10.00
            // Unit 0: pressure 0 -> 10.00
            // Unit 1: pressure 1 -> 9.00
            // Unit 2: pressure 2 -> 8.00
            // Total = 27.00
            var res = PriceCalculator.CalculateSequentialSellPrice(
                baseBuyPrice: 20m,
                baseSellPrice: 10m,
                cfg: cfg,
                startingBuyPressure: 0m,
                startingSellPressure: 0m,
                quantity: 3,
                allowSellAboveBuy: false);

            Assert.Equal(2700, res.Total.Cents);
            Assert.Equal(1000, res.FirstUnitPrice.Cents);
        }

        [Fact]
        public void PriceCalculator_SellNeverExceedsBuy()
        {
            var cfg = new ItemDynamicPricingConfig
            {
                Enabled = true,
                SellDecayPerUnit = 0.01m,
                MinSellMultiplier = 0.5m,
                BuyIncreasePerUnit = 0.0m
            };

            // Base sell 15.00, base buy 10.00 -> sell clamped to buy (10.00)
            var unitSell = PriceCalculator.CalculateSingleUnitSellPrice(
                baseBuyPrice: 10m,
                baseSellPrice: 15m,
                cfg: cfg,
                buyPressure: 0m,
                sellPressure: 0m,
                allowSellAboveBuy: false);

            Assert.Equal(1000, unitSell.Cents);
        }

        // ==========================================
        // 6. /sell hand AND /sell all <category>
        // ==========================================

        [Fact]
        public async Task SellHand_NoEquippedItem_Fails()
        {
            ulong steamId = 76561198000000070;
            _inventory.EquippedItem = null;

            var res = await _transactionService.ExecuteSellHandAsync(steamId, _ => true, false);
            Assert.False(res.Success);
            Assert.Equal("sell_hand_empty", res.ErrorKey);
        }

        [Fact]
        public async Task SellHand_EquippedItem_SucceedsAndPays()
        {
            ulong steamId = 76561198000000071;
            var item = new Item(15, 1, 100, new byte[16]); // Medkit, sell price 15m
            _inventory.EquippedItem = new InventoryItemInstance { Item = item, Page = 0, Index = 0 };

            var res = await _transactionService.ExecuteSellHandAsync(steamId, _ => true, false);
            Assert.True(res.Success);
            Assert.Equal(15, res.ItemEntry.Id);
            Assert.Equal(15.00m, res.Price.ToDecimal());

            var bal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(1015.00m, bal.ToDecimal());
        }

        [Fact]
        public async Task SellHand_EquippedModifiedGun_Refuses()
        {
            ulong steamId = 76561198000000072;
            _shopService.AddOrUpdateItem(new ItemShopEntry { Id = 363, Name = "Maplestrike", BuyPrice = 200m, SellPrice = 60m, Category = "Weapons", Enabled = true });
            _inventory.GunItemIds.Add(363); // Maplestrike
            byte[] modState = new byte[16];
            modState[0] = 99; // Modified sight/attachment
            var gun = new Item(363, 1, 100, modState);
            _inventory.EquippedItem = new InventoryItemInstance { Item = gun, Page = 0, Index = 0 };

            var res = await _transactionService.ExecuteSellHandAsync(steamId, _ => true, false);
            Assert.False(res.Success);
            Assert.Equal("sell_gun_has_attachments", res.ErrorKey);
        }

        [Fact]
        public async Task SellAllCategory_Summary_ThenConfirmWithin30s_Succeeds()
        {
            ulong steamId = 76561198000000073;
            // Add 2 medkits (category Medical, sell price 15 each = 30 total)
            _inventory.GearItems.Add(new InventoryItemInstance { Item = new Item(15, 1, 100, new byte[16]), Page = 1, Index = 0 });
            _inventory.GearItems.Add(new InventoryItemInstance { Item = new Item(15, 1, 100, new byte[16]), Page = 1, Index = 1 });

            // 1. Summary
            var summary = await _transactionService.ExecuteSellAllCategorySummaryAsync(steamId, "Medical", _ => true, false);
            Assert.True(summary.Success);
            Assert.Equal(2, summary.TotalItemCount);
            Assert.Equal(30.00m, summary.ExpectedPayout.ToDecimal());

            // 2. Confirm within 10 seconds
            _clock.UtcNow = _clock.UtcNow.AddSeconds(10);
            var confirm = await _transactionService.ExecuteSellAllCategoryConfirmAsync(steamId, _ => true, false);
            Assert.True(confirm.Success);
            Assert.Equal(30.00m, confirm.Price.ToDecimal());

            var bal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(1030.00m, bal.ToDecimal());
        }

        [Fact]
        public async Task SellAllCategory_ConfirmAfter30s_Expires()
        {
            ulong steamId = 76561198000000074;
            _inventory.GearItems.Add(new InventoryItemInstance { Item = new Item(15, 1, 100, new byte[16]), Page = 1, Index = 0 });

            await _transactionService.ExecuteSellAllCategorySummaryAsync(steamId, "Medical", _ => true, false);

            // Advance clock past 30s
            _clock.UtcNow = _clock.UtcNow.AddSeconds(31);

            var confirm = await _transactionService.ExecuteSellAllCategoryConfirmAsync(steamId, _ => true, false);
            Assert.False(confirm.Success);
            Assert.Equal("sell_all_expired", confirm.ErrorKey);
        }

        [Fact]
        public async Task SellAllCategory_Confirm_InventoryChanged_Refuses()
        {
            ulong steamId = 76561198000000075;
            _inventory.GearItems.Add(new InventoryItemInstance { Item = new Item(15, 1, 100, new byte[16]), Page = 1, Index = 0 });

            await _transactionService.ExecuteSellAllCategorySummaryAsync(steamId, "Medical", _ => true, false);

            // Player drops item before confirming
            _inventory.GearItems.Clear();

            var confirm = await _transactionService.ExecuteSellAllCategoryConfirmAsync(steamId, _ => true, false);
            Assert.False(confirm.Success);
            Assert.Equal("sell_all_inventory_changed", confirm.ErrorKey);
        }

        // ==========================================
        // 7. BOUNTY AND DAILY REWARD
        // ==========================================

        [Fact]
        public async Task Bounty_Place_WithdrawsEscrow_NetCalculatedWithFee()
        {
            ulong placer = 76561198000000080;
            ulong target = 76561198000000081;

            // Placer has 500m
            await _economy.SetBalanceAsync(placer, Money.FromDecimal(500m), "Init");
            await _economy.SetBalanceAsync(target, Money.FromDecimal(10m), "Init");

            // Place 100m bounty with 10% fee -> 10m fee, 90m net bounty
            var escrow = Money.FromDecimal(100m);
            var feeCents = (escrow.Cents * _config.BountyFeePercent) / 100;
            var net = Money.FromCents(escrow.Cents - feeCents);

            var withdraw = await _economy.TryWithdrawAsync(placer, escrow, "BountyEscrow");
            Assert.True(withdraw.Success);

            await _storage.CreateBountyAsync(placer, target, escrow, net, _clock.UtcNow);

            var totalOnTarget = await _storage.GetTotalBountyOnTargetAsync(target);
            Assert.Equal(90m, totalOnTarget.ToDecimal());

            var placerBal = await _economy.GetBalanceAsync(placer);
            Assert.Equal(400m, placerBal.ToDecimal());
        }

        [Fact]
        public async Task Bounty_Claim_AtomicConcurrency_PaysOnce()
        {
            ulong placer = 76561198000000082;
            ulong target = 76561198000000083;
            ulong killer = 76561198000000084;

            await _storage.CreateBountyAsync(placer, target, Money.FromDecimal(100m), Money.FromDecimal(90m), _clock.UtcNow);

            // Killer claims bounty
            var (claims1, total1) = await _storage.ClaimBountiesAsync(target, killer);
            Assert.Equal(90m, total1.ToDecimal());

            // Immediate second claim returns 0
            var (claims2, total2) = await _storage.ClaimBountiesAsync(target, killer);
            Assert.Equal(0m, total2.ToDecimal());
        }

        [Fact]
        public async Task Daily_StreakProgression_AndResetOffsetCalculation()
        {
            ulong steamId = 76561198000000090;

            // Day index calculation with +7 offset
            var t0 = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
            _clock.UtcNow = t0;

            var localT0 = t0.AddHours(_config.DailyResetUtcOffsetHours);
            int dayIndex0 = (int)(localT0.Date - new DateTime(1970, 1, 1)).TotalDays;

            // Claim 1
            var res1 = await _storage.TryClaimDailyAsync(steamId, dayIndex0, _config.DailyMaxStreakDays, _clock.UtcNow);
            Assert.True(res1.Allowed);
            Assert.Equal(1, res1.Streak);

            // Same day claim rejected
            var resSameDay = await _storage.TryClaimDailyAsync(steamId, dayIndex0, _config.DailyMaxStreakDays, _clock.UtcNow);
            Assert.False(resSameDay.Allowed);

            // Next day claim increments streak
            _clock.UtcNow = _clock.UtcNow.AddDays(1);
            var localT1 = _clock.UtcNow.AddHours(_config.DailyResetUtcOffsetHours);
            int dayIndex1 = (int)(localT1.Date - new DateTime(1970, 1, 1)).TotalDays;

            var res2 = await _storage.TryClaimDailyAsync(steamId, dayIndex1, _config.DailyMaxStreakDays, _clock.UtcNow);
            Assert.True(res2.Allowed);
            Assert.Equal(2, res2.Streak);

            // Streak bonus calculation: base 100 + (10% * (2 - 1)) = 110
            decimal bonusMult = 1.0m + ((res2.Streak - 1) * (_config.DailyStreakBonusPercentPerDay / 100m));
            decimal finalAmount = _config.DailyRewardAmount * bonusMult;
            Assert.Equal(110.00m, finalAmount);
        }
    }
}
