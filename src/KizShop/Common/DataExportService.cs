using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using KizShop.Storage;

namespace KizShop.Common
{
    public class DataExportService
    {
        private readonly IKizStorage _storage;

        public DataExportService(IKizStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public async Task<int> ExportAccountsToCsvAsync(string filePath)
        {
            var accounts = await _storage.GetAllAccountsAsync().ConfigureAwait(false);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("SteamID64,BalanceDecimal,BalanceCents");
            foreach (var (steamId, balance) in accounts)
            {
                sb.AppendLine($"{steamId},{balance.ToDecimal():F2},{balance.Cents}");
            }

            await Task.Run(() => File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8)).ConfigureAwait(false);
            return accounts.Count;
        }

        public static string EscapeCsvText(string input)
        {
            if (input == null) input = string.Empty;
            input = input.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            if (input.Length > 0)
            {
                char first = input[0];
                if (first == '=' || first == '+' || first == '-' || first == '@' || first == '\t' || first == '\r')
                {
                    input = "'" + input;
                }
            }
            return "\"" + input.Replace("\"", "\"\"") + "\"";
        }

        public async Task<int> ExportTransactionsToCsvAsync(string filePath, int maxRows = 10000)
        {
            var txs = await _storage.GetAllTransactionsAsync(maxRows, 0).ConfigureAwait(false);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("TimestampUtc,SteamID64,Type,AmountDecimal,AmountCents,Details");
            foreach (var tx in txs)
            {
                string safeType = EscapeCsvText(tx.Type);
                string safeDetails = EscapeCsvText(tx.Details);
                sb.AppendLine($"{tx.Timestamp:yyyy-MM-dd HH:mm:ss},{tx.SteamId},{safeType},{tx.Amount.ToDecimal():F2},{tx.Amount.Cents},{safeDetails}");
            }

            await Task.Run(() => File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8)).ConfigureAwait(false);
            return txs.Count;
        }
    }
}
