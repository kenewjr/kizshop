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

        // Pricing rules
        public bool ScaleSellByQuality { get; set; } = true;
        public bool AllowSellAboveBuy { get; set; } = false;

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

        // Visual UI settings
        public bool EnableVisualUI { get; set; } = true;
        public ushort UIEffectId { get; set; } = 32001;
        public short UIKey { get; set; } = 32001;
        public bool UseVisualUiForShopCommand { get; set; } = false;
        public int UIItemsPerPage { get; set; } = 6;

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

            EnableVisualUI = true;
            UIEffectId = 32001;
            UIKey = 32001;
            UseVisualUiForShopCommand = false;
            UIItemsPerPage = 6;

            DiscordWebhookEnabled = false;
            DiscordWebhookUrl = "";
            DiscordLogPurchases = true;
            DiscordLogAdminEco = true;
            DiscordLargeTransactionThreshold = 5000.00m;

            DiscordBotEnabled = false;
            DiscordBotToken = "";
            DiscordBotPrefix = "!ks ";
            DiscordBotAdminUserIds = "";
        }
    }
}
