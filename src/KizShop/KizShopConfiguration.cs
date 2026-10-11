using Rocket.API;

namespace KizShop
{
    public class KizShopConfiguration : IRocketPluginConfiguration
    {
        public string DatabaseType { get; set; } = "SQLite";
        public string ConnectionString { get; set; } = "kizshop.db";
        public string CurrencyName { get; set; } = "Credits";
        public string CurrencySymbol { get; set; } = "$";
        public decimal StartingBalance { get; set; } = 100.00m;
        public decimal MaxBalance { get; set; } = 0.00m; // 0 or negative = unlimited
        public int BaltopCount { get; set; } = 10;
        public int LogRetentionDays { get; set; } = 30;

        // Kill rewards
        public bool PlayerKillRewardEnabled { get; set; } = true;
        public decimal PlayerKillRewardAmount { get; set; } = 50.00m;
        public bool ZombieKillRewardEnabled { get; set; } = true;
        public decimal ZombieKillRewardAmount { get; set; } = 5.00m;
        public decimal MegaZombieKillRewardAmount { get; set; } = 50.00m;

        // Playtime rewards
        public bool PlaytimeIncomeEnabled { get; set; } = true;
        public int PlaytimeIncomeIntervalSeconds { get; set; } = 600;
        public decimal PlaytimeIncomeAmount { get; set; } = 25.00m;

        // Shop settings
        public float VehicleSpawnDistance { get; set; } = 5.0f;
        public int ShopItemsPerPage { get; set; } = 8;

        public void LoadDefaults()
        {
            DatabaseType = "SQLite";
            ConnectionString = "kizshop.db";
            CurrencyName = "Credits";
            CurrencySymbol = "$";
            StartingBalance = 100.00m;
            MaxBalance = 0.00m;
            BaltopCount = 10;
            LogRetentionDays = 30;

            PlayerKillRewardEnabled = true;
            PlayerKillRewardAmount = 50.00m;
            ZombieKillRewardEnabled = true;
            ZombieKillRewardAmount = 5.00m;
            MegaZombieKillRewardAmount = 50.00m;

            PlaytimeIncomeEnabled = true;
            PlaytimeIncomeIntervalSeconds = 600;
            PlaytimeIncomeAmount = 25.00m;

            VehicleSpawnDistance = 5.0f;
            ShopItemsPerPage = 8;
        }
    }
}
