using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Unified SMTP, SendGrid & Webhook Notification Service for wallet-found alerts.
    /// Supports RFC-5321/5322 SMTP (TLS/STARTTLS on ports 587/465/25), SendGrid v3 REST API, and JSON Webhooks.
    /// </summary>
    public class EmailAlertService
    {
        private const int MAX_RETRIES = 3;
        private const int RETRY_DELAY_MS = 2000;
        private const string SENDGRID_API_URL = "https://api.sendgrid.com/v3/mail/send";
        
        // Static HttpClient for better performance (connection pooling)
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly ConcurrentDictionary<string, DateTime> _sentAlertDedupe =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<CryptoWalletGenerator.WalletInfo> _pendingRetryQueue =
            new ConcurrentQueue<CryptoWalletGenerator.WalletInfo>();
        private static int _retryWorkerRunning = 0;

        private static string GetRetryQueueFilePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WalletRecoveryTool",
                "Logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "smtp_pending_retry_queue.jsonl");
        }

        /// <summary>
        /// Fire-and-forget helper called whenever a wallet with balance is discovered in any scanner mode.
        /// Deduplicates by address/mnemonic/key so the same wallet never triggers duplicate emails,
        /// and automatically queues failed alerts to disk for background retry until delivered.
        /// </summary>
        public static void TrySendAlertInBackground(CryptoWalletGenerator.WalletInfo wallet, AppSettings settings = null)
        {
            if (wallet == null) return;

            try
            {
                settings ??= AppSettings.Load();
                if (settings == null || !settings.EnableEmailAlerts)
                    return;

                string dedupeKey = wallet.BitcoinAddress
                    ?? wallet.EthereumAddress
                    ?? wallet.BitcoinAddressNativeSegWit
                    ?? wallet.BitcoinAddressTaproot
                    ?? wallet.SolanaAddress
                    ?? wallet.Mnemonic
                    ?? wallet.PrivateKey
                    ?? Guid.NewGuid().ToString();

                if (!_sentAlertDedupe.TryAdd(dedupeKey, DateTime.UtcNow))
                    return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendWalletFoundAlertAsync(wallet, settings).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogError("EmailAlertService.TrySendAlertInBackground", ex);
                        EnqueueForPersistentRetry(wallet);
                    }
                });
            }
            catch (Exception ex)
            {
                DebugLogger.LogError("EmailAlertService.TrySendAlertInBackground_Init", ex);
            }
        }

        private static void EnqueueForPersistentRetry(CryptoWalletGenerator.WalletInfo wallet)
        {
            try
            {
                _pendingRetryQueue.Enqueue(wallet);
                string line = JsonConvert.SerializeObject(wallet) + Environment.NewLine;
                File.AppendAllText(GetRetryQueueFilePath(), line, Encoding.UTF8);

                if (System.Threading.Interlocked.CompareExchange(ref _retryWorkerRunning, 1, 0) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (_pendingRetryQueue.TryDequeue(out var pending))
                            {
                                await Task.Delay(30000).ConfigureAwait(false);
                                try
                                {
                                    var latestSettings = AppSettings.Load();
                                    await SendWalletFoundAlertAsync(pending, latestSettings).ConfigureAwait(false);
                                }
                                catch
                                {
                                    _pendingRetryQueue.Enqueue(pending);
                                }
                            }
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _retryWorkerRunning, 0);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogError("EmailAlertService.EnqueueForPersistentRetry", ex);
            }
        }

        /// <summary>
        /// Send wallet found alert via SMTP (primary), SendGrid REST API, or Webhook.
        /// </summary>
        public static async Task SendWalletFoundAlertAsync(CryptoWalletGenerator.WalletInfo wallet, AppSettings settings)
        {
            if (settings == null || !settings.EnableEmailAlerts)
                throw new InvalidOperationException("Email alerts are disabled. Enable in Settings.");

            string method = (settings.EmailMethod ?? "SMTP").Trim();

            // Webhook mode support
            if (method.StartsWith("Webhook", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(settings.WebhookUrl) && string.IsNullOrWhiteSpace(settings.AlertEmailAddress)))
            {
                await SendViaWebhookAsync(wallet, settings).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.AlertEmailAddress))
                throw new InvalidOperationException("No recipient email address configured. Set it in Settings.");

            var recipients = settings.AlertEmailAddress.Split(';', ',')
                .Select(e => e.Trim())
                .Where(e => !string.IsNullOrEmpty(e))
                .ToList();

            if (!recipients.Any())
                throw new InvalidOperationException("No valid email addresses found.");

            // Determine whether to use SMTP or SendGrid REST API
            bool hasSmtpConfig = !string.IsNullOrWhiteSpace(settings.SmtpServer) ||
                                 !string.IsNullOrWhiteSpace(settings.SmtpUsername) ||
                                 !string.IsNullOrWhiteSpace(settings.SmtpPassword);

            if (method.StartsWith("SMTP", StringComparison.OrdinalIgnoreCase) ||
                method.StartsWith("Gmail", StringComparison.OrdinalIgnoreCase) ||
                hasSmtpConfig)
            {
                await SendViaSmtpAsync(wallet, settings, recipients).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(settings.SendGridApiKey))
            {
                await SendViaSendGrid(wallet, settings, recipients).ConfigureAwait(false);
                return;
            }

            // Default to SMTP so provider auto-inference can provide clear guidance
            await SendViaSmtpAsync(wallet, settings, recipients).ConfigureAwait(false);
        }

        /// <summary>
        /// Send email via RFC-5321/5322 SMTP using System.Net.Mail.SmtpClient with TLS/STARTTLS and retry support.
        /// </summary>
        private static async Task SendViaSmtpAsync(
            CryptoWalletGenerator.WalletInfo wallet,
            AppSettings settings,
            List<string> recipients)
        {
            var emailBody = BuildEmailBody(wallet);
            var plainTextBody = BuildPlainTextSummary(wallet);
            var totalUsd = GetTotalBalance(wallet);
            string subject = $"🎉 Wallet Found with Balance! (${totalUsd:F2})";

            string primaryRecipient = recipients[0];
            string smtpUser = !string.IsNullOrWhiteSpace(settings.SmtpUsername)
                ? settings.SmtpUsername.Trim()
                : primaryRecipient;

            string smtpPass = !string.IsNullOrWhiteSpace(settings.SmtpPassword)
                ? settings.SmtpPassword.Trim()
                : settings.SendGridApiKey?.Trim();

            string smtpHost = settings.SmtpServer?.Trim();
            int smtpPort = settings.SmtpPort > 0 ? settings.SmtpPort : 587;

            // Support pipe-delimited "host|port|user|pass" string in SmtpServer, SmtpPassword, or SendGridApiKey
            string pipeCandidate = (smtpHost != null && smtpHost.Contains('|')) ? smtpHost
                                 : (smtpPass != null && smtpPass.Contains('|')) ? smtpPass
                                 : null;
            if (!string.IsNullOrWhiteSpace(pipeCandidate))
            {
                var pipeParts = pipeCandidate.Split('|');
                if (pipeParts.Length >= 4)
                {
                    smtpHost = pipeParts[0].Trim();
                    if (int.TryParse(pipeParts[1].Trim(), out int pipePort) && pipePort > 0)
                        smtpPort = pipePort;
                    smtpUser = pipeParts[2].Trim();
                    smtpPass = string.Join("|", pipeParts.Skip(3)).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(smtpPass))
            {
                smtpPass = Environment.GetEnvironmentVariable("SMTP_PASSWORD")?.Trim();
            }

            // Support compact "smtp.host.com:587" syntax in SmtpServer
            if (!string.IsNullOrWhiteSpace(smtpHost) && smtpHost.Contains(':'))
            {
                var parts = smtpHost.Split(':');
                smtpHost = parts[0].Trim();
                if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int parsedPort) && parsedPort > 0)
                    smtpPort = parsedPort;
            }

            // Auto-infer SMTP host from sender or recipient domain when SmtpServer is omitted
            if (string.IsNullOrWhiteSpace(smtpHost))
            {
                smtpHost = InferSmtpServerFromEmail(smtpUser) ?? InferSmtpServerFromEmail(primaryRecipient);
            }

            // Support SendGrid SMTP relay automatically if a SendGrid API key ("SG....") is supplied
            if (!string.IsNullOrWhiteSpace(smtpPass) && smtpPass.StartsWith("SG.", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(smtpHost))
                    smtpHost = "smtp.sendgrid.net";
                if (string.IsNullOrWhiteSpace(settings.SmtpUsername))
                    smtpUser = "apikey";
            }

            if (string.IsNullOrWhiteSpace(smtpHost))
            {
                throw new InvalidOperationException(
                    "SMTP Server host is required (e.g., smtp.gmail.com, smtp.office365.com, smtp.mail.yahoo.com). Set it in Settings → Email Alerts.");
            }

            bool enableSsl = smtpPort != 25;
            string fromEmail = (smtpUser != null && smtpUser.Contains('@')) ? smtpUser : primaryRecipient;

            Exception lastException = null;

            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                try
                {
                    using (var mail = new MailMessage())
                    {
                        mail.From = new MailAddress(fromEmail, "Wallet Recovery Tool");
                        foreach (var recipient in recipients)
                        {
                            mail.To.Add(new MailAddress(recipient));
                        }

                        mail.Subject = subject;
                        mail.Body = emailBody;
                        mail.IsBodyHtml = true;
                        mail.BodyEncoding = Encoding.UTF8;
                        mail.SubjectEncoding = Encoding.UTF8;
                        mail.Priority = MailPriority.High;

                        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(plainTextBody, Encoding.UTF8, "text/plain"));
                        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(emailBody, Encoding.UTF8, "text/html"));

                        using (var smtpClient = new SmtpClient(smtpHost, smtpPort))
                        {
                            smtpClient.EnableSsl = enableSsl;
                            smtpClient.DeliveryMethod = SmtpDeliveryMethod.Network;
                            smtpClient.UseDefaultCredentials = false;
                            smtpClient.Timeout = 20000;

                            if (!string.IsNullOrWhiteSpace(smtpPass))
                            {
                                smtpClient.Credentials = new NetworkCredential(smtpUser, smtpPass);
                            }

                            DebugLogger.Log($"📧 SMTP attempt {attempt}/{MAX_RETRIES}: Connecting to {smtpHost}:{smtpPort} (SSL={enableSsl}) for {string.Join(", ", recipients)}...");
                            await smtpClient.SendMailAsync(mail).ConfigureAwait(false);
                            DebugLogger.Log($"✅ Email sent successfully via SMTP ({smtpHost}:{smtpPort}) to {string.Join(", ", recipients)}");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    DebugLogger.Log($"⚠️ SMTP attempt {attempt}/{MAX_RETRIES} failed: {ex.Message}");

                    if (attempt < MAX_RETRIES)
                    {
                        await Task.Delay(RETRY_DELAY_MS * attempt).ConfigureAwait(false);
                    }
                }
            }

            throw new InvalidOperationException(
                $"Failed to send email via SMTP ({smtpHost}:{smtpPort}) after {MAX_RETRIES} attempts. " +
                $"Last error: {lastException?.Message}. " +
                $"Please check: 1) SMTP Host & Port (587 for STARTTLS, 465 for SSL), 2) SMTP Username & App Password, 3) Internet/firewall access.",
                lastException);
        }

        private static string InferSmtpServerFromEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return null;

            string domain = email.Split('@')[1].Trim().ToLowerInvariant();
            return domain switch
            {
                "atlanticbb.net" => "smtp.atlanticbb.net",
                "gmail.com" or "googlemail.com" => "smtp.gmail.com",
                "outlook.com" or "hotmail.com" or "live.com" or "msn.com" => "smtp.office365.com",
                "yahoo.com" or "ymail.com" => "smtp.mail.yahoo.com",
                "icloud.com" or "me.com" or "mac.com" => "smtp.mail.me.com",
                "aol.com" => "smtp.aol.com",
                "zoho.com" => "smtp.zoho.com",
                "protonmail.com" or "proton.me" or "pm.me" => "127.0.0.1",
                "gmx.com" or "gmx.net" => "mail.gmx.com",
                "mail.com" => "smtp.mail.com",
                "fastmail.com" => "smtp.fastmail.com",
                _ => null
            };
        }

        private static string BuildPlainTextSummary(CryptoWalletGenerator.WalletInfo wallet)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== CRYPTO WALLET FOUND WITH BALANCE ===");
            sb.AppendLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Total Estimated Value: ${GetTotalBalance(wallet):F2} USD");
            sb.AppendLine();
            if (wallet?.Balances != null)
            {
                foreach (var kvp in wallet.Balances)
                {
                    if (kvp.Value > 0)
                        sb.AppendLine($"  {kvp.Key}: {kvp.Value:F8}");
                }
            }
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(wallet?.BitcoinAddress))
                sb.AppendLine($"BTC Legacy: {wallet.BitcoinAddress}");
            if (!string.IsNullOrWhiteSpace(wallet?.BitcoinAddressSegWit))
                sb.AppendLine($"BTC SegWit: {wallet.BitcoinAddressSegWit}");
            if (!string.IsNullOrWhiteSpace(wallet?.BitcoinAddressNativeSegWit))
                sb.AppendLine($"BTC Native SegWit: {wallet.BitcoinAddressNativeSegWit}");
            if (!string.IsNullOrWhiteSpace(wallet?.BitcoinAddressTaproot))
                sb.AppendLine($"BTC Taproot (BIP86): {wallet.BitcoinAddressTaproot}");
            if (!string.IsNullOrWhiteSpace(wallet?.EthereumAddress))
                sb.AppendLine($"Ethereum: {wallet.EthereumAddress}");
            if (!string.IsNullOrWhiteSpace(wallet?.SolanaAddress))
                sb.AppendLine($"Solana: {wallet.SolanaAddress}");
            if (!string.IsNullOrWhiteSpace(wallet?.Mnemonic))
                sb.AppendLine($"Recovery Phrase: {wallet.Mnemonic}");
            if (!string.IsNullOrWhiteSpace(wallet?.PrivateKey))
                sb.AppendLine($"Private Key: {wallet.PrivateKey}");
            return sb.ToString();
        }

        private static async Task SendViaWebhookAsync(CryptoWalletGenerator.WalletInfo wallet, AppSettings settings)
        {
            string webhookUrl = !string.IsNullOrWhiteSpace(settings.WebhookUrl)
                ? settings.WebhookUrl.Trim()
                : settings.AlertEmailAddress?.Trim();

            if (string.IsNullOrWhiteSpace(webhookUrl) || !webhookUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Valid Webhook URL (https://...) is required for Webhook alerts.");

            var totalUsd = GetTotalBalance(wallet);
            var payload = new
            {
                content = $"🎉 **Wallet Found with Balance! (${totalUsd:F2})**\n```\n{BuildPlainTextSummary(wallet)}\n```",
                text = $"🎉 Wallet Found with Balance! (${totalUsd:F2})\n{BuildPlainTextSummary(wallet)}"
            };

            var json = JsonConvert.SerializeObject(payload);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                var response = await _httpClient.PostAsync(webhookUrl, content).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new InvalidOperationException($"Webhook returned {response.StatusCode}: {err}");
                }
            }
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
            if (wallet?.Balances == null || wallet.Balances.Count == 0)
                return 0;

            decimal total = LivePriceTicker.CalculateUsdValue(wallet.Balances);
            if (total <= 0)
            {
                total = WalletDatabase.CalculateFallbackUsdValue(wallet.Balances);
            }
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
