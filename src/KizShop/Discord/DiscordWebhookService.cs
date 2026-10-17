using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using KizShop.Common;

namespace KizShop.Discord
{
    public class DiscordWebhookService : IDisposable
    {
        private readonly Func<KizShopConfiguration> _configProvider;
        private readonly HttpClient _httpClient;
        private DateTime _lastErrorLog = DateTime.MinValue;

        public DiscordWebhookService(Func<KizShopConfiguration> configProvider, HttpClient httpClient = null)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        public async Task SendPurchaseAlertAsync(ulong steamId, string itemName, int quantity, Money price, Money newBalance)
        {
            var cfg = _configProvider();
            if (!cfg.DiscordWebhookEnabled || string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl)) return;
            if (!cfg.DiscordLogPurchases) return;

            var embed = new
            {
                title = "🛒 Shop Purchase",
                color = 0x2ECC71, // Green
                description = $"Player `{steamId}` purchased **{quantity}x {itemName}**",
                fields = new[]
                {
                    new { name = "Cost", value = $"{cfg.CurrencySymbol}{price.ToDecimal():N2}", inline = true },
                    new { name = "Remaining Balance", value = $"{cfg.CurrencySymbol}{newBalance.ToDecimal():N2}", inline = true }
                },
                timestamp = DateTime.UtcNow.ToString("o"),
                footer = new { text = "KizShop Economy" }
            };

            await PostPayloadAsync(new { embeds = new[] { embed } }).ConfigureAwait(false);
        }

        public async Task SendSaleAlertAsync(ulong steamId, string itemName, int quantity, Money payout, Money newBalance)
        {
            var cfg = _configProvider();
            if (!cfg.DiscordWebhookEnabled || string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl)) return;
            if (!cfg.DiscordLogPurchases) return;

            var embed = new
            {
                title = "💰 Shop Sale",
                color = 0x3498DB, // Blue
                description = $"Player `{steamId}` sold **{quantity}x {itemName}**",
                fields = new[]
                {
                    new { name = "Payout", value = $"{cfg.CurrencySymbol}{payout.ToDecimal():N2}", inline = true },
                    new { name = "New Balance", value = $"{cfg.CurrencySymbol}{newBalance.ToDecimal():N2}", inline = true }
                },
                timestamp = DateTime.UtcNow.ToString("o"),
                footer = new { text = "KizShop Economy" }
            };

            await PostPayloadAsync(new { embeds = new[] { embed } }).ConfigureAwait(false);
        }

        public async Task SendAdminEcoAlertAsync(string adminName, string action, ulong targetSteamId, Money amount, Money newBalance)
        {
            var cfg = _configProvider();
            if (!cfg.DiscordWebhookEnabled || string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl)) return;
            if (!cfg.DiscordLogAdminEco) return;

            var embed = new
            {
                title = "⚖️ Admin Economy Action",
                color = 0xF1C40F, // Yellow
                description = $"Admin **{adminName}** ran `/eco {action}` on `{targetSteamId}`",
                fields = new[]
                {
                    new { name = "Amount", value = $"{cfg.CurrencySymbol}{amount.ToDecimal():N2}", inline = true },
                    new { name = "New Balance", value = $"{cfg.CurrencySymbol}{newBalance.ToDecimal():N2}", inline = true }
                },
                timestamp = DateTime.UtcNow.ToString("o"),
                footer = new { text = "KizShop Audit" }
            };

            await PostPayloadAsync(new { embeds = new[] { embed } }).ConfigureAwait(false);
        }

        public async Task SendGiveawayAlertAsync(string adminName, ulong adminSteamId, Money amountPerPlayer, int recipientCount, Money totalAmount)
        {
            var cfg = _configProvider();
            if (!cfg.DiscordWebhookEnabled || string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl)) return;
            if (!cfg.DiscordLogAdminEco) return;

            var embed = new
            {
                title = "🎁 Giveaway Distribution",
                color = 0x9B59B6, // Purple
                description = $"Admin **{adminName}** (`{adminSteamId}`) distributed giveaway to **{recipientCount}** online players",
                fields = new[]
                {
                    new { name = "Amount Per Player", value = $"{cfg.CurrencySymbol}{amountPerPlayer.ToDecimal():N2}", inline = true },
                    new { name = "Recipients", value = recipientCount.ToString(), inline = true },
                    new { name = "Total Distributed", value = $"{cfg.CurrencySymbol}{totalAmount.ToDecimal():N2}", inline = true }
                },
                timestamp = DateTime.UtcNow.ToString("o"),
                footer = new { text = "KizShop Audit" }
            };

            await PostPayloadAsync(new { embeds = new[] { embed } }).ConfigureAwait(false);
        }

        public async Task SendLargeTransactionAlertAsync(ulong steamId, string type, Money amount, string details)
        {
            var cfg = _configProvider();
            if (!cfg.DiscordWebhookEnabled || string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl)) return;

            var embed = new
            {
                title = "🚨 Large Transaction Alert",
                color = 0xE67E22, // Orange
                description = $"Large transaction threshold reached ({type}) by `{steamId}`",
                fields = new[]
                {
                    new { name = "Amount", value = $"{cfg.CurrencySymbol}{amount.ToDecimal():N2}", inline = true },
                    new { name = "Details", value = details ?? "N/A", inline = true }
                },
                timestamp = DateTime.UtcNow.ToString("o"),
                footer = new { text = "KizShop Security" }
            };

            await PostPayloadAsync(new { embeds = new[] { embed } }).ConfigureAwait(false);
        }

        public async Task SendErrorAlertAsync(string errorMessage)
        {
            var cfg = _configProvider();
            if (!cfg.DiscordWebhookEnabled || string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl)) return;

            var embed = new
            {
                title = "⚠️ KizShop Error Alert",
                color = 0xE74C3C, // Red
                description = $"An error was logged:\n```{errorMessage}```",
                timestamp = DateTime.UtcNow.ToString("o"),
                footer = new { text = "KizShop Error Monitor" }
            };

            await PostPayloadAsync(new { embeds = new[] { embed } }).ConfigureAwait(false);
        }

        private async Task PostPayloadAsync(object payload)
        {
            var cfg = _configProvider();
            string url = cfg.DiscordWebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                string json = JsonConvert.SerializeObject(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var res = await _httpClient.PostAsync(url, content).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode && (DateTime.UtcNow - _lastErrorLog).TotalMinutes >= 5)
                {
                    _lastErrorLog = DateTime.UtcNow;
                    KizLog.Warning($"[KizShop] Discord Webhook returned status code {(int)res.StatusCode} {res.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _lastErrorLog).TotalMinutes >= 5)
                {
                    _lastErrorLog = DateTime.UtcNow;
                    KizLog.Warning($"[KizShop] Failed to send Discord Webhook: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            try { _httpClient?.Dispose(); }
            catch (Exception ex)
            {
                // ponytail: best-effort httpClient disposal
                KizLog.Warning($"[KizShop] DiscordWebhookService HttpClient dispose notice: {ex.Message}");
            }
        }
    }
}
