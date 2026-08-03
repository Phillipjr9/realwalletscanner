using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// SendGrid email service for wallet found alerts
    /// Simple, reliable, no SMTP configuration needed
    /// </summary>
    public class EmailAlertService
    {
        private const int MAX_RETRIES = 3;
        private const int RETRY_DELAY_MS = 2000;
        private const string SENDGRID_API_URL = "https://api.sendgrid.com/v3/mail/send";
        
        // Static HttpClient for better performance (connection pooling)
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// Send wallet found alert via SendGrid
        /// </summary>
        public static async Task SendWalletFoundAlertAsync(CryptoWalletGenerator.WalletInfo wallet, AppSettings settings)
        {
            // Validate settings
            if (!settings.EnableEmailAlerts)
                throw new InvalidOperationException("Email alerts are disabled. Enable in Settings.");

            if (string.IsNullOrEmpty(settings.AlertEmailAddress))
                throw new InvalidOperationException("No email address configured. Set it in Settings.");

            if (string.IsNullOrEmpty(settings.SendGridApiKey))
                throw new InvalidOperationException("SendGrid API key is required. Get it from https://sendgrid.com");

            var recipients = settings.AlertEmailAddress.Split(';', ',')
                .Select(e => e.Trim())
                .Where(e => !string.IsNullOrEmpty(e))
                .ToList();

            if (!recipients.Any())
                throw new InvalidOperationException("No valid email addresses found.");

            await SendViaSendGrid(wallet, settings, recipients);
        }

        /// <summary>
        /// Send email via SendGrid API
        /// </summary>
        private static async Task SendViaSendGrid(CryptoWalletGenerator.WalletInfo wallet, AppSettings settings, List<string> recipients)
        {
            var emailBody = BuildEmailBody(wallet);
            var totalUsd = GetTotalBalance(wallet);

            // Build SendGrid email object
            var sendGridEmail = new
            {
                personalizations = new[]
                {
                    new
                    {
                        to = recipients.Select(e => new { email = e }).ToArray(),
                        subject = $"🎉 Wallet Found with Balance! (${totalUsd:F2})"
                    }
                },
                from = new { email = settings.AlertEmailAddress, name = "Wallet Recovery Tool" },
                content = new[]
                {
                    new
                    {
                        type = "text/html",
                        value = emailBody
                    }
                }
            };

            var jsonContent = JsonConvert.SerializeObject(sendGridEmail);
            Exception lastException = null;

            // Retry logic
            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                try
                {
                    // Use request message to set auth per-request (thread-safe with static client)
                    using (var request = new HttpRequestMessage(HttpMethod.Post, SENDGRID_API_URL))
                    {
                        request.Headers.Authorization = 
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.SendGridApiKey);
                        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                        
                        DebugLogger.Log($"📧 SendGrid attempt {attempt}: Sending to {string.Join(", ", recipients)}...");
                        var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            DebugLogger.Log($"✅ Email sent successfully via SendGrid to {string.Join(", ", recipients)}");
                            return;
                        }

                        // Handle error response
                        var errorContent = await response.Content.ReadAsStringAsync();
                        DebugLogger.Log($"❌ SendGrid returned {response.StatusCode}: {errorContent}");
                        lastException = new InvalidOperationException(
                            $"SendGrid error {response.StatusCode}: {errorContent}");

                        if (attempt < MAX_RETRIES)
                        {
                            DebugLogger.Log($"⚠️ SendGrid attempt {attempt} failed. Retrying in {RETRY_DELAY_MS}ms...");
                            await Task.Delay(RETRY_DELAY_MS);
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    DebugLogger.Log($"⚠️ SendGrid attempt {attempt} failed: {ex.Message}");
                    
                    if (attempt < MAX_RETRIES)
                    {
                        await Task.Delay(RETRY_DELAY_MS);
                    }
                }
            }

            // All retries failed
            throw new InvalidOperationException(
                $"Failed to send email via SendGrid after {MAX_RETRIES} attempts. " +
                $"Last error: {lastException?.Message}. " +
                $"Please check: 1) API key is correct, 2) Internet connection, 3) SendGrid API status",
                lastException);
        }

        /// <summary>
        /// Calculate total USD value of wallet balances
        /// </summary>
        private static decimal GetTotalBalance(CryptoWalletGenerator.WalletInfo wallet)
        {
            decimal total = 0;
            
            if (wallet.Balances.ContainsKey("BTC"))
                total += wallet.Balances["BTC"] * LivePriceTicker.GetPrice("BTC");
            if (wallet.Balances.ContainsKey("ETH"))
                total += wallet.Balances["ETH"] * LivePriceTicker.GetPrice("ETH");
            if (wallet.Balances.ContainsKey("SOL"))
                total += wallet.Balances["SOL"] * LivePriceTicker.GetPrice("SOL");
            if (wallet.Balances.ContainsKey("USDT"))
                total += wallet.Balances["USDT"]; // Stablecoin pegged to $1
            if (wallet.Balances.ContainsKey("LTC"))
                total += wallet.Balances["LTC"] * LivePriceTicker.GetPrice("LTC");
            if (wallet.Balances.ContainsKey("DOGE"))
                total += wallet.Balances["DOGE"] * LivePriceTicker.GetPrice("DOGE");
            if (wallet.Balances.ContainsKey("MATIC"))
                total += wallet.Balances["MATIC"] * LivePriceTicker.GetPrice("MATIC");
            if (wallet.Balances.ContainsKey("BNB"))
                total += wallet.Balances["BNB"] * LivePriceTicker.GetPrice("BNB");
            if (wallet.Balances.ContainsKey("XRP"))
                total += wallet.Balances["XRP"] * LivePriceTicker.GetPrice("XRP");
            if (wallet.Balances.ContainsKey("ADA"))
                total += wallet.Balances["ADA"] * LivePriceTicker.GetPrice("ADA");
            
            return total;
        }

        /// <summary>
        /// Build professional HTML email body
        /// </summary>
        private static string BuildEmailBody(CryptoWalletGenerator.WalletInfo wallet)
        {
            var balanceInfo = "";
            decimal totalUsd = 0;
            
            if (wallet.Balances.ContainsKey("BTC") && wallet.Balances["BTC"] > 0)
            {
                balanceInfo += $"<li><strong>🪙 Bitcoin:</strong> {wallet.Balances["BTC"]:F8} BTC</li>";
                totalUsd += wallet.Balances["BTC"] * LivePriceTicker.GetPrice("BTC");
            }
            if (wallet.Balances.ContainsKey("ETH") && wallet.Balances["ETH"] > 0)
            {
                balanceInfo += $"<li><strong>Ξ Ethereum:</strong> {wallet.Balances["ETH"]:F8} ETH</li>";
                totalUsd += wallet.Balances["ETH"] * LivePriceTicker.GetPrice("ETH");
            }
            if (wallet.Balances.ContainsKey("SOL") && wallet.Balances["SOL"] > 0)
            {
                balanceInfo += $"<li><strong>◎ Solana:</strong> {wallet.Balances["SOL"]:F8} SOL</li>";
                totalUsd += wallet.Balances["SOL"] * LivePriceTicker.GetPrice("SOL");
            }
            if (wallet.Balances.ContainsKey("USDT") && wallet.Balances["USDT"] > 0)
            {
                balanceInfo += $"<li><strong>💵 USDT:</strong> {wallet.Balances["USDT"]:F2} USDT</li>";
                totalUsd += wallet.Balances["USDT"];
            }
            if (wallet.Balances.ContainsKey("LTC") && wallet.Balances["LTC"] > 0)
            {
                balanceInfo += $"<li><strong>Ł Litecoin:</strong> {wallet.Balances["LTC"]:F8} LTC</li>";
                totalUsd += wallet.Balances["LTC"] * LivePriceTicker.GetPrice("LTC");
            }
            if (wallet.Balances.ContainsKey("DOGE") && wallet.Balances["DOGE"] > 0)
            {
                balanceInfo += $"<li><strong>Ð Dogecoin:</strong> {wallet.Balances["DOGE"]:F8} DOGE</li>";
                totalUsd += wallet.Balances["DOGE"] * LivePriceTicker.GetPrice("DOGE");
            }
            if (wallet.Balances.ContainsKey("MATIC") && wallet.Balances["MATIC"] > 0)
            {
                balanceInfo += $"<li><strong>⬡ Polygon:</strong> {wallet.Balances["MATIC"]:F8} MATIC</li>";
                totalUsd += wallet.Balances["MATIC"] * LivePriceTicker.GetPrice("MATIC");
            }
            if (wallet.Balances.ContainsKey("BNB") && wallet.Balances["BNB"] > 0)
            {
                balanceInfo += $"<li><strong>🔶 BNB:</strong> {wallet.Balances["BNB"]:F8} BNB</li>";
                totalUsd += wallet.Balances["BNB"] * LivePriceTicker.GetPrice("BNB");
            }
            if (wallet.Balances.ContainsKey("XRP") && wallet.Balances["XRP"] > 0)
            {
                balanceInfo += $"<li><strong>✕ Ripple:</strong> {wallet.Balances["XRP"]:F6} XRP</li>";
                totalUsd += wallet.Balances["XRP"] * LivePriceTicker.GetPrice("XRP");
            }
            if (wallet.Balances.ContainsKey("ADA") && wallet.Balances["ADA"] > 0)
            {
                balanceInfo += $"<li><strong>₳ Cardano:</strong> {wallet.Balances["ADA"]:F6} ADA</li>";
                totalUsd += wallet.Balances["ADA"] * LivePriceTicker.GetPrice("ADA");
            }
            
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss UTC");
            var version = UpdateChecker.GetCurrentVersion();
            
            return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{ 
            font-family: 'Segoe UI', Arial, sans-serif; 
            padding: 20px; 
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            line-height: 1.6;
        }}
        .container {{ 
            background-color: white; 
            padding: 40px; 
            border-radius: 12px; 
            max-width: 700px; 
            margin: 0 auto; 
            box-shadow: 0 10px 40px rgba(0,0,0,0.2);
        }}
        h1 {{ 
            color: #4CAF50; 
            margin-top: 0; 
            font-size: 28px;
            border-bottom: 3px solid #4CAF50;
            padding-bottom: 15px;
        }}
        h2 {{ color: #333; font-size: 18px; margin-top: 20px; }}
        h3 {{ color: #555; font-size: 16px; margin-top: 15px; }}
        .balance-card {{ 
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            padding: 20px;
            border-radius: 8px;
            margin: 20px 0;
            text-align: center;
            font-size: 18px;
            font-weight: bold;
        }}
        .wallet-info {{ 
            background-color: #f9f9f9; 
            padding: 20px; 
            border-radius: 8px; 
            margin: 20px 0;
            border-left: 4px solid #667eea;
        }}
        .mnemonic {{ 
            background-color: #fff3cd; 
            padding: 20px; 
            border-radius: 8px; 
            border-left: 4px solid #ffc107; 
            margin: 20px 0;
        }}
        .warning {{ 
            background-color: #ffe6e6; 
            padding: 20px; 
            border-radius: 8px; 
            border-left: 4px solid #f44336; 
            margin: 20px 0;
        }}
        .success {{
            background-color: #e6ffe6;
            padding: 20px;
            border-radius: 8px;
            border-left: 4px solid #4CAF50;
            margin: 20px 0;
        }}
        ul {{ 
            padding-left: 25px;
            margin: 10px 0;
        }}
        li {{ 
            margin: 8px 0;
            font-size: 15px;
        }}
        code {{ 
            background-color: #f5f5f5; 
            padding: 8px 12px;
            border-radius: 4px; 
            font-family: 'Courier New', monospace;
            font-size: 12px;
            word-break: break-all;
            display: block;
            margin: 10px 0;
        }}
        .footer {{
            color: #888;
            font-size: 12px;
            margin-top: 30px;
            padding-top: 20px;
            border-top: 1px solid #ddd;
        }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>🎉 Wallet Found with Balance!</h1>
        
        <div class='success'>
            <p><strong>A wallet with cryptocurrency balance has been discovered!</strong></p>
        </div>
        
        <div class='balance-card'>
            💰 Estimated Total Value: ${totalUsd:F2} USD
        </div>
        
        <div class='wallet-info'>
            <h2>💳 Wallet Balances</h2>
            <ul>
                {balanceInfo}
            </ul>
        </div>
        
        <div class='mnemonic'>
            <h3>🔑 Recovery Phrase</h3>
            <p><strong>Your 12-word recovery phrase for wallet access:</strong></p>
            <code>{wallet.Mnemonic}</code>
        </div>
        
        <div class='wallet-info'>
            <h2>📍 Wallet Addresses</h2>
            <p><strong>Bitcoin (Legacy - starts with 1):</strong></p>
            <code>{wallet.BitcoinAddress}</code>
            
            <p><strong>Bitcoin (SegWit - starts with 3):</strong></p>
            <code>{wallet.BitcoinAddressSegWit}</code>
            
            <p><strong>Bitcoin (Native SegWit - starts with bc1):</strong></p>
            <code>{wallet.BitcoinAddressNativeSegWit}</code>
            
            <p><strong>Ethereum / Polygon / BSC:</strong></p>
            <code>{wallet.EthereumAddress}</code>
            
            <p><strong>Solana:</strong></p>
            <code>{wallet.SolanaAddress}</code>
        </div>
        
        <div class='warning'>
            <h3>⚠️ URGENT SECURITY ACTIONS</h3>
            <ul>
                <li><strong>1. Secure this email</strong> - Delete it after saving the recovery phrase</li>
                <li><strong>2. Store recovery phrase securely</strong> - Use hardware wallet, encrypted storage, or offline</li>
                <li><strong>3. Transfer funds immediately</strong> - Move crypto to a wallet you control completely</li>
                <li><strong>4. Never share this email</strong> - Your recovery phrase is the master key</li>
                <li><strong>5. This wallet was found by scanning</strong> - Others may also have access. Act quickly!</li>
                <li><strong>6. Use a strong password</strong> - If creating a new wallet, use 20+ characters</li>
            </ul>
        </div>
        
        <div class='success'>
            <p><strong>Next Steps:</strong></p>
            <ol style='padding-left: 25px;'>
                <li>Import this recovery phrase into a trusted wallet (MetaMask, Trust Wallet, etc.)</li>
                <li>Verify the balances match</li>
                <li>Transfer all funds to your primary wallet</li>
                <li>Delete this recovery phrase from all devices</li>
            </ol>
        </div>
        
        <div class='footer'>
            <p>
                🤖 <strong>Wallet Recovery Tool</strong> v{version}<br/>
                Generated: {timestamp}<br/>
                <br/>
                ⚖️ <strong>Legal Notice:</strong> This tool is for personal wallet recovery only. 
                Using it to access wallets without authorization is illegal.
            </p>
        </div>
    </div>
</body>
</html>";
        }
    }
}
