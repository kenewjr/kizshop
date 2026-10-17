using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using Microsoft.Data.Sqlite;
using KizShop.Common;

namespace KizShop.Storage
{
    public class DatabaseBackupManager
    {
        private static readonly Regex BackupFileNameRegex =
            new Regex(@"^kizshop_(litedb|sqlite|mysql)_\d{8}_\d{6}\.(?!tmp$)[a-zA-Z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _backupsFolder;
        private readonly string _baseDirectory;
        private readonly IKizStorage _storage;
        private readonly Func<KizShopConfiguration> _configProvider;
        private readonly IClock _clock;
        private readonly object _lock = new object();

        private DateTime _lastBackupUtc = DateTime.MinValue;
        private string _lastBackupResult = "None";

        public DateTime LastBackupUtc => _lastBackupUtc;
        public string LastBackupResult => _lastBackupResult;
        public string BackupsFolder => _backupsFolder;

        public DatabaseBackupManager(
            string baseDirectory,
            IKizStorage storage,
            Func<KizShopConfiguration> configProvider,
            IClock clock = null)
        {
            _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
            _backupsFolder = Path.Combine(baseDirectory, "backups");
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _clock = clock ?? SystemClock.Instance;
        }

        public async Task<(bool Success, string Message, string FileName, long FileSize)> CreateBackupAsync(bool isManual = false)
        {
            return await Task.Run(async () =>
            {
                lock (_lock)
                {
                    if (!Directory.Exists(_backupsFolder))
                    {
                        Directory.CreateDirectory(_backupsFolder);
                    }
                }

                var cfg = _configProvider();
                string backendType = (cfg.DatabaseType ?? "LiteDB").ToLowerInvariant();
                var now = _clock.UtcNow;
                string timestamp = now.ToString("yyyyMMdd_HHmmss");

                // Unwrap InstrumentedKizStorage if present
                IKizStorage rawStorage = _storage;
                if (rawStorage is InstrumentedKizStorage inst)
                {
                    rawStorage = inst.InnerStorage;
                }

                string tmpPath = null;

                try
                {
                    if (backendType.Contains("mysql"))
                    {
                        KizLog.Info("[KizShop] MySQL database backups should be managed externally by database administrator. Skipping auto backup.");
                        _lastBackupUtc = now;
                        _lastBackupResult = "Skipped (MySQL external)";
                        return (true, "MySQL backups should be managed by DBA.", "N/A", 0L);
                    }

                    if (backendType.Contains("sqlite"))
                    {
                        string fileName = $"kizshop_sqlite_{timestamp}.sqlite";
                        tmpPath = Path.Combine(_backupsFolder, fileName + ".tmp");
                        string finalPath = Path.Combine(_backupsFolder, fileName);

                        if (File.Exists(tmpPath))
                        {
                            File.Delete(tmpPath);
                        }

                        if (rawStorage is SqliteStorage sqliteStorage)
                        {
                            await sqliteStorage.BackupAsync(tmpPath).ConfigureAwait(false);
                        }
                        else
                        {
                            throw new InvalidOperationException($"Storage is not SqliteStorage: {rawStorage?.GetType().Name}");
                        }

                        long size = File.Exists(tmpPath) ? new FileInfo(tmpPath).Length : 0L;
                        if (size <= 0)
                        {
                            throw new InvalidOperationException("SQLite backup validation failed: file size is 0 bytes.");
                        }

                        ValidateSqliteBackup(tmpPath);

                        if (File.Exists(finalPath))
                        {
                            File.Delete(finalPath);
                        }
                        File.Move(tmpPath, finalPath);
                        tmpPath = null;

                        PruneOldBackups(cfg.BackupKeepCount);
                        _lastBackupUtc = now;
                        _lastBackupResult = $"Success ({fileName})";
                        KizLog.Info($"[KizShop] SQLite database backup created successfully: {fileName} ({size} bytes).");
                        return (true, "SQLite backup created successfully.", fileName, size);
                    }

                    // Default: LiteDB
                    {
                        string fileName = $"kizshop_litedb_{timestamp}.db";
                        tmpPath = Path.Combine(_backupsFolder, fileName + ".tmp");
                        string finalPath = Path.Combine(_backupsFolder, fileName);

                        if (File.Exists(tmpPath))
                        {
                            File.Delete(tmpPath);
                        }

                        if (rawStorage is LiteDbStorage liteDb)
                        {
                            liteDb.CopyDatabaseTo(tmpPath);
                        }
                        else
                        {
                            throw new InvalidOperationException($"Storage is not LiteDbStorage: {rawStorage?.GetType().Name}");
                        }

                        long size = File.Exists(tmpPath) ? new FileInfo(tmpPath).Length : 0L;
                        if (size <= 0)
                        {
                            throw new InvalidOperationException("LiteDB backup validation failed: file size is 0 bytes.");
                        }

                        ValidateLiteDbBackup(tmpPath);

                        if (File.Exists(finalPath))
                        {
                            File.Delete(finalPath);
                        }
                        File.Move(tmpPath, finalPath);
                        tmpPath = null;

                        PruneOldBackups(cfg.BackupKeepCount);
                        _lastBackupUtc = now;
                        _lastBackupResult = $"Success ({fileName})";
                        KizLog.Info($"[KizShop] LiteDB database backup created successfully: {fileName} ({size} bytes).");
                        return (true, "LiteDB backup created successfully.", fileName, size);
                    }
                }
                catch (Exception ex)
                {
                    if (!string.IsNullOrEmpty(tmpPath))
                    {
                        try
                        {
                            if (File.Exists(tmpPath))
                            {
                                File.Delete(tmpPath);
                            }
                        }
                        catch (Exception delEx)
                        {
                            KizLog.Warning($"[KizShop] Could not delete temporary backup file {tmpPath}: {delEx.Message}");
                        }
                    }

                    _lastBackupResult = $"Failed: {ex.Message}";
                    KizLog.Error($"[KizShop] Database backup failed: {ex}");
                    return (false, $"Backup failed: {ex.Message}", null, 0L);
                }
            }).ConfigureAwait(false);
        }

        private static void ValidateSqliteBackup(string path)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check;";
                var res = cmd.ExecuteScalar()?.ToString();
                if (!string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"SQLite backup quick_check failed: {res}");
                }
            }
            SqliteConnection.ClearAllPools();
        }

        private static void ValidateLiteDbBackup(string path)
        {
            using var vDb = new LiteDatabase(new ConnectionString { Filename = path, ReadOnly = true });
            _ = vDb.GetCollectionNames().Count();
        }

        public int CleanupLeftoverTempFiles()
        {
            if (!Directory.Exists(_backupsFolder)) return 0;

            try
            {
                var dir = new DirectoryInfo(_backupsFolder);
                var tmpFiles = dir.GetFiles("*.tmp");
                int cleaned = 0;
                foreach (var file in tmpFiles)
                {
                    try
                    {
                        file.Delete();
                        cleaned++;
                    }
                    catch (Exception ex)
                    {
                        KizLog.Warning($"[KizShop] Could not delete leftover backup temp file {file.Name}: {ex.Message}");
                    }
                }
                return cleaned;
            }
            catch (Exception ex)
            {
                KizLog.Warning($"[KizShop] Failed while cleaning up leftover backup temp files: {ex.Message}");
                return 0;
            }
        }

        public int PruneOldBackups(int keepCount)
        {
            if (keepCount < 1) keepCount = 10;
            if (!Directory.Exists(_backupsFolder)) return 0;

            try
            {
                var dir = new DirectoryInfo(_backupsFolder);
                var matchingFiles = dir.GetFiles()
                    .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && BackupFileNameRegex.IsMatch(f.Name))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ThenByDescending(f => f.Name)
                    .ToList();

                if (matchingFiles.Count <= keepCount) return 0;

                int pruned = 0;
                var toDelete = matchingFiles.Skip(keepCount);
                foreach (var file in toDelete)
                {
                    try
                    {
                        file.Delete();
                        pruned++;
                    }
                    catch (Exception ex)
                    {
                        KizLog.Warning($"[KizShop] Could not delete old backup {file.Name}: {ex.Message}");
                    }
                }

                return pruned;
            }
            catch (Exception ex)
            {
                KizLog.Warning($"[KizShop] Failed while pruning old database backups: {ex.Message}");
                return 0;
            }
        }

        public bool ShouldBackupOnUnload(TimeSpan threshold)
        {
            if (_lastBackupUtc == DateTime.MinValue) return true;
            return (_clock.UtcNow - _lastBackupUtc) >= threshold;
        }

        public static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}
