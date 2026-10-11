using System;
using System.IO;

namespace KizShop.Storage
{
    public static class StorageFactory
    {
        public static IKizStorage Create(string storageType, string connectionString, string dataFolder)
        {
            if (string.Equals(storageType, "MySQL", StringComparison.OrdinalIgnoreCase))
            {
                return new MySqlStorage(connectionString);
            }

            // Default to SQLite
            string dbPath;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                dbPath = Path.Combine(dataFolder, "kizshop.db");
            }
            else if (connectionString.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || connectionString.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            {
                dbPath = Path.IsPathRooted(connectionString) ? connectionString : Path.Combine(dataFolder, connectionString);
            }
            else
            {
                dbPath = Path.Combine(dataFolder, "kizshop.db");
            }

            return new SqliteStorage(dbPath);
        }
    }
}
