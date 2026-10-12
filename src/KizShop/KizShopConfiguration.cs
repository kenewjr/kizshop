using Rocket.API;

namespace KizShop
{
    public class KizShopConfiguration : IRocketPluginConfiguration
    {
        // Database settings: "LiteDB" (default, pure managed), "SQLite", "MySQL"
        public string DatabaseType { get; set; } = "LiteDB";
        public string ConnectionString { get; set; } = "kizshop.litedb";

        // Currency & Economy display
        public string CurrencyName { get; set; } = "Credits";
        public string CurrencySymbol { get; set; } = "$";
        public decimal StartingBalance { get; set; } = 100.00m;
        public decimal MaxBalance { get; set; } = 0.00m; // 0 = unlimited
        public decimal MaxTransactionAmount { get; set; } = 10000000.00m; // 0 = unlimited
        public int BaltopCount { get; set; } = 10;
        public int LogRetentionDays { get; set; } = 30;

        // Pricing & item rules
        public bool ScaleSellByQuality { get; set; } = true;
        public bool AllowSellAboveBuy { get; set; } = false;
        public bool GunPurchaseIncludesMagazine { get; set; } = false;
        public bool AutoEquipPurchasedItems { get; set; } = false;

        // Kill rewards
        public bool PlayerKillRewardEnabled { get; set; } = true;
        public decimal PlayerKillRewardAmount { get; set; } = 50.00m;
        public int PlayerKillRewardCooldownSeconds { get; set; } = 600;
        public bool ZombieKillRewardEnabled { get; set; } = true;
        public decimal ZombieKillRewardAmount { get; set; } = 5.00m;
        public decimal MegaZombieKillRewardAmount { get; set; } = 50.00m;

        // Playtime rewards
        public bool PlaytimeIncomeEnabled { get; set; } = true;
        public int PlaytimeIncomeIntervalSeconds { get; set; } = 600;
        public decimal PlaytimeIncomeAmount { get; set; } = 25.00m;

        // Shop & Vehicle settings
        public float VehicleSpawnDistance { get; set; } = 5.0f;
        public int VehicleCooldownSeconds { get; set; } = 30;
        public int MaxVehiclesPerPlayer { get; set; } = 2;
        public int ShopItemsPerPage { get; set; } = 8;

        // Inventory logging
        public bool VerboseInventoryLogging { get; set; } = false;

        // Discord Webhook settings
        public bool DiscordWebhookEnabled { get; set; } = false;
        public string DiscordWebhookUrl { get; set; } = "";
        public bool DiscordLogPurchases { get; set; } = true;
        public bool DiscordLogAdminEco { get; set; } = true;
        public decimal DiscordLargeTransactionThreshold { get; set; } = 5000.00m;

        // Discord Bot settings
        public bool DiscordBotEnabled { get; set; } = false;
        public string DiscordBotToken { get; set; } = "";
        public string DiscordBotPrefix { get; set; } = "!ks ";
        public string DiscordBotAdminUserIds { get; set; } = "";

        // Combat Tag
        public bool CombatTagEnabled { get; set; } = true;
        public int CombatTagSeconds { get; set; } = 20;
        public bool CombatTagBlocksBuy { get; set; } = true;
        public bool CombatTagBlocksSell { get; set; } = true;
        public bool CombatTagBlocksVehicle { get; set; } = true;

        // Uconomy Import
        public string UconomyImportConnectionString { get; set; } = "";
        public string UconomyImportTable { get; set; } = "uconomy";
        public string UconomyImportSteamIdColumn { get; set; } = "steamId";
        public string UconomyImportBalanceColumn { get; set; } = "balance";

        // Boss Reward
        public int BossRewardTopDamagePercent { get; set; } = 30;
        public int BossRewardLastHitPercent { get; set; } = 10;
        public int BossRewardParticipationPercent { get; set; } = 60;
        public int BossRewardMinDamageSharePercent { get; set; } = 1;

        // Dynamic Pricing
        public bool DynamicPricingDefaultEnabled { get; set; } = false;

        // Bounty
        public bool BountyEnabled { get; set; } = true;
        public decimal BountyMinAmount { get; set; } = 100.00m;
        public decimal BountyMaxPerTarget { get; set; } = 100000.00m;
        public int BountyFeePercent { get; set; } = 10;
        public int BountyExpiryDays { get; set; } = 7;
        public bool BountyAnnounceInChat { get; set; } = true;

        // Daily Reward
        public bool DailyRewardEnabled { get; set; } = true;
        public decimal DailyRewardAmount { get; set; } = 100.00m;
        public int DailyStreakBonusPercentPerDay { get; set; } = 10;
        public int DailyMaxStreakDays { get; set; } = 7;
        public int DailyResetUtcOffsetHours { get; set; } = 7;

        public void LoadDefaults()
        {
            DatabaseType = "LiteDB";
            ConnectionString = "kizshop.litedb";
            CurrencyName = "Credits";
            CurrencySymbol = "$";
            StartingBalance = 100.00m;
            MaxBalance = 0.00m;
            MaxTransactionAmount = 10000000.00m;
            BaltopCount = 10;
            LogRetentionDays = 30;

            ScaleSellByQuality = true;
            AllowSellAboveBuy = false;
            GunPurchaseIncludesMagazine = false;
            AutoEquipPurchasedItems = false;

            PlayerKillRewardEnabled = true;
            PlayerKillRewardAmount = 50.00m;
            PlayerKillRewardCooldownSeconds = 600;
            ZombieKillRewardEnabled = true;
            ZombieKillRewardAmount = 5.00m;
            MegaZombieKillRewardAmount = 50.00m;

            PlaytimeIncomeEnabled = true;
            PlaytimeIncomeIntervalSeconds = 600;
            PlaytimeIncomeAmount = 25.00m;

            VehicleSpawnDistance = 5.0f;
            VehicleCooldownSeconds = 30;
            MaxVehiclesPerPlayer = 2;
            ShopItemsPerPage = 8;

            VerboseInventoryLogging = false;

            DiscordWebhookEnabled = false;
            DiscordWebhookUrl = "";
            DiscordLogPurchases = true;
            DiscordLogAdminEco = true;
            DiscordLargeTransactionThreshold = 5000.00m;

            DiscordBotEnabled = false;
            DiscordBotToken = "";
            DiscordBotPrefix = "!ks ";
            DiscordBotAdminUserIds = "";

            CombatTagEnabled = true;
            CombatTagSeconds = 20;
            CombatTagBlocksBuy = true;
            CombatTagBlocksSell = true;
            CombatTagBlocksVehicle = true;

            UconomyImportConnectionString = "";
            UconomyImportTable = "uconomy";
            UconomyImportSteamIdColumn = "steamId";
            UconomyImportBalanceColumn = "balance";

            BossRewardTopDamagePercent = 30;
            BossRewardLastHitPercent = 10;
            BossRewardParticipationPercent = 60;
            BossRewardMinDamageSharePercent = 1;

            DynamicPricingDefaultEnabled = false;

            BountyEnabled = true;
            BountyMinAmount = 100.00m;
            BountyMaxPerTarget = 100000.00m;
            BountyFeePercent = 10;
            BountyExpiryDays = 7;
            BountyAnnounceInChat = true;

            DailyRewardEnabled = true;
            DailyRewardAmount = 100.00m;
            DailyStreakBonusPercentPerDay = 10;
            DailyMaxStreakDays = 7;
            DailyResetUtcOffsetHours = 7;
        }
    }
}
