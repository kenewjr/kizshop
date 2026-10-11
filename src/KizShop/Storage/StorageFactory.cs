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

            if (string.Equals(storageType, "SQLite", StringComparison.OrdinalIgnoreCase))
            {
                string sqlitePath;
                if (string.IsNullOrWhiteSpace(connectionString) || connectionString.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || connectionString.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    sqlitePath = string.IsNullOrWhiteSpace(connectionString) ? Path.Combine(dataFolder, "kizshop.db") : (Path.IsPathRooted(connectionString) ? connectionString : Path.Combine(dataFolder, connectionString));
                }
                else
                {
                    sqlitePath = Path.Combine(dataFolder, "kizshop.db");
                }
                return new SqliteStorage(sqlitePath);
            }

            // Default to LiteDB (pure managed, zero native binary dependencies)
            string liteDbPath;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                liteDbPath = Path.Combine(dataFolder, "kizshop.litedb");
            }
            else if (connectionString.EndsWith(".litedb", StringComparison.OrdinalIgnoreCase) || connectionString.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                liteDbPath = Path.IsPathRooted(connectionString) ? connectionString : Path.Combine(dataFolder, connectionString);
            }
            else
            {
                liteDbPath = Path.Combine(dataFolder, "kizshop.litedb");
            }

            return new LiteDbStorage(liteDbPath);
        }
    }
}
