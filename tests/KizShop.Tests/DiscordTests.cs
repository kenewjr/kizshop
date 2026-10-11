using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Newtonsoft.Json.Linq;
using KizShop.Common;
using KizShop.Discord;
using KizShop.Economy;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class DiscordTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LiteDbStorage _storage;
        private readonly EconomyService _economy;
        private readonly KizShopConfiguration _config;

        public DiscordTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"kizshop_disctest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            string dbPath = Path.Combine(_tempDir, "test.litedb");
            _storage = new LiteDbStorage(dbPath);
            _storage.InitializeAsync().GetAwaiter().GetResult();

            _economy = new EconomyService(_storage, () => Money.FromDecimal(100m), () => null, true, new TestMainThreadDispatcher());
            _config = new KizShopConfiguration
            {
                CurrencyName = "Credits",
                CurrencySymbol = "$",
                MaxTransactionAmount = 1000000m,
                DiscordWebhookEnabled = true,
                DiscordWebhookUrl = "https://discord.com/api/webhooks/test/mock",
                DiscordLogPurchases = true,
                DiscordLogAdminEco = true,
                DiscordLargeTransactionThreshold = 500m,
                DiscordBotEnabled = true,
                DiscordBotAdminUserIds = "123456789,987654321"
            };
        }

        public void Dispose()
        {
            _storage?.Dispose();
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public async Task Bot_HelpCommand_ReturnsHelpMessage()
        {
            using var bot = new DiscordBotService(_economy, _storage, () => _config);
            string reply = await bot.ExecuteBotCommandAsync("help", "any_user");

            Assert.Contains("!ks bal", reply);
            Assert.Contains("!ks baltop", reply);
            Assert.Contains("!ks eco", reply);
        }

        [Fact]
        public async Task Bot_BalanceCommand_ReturnsPlayerBalance()
        {
            ulong steamId = 76561198000000010;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(250.50m), "TestInit");

            using var bot = new DiscordBotService(_economy, _storage, () => _config);
            string reply = await bot.ExecuteBotCommandAsync($"bal {steamId}", "any_user");

            Assert.Contains("250.50", reply);
            Assert.Contains(steamId.ToString(), reply);
        }

        [Fact]
        public async Task Bot_BaltopCommand_ReturnsTopList()
        {
            ulong s1 = 76561198000000011;
            ulong s2 = 76561198000000012;
            await _economy.SetBalanceAsync(s1, Money.FromDecimal(1000m), "Test");
            await _economy.SetBalanceAsync(s2, Money.FromDecimal(2000m), "Test");

            using var bot = new DiscordBotService(_economy, _storage, () => _config);
            string reply = await bot.ExecuteBotCommandAsync("baltop", "any_user");

            Assert.Contains("#1", reply);
            Assert.Contains(s2.ToString(), reply);
        }

        [Fact]
        public async Task Bot_EcoCommand_UnauthorizedUser_Rejected()
        {
            using var bot = new DiscordBotService(_economy, _storage, () => _config);
            string reply = await bot.ExecuteBotCommandAsync("eco give 76561198000000010 50", "unauthorized_user");

            Assert.Contains("permission", reply, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Bot_EcoCommand_AuthorizedAdmin_GivesTakesAndSetsBalance()
        {
            ulong target = 76561198000000013;
            string adminId = "123456789";

            using var bot = new DiscordBotService(_economy, _storage, () => _config);

            // Give
            string giveReply = await bot.ExecuteBotCommandAsync($"eco give {target} 100", adminId);
            Assert.Contains("Gave", giveReply);
            var bal = await _economy.GetBalanceAsync(target);
            Assert.Equal(200m, bal.ToDecimal()); // 100 starting + 100 given

            // Take
            string takeReply = await bot.ExecuteBotCommandAsync($"eco take {target} 50", adminId);
            Assert.Contains("Deducted", takeReply);
            bal = await _economy.GetBalanceAsync(target);
            Assert.Equal(150m, bal.ToDecimal());

            // Set
            string setReply = await bot.ExecuteBotCommandAsync($"eco set {target} 300", adminId);
            Assert.Contains("Set balance", setReply);
            bal = await _economy.GetBalanceAsync(target);
            Assert.Equal(300m, bal.ToDecimal());
        }

        [Fact]
        public async Task Webhook_SendPurchaseAlert_SendsExpectedEmbedPayload()
        {
            var capturedRequests = new List<string>();
            var handler = new MockHttpMessageHandler(async req =>
            {
                capturedRequests.Add(await req.Content.ReadAsStringAsync());
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });

            var httpClient = new HttpClient(handler);
            using var webhook = new DiscordWebhookService(() => _config, httpClient);

            await webhook.SendPurchaseAlertAsync(
                76561198000000014,
                "Timberwolf",
                1,
                Money.FromDecimal(500m),
                Money.FromDecimal(250m));

            Assert.Single(capturedRequests);
            var json = JObject.Parse(capturedRequests[0]);
            var embeds = json["embeds"] as JArray;
            Assert.NotNull(embeds);
            Assert.Single(embeds);
            Assert.Contains("Purchase", embeds[0].Value<string>("title"));
            Assert.Contains("Timberwolf", embeds[0].Value<string>("description"));
        }

        [Fact]
        public async Task Webhook_SendLargeTransactionAlert_SendsSecurityEmbed()
        {
            var capturedRequests = new List<string>();
            var handler = new MockHttpMessageHandler(async req =>
            {
                capturedRequests.Add(await req.Content.ReadAsStringAsync());
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });

            var httpClient = new HttpClient(handler);
            using var webhook = new DiscordWebhookService(() => _config, httpClient);

            await webhook.SendLargeTransactionAlertAsync(
                76561198000000015,
                "Purchase",
                Money.FromDecimal(9999m),
                "Bulk 20x Items");

            Assert.Single(capturedRequests);
            var json = JObject.Parse(capturedRequests[0]);
            var embeds = json["embeds"] as JArray;
            Assert.NotNull(embeds);
            Assert.Contains("Large Transaction Alert", embeds[0].Value<string>("title"));
        }
    }

    public class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
