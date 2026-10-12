using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using MySqlConnector;
using KizShop.Common;
using KizShop.Storage;

namespace KizShop.Economy
{
    public class UconomyImportReport
    {
        public bool DryRun { get; set; }
        public bool Overwrite { get; set; }
        public int TotalRead { get; set; }
        public int ImportedCount { get; set; }
        public int SkippedExisting { get; set; }
        public int SkippedInvalidSteamId { get; set; }
        public int SkippedInvalidBalance { get; set; }
        public int SkippedExceedsMax { get; set; }
        public int ErrorCount { get; set; }
        public List<string> ErrorDetails { get; set; } = new List<string>();

        public string SummaryText =>
            $"[Uconomy Import] Mode: {(DryRun ? "DRY-RUN" : "APPLIED")}, Overwrite: {Overwrite}. " +
            $"Read: {TotalRead}, Imported: {ImportedCount}, " +
            $"Skipped Existing: {SkippedExisting}, " +
            $"Invalid SteamID: {SkippedInvalidSteamId}, " +
            $"Invalid Balance: {SkippedInvalidBalance}, " +
            $"Exceeds Max: {SkippedExceedsMax}, Errors: {ErrorCount}.";
    }

    public class UconomyImportService
    {
        private readonly IKizStorage _storage;
        private readonly Func<KizShopConfiguration> _configProvider;
        private readonly string _pluginFolder;

        public UconomyImportService(IKizStorage storage, Func<KizShopConfiguration> configProvider, string pluginFolder)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _pluginFolder = pluginFolder ?? throw new ArgumentNullException(nameof(pluginFolder));
        }

        public async Task<UconomyImportReport> ImportFromCsvAsync(bool overwrite, bool apply)
        {
            var report = new UconomyImportReport
            {
                DryRun = !apply,
                Overwrite = overwrite
            };

            string csvPath = Path.Combine(_pluginFolder, "uconomy_import.csv");
            if (!File.Exists(csvPath))
            {
                report.ErrorCount++;
                report.ErrorDetails.Add($"File not found: {csvPath}");
                return report;
            }

            var lines = await Task.Run(() => File.ReadAllLines(csvPath)).ConfigureAwait(false);
            var rows = new List<(string SteamIdStr, string BalanceStr)>();

            foreach (var rawLine in lines)
            {
                string line = rawLine?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                var parts = line.Split(new[] { ',', ';' }, 2);
                if (parts.Length < 2) continue;

                // Skip header line if present
                if (parts[0].Equals("steamid", StringComparison.OrdinalIgnoreCase) ||
                    parts[0].Equals("steamid64", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rows.Add((parts[0].Trim(), parts[1].Trim()));
            }

            report.TotalRead = rows.Count;
            await ProcessRowsAsync(rows, overwrite, apply, report).ConfigureAwait(false);
            return report;
        }

        public async Task<UconomyImportReport> ImportFromMySqlAsync(bool overwrite, bool apply)
        {
            var report = new UconomyImportReport
            {
                DryRun = !apply,
                Overwrite = overwrite
            };

            var cfg = _configProvider();
            string connStr = cfg.UconomyImportConnectionString;
            if (string.IsNullOrWhiteSpace(connStr))
            {
                report.ErrorCount++;
                report.ErrorDetails.Add("UconomyImportConnectionString is not configured.");
                return report;
            }

            string table = string.IsNullOrWhiteSpace(cfg.UconomyImportTable) ? "uconomy" : cfg.UconomyImportTable.Trim();
            string steamCol = string.IsNullOrWhiteSpace(cfg.UconomyImportSteamIdColumn) ? "steamId" : cfg.UconomyImportSteamIdColumn.Trim();
            string balCol = string.IsNullOrWhiteSpace(cfg.UconomyImportBalanceColumn) ? "balance" : cfg.UconomyImportBalanceColumn.Trim();

            var rows = new List<(string SteamIdStr, string BalanceStr)>();

            try
            {
                await Task.Run(async () =>
                {
                    using var conn = new MySqlConnection(connStr);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    // Sanitized column and table identifiers
                    cmd.CommandText = $"SELECT `{steamCol}`, `{balCol}` FROM `{table}`;";

                    using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var sId = reader.IsDBNull(0) ? string.Empty : reader.GetValue(0)?.ToString();
                        var bal = reader.IsDBNull(1) ? string.Empty : reader.GetValue(1)?.ToString();
                        rows.Add((sId, bal));
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                report.ErrorCount++;
                report.ErrorDetails.Add($"MySQL connection/query error: {ex.Message}");
                return report;
            }

            report.TotalRead = rows.Count;
            await ProcessRowsAsync(rows, overwrite, apply, report).ConfigureAwait(false);
            return report;
        }

        private async Task ProcessRowsAsync(
            List<(string SteamIdStr, string BalanceStr)> rows,
            bool overwrite,
            bool apply,
            UconomyImportReport report)
        {
            var cfg = _configProvider();
            decimal maxTx = cfg.MaxTransactionAmount;

            foreach (var (rawSteamId, rawBalance) in rows)
            {
                // Validate 17-digit SteamID64
                if (string.IsNullOrEmpty(rawSteamId) || rawSteamId.Length != 17 || !ulong.TryParse(rawSteamId, out ulong steamId))
                {
                    report.SkippedInvalidSteamId++;
                    continue;
                }

                // Validate Balance
                if (!decimal.TryParse(rawBalance, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal balance) || balance < 0m)
                {
                    report.SkippedInvalidBalance++;
                    continue;
                }

                if (balance > maxTx)
                {
                    report.SkippedExceedsMax++;
                    continue;
                }

                if (!Money.TryFromDecimalAllowZero(balance, out var money, out _))
                {
                    report.SkippedInvalidBalance++;
                    continue;
                }

                bool exists = await _storage.AccountExistsAsync(steamId).ConfigureAwait(false);
                if (exists && !overwrite)
                {
                    report.SkippedExisting++;
                    continue;
                }

                if (apply)
                {
                    try
                    {
                        await _storage.SetBalanceAsync(steamId, money, "UconomyImport").ConfigureAwait(false);
                        report.ImportedCount++;
                    }
                    catch (Exception ex)
                    {
                        report.ErrorCount++;
                        report.ErrorDetails.Add($"Error setting balance for {steamId}: {ex.Message}");
                    }
                }
                else
                {
                    // Dry run
                    report.ImportedCount++;
                }
            }
        }
    }
}
