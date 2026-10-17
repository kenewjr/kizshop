using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Storage;

namespace KizShop.Discord
{
    public class DiscordBotService : IDisposable
    {
        private readonly EconomyService _economyService;
        private readonly IKizStorage _storage;
        private readonly Func<KizShopConfiguration> _configProvider;
        private readonly HttpClient _httpClient;

        private CancellationTokenSource _cts;
        private Task _botLoopTask;
        private bool _disposed;

        public DiscordBotService(
            EconomyService economyService,
            IKizStorage storage,
            Func<KizShopConfiguration> configProvider,
            HttpClient httpClient = null)
        {
            _economyService = economyService ?? throw new ArgumentNullException(nameof(economyService));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public void Start()
        {
            var cfg = _configProvider();
            if (!cfg.DiscordBotEnabled || string.IsNullOrWhiteSpace(cfg.DiscordBotToken))
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _botLoopTask = Task.Run(() => RunGatewayLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
            }
            catch (Exception ex)
            {
                // ponytail: best-effort cancellation token cancel
                KizLog.Warning($"[KizShop] DiscordBotService cancellation error: {ex.Message}");
            }
        }

        private async Task RunGatewayLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var cfg = _configProvider();
                string token = cfg.DiscordBotToken?.Trim();
                if (string.IsNullOrEmpty(token)) break;

                using var ws = new ClientWebSocket();
                try
                {
                    var uri = new Uri("wss://gateway.discord.gg/?v=10&encoding=json");
                    await ws.ConnectAsync(uri, ct).ConfigureAwait(false);

                    KizLog.Info("[KizShop] Discord Bot connected to Gateway.");

                    // Main connection session
                    await HandleConnectionSessionAsync(ws, token, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    KizLog.Warning($"[KizShop] Discord Bot gateway error: {ex.Message}. Reconnecting in 10s...");
                    try { await Task.Delay(10000, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }

        private async Task HandleConnectionSessionAsync(ClientWebSocket ws, string token, CancellationToken ct)
        {
            int? heartbeatInterval = null;
            CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task heartbeatTask = null;

            var buffer = new byte[8192];
            using var ms = new MemoryStream();

            try
            {
                while (ws.State == WebSocketState.Open && !sessionCts.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    ms.SetLength(0);
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), sessionCts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    ms.Seek(0, SeekOrigin.Begin);
                    using var reader = new StreamReader(ms, Encoding.UTF8);
                    string json = await reader.ReadToEndAsync().ConfigureAwait(false);

                    var msg = JObject.Parse(json);
                    int op = msg.Value<int>("op");

                    if (op == 10) // Hello
                    {
                        heartbeatInterval = msg["d"]?.Value<int>("heartbeat_interval");
                        if (heartbeatInterval.HasValue)
                        {
                            heartbeatTask = Task.Run(() => HeartbeatLoopAsync(ws, heartbeatInterval.Value, sessionCts.Token));
                        }

                        // Send Identify
                        var identify = new
                        {
                            op = 2,
                            d = new
                            {
                                token = token,
                                intents = 33280, // GUILDS (1) | GUILD_MESSAGES (512) | MESSAGE_CONTENT (32768)
                                properties = new
                                {
                                    os = "windows",
                                    browser = "KizShop",
                                    device = "KizShop"
                                }
                            }
                        };
                        string identifyJson = JsonConvert.SerializeObject(identify);
                        byte[] identifyBytes = Encoding.UTF8.GetBytes(identifyJson);
                        await ws.SendAsync(new ArraySegment<byte>(identifyBytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                    }
                    else if (op == 0) // Dispatch
                    {
                        string eventType = msg.Value<string>("t");
                        if (string.Equals(eventType, "MESSAGE_CREATE", StringComparison.OrdinalIgnoreCase))
                        {
                            var data = msg["d"];
                            if (data != null)
                            {
                                await ProcessMessageCreateAsync(data, token).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            finally
            {
                sessionCts.Cancel();
                if (heartbeatTask != null)
                {
                    try { await heartbeatTask.ConfigureAwait(false); }
                    catch (Exception hbEx)
                    {
                        // ponytail: best-effort heartbeat task drain during session teardown
                        KizLog.Warning($"[KizShop] DiscordBotService heartbeat task completed with: {hbEx.Message}");
                    }
                }
            }
        }

        private async Task HeartbeatLoopAsync(ClientWebSocket ws, int intervalMs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                    if (ws.State != WebSocketState.Open) break;

                    string payload = JsonConvert.SerializeObject(new { op = 1, d = (object)null });
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }

        public async Task ProcessMessageCreateAsync(JToken data, string token)
        {
            bool isBot = data["author"]?["bot"]?.Value<bool>() ?? false;
            if (isBot) return;

            string content = data.Value<string>("content");
            if (string.IsNullOrWhiteSpace(content)) return;

            var cfg = _configProvider();
            string prefix = cfg.DiscordBotPrefix ?? "!ks ";

            if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string channelId = data.Value<string>("channel_id");
            string authorId = data["author"]?.Value<string>("id");
            string commandText = content.Substring(prefix.Length).Trim();

            string reply = await ExecuteBotCommandAsync(commandText, authorId).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(reply) && !string.IsNullOrEmpty(channelId))
            {
                await SendChannelMessageAsync(channelId, reply, token).ConfigureAwait(false);
            }
        }

        public async Task<string> ExecuteBotCommandAsync(string commandText, string authorId)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return "❓ Usage: `!ks bal <steamid>`, `!ks baltop`, `!ks eco <give|take|set> <steamid> <amount>`, `!ks help`";
            }

            var parts = commandText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts[0].ToLowerInvariant();
            var cfg = _configProvider();

            if (sub == "help")
            {
                return "📖 **KizShop Discord Bot Commands**:\n" +
                       "• `!ks bal <steamid>` — Check a player's balance\n" +
                       "• `!ks baltop` — Show top wealthiest players\n" +
                       "• `!ks eco <give|take|set> <steamid> <amount>` — Admin balance adjustment\n" +
                       "• `!ks help` — Show this help message";
            }

            if (sub == "bal" || sub == "balance")
            {
                if (parts.Length < 2 || !ulong.TryParse(parts[1], out ulong steamId))
                {
                    return "❌ Usage: `!ks bal <SteamID64>`";
                }

                var bal = await _economyService.GetBalanceAsync(steamId).ConfigureAwait(false);
                return $"💰 **Balance for `{steamId}`**: {cfg.CurrencySymbol}{bal.ToDecimal():N2} {cfg.CurrencyName}";
            }

            if (sub == "baltop")
            {
                var top = await _storage.GetTopBalancesAsync(cfg.BaltopCount).ConfigureAwait(false);
                if (top.Count == 0)
                {
                    return "🏆 No accounts recorded yet.";
                }

                var sb = new StringBuilder();
                sb.AppendLine("🏆 **Top Balances**:");
                for (int i = 0; i < top.Count; i++)
                {
                    var entry = top[i];
                    sb.AppendLine($"`#{i + 1}` **{entry.SteamId}** — {cfg.CurrencySymbol}{entry.Balance.ToDecimal():N2} {cfg.CurrencyName}");
                }
                return sb.ToString().TrimEnd();
            }

            if (sub == "eco")
            {
                // Verify admin
                var adminList = (cfg.DiscordBotAdminUserIds ?? "")
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (!adminList.Contains(authorId))
                {
                    return "🚫 You do not have permission to execute economy admin commands.";
                }

                if (parts.Length < 4)
                {
                    return "❌ Usage: `!ks eco <give|take|set> <SteamID64> <amount>`";
                }

                string action = parts[1].ToLowerInvariant();
                if (!ulong.TryParse(parts[2], out ulong steamId))
                {
                    return "❌ Invalid SteamID64.";
                }

                if (!decimal.TryParse(parts[3], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal amountDecimal))
                {
                    return "❌ Invalid number format for amount.";
                }

                if (!Money.TryFromDecimal(amountDecimal, cfg.MaxTransactionAmount, out var amountMoney, out string err))
                {
                    return $"❌ Invalid amount: {err}";
                }

                if (action == "give")
                {
                    var res = await _economyService.DepositAsync(steamId, amountMoney, "DiscordBot eco give").ConfigureAwait(false);
                    return $"✅ Gave {cfg.CurrencySymbol}{amountMoney.ToDecimal():N2} to `{steamId}`. New balance: **{cfg.CurrencySymbol}{res.NewBalance.ToDecimal():N2}**";
                }
                if (action == "take")
                {
                    var res = await _economyService.TryWithdrawAsync(steamId, amountMoney, "DiscordBot eco take").ConfigureAwait(false);
                    if (!res.Success)
                    {
                        return $"❌ Insufficient funds. Current balance: **{cfg.CurrencySymbol}{res.NewBalance.ToDecimal():N2}**";
                    }
                    return $"✅ Deducted {cfg.CurrencySymbol}{amountMoney.ToDecimal():N2} from `{steamId}`. New balance: **{cfg.CurrencySymbol}{res.NewBalance.ToDecimal():N2}**";
                }
                if (action == "set")
                {
                    await _economyService.SetBalanceAsync(steamId, amountMoney, "DiscordBot eco set").ConfigureAwait(false);
                    return $"✅ Set balance for `{steamId}` to **{cfg.CurrencySymbol}{amountMoney.ToDecimal():N2}**";
                }

                return "❌ Unknown action. Valid actions: `give`, `take`, `set`.";
            }

            return "❓ Unknown command. Type `!ks help` for available commands.";
        }

        public async Task SendChannelMessageAsync(string channelId, string text, string token)
        {
            try
            {
                string url = $"https://discord.com/api/v10/channels/{channelId}/messages";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Authorization", $"Bot {token}");

                string json = JsonConvert.SerializeObject(new { content = text });
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                await _httpClient.SendAsync(req).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                KizLog.Warning($"[KizShop] Discord Bot reply failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            try { _httpClient?.Dispose(); }
            catch (Exception ex)
            {
                // ponytail: best-effort httpClient disposal
                KizLog.Warning($"[KizShop] DiscordBotService HttpClient dispose notice: {ex.Message}");
            }
        }
    }
}
